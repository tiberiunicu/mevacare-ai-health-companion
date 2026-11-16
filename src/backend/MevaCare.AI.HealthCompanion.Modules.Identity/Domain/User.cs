using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Domain;

public static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
}

public sealed class User : AuditableEntity
{
    private readonly List<UserRole> _roles = [];
    private readonly List<ExternalLogin> _externalLogins = [];

    private User() { }

    public string Email { get; private set; } = null!;
    public string NormalizedEmail { get; private set; } = null!;

    /// <summary>Null for an account created with an outside provider (Google) that never set a password.</summary>
    public string? PasswordHash { get; private set; }
    public bool HasPassword => PasswordHash is not null;
    public IReadOnlyCollection<ExternalLogin> ExternalLogins => _externalLogins;
    public string DisplayName { get; private set; } = null!;
    public bool IsActive { get; private set; } = true;
    public int FailedLoginCount { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }
    public IReadOnlyCollection<UserRole> Roles => _roles;

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public static User Register(string email, string displayName, string passwordHash)
    {
        var user = new User
        {
            Email = email.Trim(),
            NormalizedEmail = Normalize(email),
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHash,
        };
        user._roles.Add(new UserRole(user.Id, Domain.Roles.User));
        return user;
    }

    /// <summary>An account created by signing in with an outside provider: no password, the provider proves the identity.</summary>
    public static User RegisterExternal(string email, string displayName, string provider, string subject, DateTime nowUtc)
    {
        var user = new User
        {
            Email = email.Trim(),
            NormalizedEmail = Normalize(email),
            DisplayName = displayName.Trim(),
            PasswordHash = null,
        };
        user._roles.Add(new UserRole(user.Id, Domain.Roles.User));
        user._externalLogins.Add(new ExternalLogin(user.Id, provider, subject, email.Trim(), nowUtc));
        return user;
    }

    /// <summary>Records a sign-in made with an outside provider. Unlike a password sign-in it does not clear a password lockout.</summary>
    public void RegisterExternalSignIn(DateTime nowUtc) => LastLoginAtUtc = nowUtc;

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    public void RegisterFailedLogin(DateTime nowUtc, int maxAttempts, TimeSpan lockoutDuration)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
        {
            LockoutEndUtc = nowUtc.Add(lockoutDuration);
            FailedLoginCount = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTime nowUtc)
    {
        FailedLoginCount = 0;
        LockoutEndUtc = null;
        LastLoginAtUtc = nowUtc;
    }

    public void UpdatePasswordHash(string passwordHash) => PasswordHash = passwordHash;
}

public sealed class UserRole(Guid userId, string role)
{
    public Guid UserId { get; private set; } = userId;
    public string Role { get; private set; } = role;
}
