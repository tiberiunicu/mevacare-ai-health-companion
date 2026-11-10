using MevaCare.AI.HealthCompanion.Contracts.Assistant;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Features;

/// <summary>
/// What the assistant may do with connected wearables: see their status and run a manual synchronization. It goes through the same
/// services as the application's own page, so consents, the per-account lock and the minimum interval between manual runs apply
/// unchanged. Connecting or disconnecting an account is never offered to the assistant.
/// </summary>
public sealed class WearablesAssistantApi(ConnectionService connections, SyncService sync) : IWearablesAssistantApi
{
    public async Task<IReadOnlyList<WearableStatusDto>> GetStatusAsync(Guid userId, CancellationToken ct) =>
        (await connections.ListAsync(userId, ct))
            .Select(i => new WearableStatusDto(i.Provider, i.DisplayName, i.Configured, i.Connected, i.Status, i.LastSyncAtUtc, i.LastSyncStatus))
            .ToList();

    public async Task<WearableSyncResultDto> SyncAsync(Guid userId, string provider, CancellationToken ct)
    {
        var job = await sync.SyncAsync(userId, provider, SyncTrigger.Manual, ct);
        return new WearableSyncResultDto(job.Status, job.RecordsAdded, job.RecordsUpdated, job.ErrorCode);
    }
}
