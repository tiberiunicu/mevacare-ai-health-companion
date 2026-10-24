using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.IntegrationTests.Support;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Features;
using MevaCare.AI.HealthCompanion.Modules.Measurements.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MevaCare.AI.HealthCompanion.IntegrationTests;

/// <summary>
/// The wearable integration end to end against a SCRIPTED stand-in for WHOOP (documented response shapes, rotating refresh tokens,
/// paging, failures). It proves the application's behaviour; it does not prove that the real WHOOP service answers identically.
/// </summary>
[Collection(ApiCollection.Name)]
public class WearableTests : IDisposable
{
    private readonly ApiFactory _factory;

    public WearableTests(ApiFactory factory)
    {
        _factory = factory;
        factory.Whoop.Reset();
    }

    public void Dispose() => _factory.Whoop.Reset();

    // ---- helpers ----

    private async Task<TestUser> UserAsync(bool integrationsConsent = true, bool healthConsent = true)
    {
        var user = await TestUser.RegisterAsync(_factory, healthConsent);
        if (integrationsConsent)
        {
            (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = true })).EnsureSuccessStatusCode();
        }

        return user;
    }

    private static string StateOf(string authorizationUrl) => HttpUtility.ParseQueryString(new Uri(authorizationUrl).Query)["state"]!;

    private async Task<(string Location, string State)> CompleteAuthorizationAsync(TestUser user, string code = "code-1", string? error = null)
    {
        var started = await user.Client.PostAsync("/api/integrations/whoop/connect", null);
        started.EnsureSuccessStatusCode();
        var url = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!;
        var state = StateOf(url);
        var location = await CallbackAsync(state, code, error);
        return (location, state);
    }

    /// <summary>The provider's redirect: an anonymous browser navigation (no bearer token).</summary>
    private async Task<string> CallbackAsync(string? state, string? code, string? error = null)
    {
        var query = new List<string>();
        if (code is not null)
        {
            query.Add("code=" + Uri.EscapeDataString(code));
        }

        if (state is not null)
        {
            query.Add("state=" + Uri.EscapeDataString(state));
        }

        if (error is not null)
        {
            query.Add("error=" + Uri.EscapeDataString(error));
        }

        var response = await _factory.CreateApiClient().GetAsync("/api/integrations/whoop/callback?" + string.Join("&", query));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.ToString();
    }

    private async Task<TestUser> ConnectedUserAsync(string code = "code-1")
    {
        var user = await UserAsync();
        var (location, _) = await CompleteAuthorizationAsync(user, code);
        Assert.Equal("http://localhost:5173/devices?connected=whoop", location);
        return user;
    }

    private static async Task<JsonElement> SyncAsync(TestUser user, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await user.Client.PostAsync("/api/integrations/whoop/sync", null);
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static readonly DateTimeOffset T = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Two nights, a nap, two recoveries, two cycles and one workout, in different time zones.</summary>
    private void ScriptTypicalWeek()
    {
        var whoop = _factory.Whoop;
        whoop.Sleeps.Add(FakeWhoopHandler.Sleep("sleep-a", T.AddHours(-2), T.AddHours(6), "+03:00", lightMin: 240, deepMin: 80, remMin: 100, awakeMin: 60, performance: 91));
        whoop.Sleeps.Add(FakeWhoopHandler.Sleep("sleep-b", T.AddDays(-1).AddHours(-3), T.AddDays(-1).AddHours(5.5), "-05:00", lightMin: 200, deepMin: 90, remMin: 90, awakeMin: 40));
        whoop.Sleeps.Add(FakeWhoopHandler.Sleep("nap-1", T.AddHours(10), T.AddHours(11), "+03:00", nap: true, lightMin: 20, deepMin: 0, remMin: 0, awakeMin: 0));
        whoop.Recoveries.Add(FakeWhoopHandler.Recovery(101, "sleep-a", score: 70, restingHeartRate: 50, hrvMilli: 65.0));
        whoop.Recoveries.Add(FakeWhoopHandler.Recovery(100, "sleep-b", score: 40, restingHeartRate: 55, hrvMilli: 48.2));
        whoop.Cycles.Add(FakeWhoopHandler.Cycle(101, T.AddHours(3), null, "+03:00", strain: 12.5, kilojoules: 10_000));
        whoop.Cycles.Add(FakeWhoopHandler.Cycle(100, T.AddDays(-1).AddHours(3), T.AddHours(3), "+03:00", strain: 9.1, kilojoules: 8_368));
        whoop.Workouts.Add(FakeWhoopHandler.Workout("workout-1", T.AddHours(15), T.AddHours(16), "+03:00", sport: "running", strain: 8.2, kilojoules: 1_569.3));
    }

    // ---- configuration, consent ----

    [Fact]
    public async Task Connecting_needs_both_the_external_integrations_and_the_health_data_consent()
    {
        var withoutIntegrations = await UserAsync(integrationsConsent: false);
        var withoutHealth = await UserAsync(healthConsent: false);
        (await withoutHealth.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = true })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Forbidden, (await withoutIntegrations.Client.PostAsync("/api/integrations/whoop/connect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutHealth.Client.PostAsync("/api/integrations/whoop/connect", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await withoutIntegrations.Client.PostAsync("/api/integrations/whoop/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_providers_are_not_found_and_the_list_shows_configuration_and_connection_state()
    {
        var user = await UserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.PostAsync("/api/integrations/fitbit/connect", null)).StatusCode);

        var list = await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("whoop", list[0].GetProperty("provider").GetString());
        Assert.True(list[0].GetProperty("configured").GetBoolean());
        Assert.False(list[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task The_authorization_url_carries_the_registered_redirect_the_read_scopes_and_an_unguessable_state()
    {
        var user = await UserAsync();

        var response = await user.Client.PostAsync("/api/integrations/whoop/connect", null);
        var url = new Uri((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!);
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("api.prod.whoop.com", url.Host);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("test-client-id", query["client_id"]);
        Assert.Equal("http://localhost:5080/api/integrations/whoop/callback", query["redirect_uri"]);
        Assert.Contains("offline", query["scope"]!.Split(' '));
        Assert.Contains("read:sleep", query["scope"]!.Split(' '));
        Assert.DoesNotContain("test-client-secret", url.Query, StringComparison.Ordinal);
        Assert.True(query["state"]!.Length >= 32);
        var second = await user.Client.PostAsync("/api/integrations/whoop/connect", null);
        var secondUrl = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!;
        Assert.NotEqual(StateOf(secondUrl), query["state"]);
    }

    // ---- OAuth callback security ----

    [Fact]
    public async Task A_completed_authorization_connects_the_account_and_stores_the_tokens_only_in_protected_form()
    {
        var user = await UserAsync();

        var (location, _) = await CompleteAuthorizationAsync(user);

        Assert.Equal("http://localhost:5173/devices?connected=whoop", location);
        var list = await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations");
        Assert.True(list[0].GetProperty("connected").GetBoolean());
        Assert.Equal("Connected", list[0].GetProperty("status").GetString());

        var account = await InDbAsync(db => db.Set<ConnectedAccount>().AsNoTracking().SingleAsync(a => a.UserId == user.Id));
        Assert.Equal(_factory.Whoop.ExternalUserId, account.ExternalUserId);
        Assert.DoesNotContain("access-token", account.AccessTokenProtected, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token", account.RefreshTokenProtected, StringComparison.Ordinal);
        Assert.NotEqual(account.AccessTokenProtected, account.RefreshTokenProtected);

        // tokens never leave through the API
        Assert.DoesNotContain("token", await user.Client.GetStringAsync("/api/integrations"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access-token", await user.Client.GetStringAsync("/api/account/export"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_request_carries_the_code_the_registered_redirect_and_the_client_credentials()
    {
        await ConnectedUserAsync("code-xyz");

        var exchange = _factory.Whoop.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/oauth/oauth2/token", StringComparison.Ordinal));
        var form = HttpUtility.ParseQueryString(exchange.Body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("code-xyz", form["code"]);
        Assert.Equal("http://localhost:5080/api/integrations/whoop/callback", form["redirect_uri"]);
        Assert.Equal("test-client-id", form["client_id"]);
        Assert.Equal("test-client-secret", form["client_secret"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-state")]
    public async Task A_callback_without_a_valid_state_is_rejected_and_connects_nothing(string? state)
    {
        var location = await CallbackAsync(state, "code-1");

        Assert.Equal("http://localhost:5173/devices?error=invalid_state", location);
        Assert.Empty(_factory.Whoop.Requests);
    }

    [Fact]
    public async Task A_state_works_exactly_once()
    {
        var user = await UserAsync();
        var (first, state) = await CompleteAuthorizationAsync(user);

        var replay = await CallbackAsync(state, "code-1");

        Assert.EndsWith("connected=whoop", first, StringComparison.Ordinal);
        Assert.EndsWith("error=invalid_state", replay, StringComparison.Ordinal);
        Assert.Equal(1, _factory.Whoop.Count("POST", "/oauth/oauth2/token"));
    }

    [Fact]
    public async Task An_expired_state_is_rejected()
    {
        var user = await UserAsync();
        const string state = "expired-state-value-0123456789abcdef";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        await InDbAsync(async db =>
        {
            db.Add(new OAuthState(user.Id, "whoop", hash, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow.AddMinutes(-20)));
            await db.SaveChangesAsync();
            return 0;
        });

        var location = await CallbackAsync(state, "code-1");

        Assert.EndsWith("error=invalid_state", location, StringComparison.Ordinal);
        Assert.Equal(0, _factory.Whoop.Count("POST", "/oauth/oauth2/token"));
    }

    [Fact]
    public async Task Cancelling_at_the_provider_reports_a_denied_authorization_and_still_consumes_the_state()
    {
        var user = await UserAsync();

        var (location, state) = await CompleteAuthorizationAsync(user, code: "ignored", error: "access_denied");

        Assert.EndsWith("error=authorization_denied", location, StringComparison.Ordinal);
        Assert.EndsWith("error=invalid_state", await CallbackAsync(state, "code-1"), StringComparison.Ordinal);
        Assert.False((await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task A_state_always_connects_the_user_who_started_it_whoever_opens_the_callback()
    {
        var alice = await UserAsync();
        var bob = await UserAsync();
        var started = await alice.Client.PostAsync("/api/integrations/whoop/connect", null);
        var state = StateOf((await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!);

        await CallbackAsync(state, "code-1");

        Assert.True((await alice.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
        Assert.False((await bob.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task One_provider_account_cannot_be_linked_to_two_users_and_a_user_cannot_swap_to_another_provider_account()
    {
        var alice = await ConnectedUserAsync();
        var bob = await UserAsync();

        var (bobResult, _) = await CompleteAuthorizationAsync(bob);
        _factory.Whoop.ExternalUserId = (long.Parse(_factory.Whoop.ExternalUserId) + 1).ToString();
        var (aliceSwap, _) = await CompleteAuthorizationAsync(alice);

        Assert.EndsWith("error=account_already_linked", bobResult, StringComparison.Ordinal);
        Assert.False((await bob.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
        Assert.EndsWith("error=different_account", aliceSwap, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_returns_no_refresh_token_is_refused()
    {
        _factory.Whoop.OmitRefreshToken = true;
        var user = await UserAsync();

        var (location, _) = await CompleteAuthorizationAsync(user);

        Assert.EndsWith("error=no_refresh_token", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_code_is_reported_without_connecting()
    {
        var user = await UserAsync();

        var (location, _) = await CompleteAuthorizationAsync(user, code: "bad");

        Assert.EndsWith("error=authorization_failed", location, StringComparison.Ordinal);
        Assert.False((await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Consent_withdrawn_between_start_and_callback_blocks_the_connection()
    {
        var user = await UserAsync();
        var started = await user.Client.PostAsync("/api/integrations/whoop/connect", null);
        var state = StateOf((await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!);
        (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = false })).EnsureSuccessStatusCode();

        var location = await CallbackAsync(state, "code-1");

        Assert.EndsWith("error=consent_required", location, StringComparison.Ordinal);
    }

    // ---- synchronization ----

    [Fact]
    public async Task A_synchronization_normalizes_and_stores_sleep_recovery_strain_and_workouts_with_their_local_days()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();

        var job = await SyncAsync(user);

        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.Equal(14, job.GetProperty("recordsAdded").GetInt32()); // 3 sleeps + 6 recovery values + 4 cycle values + 1 workout
        Assert.Equal(0, job.GetProperty("recordsUpdated").GetInt32());

        var days = (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days");
        var oct20 = days.EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-10-20");
        var oct19 = days.EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-10-19");

        // sleep-a ended 06:00 UTC = 09:00 at +03:00 -> the night of the 20th; stages are minutes, asleep = light + deep + REM
        Assert.Equal("whoop", oct20.GetProperty("provider").GetString());
        Assert.Equal(420, oct20.GetProperty("sleep").GetProperty("asleepMinutes").GetInt32());
        Assert.Equal(480, oct20.GetProperty("sleep").GetProperty("inBedMinutes").GetInt32());
        Assert.Equal(80, oct20.GetProperty("sleep").GetProperty("deepMinutes").GetInt32());
        Assert.Equal(91m, oct20.GetProperty("sleep").GetProperty("performancePercent").GetDecimal());
        Assert.Equal(20, oct20.GetProperty("napMinutes").GetInt32());
        Assert.Equal(70m, oct20.GetProperty("recovery").GetProperty("score").GetDecimal());
        Assert.Equal(50m, oct20.GetProperty("recovery").GetProperty("restingHeartRate").GetDecimal());
        Assert.Equal(65m, oct20.GetProperty("recovery").GetProperty("hrvRmssdMilliseconds").GetDecimal());

        // sleep-b ended 2026-10-19 05:30 UTC = 00:30 local time at UTC-05:00, still the 19th
        Assert.Equal(380, oct19.GetProperty("sleep").GetProperty("asleepMinutes").GetInt32());
        Assert.Equal(40m, oct19.GetProperty("recovery").GetProperty("score").GetDecimal());

        var activity = (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/activity?from=2026-10-15&to=2026-10-25")).GetProperty("days");
        var activeDay = activity.EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-10-20");
        Assert.Equal(12.5m, activeDay.GetProperty("dayStrain").GetDecimal());
        Assert.Equal(2390.1m, activeDay.GetProperty("caloriesBurned").GetDecimal()); // 10,000 kJ / 4.184
        var workout = activeDay.GetProperty("workouts")[0];
        Assert.Equal("running", workout.GetProperty("sportName").GetString());
        Assert.Equal(375.1m, workout.GetProperty("calories").GetDecimal()); // 1,569.3 kJ / 4.184
        Assert.Equal(180, workout.GetProperty("utcOffsetMinutes").GetInt32());
    }

    [Fact]
    public async Task Repeating_a_synchronization_updates_records_in_place_and_never_duplicates_them()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        await SyncAsync(user);
        var before = await InDbAsync(async db => (
            await db.Set<HealthMeasurement>().CountAsync(m => m.UserId == user.Id),
            await db.Set<SleepSession>().CountAsync(s => s.UserId == user.Id),
            await db.Set<Workout>().CountAsync(w => w.UserId == user.Id)));

        var second = await SyncAsync(user);
        var third = await SyncAsync(user);

        Assert.Equal(0, second.GetProperty("recordsAdded").GetInt32());
        Assert.Equal(14, second.GetProperty("recordsUpdated").GetInt32());
        Assert.Equal(0, third.GetProperty("recordsAdded").GetInt32());
        var after = await InDbAsync(async db => (
            await db.Set<HealthMeasurement>().CountAsync(m => m.UserId == user.Id),
            await db.Set<SleepSession>().CountAsync(s => s.UserId == user.Id),
            await db.Set<Workout>().CountAsync(w => w.UserId == user.Id)));
        Assert.Equal(before, after);
        Assert.Equal((10, 3, 1), after);
    }

    [Fact]
    public async Task A_record_that_was_pending_is_updated_when_the_provider_has_scored_it_later()
    {
        var user = await ConnectedUserAsync();
        _factory.Whoop.Sleeps.Add(FakeWhoopHandler.Sleep("sleep-a", T.AddHours(-2), T.AddHours(6), "+00:00", scored: false));
        await SyncAsync(user);
        var pending = (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days")[0];
        Assert.False(pending.GetProperty("sleep").GetProperty("isScored").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("sleep").GetProperty("asleepMinutes").ValueKind);

        _factory.Whoop.Sleeps.Clear();
        _factory.Whoop.Sleeps.Add(FakeWhoopHandler.Sleep("sleep-a", T.AddHours(-2), T.AddHours(6), "+00:00", lightMin: 300, deepMin: 60, remMin: 90, awakeMin: 30));
        var job = await SyncAsync(user);

        Assert.Equal(0, job.GetProperty("recordsAdded").GetInt32());
        var scored = (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days")[0];
        Assert.True(scored.GetProperty("sleep").GetProperty("isScored").GetBoolean());
        Assert.Equal(450, scored.GetProperty("sleep").GetProperty("asleepMinutes").GetInt32());
    }

    [Fact]
    public async Task Pages_are_followed_until_the_provider_has_no_next_token()
    {
        var user = await ConnectedUserAsync();
        for (var i = 0; i < 5; i++)
        {
            _factory.Whoop.Sleeps.Add(FakeWhoopHandler.Sleep($"sleep-{i}", T.AddDays(-i).AddHours(-2), T.AddDays(-i).AddHours(6)));
        }

        var job = await SyncAsync(user);

        Assert.Equal(5, job.GetProperty("recordsAdded").GetInt32());
        Assert.Equal(3, _factory.Whoop.Requests.Count(r => r.Path.EndsWith("/v2/activity/sleep", StringComparison.Ordinal))); // page size 2 -> 2 + 2 + 1
        Assert.Contains(_factory.Whoop.Requests, r => r.Query.Contains("nextToken=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_first_synchronization_reads_the_backfill_window_and_later_ones_an_overlap_window()
    {
        var user = await ConnectedUserAsync();

        await SyncAsync(user);
        await SyncAsync(user);

        var sleepCalls = _factory.Whoop.Requests.Where(r => r.Path.EndsWith("/v2/activity/sleep", StringComparison.Ordinal)).ToList();
        DateTime Start(FakeWhoopHandler.RecordedRequest r) =>
            DateTime.Parse(HttpUtility.ParseQueryString(r.Query)["start"]!, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.InRange((DateTime.UtcNow - Start(sleepCalls[0])).TotalDays, 29.9, 30.1);
        Assert.InRange((DateTime.UtcNow - Start(sleepCalls[1])).TotalDays, 6.9, 7.1);
    }

    [Fact]
    public async Task Tokens_are_refreshed_when_they_are_about_to_expire_and_the_rotated_refresh_token_is_the_one_kept()
    {
        _factory.Whoop.AccessTokenLifetimeSeconds = 60; // inside the 2 minute safety margin: every sync refreshes first
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();

        var first = await SyncAsync(user);
        var second = await SyncAsync(user);

        Assert.Equal("Succeeded", first.GetProperty("status").GetString());
        Assert.Equal("Succeeded", second.GetProperty("status").GetString());
        var refreshes = _factory.Whoop.Requests.Where(r => r.Body is not null && r.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, refreshes.Count);
        Assert.Contains("refresh_token=refresh-token-1", refreshes[0].Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=refresh-token-2", refreshes[1].Body, StringComparison.Ordinal); // the rotated one, not the old one
        Assert.Contains("scope=offline", refreshes[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_access_token_rejected_before_its_expiry_is_refreshed_once_and_the_read_succeeds()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        _factory.Whoop.InvalidateAccessToken();

        var job = await SyncAsync(user);

        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        Assert.Equal(14, job.GetProperty("recordsAdded").GetInt32());
        Assert.Equal(1, _factory.Whoop.Requests.Count(r => r.Body is not null && r.Body.Contains("grant_type=refresh_token", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task When_the_provider_refuses_the_refresh_the_account_needs_reauthorization_and_reconnecting_repairs_it()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        _factory.Whoop.InvalidateAccessToken();
        _factory.Whoop.RejectRefresh = true;

        var failed = await SyncAsync(user);
        var dataCallsAfterFailure = _factory.Whoop.Requests.Count(r => r.Path.Contains("/v2/", StringComparison.Ordinal));
        var skipped = await SyncAsync(user);

        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("reauthorization_required", failed.GetProperty("errorCode").GetString());
        Assert.Equal("reauthorization_required", skipped.GetProperty("errorCode").GetString());
        Assert.Equal(dataCallsAfterFailure, _factory.Whoop.Requests.Count(r => r.Path.Contains("/v2/", StringComparison.Ordinal))); // no calls with dead tokens
        var entry = (await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0];
        Assert.Equal("NeedsReauthorization", entry.GetProperty("status").GetString());

        _factory.Whoop.RejectRefresh = false;
        var (location, _) = await CompleteAuthorizationAsync(user, "code-2");
        Assert.EndsWith("connected=whoop", location, StringComparison.Ordinal);
        Assert.Equal("Connected", (await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("status").GetString());
        Assert.Equal("Succeeded", (await SyncAsync(user)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_rate_limit_stops_the_run_with_a_stable_code_and_keeps_what_was_already_stored()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        _factory.Whoop.Interceptor = request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/v2/recovery", StringComparison.Ordinal))
            {
                return null;
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down {secret-details}") };
            response.Headers.Add("X-RateLimit-Reset", "30");
            return response;
        };

        var failed = await SyncAsync(user);

        Assert.Equal("Failed", failed.GetProperty("status").GetString());
        Assert.Equal("rate_limited", failed.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("secret-details", failed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(3, await InDbAsync(db => db.Set<SleepSession>().CountAsync(s => s.UserId == user.Id))); // sleeps came before the limit

        _factory.Whoop.Interceptor = null;
        var recovered = await SyncAsync(user);
        Assert.Equal("Succeeded", recovered.GetProperty("status").GetString());
        Assert.Equal(11, recovered.GetProperty("recordsAdded").GetInt32());
    }

    [Fact]
    public async Task A_provider_outage_is_reported_without_leaking_what_the_provider_said()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        _factory.Whoop.Interceptor = request => request.RequestUri!.AbsolutePath.EndsWith("/v2/cycle", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("stack trace: secret-details") }
            : null;

        var failed = await SyncAsync(user);

        Assert.Equal("provider_unavailable", failed.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("secret-details", failed.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("provider_unavailable", (await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("lastSyncStatus").GetString());
    }

    [Fact]
    public async Task Malformed_provider_data_does_not_break_the_run_and_impossible_values_are_not_stored()
    {
        var user = await ConnectedUserAsync();
        _factory.Whoop.Sleeps.Add(FakeWhoopHandler.Sleep("sleep-a", T.AddHours(-2), T.AddHours(6)));
        _factory.Whoop.Recoveries.Add(FakeWhoopHandler.Recovery(101, "sleep-a", score: 4000, restingHeartRate: -5, hrvMilli: 61.5));
        _factory.Whoop.Cycles.Add(FakeWhoopHandler.Cycle(101, T.AddHours(3), null, strain: 99, kilojoules: -1));

        var job = await SyncAsync(user);

        Assert.Equal("Succeeded", job.GetProperty("status").GetString());
        var stored = await InDbAsync(db => db.Set<HealthMeasurement>().AsNoTracking().Where(m => m.UserId == user.Id).ToListAsync());
        Assert.Equal(["HrvRmssd"], stored.Select(m => m.Metric.ToString()));
    }

    [Fact]
    public async Task Synchronizing_an_account_that_is_not_connected_or_belongs_to_someone_else_is_not_found()
    {
        var owner = await ConnectedUserAsync();
        var stranger = await UserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.PostAsync("/api/integrations/whoop/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Client.DeleteAsync("/api/integrations/whoop")).StatusCode);
        Assert.Equal(0, (await stranger.Client.GetFromJsonAsync<JsonElement>("/api/integrations/whoop/sync-jobs")).GetArrayLength());
        Assert.True((await owner.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task Recent_runs_are_listed_newest_first_for_the_owner_only()
    {
        var user = await ConnectedUserAsync();
        await SyncAsync(user);
        await SyncAsync(user);

        var jobs = await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations/whoop/sync-jobs");

        Assert.Equal(2, jobs.GetArrayLength());
        Assert.True(jobs[0].GetProperty("startedAtUtc").GetDateTime() >= jobs[1].GetProperty("startedAtUtc").GetDateTime());
        Assert.Equal("Manual", jobs[0].GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task Withdrawing_the_consent_stops_user_requested_and_scheduled_synchronizations_before_any_provider_call()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = false })).EnsureSuccessStatusCode();
        _factory.Whoop.Requests.Clear();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.Client.PostAsync("/api/integrations/whoop/sync", null)).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var scheduled = await scope.ServiceProvider.GetRequiredService<SyncService>().SyncAsync(user.Id, "whoop", SyncTrigger.Scheduled, default);

        Assert.Equal("Skipped", scheduled.Status);
        Assert.Equal("consent_required", scheduled.ErrorCode);
        Assert.Empty(_factory.Whoop.Requests);
    }

    [Fact]
    public async Task The_background_worker_synchronizes_due_accounts_once_and_leaves_fresh_ones_alone()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        var worker = _factory.Services.GetServices<IHostedService>().OfType<SyncBackgroundService>().Single();
        await InDbAsync(async db =>
        {
            // accounts left behind by other tests must not be picked up (or starve this one)
            foreach (var other in await db.Set<ConnectedAccount>().Where(a => a.UserId != user.Id).ToListAsync())
            {
                other.RecordAttempt(DateTime.UtcNow, "succeeded", succeeded: true);
            }

            await db.SaveChangesAsync();
            return 0;
        });

        await worker.RunOnceAsync(default);
        var afterFirst = await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations/whoop/sync-jobs");
        await worker.RunOnceAsync(default);
        var afterSecond = await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations/whoop/sync-jobs");

        Assert.Equal(1, afterFirst.GetArrayLength());
        Assert.Equal("Scheduled", afterFirst[0].GetProperty("trigger").GetString());
        Assert.Equal("Succeeded", afterFirst[0].GetProperty("status").GetString());
        Assert.Equal(1, afterSecond.GetArrayLength());
        var days = (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days");
        Assert.Equal(2, days.GetArrayLength());
    }

    // ---- disconnect, ownership, export, deletion ----

    [Fact]
    public async Task Disconnecting_revokes_access_at_the_provider_removes_the_tokens_and_keeps_the_imported_history_by_default()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        await SyncAsync(user);

        var response = await user.Client.DeleteAsync("/api/integrations/whoop");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, _factory.Whoop.Count("DELETE", "/v2/user/access"));
        Assert.False((await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("connected").GetBoolean());
        Assert.Equal(0, await InDbAsync(db => db.Set<ConnectedAccount>().CountAsync(a => a.UserId == user.Id)));
        Assert.Equal(2, (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await user.Client.PostAsync("/api/integrations/whoop/sync", null)).StatusCode);
    }

    [Fact]
    public async Task Disconnecting_can_also_delete_the_imported_data_but_never_the_users_manual_entries()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        await SyncAsync(user);
        (await user.Client.PostAsJsonAsync("/api/measurements/weight", new { kilograms = 70.5, measuredAt = "2026-10-20T08:00:00+00:00" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync("/api/integrations/whoop?deleteData=true")).StatusCode);

        Assert.Equal(0, (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days").GetArrayLength());
        Assert.Equal(0, (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/activity?from=2026-10-15&to=2026-10-25")).GetProperty("days").GetArrayLength());
        Assert.Equal(1, (await user.Client.GetFromJsonAsync<JsonElement>("/api/measurements/weight?from=2026-10-15&to=2026-10-25")).GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Disconnecting_works_even_if_the_provider_cannot_be_reached()
    {
        var user = await ConnectedUserAsync();
        _factory.Whoop.Interceptor = _ => throw new HttpRequestException("network down");

        Assert.Equal(HttpStatusCode.NoContent, (await user.Client.DeleteAsync("/api/integrations/whoop")).StatusCode);
        Assert.Equal(0, await InDbAsync(db => db.Set<ConnectedAccount>().CountAsync(a => a.UserId == user.Id)));
    }

    [Fact]
    public async Task Users_only_ever_see_their_own_synchronized_data()
    {
        var alice = await ConnectedUserAsync();
        var bob = await UserAsync();
        ScriptTypicalWeek();
        await SyncAsync(alice);

        foreach (var path in new[] { "sleep-recovery", "activity" })
        {
            Assert.Equal(0, (await bob.Client.GetFromJsonAsync<JsonElement>($"/api/measurements/{path}?from=2026-10-15&to=2026-10-25")).GetProperty("days").GetArrayLength());
        }

        Assert.Equal(2, (await alice.Client.GetFromJsonAsync<JsonElement>("/api/measurements/sleep-recovery?from=2026-10-15&to=2026-10-25")).GetProperty("days").GetArrayLength());
    }

    [Fact]
    public async Task The_export_contains_measurements_and_connections_but_no_tokens_and_deleting_the_account_removes_everything()
    {
        var user = await ConnectedUserAsync();
        ScriptTypicalWeek();
        await SyncAsync(user);

        var export = await user.Client.GetFromJsonAsync<JsonElement>("/api/account/export");
        var text = export.GetRawText();

        Assert.Equal(3, export.GetProperty("measurements").GetProperty("sleepSessions").GetArrayLength());
        Assert.Equal("whoop", export.GetProperty("integrations").GetProperty("connectedAccounts")[0].GetProperty("provider").GetString());
        Assert.DoesNotContain("access-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token", text, StringComparison.Ordinal);

        (await user.Client.PostAsJsonAsync("/api/account/delete", new { password = user.Password })).EnsureSuccessStatusCode();

        Assert.Equal(0, await InDbAsync(async db =>
            await db.Set<ConnectedAccount>().CountAsync(a => a.UserId == user.Id)
            + await db.Set<MeasurementSource>().CountAsync(s => s.UserId == user.Id)
            + await db.Set<HealthMeasurement>().CountAsync(m => m.UserId == user.Id)
            + await db.Set<SleepSession>().CountAsync(s => s.UserId == user.Id)
            + await db.Set<Workout>().CountAsync(w => w.UserId == user.Id)));
    }
}

/// <summary>A server without WHOOP credentials: the integration is visible but cannot be connected, and says why.</summary>
public class UnconfiguredWhoopTests : IClassFixture<UnconfiguredWhoopTests.UnconfiguredFactory>
{
    private readonly UnconfiguredFactory _factory;

    public UnconfiguredWhoopTests(UnconfiguredFactory factory) => _factory = factory;

    public sealed class UnconfiguredFactory : ApiFactory
    {
        protected override bool WhoopConfigured => false;
    }

    [Fact]
    public async Task Connecting_without_client_credentials_is_a_clear_503_and_the_list_says_not_configured()
    {
        var user = await TestUser.RegisterAsync(_factory);
        (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = true })).EnsureSuccessStatusCode();

        var response = await user.Client.PostAsync("/api/integrations/whoop/connect", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("integration_not_configured", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.False((await user.Client.GetFromJsonAsync<JsonElement>("/api/integrations"))[0].GetProperty("configured").GetBoolean());
    }
}

/// <summary>User-requested synchronizations are throttled per account.</summary>
public class ThrottledWhoopTests : IClassFixture<ThrottledWhoopTests.ThrottledFactory>
{
    private readonly ThrottledFactory _factory;

    public ThrottledWhoopTests(ThrottledFactory factory) => _factory = factory;

    public sealed class ThrottledFactory : ApiFactory
    {
        protected override int ManualSyncIntervalSeconds => 300;
    }

    [Fact]
    public async Task A_second_manual_synchronization_right_after_the_first_is_refused_with_429()
    {
        var user = await TestUser.RegisterAsync(_factory);
        (await user.Client.PutAsJsonAsync("/api/consents/ExternalIntegrations", new { granted = true })).EnsureSuccessStatusCode();
        var started = await user.Client.PostAsync("/api/integrations/whoop/connect", null);
        var state = HttpUtility.ParseQueryString(new Uri((await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authorizationUrl").GetString()!).Query)["state"];
        await _factory.CreateApiClient().GetAsync($"/api/integrations/whoop/callback?code=code-1&state={Uri.EscapeDataString(state!)}");

        var first = await user.Client.PostAsync("/api/integrations/whoop/sync", null);
        var second = await user.Client.PostAsync("/api/integrations/whoop/sync", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }
}
