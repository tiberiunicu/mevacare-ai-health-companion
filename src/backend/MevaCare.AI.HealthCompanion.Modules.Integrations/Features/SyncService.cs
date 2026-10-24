using System.Collections.Concurrent;
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

public sealed record SyncJobDto(
    Guid Id, string Trigger, string Status, DateTime StartedAtUtc, DateTime? FinishedAtUtc, int RecordsFetched, int RecordsAdded, int RecordsUpdated,
    string? ErrorCode);

/// <summary>One lock per account: a manual and a scheduled synchronization must never refresh tokens or write at the same time.</summary>
public sealed class SyncLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public SemaphoreSlim For(Guid accountId) => _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// Pulls data from a connected provider into the Measurements module. Idempotent (the ingest matches provider record ids) and
/// resumable (data is stored batch by batch, so a failure keeps what was already fetched; the next run re-reads an overlap window).
/// Failure codes are stable strings; provider messages and tokens are never stored or logged.
/// </summary>
public sealed class SyncService(
    AppDbContext db,
    IConsentService consents,
    IEnumerable<IWearableProvider> providers,
    ITokenProtector protector,
    IMeasurementIngest ingest,
    SyncLocks locks,
    IOptions<IntegrationsOptions> options,
    TimeProvider clock,
    ILogger<SyncService> logger)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);
    private const int JobsKept = 30;

    public async Task<SyncJobDto> SyncAsync(Guid userId, string providerKey, SyncTrigger trigger, CancellationToken ct)
    {
        var provider = providers.FirstOrDefault(p => p.Key == providerKey.Trim().ToLowerInvariant())
            ?? throw new NotFoundException("Unknown integration.");
        if (trigger == SyncTrigger.Manual)
        {
            await consents.EnsureGrantedAsync(userId, ConsentType.ExternalIntegrations, ct);
            await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        }

        var accountId = await db.Set<ConnectedAccount>().AsNoTracking()
            .Where(a => a.UserId == userId && a.Provider == provider.Key).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("This integration is not connected.");

        var gate = locks.For(accountId);
        if (!await gate.WaitAsync(0, ct))
        {
            throw new ConflictException("sync_in_progress", "A synchronization is already running for this account.");
        }

        try
        {
            // Loaded only after the lock is held, so the tokens are never stale after another run rotated them.
            db.ChangeTracker.Clear();
            var account = await db.Set<ConnectedAccount>().SingleAsync(a => a.Id == accountId, ct);
            return await RunAsync(account, provider, trigger, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SyncJobDto>> RecentJobsAsync(Guid userId, string providerKey, int limit, CancellationToken ct)
    {
        var key = providerKey.Trim().ToLowerInvariant();
        var jobs = await (
                from j in db.Set<SyncJob>().AsNoTracking()
                join a in db.Set<ConnectedAccount>().AsNoTracking() on j.ConnectedAccountId equals a.Id
                where a.UserId == userId && a.Provider == key
                orderby j.StartedAtUtc descending
                select j)
            .Take(Math.Clamp(limit, 1, 20))
            .ToListAsync(ct);
        return jobs.Select(ToDto).ToList();
    }

    private async Task<SyncJobDto> RunAsync(ConnectedAccount account, IWearableProvider provider, SyncTrigger trigger, CancellationToken ct)
    {
        var sync = options.Value.Sync;
        var now = clock.GetUtcNow().UtcDateTime;
        if (trigger == SyncTrigger.Manual && account.LastAttemptAtUtc is { } last && now - last < TimeSpan.FromSeconds(sync.MinManualIntervalSeconds))
        {
            throw new RateLimitedException("This account was synchronized a moment ago. Please wait a minute and try again.");
        }

        var from = account.LastSyncAtUtc is { } lastSuccess ? lastSuccess.AddDays(-sync.OverlapDays) : now.AddDays(-sync.InitialBackfillDays);
        var job = new SyncJob(account.Id, trigger, now, from, now);
        db.Add(job);

        var status = SyncStatus.Succeeded;
        string? error = null;
        try
        {
            if (!await consents.IsGrantedAsync(account.UserId, ConsentType.ExternalIntegrations, ct)
                || !await consents.IsGrantedAsync(account.UserId, ConsentType.HealthDataProcessing, ct))
            {
                status = SyncStatus.Skipped;
                error = "consent_required";
            }
            else if (account.Status == AccountStatus.NeedsReauthorization)
            {
                status = SyncStatus.Failed;
                error = "reauthorization_required";
            }
            else
            {
                await ExecuteAsync(account, provider, job, from, now, ct);
            }
        }
        catch (Exception ex) when (ex is WearableAuthException or System.Security.Cryptography.CryptographicException)
        {
            account.MarkNeedsReauthorization();
            status = SyncStatus.Failed;
            error = "reauthorization_required";
        }
        catch (WearableRateLimitedException)
        {
            status = SyncStatus.Failed;
            error = "rate_limited";
        }
        catch (WearableUnavailableException ex)
        {
            logger.LogWarning("{Provider} synchronization failed: {Reason}", provider.Key, ex.Message);
            status = SyncStatus.Failed;
            error = "provider_unavailable";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected (for example a database error): the tracked entities may be in an unusable state, so record the failure afresh.
            logger.LogError("{Provider} synchronization failed unexpectedly: {Type}", provider.Key, ex.GetType().Name);
            db.ChangeTracker.Clear();
            return await RecordFailureAsync(account.Id, trigger, from, now, ct);
        }

        var finished = clock.GetUtcNow().UtcDateTime;
        job.Finish(status, finished, error);
        account.RecordAttempt(now, error ?? "succeeded", error is null);
        await PruneAsync(account.Id, ct);
        await db.SaveChangesAsync(ct);
        return ToDto(job);
    }

    private async Task ExecuteAsync(ConnectedAccount account, IWearableProvider provider, SyncJob job, DateTime from, DateTime to, CancellationToken ct)
    {
        var accessToken = await EnsureAccessTokenAsync(account, provider, forceRefresh: false, ct);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await foreach (var batch in provider.FetchAsync(accessToken, from, to, ct))
                {
                    var counts = await ingest.UpsertAsync(account.UserId, provider.Key, batch, ct);
                    job.AddProgress(batch.Count, counts.Added, counts.Updated);
                }

                return;
            }
            catch (WearableAuthException) when (attempt == 0)
            {
                // The access token can be rejected before its expiry (revoked, clock skew): refresh once and read again (the ingest is idempotent).
                accessToken = await EnsureAccessTokenAsync(account, provider, forceRefresh: true, ct);
            }
        }
    }

    private async Task<string> EnsureAccessTokenAsync(ConnectedAccount account, IWearableProvider provider, bool forceRefresh, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!forceRefresh && account.AccessTokenExpiresAtUtc - now > RefreshMargin && protector.TryUnprotect(account.AccessTokenProtected, out var current))
        {
            return current;
        }

        if (!protector.TryUnprotect(account.RefreshTokenProtected, out var refreshToken))
        {
            throw new WearableAuthException("The stored tokens cannot be read.");
        }

        var tokens = await provider.RefreshAsync(refreshToken, ct);

        // Refresh tokens are single-use: the new one must be stored before anything else happens, and not be lost to a cancellation.
        account.SetTokens(
            tokens.Scopes.Length > 0 ? tokens.Scopes : account.Scopes,
            protector.Protect(tokens.AccessToken),
            protector.Protect(tokens.RefreshToken ?? refreshToken),
            tokens.ExpiresAtUtc);
        await db.SaveChangesAsync(CancellationToken.None);
        return tokens.AccessToken;
    }

    private async Task<SyncJobDto> RecordFailureAsync(Guid accountId, SyncTrigger trigger, DateTime from, DateTime now, CancellationToken ct)
    {
        var account = await db.Set<ConnectedAccount>().SingleAsync(a => a.Id == accountId, ct);
        var job = new SyncJob(accountId, trigger, now, from, now);
        job.Finish(SyncStatus.Failed, clock.GetUtcNow().UtcDateTime, "internal_error");
        db.Add(job);
        account.RecordAttempt(now, "internal_error", succeeded: false);
        await db.SaveChangesAsync(ct);
        return ToDto(job);
    }

    private async Task PruneAsync(Guid accountId, CancellationToken ct)
    {
        var old = await db.Set<SyncJob>().Where(j => j.ConnectedAccountId == accountId)
            .OrderByDescending(j => j.StartedAtUtc).Skip(JobsKept).ToListAsync(ct);
        db.RemoveRange(old);
    }

    internal static SyncJobDto ToDto(SyncJob j) => new(
        j.Id, j.Trigger.ToString(), j.Status.ToString(), j.StartedAtUtc, j.FinishedAtUtc, j.RecordsFetched, j.RecordsAdded, j.RecordsUpdated, j.ErrorCode);
}
