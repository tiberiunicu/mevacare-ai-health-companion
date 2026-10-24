using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>Manual weight entries, historical queries and the assistant's read tools over stored measurements.</summary>
[Collection(ApiCollection.Name)]
public class MeasurementsApiTests : IDisposable
{
    private readonly ApiFactory _factory;

    public MeasurementsApiTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Chat.Reset();
    }

    public void Dispose() => _factory.Chat.Reset();

    private static async Task<JsonElement> AddWeightAsync(TestUser user, double kilograms, string? measuredAt = null)
    {
        var response = await user.Client.PostAsJsonAsync("/api/measurements/weight", new { kilograms, measuredAt });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task IngestAsync(Guid userId, string provider, NormalizedHealthData data)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMeasurementIngest>().UpsertAsync(userId, provider, data, default);
    }

    // ---- weight ----

    [Fact]
    public async Task Weight_entries_keep_the_users_own_clock_and_come_back_in_order_with_the_change_over_the_period()
    {
        var user = await TestUser.RegisterAsync(_factory);

        var late = await AddWeightAsync(user, 71.2, "2026-10-20T23:30:00-05:00");
        await AddWeightAsync(user, 72.0, "2026-10-01T08:00:00+03:00");
        await AddWeightAsync(user, 70.5, "2026-10-25T07:15:00+03:00");

        Assert.Equal("2026-10-20", late.GetProperty("localDate").GetString()); // the local day, not the UTC day (04:30 on the 21st)
        Assert.Equal(-300, late.GetProperty("utcOffsetMinutes").GetInt32());
        Assert.Equal("manual", late.GetProperty("provider").GetString());

        var history = await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/weight?from=2026-10-01&to=2026-10-31");
        Assert.Equal([72.0m, 71.2m, 70.5m], history.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("kilograms").GetDecimal()));
        Assert.Equal(70.5m, history.GetProperty("latestKilograms").GetDecimal());
        Assert.Equal(-1.5m, history.GetProperty("changeKilograms").GetDecimal());

        var october20 = await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/weight?from=2026-10-20&to=2026-10-20");
        Assert.Equal(1, october20.GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, october20.GetProperty("changeKilograms").ValueKind); // one point is not a change
    }

    [Theory]
    [InlineData(19.9)]
    [InlineData(500.1)]
    [InlineData(0)]
    [InlineData(-70)]
    public async Task Implausible_weights_are_rejected(double kilograms)
    {
        var user = await TestUser.RegisterAsync(_factory);

        var response = await user.Client.PostAsJsonAsync("/api/measurements/weight", new { kilograms });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Times_far_in_the_future_or_the_distant_past_are_rejected()
    {
        var user = await TestUser.RegisterAsync(_factory);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/measurements/weight", new { kilograms = 70, measuredAt = DateTimeOffset.UtcNow.AddDays(3) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.Client.PostAsJsonAsync("/api/measurements/weight", new { kilograms = 70, measuredAt = "1950-01-01T00:00:00+00:00" })).StatusCode);
    }

    [Fact]
    public async Task Ranges_must_be_ordered_and_at_most_366_days_for_every_historical_query()
    {
        var user = await TestUser.RegisterAsync(_factory);

        foreach (var path in new[] { "weight", "sleep-recovery", "activity" })
        {
            var backwards = await user.Client.GetAsync($"/api/measurements/{path}?from=2026-10-20&to=2026-10-19");
            var tooLong = await user.Client.GetAsync($"/api/measurements/{path}?from=2025-01-01&to=2026-10-19");
            Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
            Assert.Equal("invalid_range", (await backwards.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal("range_too_long", (await tooLong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Measurements_need_the_health_data_consent_and_are_visible_again_when_it_is_granted()
    {
        var user = await TestUser.RegisterAsync(_factory, healthConsent: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PostAsJsonAsync("/api/measurements/weight", new { kilograms = 70 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/measurements/weight?from=2026-10-01&to=2026-10-31")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/measurements/sleep-recovery?from=2026-10-01&to=2026-10-31")).StatusCode);

        (await user.Client.PutAsJsonAsync("/api/consents/HealthDataProcessing", new { granted = true })).EnsureSuccessStatusCode();
        await AddWeightAsync(user, 70);
        (await user.Client.PutAsJsonAsync("/api/consents/HealthDataProcessing", new { granted = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.GetAsync("/api/measurements/weight?from=2026-10-01&to=2026-10-31")).StatusCode);
    }

    [Fact]
    public async Task Entries_can_be_deleted_by_their_owner_only_and_imported_values_cannot_be_deleted_here()
    {
        var alice = await TestUser.RegisterAsync(_factory);
        var bob = await TestUser.RegisterAsync(_factory);
        var entry = await AddWeightAsync(alice, 70, "2026-10-20T08:00:00+00:00");
        await IngestAsync(alice.Id, "scale", new NormalizedHealthData(
            [new NormalizedMeasurement(HealthMetric.Weight, 69.8m, new DateTimeOffset(2026, 10, 21, 7, 0, 0, TimeSpan.Zero), "scale-1")], [], []));
        var imported = (await alice.Client.GetFromJsonAsync<JsonElement>("/api/measurements/weight?from=2026-10-01&to=2026-10-31"))
            .GetProperty("points").EnumerateArray().Single(p => p.GetProperty("provider").GetString() == "scale");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.DeleteAsync($"/api/measurements/weight/{entry.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Client.DeleteAsync($"/api/measurements/weight/{imported.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Client.DeleteAsync($"/api/measurements/weight/{entry.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Client.DeleteAsync($"/api/measurements/weight/{entry.GetProperty("id").GetGuid()}")).StatusCode);

        var remaining = (await alice.Client.GetFromJsonAsync<JsonElement>("/api/measurements/weight?from=2026-10-01&to=2026-10-31")).GetProperty("points");
        Assert.Equal(["scale"], remaining.EnumerateArray().Select(p => p.GetProperty("provider").GetString()));
        Assert.Equal(0, (await bob.Client.GetFromJsonAsync<JsonElement>("/api/measurements/weight?from=2026-10-01&to=2026-10-31")).GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Measurement_endpoints_require_a_signed_in_user()
    {
        var anonymous = _factory.CreateApiClient();

        foreach (var path in new[] { "/api/measurements/weight?from=2026-10-01&to=2026-10-02", "/api/measurements/sleep-recovery?from=2026-10-01&to=2026-10-02", "/api/measurements/activity?from=2026-10-01&to=2026-10-02", "/api/integrations" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }
    }

    // ---- the day model: providers never mix, scores keep their owner ----

    [Fact]
    public async Task Days_are_reported_per_provider_so_scores_of_different_providers_are_never_blended()
    {
        var user = await TestUser.RegisterAsync(_factory);
        var morning = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);
        NormalizedHealthData DataFor(decimal score, string id) => new(
            [new NormalizedMeasurement(HealthMetric.RecoveryScore, score, morning, id)],
            [new NormalizedSleep(id, morning.AddHours(-8), morning, false, true, 480, 30, 250, 100, 100, 90, 92, 88, 15, 5)],
            []);
        await IngestAsync(user.Id, "whoop", DataFor(80, "w-1"));
        await IngestAsync(user.Id, "otherband", DataFor(30, "o-1"));

        var range = await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-20&to=2026-10-20");

        var days = range.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(["otherband", "whoop"], days.Select(d => d.GetProperty("provider").GetString()));
        Assert.Equal([30m, 80m], days.Select(d => d.GetProperty("recovery").GetProperty("score").GetDecimal()));
        var averages = range.GetProperty("averages").EnumerateArray().ToList();
        Assert.Equal([30m, 80m], averages.Select(a => a.GetProperty("averageRecoveryScore").GetDecimal())); // one average per provider, never one blended number
    }

    [Fact]
    public async Task The_night_of_a_day_is_its_longest_sleep_and_naps_are_counted_separately()
    {
        var user = await TestUser.RegisterAsync(_factory);
        var wake = new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero);
        await IngestAsync(user.Id, "whoop", new NormalizedHealthData([], [
            new NormalizedSleep("short", wake.AddHours(-3), wake, false, true, 180, 10, 100, 40, 30, 80, 85, 80, 15, 2),
            new NormalizedSleep("long", wake.AddHours(-9), wake.AddHours(-1), false, true, 480, 30, 250, 100, 100, 90, 92, 88, 15, 5),
            new NormalizedSleep("nap", wake.AddHours(6), wake.AddHours(7), true, true, 60, 10, 40, 0, 0, 70, 80, 80, 15, 1),
        ], []));

        var day = (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-20&to=2026-10-20")).GetProperty("days")[0];

        Assert.Equal(450, day.GetProperty("sleep").GetProperty("asleepMinutes").GetInt32());
        Assert.Equal(40, day.GetProperty("napMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, day.GetProperty("recovery").ValueKind);
    }

    // ---- assistant tools ----

    private static List<string> ToolResults(ApiFactory factory) =>
        factory.Chat.Calls.SelectMany(c => c).SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => JsonSerializer.Serialize(r.Result)).ToList();

    [Fact]
    public async Task The_assistant_reads_only_the_callers_weights_and_wearable_data_through_its_tools()
    {
        var alice = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var bob = await TestUser.RegisterAsync(_factory, aiConsent: true);
        await AddWeightAsync(alice, 70.5, "2026-10-20T08:00:00+00:00");
        await AddWeightAsync(bob, 99.9, "2026-10-20T08:00:00+00:00");
        var morning = new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);
        await IngestAsync(alice.Id, "whoop", new NormalizedHealthData(
            [new NormalizedMeasurement(HealthMetric.RecoveryScore, 66, morning, "c-1"), new NormalizedMeasurement(HealthMetric.HrvRmssd, 61.5m, morning, "c-1")],
            [new NormalizedSleep("s-1", morning.AddHours(-8), morning, false, true, 480, 30, 250, 100, 100, 90, 92, 88, 15, 5)], []));
        await IngestAsync(bob.Id, "whoop", new NormalizedHealthData(
            [new NormalizedMeasurement(HealthMetric.RecoveryScore, 12, morning, "c-9")], [], []));
        _factory.Chat.EnqueueToolCall("GetWeightHistory", new { from = "2026-10-01", to = "2026-10-31" }, "call-1");
        _factory.Chat.EnqueueText("Your latest weight is 70.5 kg.");

        var weight = await alice.Client.PostAsJsonAsync("/api/chat/stream", new { message = "How has my weight changed?", clientMessageId = Guid.NewGuid() });
        weight.EnsureSuccessStatusCode();
        var weightResult = ToolResults(_factory).Single();
        Assert.Contains("70.5", weightResult, StringComparison.Ordinal);
        Assert.DoesNotContain("99.9", weightResult, StringComparison.Ordinal);

        _factory.Chat.Reset();
        _factory.Chat.EnqueueToolCall("GetSleepAndRecovery", new { from = "2026-10-15", to = "2026-10-25" }, "call-2");
        _factory.Chat.EnqueueText("You slept 7 hours.");
        (await alice.Client.PostAsJsonAsync("/api/chat/stream", new { message = "How did I sleep?", clientMessageId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sleepResult = ToolResults(_factory).Single();
        Assert.Contains("\"recoveryScore\":66", sleepResult, StringComparison.Ordinal);
        Assert.Contains("\"asleepMinutes\":450", sleepResult, StringComparison.Ordinal);
        Assert.Contains("whoop", sleepResult, StringComparison.Ordinal);
        Assert.DoesNotContain("\"recoveryScore\":12", sleepResult, StringComparison.Ordinal); // Bob's score
        _factory.Chat.Reset();
    }

    [Fact]
    public async Task Without_data_or_consent_the_tools_say_so_instead_of_inventing_values()
    {
        var empty = await TestUser.RegisterAsync(_factory, aiConsent: true);
        var noConsent = await TestUser.RegisterAsync(_factory, healthConsent: false, aiConsent: true);
        _factory.Chat.EnqueueToolCall("GetSleepAndRecovery", new { from = "2026-10-15", to = "2026-10-25" }, "call-1");
        _factory.Chat.EnqueueText("I have no sleep data.");
        (await empty.Client.PostAsJsonAsync("/api/chat/stream", new { message = "How did I sleep?", clientMessageId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var emptyResult = ToolResults(_factory).Single();
        Assert.Contains("No sleep or recovery data is stored", emptyResult, StringComparison.Ordinal);

        _factory.Chat.Reset();
        _factory.Chat.EnqueueToolCall("GetWeightHistory", new { from = "2026-10-01", to = "2026-10-31" }, "call-2");
        _factory.Chat.EnqueueText("I cannot see that.");
        (await noConsent.Client.PostAsJsonAsync("/api/chat/stream", new { message = "My weight?", clientMessageId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        Assert.Contains("consent_required", ToolResults(_factory).Single(), StringComparison.Ordinal);
        _factory.Chat.Reset();
    }
}
