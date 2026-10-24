using System.Net;
using System.Text;
using System.Text.Json;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>
/// A scripted stand-in for the WHOOP service (OAuth token endpoint + developer API v2) that answers with the response shapes shown in
/// WHOOP's public documentation. NOT the real service: it proves that the application handles those shapes, rotation of refresh
/// tokens, paging, expiry and error statuses; it cannot prove that WHOOP itself behaves the same way.
/// </summary>
public sealed class FakeWhoopHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private int _tokenSequence;

    public sealed record RecordedRequest(string Method, string Path, string Query, string? Authorization, string? Body);

    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>A fresh id per test: the database is shared by the whole test collection and one provider account can only be linked once.</summary>
    public string ExternalUserId { get; set; } = NewExternalUserId();

    private static string NewExternalUserId() => Random.Shared.NextInt64(1_000_000, 900_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public List<string> Sleeps { get; } = [];
    public List<string> Recoveries { get; } = [];
    public List<string> Cycles { get; } = [];
    public List<string> Workouts { get; } = [];

    /// <summary>The only access token the data API accepts (null = none issued yet).</summary>
    public string? CurrentAccessToken { get; private set; }

    /// <summary>The only refresh token the token endpoint accepts; refreshing rotates it, like WHOOP's documented behaviour.</summary>
    public string? ValidRefreshToken { get; private set; }

    public int AccessTokenLifetimeSeconds { get; set; } = 3600;
    public int PageSize { get; set; } = 2;
    public bool RejectRefresh { get; set; }
    public bool OmitRefreshToken { get; set; }

    /// <summary>Optional override evaluated first; return null to continue with the normal behaviour.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Interceptor { get; set; }

    public void Reset()
    {
        lock (_gate)
        {
            Requests.Clear();
            Sleeps.Clear();
            Recoveries.Clear();
            Cycles.Clear();
            Workouts.Clear();
            ExternalUserId = NewExternalUserId();
            _tokenSequence = 0;
            CurrentAccessToken = null;
            ValidRefreshToken = null;
            AccessTokenLifetimeSeconds = 3600;
            PageSize = 2;
            RejectRefresh = false;
            OmitRefreshToken = false;
            Interceptor = null;
        }
    }

    public int Count(string method, string pathContains) =>
        Requests.Count(r => r.Method == method && r.Path.Contains(pathContains, StringComparison.Ordinal));

    /// <summary>Makes the current access token invalid, as if the user revoked access or it expired early.</summary>
    public void InvalidateAccessToken() => CurrentAccessToken = "revoked-" + Guid.NewGuid().ToString("N");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Headers.Authorization?.ToString(), body));
            if (Interceptor?.Invoke(request) is { } custom)
            {
                return custom;
            }

            return Handle(request, body);
        }
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string? body)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/oauth/oauth2/token", StringComparison.Ordinal))
        {
            return HandleToken(body ?? "");
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != CurrentAccessToken)
        {
            return Json(HttpStatusCode.Unauthorized, new { error = "unauthorized" });
        }

        if (request.Method == HttpMethod.Delete && path.EndsWith("/v2/user/access", StringComparison.Ordinal))
        {
            CurrentAccessToken = null;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/v2/user/profile/basic", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, new { user_id = long.Parse(ExternalUserId), email = "synthetic@example.test", first_name = "Syn", last_name = "Thetic" });
        }

        if (path.Contains("/v2/activity/sleep/", StringComparison.Ordinal))
        {
            var id = path[(path.LastIndexOf('/') + 1)..];
            var found = Sleeps.FirstOrDefault(s => JsonDocument.Parse(s).RootElement.GetProperty("id").GetString() == id);
            return found is null ? Json(HttpStatusCode.NotFound, new { error = "not_found" }) : Raw(HttpStatusCode.OK, found);
        }

        var source = path switch
        {
            var p when p.EndsWith("/v2/activity/sleep", StringComparison.Ordinal) => Sleeps,
            var p when p.EndsWith("/v2/recovery", StringComparison.Ordinal) => Recoveries,
            var p when p.EndsWith("/v2/cycle", StringComparison.Ordinal) => Cycles,
            var p when p.EndsWith("/v2/activity/workout", StringComparison.Ordinal) => Workouts,
            _ => null,
        };
        if (source is null)
        {
            return Json(HttpStatusCode.NotFound, new { error = "unknown_path" });
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        var limit = Math.Min(int.TryParse(query["limit"], out var l) ? l : 10, PageSize);
        var offset = int.TryParse(query["nextToken"], out var o) ? o : 0;
        var records = source.Skip(offset).Take(limit).ToList();
        var next = offset + records.Count < source.Count ? (offset + records.Count).ToString() : null;
        var payload = $"{{\"records\":[{string.Join(",", records)}],\"next_token\":{(next is null ? "null" : $"\"{next}\"")}}}";
        return Raw(HttpStatusCode.OK, payload);
    }

    private HttpResponseMessage HandleToken(string form)
    {
        var values = System.Web.HttpUtility.ParseQueryString(form);
        if (string.IsNullOrEmpty(values["client_id"]) || string.IsNullOrEmpty(values["client_secret"]))
        {
            return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client" });
        }

        switch (values["grant_type"])
        {
            case "authorization_code":
                if (string.IsNullOrEmpty(values["code"]) || !values["code"]!.StartsWith("code-", StringComparison.Ordinal) || string.IsNullOrEmpty(values["redirect_uri"]))
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
                }

                break;
            case "refresh_token":
                if (RejectRefresh || values["refresh_token"] != ValidRefreshToken)
                {
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });
                }

                break;
            default:
                return Json(HttpStatusCode.BadRequest, new { error = "unsupported_grant_type" });
        }

        _tokenSequence++;
        CurrentAccessToken = $"access-token-{_tokenSequence}";
        ValidRefreshToken = $"refresh-token-{_tokenSequence}";
        return Json(HttpStatusCode.OK, new
        {
            access_token = CurrentAccessToken,
            expires_in = AccessTokenLifetimeSeconds,
            refresh_token = OmitRefreshToken ? null : ValidRefreshToken,
            scope = "offline read:profile read:recovery read:cycles read:sleep read:workout",
            token_type = "bearer",
        });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) => Raw(status, JsonSerializer.Serialize(value));

    private static HttpResponseMessage Raw(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---- record builders in the documented shapes ----

    public static string Sleep(
        string id, DateTimeOffset startUtc, DateTimeOffset endUtc, string offset = "+00:00", bool scored = true, bool nap = false,
        int lightMin = 240, int deepMin = 80, int remMin = 100, int awakeMin = 30, double performance = 90, double efficiency = 92.5, double consistency = 88)
    {
        object? score = scored
            ? new
            {
                stage_summary = new
                {
                    total_in_bed_time_milli = (lightMin + deepMin + remMin + awakeMin) * 60_000L,
                    total_awake_time_milli = awakeMin * 60_000L,
                    total_no_data_time_milli = 0,
                    total_light_sleep_time_milli = lightMin * 60_000L,
                    total_slow_wave_sleep_time_milli = deepMin * 60_000L,
                    total_rem_sleep_time_milli = remMin * 60_000L,
                    sleep_cycle_count = 4,
                    disturbance_count = 7,
                },
                sleep_needed = new { baseline_milli = 27_000_000, need_from_sleep_debt_milli = 0, need_from_recent_strain_milli = 100, need_from_recent_nap_milli = -12 },
                respiratory_rate = 15.5,
                sleep_performance_percentage = performance,
                sleep_consistency_percentage = consistency,
                sleep_efficiency_percentage = efficiency,
            }
            : null;
        return JsonSerializer.Serialize(new
        {
            id,
            cycle_id = 93845,
            user_id = 10129,
            created_at = startUtc,
            updated_at = endUtc,
            start = startUtc,
            end = endUtc,
            timezone_offset = offset,
            nap,
            score_state = scored ? "SCORED" : "PENDING_SCORE",
            score,
        });
    }

    public static string Recovery(long cycleId, string sleepId, double score = 66, double restingHeartRate = 52, double hrvMilli = 61.5, bool scored = true)
    {
        object? body = scored
            ? new { user_calibrating = false, recovery_score = score, resting_heart_rate = restingHeartRate, hrv_rmssd_milli = hrvMilli, spo2_percentage = 95.5, skin_temp_celsius = 33.7 }
            : null;
        return JsonSerializer.Serialize(new
        {
            cycle_id = cycleId,
            sleep_id = sleepId,
            user_id = 10129,
            created_at = DateTimeOffset.UtcNow,
            updated_at = DateTimeOffset.UtcNow,
            score_state = scored ? "SCORED" : "PENDING_SCORE",
            score = body,
        });
    }

    public static string Cycle(long id, DateTimeOffset startUtc, DateTimeOffset? endUtc, string offset = "+00:00", double strain = 11.2, double kilojoules = 9000, bool scored = true)
    {
        object? score = scored ? new { strain, kilojoule = kilojoules, average_heart_rate = 66, max_heart_rate = 151 } : null;
        return JsonSerializer.Serialize(new
        {
            id,
            user_id = 10129,
            created_at = startUtc,
            updated_at = startUtc,
            start = startUtc,
            end = endUtc,
            timezone_offset = offset,
            score_state = scored ? "SCORED" : "PENDING_SCORE",
            score,
        });
    }

    public static string Workout(
        string id, DateTimeOffset startUtc, DateTimeOffset endUtc, string offset = "+00:00", string sport = "running", double strain = 8.2, double kilojoules = 1569.3,
        bool scored = true)
    {
        object? score = scored
            ? new
            {
                strain,
                average_heart_rate = 123,
                max_heart_rate = 146,
                kilojoule = kilojoules,
                percent_recorded = 100,
                distance_meter = 1772.77,
                altitude_gain_meter = 3.2,
                altitude_change_meter = -1.1,
                zone_durations = new { zone_zero_milli = 1, zone_one_milli = 2, zone_two_milli = 3, zone_three_milli = 4, zone_four_milli = 5, zone_five_milli = 6 },
            }
            : null;
        return JsonSerializer.Serialize(new
        {
            id,
            user_id = 10129,
            created_at = startUtc,
            updated_at = endUtc,
            start = startUtc,
            end = endUtc,
            timezone_offset = offset,
            sport_name = sport,
            sport_id = 0,
            score_state = scored ? "SCORED" : "PENDING_SCORE",
            score,
        });
    }
}
