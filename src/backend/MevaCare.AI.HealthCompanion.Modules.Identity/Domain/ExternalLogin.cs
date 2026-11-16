using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Domain;

public static class ExternalProviders
{
    public const string Google = "Google";
}

/// <summary>
/// A sign-in identity at an outside provider (today only Google) linked to one account. The provider's stable subject id
/// (Google's <c>sub</c>) identifies the person, never the e-mail address, which the person can change at the provider.
/// </summary>
public sealed class ExternalLogin : Entity
{
    private ExternalLogin() { }

    public ExternalLogin(Guid userId, string provider, string subject, string? email, DateTime createdAtUtc)
    {
        UserId = userId;
        Provider = provider;
        Subject = subject;
        Email = email;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid UserId { get; private set; }
    public string Provider { get; private set; } = null!;
    public string Subject { get; private set; } = null!;

    /// <summary>The e-mail address the provider reported when the link was made (informational; not used to find the account later).</summary>
    public string? Email { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
}
