using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Features;

/// <summary>
/// Keeps connected accounts up to date: every PollMinutes it synchronizes the accounts whose last success is older than IntervalMinutes
/// (and that were not attempted within the last poll period, so a failing account is not hammered). Runs inside the API process
/// (modular monolith); it is not a distributed scheduler, so run a single instance or move it to a worker before scaling out.
/// </summary>
public sealed class SyncBackgroundService(
    IServiceScopeFactory scopes, IOptions<IntegrationsOptions> options, TimeProvider clock, ILogger<SyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sync = options.Value.Sync;
        if (!sync.BackgroundEnabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, sync.PollMinutes)));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var sync = options.Value.Sync;
        var now = clock.GetUtcNow().UtcDateTime;
        var dueBefore = now.AddMinutes(-sync.IntervalMinutes);
        var attemptedBefore = now.AddMinutes(-Math.Max(1, sync.PollMinutes));

        List<(Guid UserId, string Provider)> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            due = (await db.Set<ConnectedAccount>().AsNoTracking()
                    .Where(a => a.Status == AccountStatus.Connected
                        && (a.LastSyncAtUtc == null || a.LastSyncAtUtc < dueBefore)
                        && (a.LastAttemptAtUtc == null || a.LastAttemptAtUtc < attemptedBefore))
                    .OrderBy(a => a.LastSyncAtUtc)
                    .Take(50)
                    .Select(a => new { a.UserId, a.Provider })
                    .ToListAsync(ct))
                .Select(a => (a.UserId, a.Provider))
                .ToList();
        }

        foreach (var (userId, provider) in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SyncService>().SyncAsync(userId, provider, SyncTrigger.Scheduled, ct);
            }
            catch (Exception ex) when (ex is AppException or DbUpdateException)
            {
                logger.LogWarning("Scheduled synchronization of {Provider} was not run: {Type}", provider, ex.GetType().Name);
            }
        }
    }
}
