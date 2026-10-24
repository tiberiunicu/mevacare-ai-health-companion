using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;

/// <summary>
/// WHOOP developer API v2 (OAuth 2.0 authorization-code flow, https://developer.whoop.com).
/// Written from the public documentation. NOT yet exercised against the real WHOOP service (no developer app or device was available
/// while building it): tests use a scripted HTTP handler that returns documented response shapes.
/// Response bodies and tokens are never put into exception messages or logs.
/// </summary>
public sealed class WhoopProvider(HttpClient http, IOptions<IntegrationsOptions> options, TimeProvider clock, ILogger<WhoopProvider>? logger = null) : IWearableProvider
{
    private const int PageSize = 25;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private WhoopOptions Whoop => options.Value.Whoop;

    public string Key => "whoop";

    public string DisplayName => "WHOOP";

    public bool IsConfigured => Whoop.IsConfigured;

    public Uri BuildAuthorizationUri(string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = Whoop.ClientId,
            ["redirect_uri"] = Whoop.RedirectUri,
            ["scope"] = Whoop.Scopes,
            ["state"] = state,
        };
        var builder = new UriBuilder(Whoop.AuthorizationEndpoint)
        {
            Query = string.Join("&", query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")),
        };
        return builder.Uri;
    }

    public Task<WearableTokens> ExchangeCodeAsync(string code, CancellationToken ct) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Whoop.RedirectUri,
        }, ct);

    public Task<WearableTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
        RequestTokensAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = "offline",
        }, ct);

    public async Task<string> GetExternalUserIdAsync(string accessToken, CancellationToken ct)
    {
        var profile = await GetAsync<WhoopProfile>(accessToken, "v2/user/profile/basic", ct);
        return profile.UserId.ToString(CultureInfo.InvariantCulture);
    }

    public async IAsyncEnumerable<NormalizedHealthData> FetchAsync(
        string accessToken, DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken ct)
    {
        // Sleep first: a recovery has no timestamp of its own and is dated at the end of the sleep it belongs to.
        var sleepEnds = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        await foreach (var page in PagesAsync<WhoopSleep>(accessToken, "v2/activity/sleep", from, to, ct))
        {
            var sleeps = new List<NormalizedSleep>();
            foreach (var record in page)
            {
                if (WhoopMapper.Map(record) is { } mapped)
                {
                    sleeps.Add(mapped);
                    sleepEnds[record.Id] = mapped.End;
                }
            }

            yield return new NormalizedHealthData([], sleeps, []);
        }

        await foreach (var page in PagesAsync<WhoopRecovery>(accessToken, "v2/recovery", from, to, ct))
        {
            var measurements = new List<NormalizedMeasurement>();
            foreach (var record in page)
            {
                if (record.SleepId is null || !string.Equals(record.ScoreState, WhoopMapper.ScoredState, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!sleepEnds.TryGetValue(record.SleepId, out var sleepEnd))
                {
                    // The sleep started before the requested window: look it up once.
                    var sleep = await GetAsync<WhoopSleep>(accessToken, $"v2/activity/sleep/{Uri.EscapeDataString(record.SleepId)}", ct);
                    if (WhoopMapper.Map(sleep) is not { } mappedSleep)
                    {
                        continue;
                    }

                    sleepEnd = mappedSleep.End;
                    sleepEnds[record.SleepId] = sleepEnd;
                }

                measurements.AddRange(WhoopMapper.Map(record, sleepEnd));
            }

            yield return new NormalizedHealthData(measurements, [], []);
        }

        await foreach (var page in PagesAsync<WhoopCycle>(accessToken, "v2/cycle", from, to, ct))
        {
            yield return new NormalizedHealthData(page.SelectMany(WhoopMapper.Map).ToList(), [], []);
        }

        await foreach (var page in PagesAsync<WhoopWorkout>(accessToken, "v2/activity/workout", from, to, ct))
        {
            yield return new NormalizedHealthData([], [], page.Select(WhoopMapper.Map).OfType<NormalizedWorkout>().ToList());
        }
    }

    public async Task RevokeAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ApiUri("v2/user/access"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, ct);
    }

    // ---- plumbing ----

    private async IAsyncEnumerable<List<T>> PagesAsync<T>(
        string accessToken, string path, DateTimeOffset from, DateTimeOffset to, [EnumeratorCancellation] CancellationToken ct)
    {
        string? next = null;
        for (var page = 0; page < options.Value.Sync.MaxPagesPerCollection; page++)
        {
            var query = new StringBuilder($"limit={PageSize}&start={Uri.EscapeDataString(Format(from))}&end={Uri.EscapeDataString(Format(to))}");
            if (!string.IsNullOrEmpty(next))
            {
                query.Append("&nextToken=").Append(Uri.EscapeDataString(next));
            }

            var result = await GetAsync<WhoopPage>(accessToken, $"{path}?{query}", ct);
            var records = new List<T>();
            var skipped = 0;
            foreach (var element in result.Records ?? [])
            {
                // One record in an unexpected shape must not block every later synchronization: skip it, count it, never log its content.
                try
                {
                    if (element.Deserialize<T>(Json) is { } record)
                    {
                        records.Add(record);
                        continue;
                    }
                }
                catch (JsonException)
                {
                    // counted below
                }

                skipped++;
            }

            if (skipped > 0)
            {
                logger?.LogWarning("{Count} records from {Path} were in an unexpected format and were skipped", skipped, path);
            }

            if (records.Count > 0)
            {
                yield return records;
            }

            next = result.NextToken;
            if (string.IsNullOrEmpty(next))
            {
                yield break;
            }
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private Uri ApiUri(string pathAndQuery) => new(new Uri(Whoop.ApiBaseUrl), pathAndQuery);

    private async Task<T> GetAsync<T>(string accessToken, string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUri(pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, ct);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new WearableUnavailableException("The provider returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new WearableUnavailableException("The provider returned a response in an unexpected format.", ex);
        }
    }

    private async Task<WearableTokens> RequestTokensAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Whoop.TokenEndpoint);
        if (Whoop.ClientAuthentication == ClientAuthentication.Basic)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(Whoop.ClientId)}:{Uri.EscapeDataString(Whoop.ClientSecret)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        else
        {
            form["client_id"] = Whoop.ClientId;
            form["client_secret"] = Whoop.ClientSecret;
        }

        request.Content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new WearableUnavailableException("The provider's token endpoint could not be reached.", ex);
        }

        using (response)
        {
            WhoopTokenResponse? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<WhoopTokenResponse>(Json, ct);
            }
            catch (JsonException)
            {
                // handled below through the status code
            }

            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => new WearableRateLimitedException(RetryAfter(response)),
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized when body?.Error == "invalid_client" =>
                        new WearableUnavailableException("The provider rejected the application's client credentials."),
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized => new WearableAuthException("The provider rejected the authorization."),
                    _ => new WearableUnavailableException($"The provider's token endpoint answered {(int)response.StatusCode}."),
                };
            }

            if (string.IsNullOrWhiteSpace(body?.AccessToken))
            {
                throw new WearableUnavailableException("The provider's token response did not contain an access token.");
            }

            return new WearableTokens(
                body.AccessToken,
                string.IsNullOrWhiteSpace(body.RefreshToken) ? null : body.RefreshToken,
                clock.GetUtcNow().UtcDateTime.AddSeconds(body.ExpiresIn is > 0 ? body.ExpiresIn.Value : 3600),
                body.Scope ?? "");
        }
    }

    /// <summary>Sends a data API request and maps failures to the provider exceptions. The caller disposes the response.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new WearableUnavailableException("The provider could not be reached.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = response.StatusCode;
        var retryAfter = RetryAfter(response);
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new WearableAuthException("The provider rejected the access token."),
            HttpStatusCode.TooManyRequests => new WearableRateLimitedException(retryAfter),
            _ => new WearableUnavailableException($"The provider answered {(int)status}."),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        // WHOOP documents X-RateLimit-Reset (seconds until the window resets); Retry-After is honoured too if a gateway adds it.
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}
