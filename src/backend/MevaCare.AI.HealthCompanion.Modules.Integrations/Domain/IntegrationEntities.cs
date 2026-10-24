using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Domain;

public enum AccountStatus
{
    Connected = 1,

    /// <summary>The provider refused the stored tokens (revoked in the provider's app, or the key ring was lost). The user must reconnect.</summary>
    NeedsReauthorization = 2,
}

/// <summary>A user's link to one provider account. Tokens are stored only in protected form (ASP.NET Core Data Protection).</summary>
public sealed class ConnectedAccount : AuditableEntity
{
    private ConnectedAccount() { }

    public ConnectedAccount(Guid userId, string provider, string externalUserId, string scopes, string accessTokenProtected, string refreshTokenProtected, DateTime accessTokenExpiresAtUtc, DateTime connectedAtUtc)
    {
        UserId = userId;
        Provider = provider;
        ExternalUserId = externalUserId;
        ConnectedAtUtc = connectedAtUtc;
        SetTokens(scopes, accessTokenProtected, refreshTokenProtected, accessTokenExpiresAtUtc);
    }

    public Guid UserId { get; private set; }

    /// <summary>Lower-case provider key ("whoop").</summary>
    public string Provider { get; private set; } = "";

    /// <summary>The provider's own id for the account. Unique per provider: one WHOOP account cannot feed two MevaCare users.</summary>
    public string ExternalUserId { get; private set; } = "";

    public AccountStatus Status { get; private set; } = AccountStatus.Connected;
    public string Scopes { get; private set; } = "";
    public string AccessTokenProtected { get; private set; } = "";
    public string RefreshTokenProtected { get; private set; } = "";
    public DateTime AccessTokenExpiresAtUtc { get; private set; }
    public DateTime ConnectedAtUtc { get; private set; }

    /// <summary>Start of the last fully successful synchronization.</summary>
    public DateTime? LastSyncAtUtc { get; private set; }

    public DateTime? LastAttemptAtUtc { get; private set; }

    /// <summary>"succeeded", or the failure code of the last attempt.</summary>
    public string? LastSyncStatus { get; private set; }

    public void SetTokens(string scopes, string accessTokenProtected, string refreshTokenProtected, DateTime accessTokenExpiresAtUtc)
    {
        Scopes = scopes.Length <= 500 ? scopes : scopes[..500];
        AccessTokenProtected = accessTokenProtected;
        RefreshTokenProtected = refreshTokenProtected;
        AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc;
        Status = AccountStatus.Connected;
    }

    public void MarkNeedsReauthorization() => Status = AccountStatus.NeedsReauthorization;

    public void RecordAttempt(DateTime attemptAtUtc, string status, bool succeeded)
    {
        LastAttemptAtUtc = attemptAtUtc;
        LastSyncStatus = status;
        if (succeeded)
        {
            LastSyncAtUtc = attemptAtUtc;
        }
    }
}

public enum SyncTrigger
{
    Manual = 1,
    Scheduled = 2,
}

public enum SyncStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Skipped = 4,
}

/// <summary>One synchronization run, kept for support and for showing the user what happened (a bounded history per account).</summary>
public sealed class SyncJob : Entity
{
    private SyncJob() { }

    public SyncJob(Guid accountId, SyncTrigger trigger, DateTime startedAtUtc, DateTime rangeFromUtc, DateTime rangeToUtc)
    {
        ConnectedAccountId = accountId;
        Trigger = trigger;
        StartedAtUtc = startedAtUtc;
        RangeFromUtc = rangeFromUtc;
        RangeToUtc = rangeToUtc;
        Status = SyncStatus.Running;
    }

    public Guid ConnectedAccountId { get; private set; }
    public SyncTrigger Trigger { get; private set; }
    public SyncStatus Status { get; private set; }
    public DateTime StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public DateTime RangeFromUtc { get; private set; }
    public DateTime RangeToUtc { get; private set; }
    public int RecordsFetched { get; private set; }
    public int RecordsAdded { get; private set; }
    public int RecordsUpdated { get; private set; }

    /// <summary>Stable failure code (reauthorization_required, rate_limited, provider_unavailable, consent_required, internal_error). Never provider messages.</summary>
    public string? ErrorCode { get; private set; }

    public void AddProgress(int fetched, int added, int updated)
    {
        RecordsFetched += fetched;
        RecordsAdded += added;
        RecordsUpdated += updated;
    }

    public void Finish(SyncStatus status, DateTime finishedAtUtc, string? errorCode)
    {
        Status = status;
        FinishedAtUtc = finishedAtUtc;
        ErrorCode = errorCode;
    }
}

/// <summary>
/// The pending authorization of one connect attempt. Only the SHA-256 of the random "state" is stored; the row is deleted when used
/// or expired, so a state works once and only for the user who started it.
/// </summary>
public sealed class OAuthState : Entity
{
    private OAuthState() { }

    public OAuthState(Guid userId, string provider, string stateHash, DateTime createdAtUtc, DateTime expiresAtUtc)
    {
        UserId = userId;
        Provider = provider;
        StateHash = stateHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = "";
    public string StateHash { get; private set; } = "";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
}
