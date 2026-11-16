using System.ComponentModel.DataAnnotations;

namespace MevaCare.AI.HealthCompanion.Modules.Identity.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required] public string Issuer { get; init; } = "mevacare-ai-health-companion";
    [Required] public string Audience { get; init; } = "mevacare-ai-web";

    /// <summary>HMAC signing key. Never committed: supply it via user-secrets or the Jwt__SigningKey environment variable.</summary>
    [Required, MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters. See README.md (Configuration).")]
    public string SigningKey { get; init; } = "";

    [Range(1, 120)] public int AccessTokenMinutes { get; init; } = 15;
    [Range(1, 90)] public int RefreshTokenDays { get; init; } = 14;
}

/// <summary>
/// "Sign in with Google". The browser obtains a signed Google ID token (Google Identity Services); the API verifies its signature,
/// issuer, audience and expiry. Only the OAuth client ID is needed, and it is public (it is sent to every browser), so there is no secret.
/// Leave <see cref="ClientId"/> empty to switch the feature off: the button is not shown and the endpoints answer 404.
/// </summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "Auth:Google";

    /// <summary>OAuth 2.0 client ID of type "Web application", e.g. 1234-abc.apps.googleusercontent.com.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>Where Google publishes its signing keys (OpenID Connect discovery document). Only tests change this.</summary>
    public string DiscoveryUrl { get; init; } = "https://accounts.google.com/.well-known/openid-configuration";

    public bool Enabled => !string.IsNullOrWhiteSpace(ClientId);
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(3, 20)] public int MaxFailedLogins { get; init; } = 5;
    [Range(1, 1440)] public int LockoutMinutes { get; init; } = 15;

    /// <summary>
    /// A rotated refresh token presented again within this window is rejected without revoking the session: it is
    /// almost always two browser tabs refreshing at once. Later reuse is treated as theft. Set 0 to be strict.
    /// </summary>
    [Range(0, 60)] public int RefreshReuseGraceSeconds { get; init; } = 10;

    /// <summary>Set to false only for plain-http local development.</summary>
    public bool RefreshCookieSecure { get; init; } = true;

    /// <summary>
    /// The public address of the web app, used to build the links in e-mails (never taken from the request, so a forged Host header
    /// cannot point a reset link at another site). Set it to https://your-domain in production.
    /// </summary>
    [Url] public string PublicBaseUrl { get; init; } = "http://localhost:5173";

    /// <summary>How long a password-reset link works.</summary>
    [Range(5, 1440)] public int PasswordResetMinutes { get; init; } = 30;

    /// <summary>PBKDF2 iterations for new password hashes (OWASP guidance for PBKDF2-HMAC-SHA512 is 210,000).</summary>
    [Range(100_000, 5_000_000)] public int PasswordHashIterations { get; init; } = 210_000;
}
