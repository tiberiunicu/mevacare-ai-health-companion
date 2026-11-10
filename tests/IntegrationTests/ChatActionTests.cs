using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>
/// Changes prepared by the assistant (weight, targets, profile, meal deletion and correction): the tool only shows a card; the signed-in
/// user's confirmation applies exactly the stored payload, once.
/// </summary>
[Collection(ApiCollection.Name)]
public class ChatActionTests : IDisposable
{
    private readonly ApiFactory _factory;

    public ChatActionTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Chat.Reset();
        FoodSeed.EnsureAsync(factory.Services).GetAwaiter().GetResult();
    }

    public void Dispose() => _factory.Chat.Reset();

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    private static async Task<List<SseEvent>> Send(TestUser user, string message)
    {
        var response = await user.Client.PostAsJsonAsync("/api/chat/stream", new { conversationId = (Guid?)null, message, clientMessageId = Guid.NewGuid(), clientTime = DateTimeOffset.UtcNow });
        response.EnsureSuccessStatusCode();
        var events = new List<SseEvent>();
        foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var name = lines.Single(l => l.StartsWith("event:", StringComparison.Ordinal))[6..].Trim();
            var data = lines.Single(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
            events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    /// <summary>The model calls one tool and then answers; returns the card the stream showed, if any.</summary>
    private async Task<(JsonElement? Card, List<SseEvent> Events)> Propose(TestUser user, string tool, object arguments, string? message = null)
    {
        _factory.Chat.EnqueueToolCall(tool, arguments);
        _factory.Chat.EnqueueText("I prepared it. Please check it and press Confirm.");
        // By default the user states exactly the numbers the model passes (a change may only contain numbers the user wrote).
        var events = await Send(user, message ?? $"please do that {JsonSerializer.Serialize(arguments)}");
        var action = events.SingleOrDefault(e => e.Name == "action");
        return (action is null ? null : action.Data.GetProperty("card"), events);
    }

    private async Task<JsonElement> WeightsAsync(TestUser user) =>
        await user.Client.GetFromJsonAsync<JsonElement>($"/api/measurements/weight?from={DateTime.UtcNow.AddDays(-3):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(1):yyyy-MM-dd}");

    private static Task<HttpResponseMessage> Confirm(TestUser user, JsonElement card) =>
        user.Client.PostAsync($"/api/assistant/actions/{card.GetProperty("id").GetGuid()}/confirm", null);

    private async Task<Guid> CreateMealAsync(TestUser user, string name = "Home-made cake", decimal calories = 400m)
    {
        var response = await user.Client.PostAsJsonAsync("/api/nutrition/meals", new
        {
            type = "Lunch",
            eatenAt = DateTimeOffset.UtcNow,
            items = new[] { new { foodId = (Guid?)null, name, quantityGrams = 100m, preparation = "Unspecified", estimated = new { calories, proteinG = (decimal?)null, carbohydratesG = (decimal?)null, fatG = (decimal?)null, fiberG = (decimal?)null } } },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<List<ToolExecution>> ExecutionsAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<ToolExecution>().AsNoTracking()
            .Where(t => t.UserId == userId).OrderBy(t => t.CreatedAtUtc).ToListAsync();
    }

    // ---- weight ----

    [Fact]
    public async Task A_weight_is_only_a_card_until_the_user_confirms_and_confirming_twice_stores_it_once()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, events) = await Propose(user, "ProposeWeight", new { kilograms = 82.4m });

        Assert.NotNull(card);
        Assert.Equal("ProposeWeight", card.Value.GetProperty("kind").GetString());
        Assert.Equal("Proposed", card.Value.GetProperty("state").GetString());
        Assert.Equal(82.4m, card.Value.GetProperty("payload").GetProperty("kilograms").GetDecimal());
        Assert.Equal("done", events[^1].Name);
        Assert.Empty((await WeightsAsync(user)).GetProperty("points").EnumerateArray()); // nothing stored yet

        var first = await Confirm(user, card.Value);
        var second = await Confirm(user, card.Value);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Confirm(user, card.Value)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("Committed", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.All(parallel, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var points = (await WeightsAsync(user)).GetProperty("points").EnumerateArray().ToList();
        var point = Assert.Single(points);
        Assert.Equal(82.4m, point.GetProperty("kilograms").GetDecimal());
        Assert.Equal("manual", point.GetProperty("provider").GetString());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(900)]
    public async Task An_implausible_weight_is_rejected_by_the_tool_and_no_card_is_shown(int kilograms)
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms });

        Assert.Null(card);
        Assert.Equal(ToolOutcome.Rejected, Assert.Single(await ExecutionsAsync(user.Id)).Outcome);
        Assert.Empty((await WeightsAsync(user)).GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task A_weight_for_a_named_past_day_is_stored_on_that_day_and_a_future_day_is_rejected()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");

        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms = 80m, date = twoDaysAgo });
        await Confirm(user, card!.Value);
        var (future, _) = await Propose(user, "ProposeWeight", new { kilograms = 80m, date = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd") });

        Assert.Equal(twoDaysAgo, Assert.Single((await WeightsAsync(user)).GetProperty("points").EnumerateArray()).GetProperty("localDate").GetString());
        Assert.Null(future);
    }

    // ---- the model may only use numbers the user wrote ----

    [Theory]
    [InlineData("Log my weight", 81.5)]                       // no number at all: the model made one up
    [InlineData("I weigh about eighty kilos", 80)]            // a number in words is not a stated number: the model has to ask for digits
    [InlineData("I gained 2 kg since last week", 84.2)]       // computed from earlier data, not stated
    public async Task A_weight_the_user_did_not_write_is_refused_and_no_card_is_shown(string message, double kilograms)
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms }, message);

        Assert.Null(card);
        var execution = Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal(ToolOutcome.Rejected, execution.Outcome);
        Assert.Equal("value_not_stated", execution.ErrorCode);
    }

    [Theory]
    [InlineData("Azi cântăresc 82,4 kg", 82.4)]               // a decimal comma
    [InlineData("I weigh 82.4kg", 82.4)]                       // no space before the unit
    [InlineData("am 82.4 de kilograme acum, nu 83", 82.4)]     // several numbers in the message
    public async Task A_weight_the_user_wrote_is_accepted_in_the_ways_people_write_numbers(string message, double kilograms)
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms }, message);

        Assert.NotNull(card);
    }

    [Theory]
    [InlineData("Set my target to 2.300 calories", 2300)]      // a thousands separator
    [InlineData("target 2,300 kcal", 2300)]
    [InlineData("make it 2300", 2300)]
    public async Task A_target_written_with_a_thousands_separator_is_accepted(string message, int calories)
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeNutritionTargets", new { calories }, message);

        Assert.NotNull(card);
    }

    [Fact]
    public async Task A_number_the_model_calculated_or_invented_is_refused_for_targets_and_height_too()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        (await user.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = 2300, proteinG = (int?)null, carbohydratesG = (int?)null, fatG = (int?)null, fiberG = (int?)null })).EnsureSuccessStatusCode();

        var (invented, _) = await Propose(user, "ProposeNutritionTargets", new { calories = 1800 }, "Change my calorie target");
        var (computed, _) = await Propose(user, "ProposeNutritionTargets", new { calories = 2400 }, "increase my target by 100");
        var (height, _) = await Propose(user, "ProposeProfileUpdate", new { heightCm = 175 }, "update my height");

        Assert.Null(invented);
        Assert.Null(computed);
        Assert.Null(height);
        Assert.Equal(2300m, (await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/targets")).GetProperty("calories").GetDecimal());
    }

    [Fact]
    public async Task A_prepared_activity_level_without_any_number_needs_no_stated_number()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeProfileUpdate", new { activityLevel = "VeryActive" }, "I am very active, I train every day");

        Assert.NotNull(card);
    }

    // ---- targets ----

    [Fact]
    public async Task Changing_one_target_keeps_the_others_and_the_card_shows_before_and_after()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        (await user.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = 2000, proteinG = 100, carbohydratesG = (int?)null, fatG = (int?)null, fiberG = (int?)null })).EnsureSuccessStatusCode();

        var (card, _) = await Propose(user, "ProposeNutritionTargets", new { calories = 2200 });

        var payload = card!.Value.GetProperty("payload");
        Assert.Equal(2000m, payload.GetProperty("before").GetProperty("calories").GetDecimal());
        Assert.Equal(2200m, payload.GetProperty("after").GetProperty("calories").GetDecimal());
        Assert.Equal(100m, payload.GetProperty("after").GetProperty("proteinG").GetDecimal());
        Assert.Equal(2000m, (await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/targets")).GetProperty("calories").GetDecimal()); // not yet

        Assert.Equal(HttpStatusCode.OK, (await Confirm(user, card.Value)).StatusCode);

        var targets = await user.Client.GetFromJsonAsync<JsonElement>("/api/nutrition/targets");
        Assert.Equal(2200m, targets.GetProperty("calories").GetDecimal());
        Assert.Equal(100m, targets.GetProperty("proteinG").GetDecimal());
    }

    [Fact]
    public async Task A_target_change_that_changes_nothing_or_is_out_of_range_shows_no_card()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        (await user.Client.PutAsJsonAsync("/api/nutrition/targets", new { calories = 2000, proteinG = (int?)null, carbohydratesG = (int?)null, fatG = (int?)null, fiberG = (int?)null })).EnsureSuccessStatusCode();

        var (same, _) = await Propose(user, "ProposeNutritionTargets", new { calories = 2000 });
        var (absurd, _) = await Propose(user, "ProposeNutritionTargets", new { calories = 99999 });
        var (nothing, _) = await Propose(user, "ProposeNutritionTargets", new { });

        Assert.Null(same);
        Assert.Null(absurd);
        Assert.Null(nothing);
    }

    // ---- profile ----

    [Fact]
    public async Task Height_and_activity_level_change_after_confirmation_and_nothing_else_in_the_profile_does()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        (await user.Client.PutAsJsonAsync("/api/health-profile", new
        {
            dateOfBirth = "1985-04-12", sex = "Female", heightCm = 168.5, activityLevel = "Sedentary", timeZoneId = "UTC",
        })).EnsureSuccessStatusCode();

        var (card, _) = await Propose(user, "ProposeProfileUpdate", new { heightCm = 170, activityLevel = "moderatelyactive" });

        var payload = card!.Value.GetProperty("payload");
        Assert.Equal("Sedentary", payload.GetProperty("before").GetProperty("activityLevel").GetString());
        Assert.Equal("ModeratelyActive", payload.GetProperty("after").GetProperty("activityLevel").GetString());
        Assert.Equal(168.5m, (await user.Client.GetFromJsonAsync<JsonElement>("/api/health-profile")).GetProperty("heightCm").GetDecimal());

        Assert.Equal(HttpStatusCode.OK, (await Confirm(user, card.Value)).StatusCode);

        var profile = await user.Client.GetFromJsonAsync<JsonElement>("/api/health-profile");
        Assert.Equal(170m, profile.GetProperty("heightCm").GetDecimal());
        Assert.Equal("ModeratelyActive", profile.GetProperty("activityLevel").GetString());
        Assert.Equal("1985-04-12", profile.GetProperty("dateOfBirth").GetString());
        Assert.Equal("Female", profile.GetProperty("sex").GetString());
    }

    [Theory]
    [InlineData("heightCm", 10)]
    [InlineData("heightCm", 400)]
    public async Task An_implausible_height_shows_no_card(string name, int value)
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeProfileUpdate", new Dictionary<string, object> { [name] = value });

        Assert.Null(card);
    }

    [Fact]
    public async Task An_unknown_activity_level_shows_no_card()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);

        var (card, _) = await Propose(user, "ProposeProfileUpdate", new { activityLevel = "AstronautTraining" });

        Assert.Null(card);
    }

    // ---- deleting and correcting meals ----

    [Fact]
    public async Task A_meal_is_deleted_only_after_the_user_confirms()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var mealId = await CreateMealAsync(user);

        var (card, _) = await Propose(user, "ProposeMealDeletion", new { mealId = mealId.ToString() });

        var payload = card!.Value.GetProperty("payload");
        Assert.Equal(mealId, payload.GetProperty("mealId").GetGuid());
        Assert.Equal("Home-made cake", payload.GetProperty("items")[0].GetString());
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync($"/api/nutrition/meals/{mealId}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Confirm(user, card.Value)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.GetAsync($"/api/nutrition/meals/{mealId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Confirm(user, card.Value)).StatusCode); // idempotent
    }

    [Fact]
    public async Task Another_users_meal_cannot_be_prepared_for_deletion_or_correction()
    {
        var alice = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var bob = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var bobsMeal = await CreateMealAsync(bob);

        var (deletion, _) = await Propose(alice, "ProposeMealDeletion", new { mealId = bobsMeal.ToString() });
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "200 g chicken breast", replacesMealId = bobsMeal.ToString() });
        _factory.Chat.EnqueueText("Could not do that.");
        var correction = await Send(alice, "correct it");

        Assert.Null(deletion);
        Assert.DoesNotContain(correction, e => e.Name == "proposal");
        Assert.Equal(HttpStatusCode.OK, (await bob.Client.GetAsync($"/api/nutrition/meals/{bobsMeal}")).StatusCode);
    }

    [Fact]
    public async Task Correcting_a_meal_prepares_a_replacement_that_updates_it_once_when_saved()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var mealId = await CreateMealAsync(user);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "100 grams of chicken breast", replacesMealId = mealId.ToString() });
        _factory.Chat.EnqueueText("""{"mealType":"lunch","eatenOn":"","items":[{"foodName":"chicken breast","quantity":100,"unit":"g","preparation":"cooked","originalText":"100 grams of chicken breast"}]}""");
        _factory.Chat.EnqueueText("Prepared.");

        var events = await Send(user, "actually it was 100 g chicken breast");
        var card = events.Single(e => e.Name == "proposal").Data.GetProperty("card");
        var proposal = card.GetProperty("proposal");

        Assert.Equal(mealId, proposal.GetProperty("replacesMealId").GetGuid());
        var item = proposal.GetProperty("items")[0];
        var body = new
        {
            type = "Lunch",
            eatenAt = DateTimeOffset.UtcNow,
            proposalId = card.GetProperty("id").GetGuid(),
            items = new[] { new { foodId = item.GetProperty("food").GetProperty("foodId").GetGuid(), name = "x", quantityGrams = item.GetProperty("quantityGrams").GetDecimal(), preparation = "Cooked" } },
        };

        var first = await user.Client.PutAsJsonAsync($"/api/nutrition/meals/{mealId}", body);
        var second = await user.Client.PutAsJsonAsync($"/api/nutrition/meals/{mealId}", body);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var meal = await user.Client.GetFromJsonAsync<JsonElement>($"/api/nutrition/meals/{mealId}");
        var items = meal.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Contains("Chicken", items[0].GetProperty("name").GetString());
        var proposals = await user.Client.GetFromJsonAsync<JsonElement>($"/api/conversations/{events.First(e => e.Name == "conversation").Data.GetProperty("conversationId").GetGuid()}/proposals");
        Assert.Equal("Committed", proposals[0].GetProperty("state").GetString());
    }

    // ---- state of a card ----

    [Fact]
    public async Task A_dismissed_card_can_no_longer_be_confirmed_and_applies_nothing()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms = 70m });

        var dismiss = await user.Client.PostAsync($"/api/assistant/actions/{card!.Value.GetProperty("id").GetGuid()}/dismiss", null);
        var confirm = await Confirm(user, card.Value);

        Assert.Equal(HttpStatusCode.NoContent, dismiss.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, confirm.StatusCode);
        Assert.Empty((await WeightsAsync(user)).GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task An_applied_card_cannot_be_dismissed_afterwards()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms = 70m });
        await Confirm(user, card!.Value);

        var dismiss = await user.Client.PostAsync($"/api/assistant/actions/{card.Value.GetProperty("id").GetGuid()}/dismiss", null);

        Assert.Equal(HttpStatusCode.Conflict, dismiss.StatusCode);
    }

    [Fact]
    public async Task Another_users_card_behaves_like_a_missing_one()
    {
        var alice = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var mallory = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var (card, _) = await Propose(alice, "ProposeWeight", new { kilograms = 70m });

        var confirm = await Confirm(mallory, card!.Value);
        var dismiss = await mallory.Client.PostAsync($"/api/assistant/actions/{card.Value.GetProperty("id").GetGuid()}/dismiss", null);

        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, dismiss.StatusCode);
        Assert.Empty((await WeightsAsync(alice)).GetProperty("points").EnumerateArray());
        Assert.Empty((await WeightsAsync(mallory)).GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task A_meal_proposal_id_is_not_an_action_id()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueToolCall("PrepareMeal", new { description = "100 grams of chicken breast" });
        _factory.Chat.EnqueueText("""{"mealType":"lunch","eatenOn":"","items":[{"foodName":"chicken breast","quantity":100,"unit":"g","preparation":"cooked","originalText":"100 g chicken"}]}""");
        _factory.Chat.EnqueueText("Prepared.");
        var events = await Send(user, "I ate chicken");
        var mealProposalId = events.Single(e => e.Name == "proposal").Data.GetProperty("card").GetProperty("id").GetGuid();

        var confirm = await user.Client.PostAsync($"/api/assistant/actions/{mealProposalId}/confirm", null);

        Assert.Equal(HttpStatusCode.NotFound, confirm.StatusCode);
    }

    [Fact]
    public async Task Cards_are_listed_for_their_conversation_with_their_state()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var (card, events) = await Propose(user, "ProposeWeight", new { kilograms = 70m });
        var conversationId = events.First(e => e.Name == "conversation").Data.GetProperty("conversationId").GetGuid();

        var before = await user.Client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversationId}/actions");
        await Confirm(user, card!.Value);
        var after = await user.Client.GetFromJsonAsync<JsonElement>($"/api/conversations/{conversationId}/actions");

        Assert.Equal("Proposed", Assert.Single(before.EnumerateArray()).GetProperty("state").GetString());
        Assert.Equal("Committed", Assert.Single(after.EnumerateArray()).GetProperty("state").GetString());
        var stranger = await TestUser.RegisterAsync(_factory);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.GetAsync($"/api/conversations/{conversationId}/actions")).StatusCode);
    }

    [Fact]
    public async Task Without_the_health_data_consent_confirming_applies_nothing_and_the_card_stays_open()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var (card, _) = await Propose(user, "ProposeWeight", new { kilograms = 70m });
        (await user.Client.PutAsJsonAsync("/api/consents/HealthDataProcessing", new { granted = false })).EnsureSuccessStatusCode();

        var confirm = await Confirm(user, card!.Value);

        Assert.Equal(HttpStatusCode.Forbidden, confirm.StatusCode);
        (await user.Client.PutAsJsonAsync("/api/consents/HealthDataProcessing", new { granted = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await Confirm(user, card.Value)).StatusCode); // the claim was released, so it can be applied now
        Assert.Single((await WeightsAsync(user)).GetProperty("points").EnumerateArray());
    }

    // ---- wearables ----

    [Fact]
    public async Task Wearable_status_is_a_read_and_a_sync_without_a_connection_is_refused_without_touching_anything()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = true })).EnsureSuccessStatusCode();
        _factory.Chat.EnqueueToolCall("GetWearableStatus", new { });
        _factory.Chat.EnqueueText("Nothing is connected.");
        await Send(user, "is my WHOOP connected?");
        _factory.Chat.EnqueueToolCall("SyncWearable", new { provider = "whoop" });
        _factory.Chat.EnqueueText("It is not connected.");
        await Send(user, "sync my WHOOP");

        var executions = await ExecutionsAsync(user.Id);

        var status = executions.Single(e => e.ToolName == "GetWearableStatus");
        Assert.Equal(ToolOutcome.Succeeded, status.Outcome);
        var statusResult = JsonSerializer.Serialize(_factory.Chat.Calls[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single().Result);
        Assert.Contains("whoop", statusResult, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolOutcome.Rejected, executions.Single(e => e.ToolName == "SyncWearable").Outcome);
    }

    // ---- the model never gets to apply anything itself ----

    [Fact]
    public async Task The_model_has_no_tool_that_applies_a_change_and_none_takes_a_user_id()
    {
        var user = await TestUser.RegisterAsync(_factory, aiConsent: true);
        _factory.Chat.EnqueueText("Hello.");
        await Send(user, "hi");

        var tools = _factory.Chat.LastOptions!.Tools!.OfType<AIFunction>().ToList();

        Assert.Contains(tools, t => t.Name == "ProposeWeight");
        Assert.DoesNotContain(tools, t => t.Name.StartsWith("Save", StringComparison.Ordinal) || t.Name.StartsWith("Delete", StringComparison.Ordinal) || t.Name.StartsWith("Set", StringComparison.Ordinal) || t.Name.StartsWith("Confirm", StringComparison.Ordinal));
        Assert.All(tools, t => Assert.DoesNotContain("userId", t.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase));
    }
}
