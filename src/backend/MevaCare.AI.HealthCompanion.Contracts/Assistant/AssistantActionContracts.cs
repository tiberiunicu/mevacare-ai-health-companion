namespace MevaCare.AI.HealthCompanion.Contracts.Assistant;

/// <summary>The profile facts the assistant may change (with the user's confirmation): height and activity level.</summary>
public sealed record ProfileBasicsDto(decimal? HeightCm, string ActivityLevel);

/// <summary>Reads and changes the profile basics for the assistant. Every call is scoped to the user and re-checks the health-data consent.</summary>
public interface IProfileAssistantApi
{
    Task<ProfileBasicsDto> GetBasicsAsync(Guid userId, CancellationToken ct);

    /// <summary>Changes only the given values; sex, date of birth and time zone stay as they are. A null argument leaves that value unchanged.</summary>
    Task<ProfileBasicsDto> SetBasicsAsync(Guid userId, decimal? heightCm, string? activityLevel, CancellationToken ct);
}

public sealed record WearableStatusDto(
    string Provider, string DisplayName, bool Configured, bool Connected, string? Status, DateTime? LastSyncAtUtc, string? LastSyncStatus);

public sealed record WearableSyncResultDto(string Status, int RecordsAdded, int RecordsUpdated, string? ErrorCode);

/// <summary>Status and "synchronize now" for connected wearables. Connecting an account (OAuth) stays in the application's own page.</summary>
public interface IWearablesAssistantApi
{
    Task<IReadOnlyList<WearableStatusDto>> GetStatusAsync(Guid userId, CancellationToken ct);

    /// <summary>Runs a manual synchronization (consents, per-account lock and throttling are enforced by the integrations module).</summary>
    Task<WearableSyncResultDto> SyncAsync(Guid userId, string provider, CancellationToken ct);
}
