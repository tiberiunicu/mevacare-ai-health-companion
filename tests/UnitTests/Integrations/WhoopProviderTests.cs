using System.Net;
using System.Text;
using System.Web;
using MevaCare.AI.HealthCompanion.Modules.Integrations;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MevaCare.AI.HealthCompanion.UnitTests.Integrations;

public class WhoopProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private static WhoopProvider Provider(StubHandler handler, Action<IntegrationsOptions>? configure = null)
    {
        var options = new IntegrationsOptions
        {
            Whoop = { ClientId = "client id", ClientSecret = "s3cret&=", RedirectUri = "http://localhost:5080/api/integrations/whoop/callback" },
        };
        configure?.Invoke(options);
        return new WhoopProvider(new HttpClient(handler), Options.Create(options), Clock);
    }

    // ---- authorization ----

    [Fact]
    public void The_authorization_url_is_complete_url_encoded_and_never_contains_the_secret()
    {
        var uri = Provider(new StubHandler((_, _) => Json(HttpStatusCode.OK, "{}"))).BuildAuthorizationUri("state-value");

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("https://api.prod.whoop.com/oauth/oauth2/auth", uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client id", query["client_id"]);
        Assert.Equal("http://localhost:5080/api/integrations/whoop/callback", query["redirect_uri"]);
        Assert.Equal("state-value", query["state"]);
        Assert.Equal("offline read:profile read:recovery read:cycles read:sleep read:workout", query["scope"]);
        Assert.DoesNotContain("s3cret", uri.Query, StringComparison.Ordinal);
        Assert.Contains("client%20id", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_requires_a_client_id_a_secret_and_an_absolute_http_redirect()
    {
        Assert.True(new WhoopOptions { ClientId = "a", ClientSecret = "b", RedirectUri = "https://example.test/cb" }.IsConfigured);
        Assert.False(new WhoopOptions { ClientId = "", ClientSecret = "b", RedirectUri = "https://example.test/cb" }.IsConfigured);
        Assert.False(new WhoopOptions { ClientId = "a", ClientSecret = " ", RedirectUri = "https://example.test/cb" }.IsConfigured);
        Assert.False(new WhoopOptions { ClientId = "a", ClientSecret = "b", RedirectUri = "" }.IsConfigured);
        Assert.False(new WhoopOptions { ClientId = "a", ClientSecret = "b", RedirectUri = "/relative" }.IsConfigured);
        Assert.False(new WhoopOptions { ClientId = "a", ClientSecret = "b", RedirectUri = "javascript:alert(1)" }.IsConfigured);
    }

    // ---- tokens ----

    [Fact]
    public async Task The_code_exchange_posts_a_form_with_the_client_credentials_in_the_body_and_reads_the_tokens()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK,
            """{"access_token":"at","expires_in":3600,"refresh_token":"rt","scope":"offline read:sleep","token_type":"bearer"}"""));

        var tokens = await Provider(handler).ExchangeCodeAsync("the-code", default);

        var (request, body) = handler.Calls.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
        Assert.Null(request.Headers.Authorization);
        var form = HttpUtility.ParseQueryString(body!);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("client id", form["client_id"]);
        Assert.Equal("s3cret&=", form["client_secret"]); // special characters survive the encoding
        Assert.Equal("http://localhost:5080/api/integrations/whoop/callback", form["redirect_uri"]);
        Assert.Equal(("at", "rt", "offline read:sleep"), (tokens.AccessToken, tokens.RefreshToken, tokens.Scopes));
        Assert.Equal(Clock.GetUtcNow().UtcDateTime.AddHours(1), tokens.ExpiresAtUtc);
    }

    [Fact]
    public async Task Basic_client_authentication_keeps_the_secret_out_of_the_body()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"access_token":"at","refresh_token":"rt"}"""));

        var tokens = await Provider(handler, o => o.Whoop.ClientAuthentication = ClientAuthentication.Basic).RefreshAsync("old-rt", default);

        var (request, body) = handler.Calls.Single();
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", body, StringComparison.Ordinal);
        var form = HttpUtility.ParseQueryString(body!);
        Assert.Equal(("refresh_token", "old-rt", "offline"), (form["grant_type"], form["refresh_token"], form["scope"]));
        Assert.Equal(Clock.GetUtcNow().UtcDateTime.AddHours(1), tokens.ExpiresAtUtc); // default lifetime when the provider omits it
    }

    [Fact]
    public async Task A_missing_refresh_token_is_reported_as_null_and_not_invented()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, """{"access_token":"at","expires_in":60}"""));

        var tokens = await Provider(handler).ExchangeCodeAsync("c", default);

        Assert.Null(tokens.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""")]
    [InlineData(HttpStatusCode.BadRequest, "not json at all")]
    public async Task A_rejected_authorization_or_refresh_is_an_auth_failure(HttpStatusCode status, string body)
    {
        var handler = new StubHandler((_, _) => Json(status, body));

        await Assert.ThrowsAsync<WearableAuthException>(() => Provider(handler).RefreshAsync("rt", default));
    }

    [Fact]
    public async Task Rejected_client_credentials_are_the_operators_problem_not_a_reason_to_ask_the_user_to_reconnect()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""));

        await Assert.ThrowsAsync<WearableUnavailableException>(() => Provider(handler).ExchangeCodeAsync("c", default));
    }

    [Fact]
    public async Task Token_endpoint_outages_and_rate_limits_are_distinguished()
    {
        var down = new StubHandler((_, _) => throw new HttpRequestException("secret-network-detail"));
        var limited = new StubHandler((_, _) =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.Add("X-RateLimit-Reset", "42");
            return response;
        });

        var outage = await Assert.ThrowsAsync<WearableUnavailableException>(() => Provider(down).RefreshAsync("rt", default));
        var rate = await Assert.ThrowsAsync<WearableRateLimitedException>(() => Provider(limited).RefreshAsync("rt", default));

        Assert.DoesNotContain("secret-network-detail", outage.Message, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(42), rate.RetryAfter);
    }

    // ---- data API ----

    private const string SleepJson = """
        {"id":"ecfc6a15-4661-442f-a9a4-f160dd7afae8","cycle_id":93845,"v1_id":2222,"user_id":10129,
         "created_at":"2026-10-20T06:00:00.000Z","updated_at":"2026-10-20T06:05:00.000Z",
         "start":"2026-10-19T22:00:00.000Z","end":"2026-10-20T06:00:00.000Z","timezone_offset":"+03:00","nap":false,"score_state":"SCORED",
         "score":{"stage_summary":{"total_in_bed_time_milli":28800000,"total_awake_time_milli":1800000,"total_no_data_time_milli":0,
                  "total_light_sleep_time_milli":15000000,"total_slow_wave_sleep_time_milli":6000000,"total_rem_sleep_time_milli":6000000,
                  "sleep_cycle_count":3,"disturbance_count":12},
                  "sleep_needed":{"baseline_milli":27395716,"need_from_sleep_debt_milli":352230,"need_from_recent_strain_milli":208595,"need_from_recent_nap_milli":-12312},
                  "respiratory_rate":16.11328125,"sleep_performance_percentage":98,"sleep_consistency_percentage":90,"sleep_efficiency_percentage":91.69533848},
         "an_unknown_future_field":{"x":1}}
        """;

    private static string Page(string records, string? next = null) =>
        $"{{\"records\":[{records}],\"next_token\":{(next is null ? "null" : $"\"{next}\"")}}}";

    private static async Task<List<MevaCare.AI.HealthCompanion.Contracts.Measurements.NormalizedHealthData>> FetchAll(WhoopProvider provider, string token = "at")
    {
        var batches = new List<MevaCare.AI.HealthCompanion.Contracts.Measurements.NormalizedHealthData>();
        await foreach (var batch in provider.FetchAsync(token, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), default))
        {
            batches.Add(batch);
        }

        return batches;
    }

    [Fact]
    public async Task A_documented_sleep_record_is_parsed_unknown_fields_are_ignored_and_the_request_is_well_formed()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/v2/activity/sleep", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, Page(SleepJson))
            : Json(HttpStatusCode.OK, Page("")));

        var batches = await FetchAll(Provider(handler));

        var sleep = batches.SelectMany(b => b.Sleeps).Single();
        Assert.Equal("ecfc6a15-4661-442f-a9a4-f160dd7afae8", sleep.ExternalId);
        Assert.Equal((480, 250, 100, 100), (sleep.TimeInBedMinutes, sleep.LightMinutes, sleep.DeepMinutes, sleep.RemMinutes));
        Assert.Equal(98m, sleep.PerformancePercent);
        Assert.Equal(16.1m, Math.Round(sleep.RespiratoryRate!.Value, 1));

        var first = handler.Calls[0].Request;
        Assert.Equal("Bearer", first.Headers.Authorization!.Scheme);
        Assert.Equal("at", first.Headers.Authorization.Parameter);
        Assert.StartsWith("https://api.prod.whoop.com/developer/v2/activity/sleep?", first.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(first.RequestUri.Query);
        Assert.Equal("25", query["limit"]);
        Assert.Equal("2026-10-01T00:00:00.000Z", query["start"]);
        Assert.Equal("2026-10-24T00:00:00.000Z", query["end"]);
        Assert.Null(query["nextToken"]);
        Assert.Equal(
            ["v2/activity/sleep", "v2/recovery", "v2/cycle", "v2/activity/workout"],
            handler.Calls.Select(c => c.Request.RequestUri!.AbsolutePath.Replace("/developer/", "")));
    }

    [Fact]
    public async Task Pages_are_requested_with_the_next_token_until_it_is_empty()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/v2/activity/sleep", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Page(""));
            }

            return HttpUtility.ParseQueryString(request.RequestUri.Query)["nextToken"] switch
            {
                null => Json(HttpStatusCode.OK, Page(SleepJson.Replace("ecfc6a15-4661-442f-a9a4-f160dd7afae8", "page-1"), "tok/1+2=")),
                "tok/1+2=" => Json(HttpStatusCode.OK, Page(SleepJson.Replace("ecfc6a15-4661-442f-a9a4-f160dd7afae8", "page-2"))),
                _ => Json(HttpStatusCode.BadRequest, "{}"),
            };
        });

        var sleeps = (await FetchAll(Provider(handler))).SelectMany(b => b.Sleeps).ToList();

        Assert.Equal(["page-1", "page-2"], sleeps.Select(s => s.ExternalId));
    }

    [Fact]
    public async Task A_provider_that_never_stops_paging_is_cut_off_at_the_configured_cap()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/v2/activity/sleep", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, Page(SleepJson, "again"))
            : Json(HttpStatusCode.OK, Page("")));

        await FetchAll(Provider(handler, o => o.Sync.MaxPagesPerCollection = 3));

        Assert.Equal(3, handler.Calls.Count(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/v2/activity/sleep", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_recovery_is_dated_from_the_sleep_that_was_just_fetched_and_otherwise_from_a_lookup_by_sleep_id()
    {
        const string recovery = """{"cycle_id":93845,"sleep_id":"ecfc6a15-4661-442f-a9a4-f160dd7afae8","user_id":10129,"score_state":"SCORED","score":{"user_calibrating":false,"recovery_score":66,"resting_heart_rate":52,"hrv_rmssd_milli":61.5,"spo2_percentage":95.5,"skin_temp_celsius":33.7}}""";
        const string older = """{"cycle_id":1,"sleep_id":"older-sleep","user_id":10129,"score_state":"SCORED","score":{"recovery_score":50,"resting_heart_rate":55,"hrv_rmssd_milli":40}}""";
        var olderSleep = SleepJson.Replace("ecfc6a15-4661-442f-a9a4-f160dd7afae8", "older-sleep").Replace("2026-10-20T06:00:00.000Z\",\"timezone", "2026-09-20T06:00:00.000Z\",\"timezone").Replace("2026-10-19T22:00:00.000Z", "2026-09-19T22:00:00.000Z");
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/v2/activity/sleep", StringComparison.Ordinal) => Json(HttpStatusCode.OK, Page(SleepJson)),
            var p when p.EndsWith("/v2/recovery", StringComparison.Ordinal) => Json(HttpStatusCode.OK, Page(recovery + "," + older)),
            var p when p.EndsWith("/v2/activity/sleep/older-sleep", StringComparison.Ordinal) => Json(HttpStatusCode.OK, olderSleep),
            _ => Json(HttpStatusCode.OK, Page("")),
        });

        var measurements = (await FetchAll(Provider(handler))).SelectMany(b => b.Measurements).ToList();

        Assert.Equal(6, measurements.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero), measurements.First(m => m.ExternalId == "93845").MeasuredAt.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(3), measurements.First(m => m.ExternalId == "93845").MeasuredAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 6, 0, 0, TimeSpan.Zero), measurements.First(m => m.ExternalId == "1").MeasuredAt.ToUniversalTime());
        Assert.Equal(1, handler.Calls.Count(c => c.Request.RequestUri!.AbsolutePath.EndsWith("/v2/activity/sleep/older-sleep", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(WearableAuthException))]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(WearableRateLimitedException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(WearableUnavailableException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(WearableUnavailableException))]
    [InlineData(HttpStatusCode.Forbidden, typeof(WearableUnavailableException))]
    public async Task Failure_statuses_map_to_stable_exceptions_and_never_echo_the_response_body(HttpStatusCode status, Type expected)
    {
        var handler = new StubHandler((_, _) => Json(status, "{\"detail\":\"secret-provider-detail\"}"));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => FetchAll(Provider(handler)));

        Assert.IsType(expected, ex);
        Assert.DoesNotContain("secret-provider-detail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Garbage_responses_and_network_failures_are_unavailability_not_crashes()
    {
        var garbage = new StubHandler((_, _) => Json(HttpStatusCode.OK, "<html>maintenance</html>"));
        var down = new StubHandler((_, _) => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<WearableUnavailableException>(() => FetchAll(Provider(garbage)));
        await Assert.ThrowsAsync<WearableUnavailableException>(() => FetchAll(Provider(down)));
    }

    [Fact]
    public async Task One_record_in_an_unexpected_shape_is_skipped_and_the_others_of_the_page_are_kept()
    {
        const string broken = """{"id":"broken","start":"not a date","end":12,"score_state":"SCORED"}""";
        var good = SleepJson.Replace("ecfc6a15-4661-442f-a9a4-f160dd7afae8", "good-1");
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/v2/activity/sleep", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, Page(broken + "," + good))
            : Json(HttpStatusCode.OK, Page("")));

        var sleeps = (await FetchAll(Provider(handler))).SelectMany(b => b.Sleeps).ToList();

        Assert.Equal(["good-1"], sleeps.Select(s => s.ExternalId));
    }

    [Fact]
    public async Task The_account_id_comes_from_the_profile_and_revocation_uses_the_documented_endpoint()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : Json(HttpStatusCode.OK, """{"user_id":10129,"email":"x@example.test","first_name":"A","last_name":"B"}"""));
        var provider = Provider(handler);

        var id = await provider.GetExternalUserIdAsync("at", default);
        await provider.RevokeAsync("at", default);

        Assert.Equal("10129", id);
        Assert.Equal("/developer/v2/user/profile/basic", handler.Calls[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal((HttpMethod.Delete, "/developer/v2/user/access"), (handler.Calls[1].Request.Method, handler.Calls[1].Request.RequestUri!.AbsolutePath));
    }
}
