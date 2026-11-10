using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>Tool calling end to end with a scripted model: extraction, matching, proposal, confirmation, audit.</summary>
[Collection(ApiCollection.Name)]
public class ChatToolTests : IDisposable
{
    private readonly ApiFactory _factory;

    public ChatToolTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Chat.Reset();
        FoodSeed.EnsureAsync(factory.Services).GetAwaiter().GetResult();
    }

    public void Dispose() => _factory.Chat.Reset();

    private const string ChickenAndRiceExtraction = """
        {"mealType":"lunch","items":[
          {"foodName":"chicken breast","quantity":200,"unit":"g","preparation":"unspecified","originalText":"200 grams of chicken breast"},
          {"foodName":"rice","quantity":150,"unit":"g","preparation":"cooked","originalText":"150 grams of cooked rice"}]}
        """;

    private static async Task<(HttpResponseMessage Response, List<SseEvent> Events)> Send(TestUser user, string message, Guid? conversationId = null)
    {
        var response = await user.Client.PostAsJsonAsync("/api/chat/stream", new { conversationId, message, clientMessageId = Guid.NewGuid(), clientTime = DateTimeOffset.UtcNow });
        var events = new List<SseEvent>();
        if (response.IsSuccessStatusCode)
        {
            foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var name = lines.Single(l => l.StartsWith("event:", StringComparison.Ordinal))[6..].Trim();
                var data = lines.Single(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
                events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
            }
        }

        return (response, events);
    }

    private static Guid ConversationIdOf(List<SseEvent> events) => events.First(e => e.Name == "conversation").Data.GetProperty("conversationId").GetGuid();

    private void ScriptMealLogging(string finalText = "I prepared your lunch. Please check it and confirm.")
    {
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "I ate 200 grams of chicken breast and 150 grams of cooked rice" });
        _factory.Chat.EnqueueText(ChickenAndRiceExtraction);
        _factory.Chat.EnqueueText(finalText);
    }

    private async Task<int> MealCountAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<Meal>().CountAsync(m => m.UserId == userId);
    }

    private async Task<List<ToolExecution>> ExecutionsAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<ToolExecution>().AsNoTracking()
            .Where(t => t.ConversationId == conversationId).OrderBy(t => t.CreatedAtUtc).ToListAsync();
    }

    // ---- the Phase 3 definition of done ----

    [Fact]
    public async Task Chicken_and_rice_are_extracted_matched_proposed_confirmed_saved_and_summarised()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();

        var (_, events) = await Send(user, "I ate 200 grams of chicken breast and 150 grams of cooked rice");

        // 1) the stream shows the tool, the proposal card, and the model's final text
        Assert.Contains(events, e => e.Name == "tool" && e.Data.GetProperty("name").GetString() == "PrepareMeal" && e.Data.GetProperty("status").GetString() == "running");
        Assert.Contains(events, e => e.Name == "tool" && e.Data.GetProperty("status").GetString() == "done");
        var card = events.Single(e => e.Name == "proposal").Data.GetProperty("card");
        var proposal = card.GetProperty("proposal");
        Assert.Equal("done", events[^1].Name);

        // 2) deterministic matching and calculation (the model never supplied a number)
        var items = proposal.GetProperty("items");
        Assert.Equal("Chicken, broilers or fryers, breast, meat only, cooked, roasted", items[0].GetProperty("food").GetProperty("description").GetString());
        Assert.Equal("Rice, white, long-grain, regular, enriched, cooked", items[1].GetProperty("food").GetProperty("description").GetString());
        Assert.Equal(330m, items[0].GetProperty("nutrients").GetProperty("calories").GetDecimal());
        Assert.Equal(195m, items[1].GetProperty("nutrients").GetProperty("calories").GetDecimal());
        Assert.Equal(525m, proposal.GetProperty("totals").GetProperty("calories").GetDecimal());
        Assert.Equal("Lunch", proposal.GetProperty("mealType").GetString());

        // 3) nothing is saved yet
        Assert.Equal(0, await MealCountAsync(user.Id));

        // 4) the user confirms through the normal meal endpoint, referencing the proposal
        var proposalId = card.GetProperty("id").GetGuid();
        var confirm = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            type = "Lunch",
            eatenAt = new DateTimeOffset(2026, 10, 21, 13, 30, 0, TimeSpan.FromHours(3)),
            proposalId,
            items = items.EnumerateArray().Select(i => new
            {
                foodId = i.GetProperty("food").GetProperty("foodId").GetGuid(),
                name = "x",
                quantityGrams = i.GetProperty("quantityGrams").GetDecimal(),
                preparation = "Cooked",
            }).ToArray(),
        });
        Assert.Equal(HttpStatusCode.Created, confirm.StatusCode);
        var meal = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Assistant", meal.GetProperty("origin").GetString());

        // 5) the journal and the summary reflect it; the proposal is committed
        var summary = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/summary/daily?date=2026-10-21");
        Assert.Equal(525m, summary.GetProperty("totals").GetProperty("calories").GetDecimal());
        var proposals = await user.Client.GetFromJsonAsync<JsonElement>($"/api/conversations/{ConversationIdOf(events)}/proposals");
        Assert.Equal("Committed", proposals[0].GetProperty("state").GetString());
        Assert.Equal(meal.GetProperty("id").GetGuid(), proposals[0].GetProperty("mealId").GetGuid());
    }

    [Fact]
    public async Task The_model_sees_only_the_prepared_numbers_and_is_told_nothing_was_saved()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();

        await Send(user, "I ate 200 grams of chicken breast and 150 grams of cooked rice");

        // call 1: model, call 2: extraction (no tools), call 3: model with the tool result
        Assert.Equal(3, _factory.Chat.Calls.Count);
        var toolResult = _factory.Chat.Calls[2].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        var json = JsonSerializer.Serialize(toolResult.Result);
        Assert.Contains("proposal_shown_to_user", json, StringComparison.Ordinal);
        Assert.Contains("\"saved\":false", json, StringComparison.Ordinal);
        Assert.Contains("525", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_past_day_reaches_the_proposal_and_an_implausible_day_is_ignored()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        async Task<JsonElement> ProposalFor(string eatenOn)
        {
            _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "I ate rice yesterday" });
            _factory.Chat.EnqueueText($$"""{"mealType":"dinner","eatenOn":"{{eatenOn}}","items":[{"foodName":"rice","quantity":150,"unit":"g","preparation":"cooked","originalText":"150 g rice"}]}""");
            _factory.Chat.EnqueueText("Prepared.");
            var (_, events) = await Send(user, "I ate rice yesterday");
            return events.Single(e => e.Name == "proposal").Data.GetProperty("card").GetProperty("proposal");
        }

        var yesterday = today.AddDays(-1).ToString("yyyy-MM-dd");
        Assert.Equal(yesterday, (await ProposalFor(yesterday)).GetProperty("localDate").GetString());
        Assert.Equal(JsonValueKind.Null, (await ProposalFor(today.AddDays(3).ToString("yyyy-MM-dd"))).GetProperty("localDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ProposalFor("1999-01-01")).GetProperty("localDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ProposalFor("")).GetProperty("localDate").ValueKind);
    }

    [Fact]
    public async Task Confirming_twice_returns_the_same_meal_and_never_duplicates_it()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();
        var (_, events) = await Send(user, "I ate chicken and rice");
        var card = events.Single(e => e.Name == "proposal").Data.GetProperty("card");
        var body = new
        {
            eatenAt = DateTimeOffset.UtcNow,
            proposalId = card.GetProperty("id").GetGuid(),
            items = card.GetProperty("proposal").GetProperty("items").EnumerateArray().Select(i => new
            {
                foodId = i.GetProperty("food").GetProperty("foodId").GetGuid(),
                name = "x",
                quantityGrams = i.GetProperty("quantityGrams").GetDecimal(),
                preparation = "Cooked",
            }).ToArray(),
        };

        var first = await user.Client.PostAsJsonAsync("/api/nutrition/meals", body);
        var second = await user.Client.PostAsJsonAsync("/api/nutrition/meals", body);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => user.Client.PostAsJsonAsync("/api/nutrition/meals", body)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(id, (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        foreach (var response in parallel)
        {
            Assert.Equal(id, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        Assert.Equal(1, await MealCountAsync(user.Id));
    }

    [Fact]
    public async Task The_user_can_correct_the_proposal_before_confirming_and_the_server_recalculates()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();
        var (_, events) = await Send(user, "I ate chicken and rice");
        var card = events.Single(e => e.Name == "proposal").Data.GetProperty("card");
        var items = card.GetProperty("proposal").GetProperty("items");

        var confirm = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            eatenAt = DateTimeOffset.UtcNow,
            proposalId = card.GetProperty("id").GetGuid(),
            items = new[]
            {
                // the user halves the chicken and drops the rice
                new { foodId = items[0].GetProperty("food").GetProperty("foodId").GetGuid(), name = "x", quantityGrams = 100m, preparation = "Cooked" },
            },
        });

        confirm.EnsureSuccessStatusCode();
        Assert.Equal(165m, (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totals").GetProperty("calories").GetDecimal());
    }

    [Fact]
    public async Task A_dismissed_proposal_can_no_longer_be_confirmed()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();
        var (_, events) = await Send(user, "I ate chicken and rice");
        var card = events.Single(e => e.Name == "proposal").Data.GetProperty("card");
        var proposalId = card.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.PostAsync($"/api/assistant/proposals/{proposalId}/dismiss", null)).StatusCode);
        var confirm = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            proposalId,
            items = new[] { new { foodId = card.GetProperty("proposal").GetProperty("items")[0].GetProperty("food").GetProperty("foodId").GetGuid(), name = "x", quantityGrams = 100, preparation = "Cooked" } },
        });

        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Equal(0, await MealCountAsync(user.Id));
    }

    [Fact]
    public async Task Proposals_belong_to_their_owner()
    {
        var alice = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var bob = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();
        var (_, events) = await Send(alice, "I ate chicken and rice");
        var card = events.Single(e => e.Name == "proposal").Data.GetProperty("card");
        var proposalId = card.GetProperty("id").GetGuid();
        var foodId = card.GetProperty("proposal").GetProperty("items")[0].GetProperty("food").GetProperty("foodId").GetGuid();

        var steal = await bob.Client.PostAsJsonAsync("/api/nutrition/meals",
            new { proposalId, items = new[] { new { foodId, name = "x", quantityGrams = 100, preparation = "Cooked" } } });
        var dismiss = await bob.Client.PostAsync($"/api/assistant/proposals/{proposalId}/dismiss", null);
        var list = await bob.Client.GetAsync($"/api/conversations/{ConversationIdOf(events)}/proposals");

        Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(0, await MealCountAsync(bob.Id));
    }

    [Fact]
    public async Task Proposals_survive_a_reload_and_show_under_the_right_message()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();
        var (_, events) = await Send(user, "I ate chicken and rice");
        var conversationId = ConversationIdOf(events);

        var proposals = await user.Client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversationId}/proposals");
        var messages = await user.Client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversationId}/messages");

        Assert.Equal(1, proposals.GetArrayLength());
        Assert.Equal("Proposed", proposals[0].GetProperty("state").GetString());
        var sequence = proposals[0].GetProperty("assistantMessageSequence").GetInt32();
        Assert.Equal("Assistant", messages.EnumerateArray().Single(m => m.GetProperty("sequence").GetInt32() == sequence).GetProperty("role").GetString());
    }

    // ---- failure handling ----

    [Fact]
    public async Task Unknown_foods_stay_unmatched_and_are_never_guessed()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "dragon steak and rice" });
        _factory.Chat.EnqueueText("""{"items":[{"foodName":"dragon steak","quantity":200,"unit":"g","preparation":"unspecified","originalText":"dragon steak"},{"foodName":"rice","quantity":100,"unit":"g","preparation":"cooked","originalText":"rice"}]}""");
        _factory.Chat.EnqueueText("Done.");

        var (_, events) = await Send(user, "dragon steak and rice");

        var proposal = events.Single(e => e.Name == "proposal").Data.GetProperty("card").GetProperty("proposal");
        Assert.Equal("Unmatched", proposal.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("items")[0].GetProperty("food").ValueKind);
        Assert.Equal("Matched", proposal.GetProperty("items")[1].GetProperty("status").GetString());
        Assert.Equal(130m, proposal.GetProperty("totals").GetProperty("calories").GetDecimal()); // only the rice counts
        Assert.NotEmpty(proposal.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Household_measures_without_a_known_portion_ask_for_grams_instead_of_guessing()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "2 tablespoons of rice and 1 cup of rice" });
        _factory.Chat.EnqueueText("""{"items":[{"foodName":"rice","quantity":2,"unit":"tbsp","preparation":"cooked","originalText":"2 tablespoons of rice"},{"foodName":"rice","quantity":1,"unit":"cup","preparation":"cooked","originalText":"1 cup of rice"}]}""");
        _factory.Chat.EnqueueText("Done.");

        var (_, events) = await Send(user, "2 tablespoons of rice and 1 cup of rice");

        var items = events.Single(e => e.Name == "proposal").Data.GetProperty("card").GetProperty("proposal").GetProperty("items");
        Assert.Equal("NeedsQuantity", items[0].GetProperty("status").GetString());
        Assert.Equal("Matched", items[1].GetProperty("status").GetString());
        Assert.Equal(158m, items[1].GetProperty("quantityGrams").GetDecimal());
    }

    [Fact]
    public async Task Unstated_raw_or_cooked_is_flagged_as_an_assumption()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "100 g rice" });
        _factory.Chat.EnqueueText("""{"items":[{"foodName":"rice","quantity":100,"unit":"g","preparation":"unspecified","originalText":"100 g rice"}]}""");
        _factory.Chat.EnqueueText("Done.");

        var (_, events) = await Send(user, "100 g rice");

        var item = events.Single(e => e.Name == "proposal").Data.GetProperty("card").GetProperty("proposal").GetProperty("items")[0];
        Assert.True(item.GetProperty("preparationAssumed").GetBoolean());
        Assert.Equal("Cooked", item.GetProperty("preparation").GetString());
    }

    [Fact]
    public async Task Malformed_extraction_gets_one_repair_attempt_and_then_fails_without_a_proposal()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "stuff" });
        _factory.Chat.EnqueueText("this is not json at all");
        _factory.Chat.EnqueueText("""{"items":[]}""");
        _factory.Chat.EnqueueText("I could not understand the meal. Could you rephrase it?");

        var (_, events) = await Send(user, "stuff");

        Assert.DoesNotContain(events, e => e.Name == "proposal");
        Assert.Equal("done", events[^1].Name);
        var executions = await ExecutionsAsync(ConversationIdOf(events));
        Assert.Equal(ToolOutcome.Rejected, executions.Single().Outcome);
        Assert.Equal("meal_parse_failed", executions.Single().ErrorCode);
        Assert.Equal(0, await MealCountAsync(user.Id));
    }

    [Fact]
    public async Task Hostile_extraction_values_are_rejected()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "lots" });
        _factory.Chat.EnqueueText("""{"items":[{"foodName":"rice","quantity":99999999,"unit":"g","preparation":"cooked","originalText":"x"}]}""");
        _factory.Chat.EnqueueText("""{"items":[{"foodName":"rice","quantity":-5,"unit":"g","preparation":"cooked","originalText":"x"}]}""");
        _factory.Chat.EnqueueText("Sorry.");

        var (_, events) = await Send(user, "lots");

        Assert.DoesNotContain(events, e => e.Name == "proposal");
        Assert.Equal(ToolOutcome.Rejected, (await ExecutionsAsync(ConversationIdOf(events))).Single().Outcome);
    }

    [Fact]
    public async Task A_model_that_only_calls_the_tool_still_gets_a_deterministic_sentence()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "100 g rice" });
        _factory.Chat.EnqueueText("""{"items":[{"foodName":"rice","quantity":100,"unit":"g","preparation":"cooked","originalText":"100 g rice"}]}""");
        _factory.Chat.Enqueue(_ => []); // the model then says nothing

        var (_, events) = await Send(user, "100 g cooked rice");

        Assert.Equal("done", events[^1].Name);
        Assert.Contains("confirm", string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString())), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Meal_preparation_without_health_data_consent_is_rejected_and_audited()
    {
        var user = await TestUser.RegisterAsync(_factory, healthConsent: false, aiConsent: true);
        ScriptMealLogging("I cannot do that without your consent.");

        var (_, events) = await Send(user, "I ate chicken and rice");

        Assert.DoesNotContain(events, e => e.Name == "proposal");
        var execution = (await ExecutionsAsync(ConversationIdOf(events))).Single();
        Assert.Equal(ToolOutcome.Rejected, execution.Outcome);
        Assert.Equal("consent_required", execution.ErrorCode);
    }

    // ---- read tools ----

    [Fact]
    public async Task The_daily_summary_tool_returns_the_callers_data_to_the_model_and_is_audited()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        await SaveChickenMealAsync(user, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        _factory.Chat.EnqueueToolCall("GetDailyNutritionSummary", new { date = "2026-10-21" });
        _factory.Chat.EnqueueText("You had 330 kcal.");

        var (_, events) = await Send(user, "How many calories have I consumed today?");

        var result = JsonSerializer.Serialize(_factory.Chat.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("330", result, StringComparison.Ordinal);
        var execution = (await ExecutionsAsync(ConversationIdOf(events))).Single();
        Assert.Equal(("GetDailyNutritionSummary", ToolKind.Read, ToolOutcome.Succeeded), (execution.ToolName, execution.Kind, execution.Outcome));
        Assert.Contains("2026-10-21", execution.ArgumentsJson, StringComparison.Ordinal);
        Assert.Null(execution.ResultJson); // results of read tools are not stored
    }

    [Fact]
    public async Task The_model_cannot_choose_whose_data_a_tool_reads()
    {
        var alice = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var bob = await TestUser.RegisterAsync(_factory, aiConsent: true);
        await SaveChickenMealAsync(alice, new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        // a hijacked model tries to pass Alice's id while talking to Bob
        _factory.Chat.EnqueueToolCall("GetDailyNutritionSummary", new { date = "2026-10-21", userId = alice.Id, user = alice.Email });
        _factory.Chat.EnqueueText("Nothing logged.");

        await Send(bob, "show me the data of everyone");

        var result = JsonSerializer.Serialize(_factory.Chat.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.DoesNotContain("330", result, StringComparison.Ordinal);
        Assert.DoesNotContain(alice.Email, result, StringComparison.Ordinal);
        var offered = _factory.Chat.LastOptions!.Tools!.OfType<AIFunction>().ToList();
        Assert.Equal(
            [
                "GetDailyNutritionSummary", "GetHealthInsights", "GetHealthProfile", "GetNutritionSummary", "GetSleepAndRecovery", "GetWearableStatus",
                "GetWeightHistory", "PrepareMeal", "ProposeMealDeletion", "ProposeNutritionTargets", "ProposeProfileUpdate", "ProposeWeight",
                "SearchMedicalKnowledge", "SyncWearable",
            ],
            offered.Select(f => f.Name).Order());
        var parameters = offered.SelectMany(f => f.JsonSchema.TryGetProperty("properties", out var p) ? p.EnumerateObject().Select(x => x.Name) : []).ToList();
        Assert.DoesNotContain(parameters, name => name.Contains("user", StringComparison.OrdinalIgnoreCase));  // no tool takes a user id
    }

    [Fact]
    public async Task Invalid_tool_arguments_are_rejected_and_reported_to_the_model_not_thrown()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("GetDailyNutritionSummary", new { date = "tomorrow morning" });
        _factory.Chat.EnqueueText("I could not read that date.");

        var (response, events) = await Send(user, "how was tomorrow?");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("done", events[^1].Name);
        var result = JsonSerializer.Serialize(_factory.Chat.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("invalid_date", result, StringComparison.Ordinal);
        Assert.Equal(ToolOutcome.Rejected, (await ExecutionsAsync(ConversationIdOf(events))).Single().Outcome);
    }

    [Fact]
    public async Task The_profile_tool_gives_age_but_never_the_date_of_birth()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        (await user.Client.PutAsJsonAsync("/api/health-profile", new
        {
            dateOfBirth = "1985-04-12", sex = "Female", heightCm = 168.5, activityLevel = "LightlyActive", timeZoneId = "UTC",
        })).EnsureSuccessStatusCode();
        _factory.Chat.EnqueueToolCall("GetHealthProfile", new { });
        _factory.Chat.EnqueueText("You are 41.");

        await Send(user, "what do you know about me?");

        var result = JsonSerializer.Serialize(_factory.Chat.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("\"ageYears\"", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1985", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Europe", result, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Email, result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_tools_without_health_data_consent_return_a_consent_error_to_the_model()
    {
        var user = await TestUser.RegisterAsync(_factory, healthConsent: false, aiConsent: true);
        _factory.Chat.EnqueueToolCall("GetHealthProfile", new { });
        _factory.Chat.EnqueueText("I need your consent first.");

        var (_, events) = await Send(user, "what is my profile?");

        var result = JsonSerializer.Serialize(_factory.Chat.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("consent_required", result, StringComparison.Ordinal);
        Assert.Equal("consent_required", (await ExecutionsAsync(ConversationIdOf(events))).Single().ErrorCode);
    }

    [Fact]
    public async Task A_model_stuck_in_a_tool_loop_is_stopped()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        for (var i = 0; i < 12; i++)
        {
            var callId = $"loop-{i}";
            _factory.Chat.Enqueue(_ => [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, "GetHealthProfile", new Dictionary<string, object?>())])]);
        }

        var (response, events) = await Send(user, "loop forever");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(events[^1].Name, new[] { "done", "error" });
        Assert.True(_factory.Chat.Calls.Count <= 8, $"model was called {_factory.Chat.Calls.Count} times");
    }

    [Fact]
    public async Task Unknown_tool_names_requested_by_the_model_are_not_executed()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("DeleteAllMeals", new { });
        _factory.Chat.EnqueueText("I cannot do that.");

        var (_, events) = await Send(user, "delete everything");

        Assert.Empty(await ExecutionsAsync(ConversationIdOf(events)));
        Assert.Equal("done", events[^1].Name);
    }

    [Fact]
    public async Task Emergency_messages_still_bypass_the_model_and_all_tools()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();

        var (_, events) = await Send(user, "I have crushing chest pain and I ate chicken");

        Assert.Empty(_factory.Chat.Calls);
        Assert.Contains(events, e => e.Name == "safety");
        Assert.Empty(await ExecutionsAsync(ConversationIdOf(events)));
    }

    [Fact]
    public async Task Tools_are_offered_to_the_model_with_clear_names_and_the_read_write_split_is_recorded()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        ScriptMealLogging();

        var (_, events) = await Send(user, "I ate chicken and rice");

        var execution = (await ExecutionsAsync(ConversationIdOf(events))).Single();
        Assert.Equal(("PrepareMeal", ToolKind.Write, ToolOutcome.Proposed), (execution.ToolName, execution.Kind, execution.Outcome));
    }

    private async Task SaveChickenMealAsync(TestUser user, DateTimeOffset at)
    {
        using var scope = _factory.Services.CreateScope();
        var chicken = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<Food>().AsNoTracking().FirstAsync(f => f.SourceFoodId == "171477")).Id;
        (await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            eatenAt = at,
            items = new[] { new { foodId = chicken, name = "x", quantityGrams = 200, preparation = "Cooked" } },
        })).EnsureSuccessStatusCode();
    }
}
