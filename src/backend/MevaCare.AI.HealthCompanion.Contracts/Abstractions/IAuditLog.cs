namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>
/// Records security-relevant actions (sign-in, consent changes, exports, deletion...).
/// <see cref="Record"/> only stages the event; it is persisted by the caller's next SaveChanges so that
/// the audit trail and the audited change are committed atomically.
/// Details MUST NOT contain health data, tokens, passwords or other personal data.
/// </summary>
public interface IAuditLog
{
    void Record(string action, Guid? userId, string? details = null);
}
