using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Domain;

/// <summary>
/// The current decision of a user for one consent type. History is kept in the audit log:
/// every grant and revocation is recorded there together with the policy version.
/// </summary>
public sealed class UserConsent : AuditableEntity
{
    private UserConsent() { }

    public UserConsent(Guid userId, ConsentType type, string policyVersion, DateTime nowUtc)
    {
        UserId = userId;
        Type = type;
        PolicyVersion = policyVersion;
        GrantedAtUtc = nowUtc;
    }

    public Guid UserId { get; private set; }
    public ConsentType Type { get; private set; }
    public string PolicyVersion { get; private set; } = null!;
    public DateTime GrantedAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsGranted => RevokedAtUtc is null;

    public void Grant(string policyVersion, DateTime nowUtc)
    {
        PolicyVersion = policyVersion;
        GrantedAtUtc = nowUtc;
        RevokedAtUtc = null;
    }

    public void Revoke(DateTime nowUtc) => RevokedAtUtc = nowUtc;
}
