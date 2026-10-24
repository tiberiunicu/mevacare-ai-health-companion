using System.Security.Cryptography;
using System.Text;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Features;

public sealed record IntegrationDto(
    string Provider, string DisplayName, bool Configured, bool Connected, string? Status, DateTime? ConnectedAtUtc,
    DateTime? LastSyncAtUtc, string? LastSyncStatus, IReadOnlyList<string> Scopes);

public sealed record ConnectResponse(string AuthorizationUrl);

public sealed record CallbackResult(string? Provider, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}

/// <summary>Connect (OAuth), callback and disconnect of provider accounts.</summary>
public sealed class ConnectionService(
    AppDbContext db,
    IConsentService consents,
    IEnumerable<IWearableProvider> providers,
    ITokenProtector protector,
    IMeasurementIngest measurements,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<ConnectionService> logger)
{
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public IWearableProvider FindProvider(string key) =>
        providers.FirstOrDefault(p => p.Key == key.Trim().ToLowerInvariant()) ?? throw new NotFoundException("Unknown integration.");

    public async Task<IReadOnlyList<IntegrationDto>> ListAsync(Guid userId, CancellationToken ct)
    {
        var accounts = await db.Set<ConnectedAccount>().AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        return providers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
        {
            var account = accounts.FirstOrDefault(a => a.Provider == p.Key);
            return new IntegrationDto(
                p.Key, p.DisplayName, p.IsConfigured, account is not null, account?.Status.ToString(), account?.ConnectedAtUtc,
                account?.LastSyncAtUtc, account?.LastSyncStatus,
                account is null ? [] : account.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }).ToList();
    }

    public async Task<ConnectResponse> StartConnectAsync(Guid userId, string providerKey, CancellationToken ct)
    {
        var provider = FindProvider(providerKey);
        await consents.EnsureGrantedAsync(userId, ConsentType.ExternalIntegrations, ct);
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        if (!provider.IsConfigured)
        {
            throw new ServiceUnavailableException(
                "integration_not_configured", $"{provider.DisplayName} is not configured on this server (see docs/integrations/whoop.md).");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var expired = await db.Set<OAuthState>().Where(s => s.ExpiresAtUtc < now).ToListAsync(ct);
        db.RemoveRange(expired);

        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        db.Add(new OAuthState(userId, provider.Key, Hash(state), now, now + StateLifetime));
        await db.SaveChangesAsync(ct);
        return new ConnectResponse(provider.BuildAuthorizationUri(state).AbsoluteUri);
    }

    /// <summary>
    /// Completes the browser redirect from the provider. The request is anonymous (a top-level navigation carries no bearer token):
    /// the user is identified solely by the single-use "state" created by <see cref="StartConnectAsync"/>.
    /// Never throws for user-visible problems; returns a stable error code for the redirect instead.
    /// </summary>
    public async Task<CallbackResult> CompleteAsync(string providerKey, string? code, string? state, string? error, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Key == providerKey.Trim().ToLowerInvariant());
        if (provider is null || string.IsNullOrWhiteSpace(state) || state.Length > 200)
        {
            return new CallbackResult(provider?.Key, "invalid_state");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(state);
        var pending = await db.Set<OAuthState>().SingleOrDefaultAsync(s => s.StateHash == hash && s.Provider == provider.Key, ct);
        if (pending is null)
        {
            return new CallbackResult(provider.Key, "invalid_state");
        }

        // A state is valid exactly once, whatever happens next.
        db.Remove(pending);
        await db.SaveChangesAsync(ct);
        if (pending.ExpiresAtUtc < now)
        {
            return new CallbackResult(provider.Key, "invalid_state");
        }

        var userId = pending.UserId;
        if (!string.IsNullOrEmpty(error))
        {
            return new CallbackResult(provider.Key, "authorization_denied");
        }

        if (string.IsNullOrWhiteSpace(code) || code.Length > 4000)
        {
            return new CallbackResult(provider.Key, "invalid_request");
        }

        if (!provider.IsConfigured)
        {
            return new CallbackResult(provider.Key, "not_configured");
        }

        if (!await consents.IsGrantedAsync(userId, ConsentType.ExternalIntegrations, ct)
            || !await consents.IsGrantedAsync(userId, ConsentType.HealthDataProcessing, ct))
        {
            return new CallbackResult(provider.Key, "consent_required");
        }

        WearableTokens tokens;
        string externalUserId;
        try
        {
            tokens = await provider.ExchangeCodeAsync(code, ct);
            if (string.IsNullOrEmpty(tokens.RefreshToken))
            {
                return new CallbackResult(provider.Key, "no_refresh_token");
            }

            externalUserId = await provider.GetExternalUserIdAsync(tokens.AccessToken, ct);
        }
        catch (WearableAuthException)
        {
            return new CallbackResult(provider.Key, "authorization_failed");
        }
        catch (Exception ex) when (ex is WearableUnavailableException or WearableRateLimitedException)
        {
            logger.LogWarning("The {Provider} authorization could not be completed: {Reason}", provider.Key, ex.GetType().Name);
            return new CallbackResult(provider.Key, "provider_unavailable");
        }

        if (await db.Set<ConnectedAccount>().AnyAsync(a => a.Provider == provider.Key && a.ExternalUserId == externalUserId && a.UserId != userId, ct))
        {
            return new CallbackResult(provider.Key, "account_already_linked");
        }

        var existing = await db.Set<ConnectedAccount>().SingleOrDefaultAsync(a => a.UserId == userId && a.Provider == provider.Key, ct);
        if (existing is not null && existing.ExternalUserId != externalUserId)
        {
            // Mixing two people's (or two accounts') data under one connection would corrupt the history.
            return new CallbackResult(provider.Key, "different_account");
        }

        var accessProtected = protector.Protect(tokens.AccessToken);
        var refreshProtected = protector.Protect(tokens.RefreshToken);
        if (existing is null)
        {
            db.Add(new ConnectedAccount(userId, provider.Key, externalUserId, tokens.Scopes, accessProtected, refreshProtected, tokens.ExpiresAtUtc, now));
        }
        else
        {
            existing.SetTokens(tokens.Scopes.Length > 0 ? tokens.Scopes : existing.Scopes, accessProtected, refreshProtected, tokens.ExpiresAtUtc);
        }

        audit.Record("integration.connected", userId, provider.Key);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return new CallbackResult(provider.Key, "account_already_linked");
        }

        return new CallbackResult(provider.Key, null);
    }

    public async Task DisconnectAsync(Guid userId, string providerKey, bool deleteData, CancellationToken ct)
    {
        var provider = FindProvider(providerKey);
        var account = await db.Set<ConnectedAccount>().SingleOrDefaultAsync(a => a.UserId == userId && a.Provider == provider.Key, ct)
            ?? throw new NotFoundException("This integration is not connected.");

        // Best effort: ask the provider to invalidate the access. Failure must not stop the user from disconnecting.
        var now = clock.GetUtcNow().UtcDateTime;
        if (account.AccessTokenExpiresAtUtc > now && protector.TryUnprotect(account.AccessTokenProtected, out var accessToken))
        {
            try
            {
                await provider.RevokeAsync(accessToken, ct);
            }
            catch (Exception ex) when (ex is WearableAuthException or WearableUnavailableException or WearableRateLimitedException)
            {
                logger.LogInformation("The {Provider} access could not be revoked at the provider: {Reason}", provider.Key, ex.GetType().Name);
            }
        }

        db.Remove(account);
        audit.Record(deleteData ? "integration.disconnected_and_data_deleted" : "integration.disconnected", userId, provider.Key);
        await db.SaveChangesAsync(ct);

        if (deleteData)
        {
            await measurements.DeleteProviderDataAsync(userId, provider.Key, ct);
        }
    }

    internal static string Hash(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
