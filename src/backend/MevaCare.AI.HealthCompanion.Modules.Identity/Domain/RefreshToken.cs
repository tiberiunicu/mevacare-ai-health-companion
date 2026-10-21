using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Domain;

/// <summary>
/// A rotating refresh token. Only a SHA-256 hash is stored, so a database leak does not yield usable tokens.
/// All tokens issued from one sign-in share a <see cref="FamilyId"/>; presenting an already-rotated token
/// revokes the whole family (token theft detection).
/// </summary>
public sealed class RefreshToken : Entity
{
    private RefreshToken() { }

    public RefreshToken(Guid userId, Guid familyId, string tokenHash, DateTime createdAtUtc, DateTime expiresAtUtc)
    {
        UserId = userId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid UserId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Revoke(DateTime nowUtc) => RevokedAtUtc ??= nowUtc;
}
