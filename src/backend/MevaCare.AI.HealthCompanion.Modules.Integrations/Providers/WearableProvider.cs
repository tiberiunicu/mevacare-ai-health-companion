using MevaCare.AI.HealthCompanion.Contracts.Measurements;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Providers;

public sealed record WearableTokens(string AccessToken, string? RefreshToken, DateTime ExpiresAtUtc, string Scopes);

/// <summary>The access token or refresh token was rejected (expired, revoked by the user at the provider, or already rotated).</summary>
public sealed class WearableAuthException(string message) : Exception(message);

public sealed class WearableRateLimitedException(TimeSpan? retryAfter) : Exception("The provider rate limit was reached.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>The provider is down, timed out or answered unexpectedly. Messages never contain response bodies.</summary>
public sealed class WearableUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// What the application needs from a wearable provider. Nothing here (or in the Measurements module) is specific to one vendor:
/// a provider authorizes the user, hands out tokens and returns data already mapped to the normalized contracts.
/// </summary>
public interface IWearableProvider
{
    /// <summary>Lower-case key used in URLs, in stored accounts and as the measurement source ("whoop").</summary>
    string Key { get; }

    string DisplayName { get; }

    /// <summary>False when the operator has not entered the provider's client credentials.</summary>
    bool IsConfigured { get; }

    Uri BuildAuthorizationUri(string state);

    Task<WearableTokens> ExchangeCodeAsync(string code, CancellationToken ct);

    /// <summary>Providers may rotate refresh tokens: the returned set replaces the old one, which must not be used again.</summary>
    Task<WearableTokens> RefreshAsync(string refreshToken, CancellationToken ct);

    /// <summary>The provider's own stable id of the account (never the e-mail address or the name).</summary>
    Task<string> GetExternalUserIdAsync(string accessToken, CancellationToken ct);

    /// <summary>
    /// Streams normalized data for [from, to) in batches so that progress is kept when a later page fails.
    /// Throws <see cref="WearableAuthException"/>, <see cref="WearableRateLimitedException"/> or <see cref="WearableUnavailableException"/>.
    /// </summary>
    IAsyncEnumerable<NormalizedHealthData> FetchAsync(string accessToken, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);

    /// <summary>Best effort: tells the provider to invalidate the application's access for the user.</summary>
    Task RevokeAsync(string accessToken, CancellationToken ct);
}
