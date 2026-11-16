using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Domain;

/// <summary>
/// A one-time, short-lived password-reset link. Only a SHA-256 hash of the random token is stored (like refresh tokens), so a database
/// leak does not yield usable links. A concurrency token makes "use it" atomic: of two simultaneous uses only one can succeed.
/// </summary>
public sealed class PasswordResetToken : Entity
{
    private PasswordResetToken() { }

    public PasswordResetToken(Guid userId, string tokenHash, DateTime createdAtUtc, DateTime expiresAtUtc)
    {
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }

    public bool IsUsable(DateTime nowUtc) => UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc ??= nowUtc;
}
