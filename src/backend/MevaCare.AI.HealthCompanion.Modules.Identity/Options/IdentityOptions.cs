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

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(3, 20)] public int MaxFailedLogins { get; init; } = 5;
    [Range(1, 1440)] public int LockoutMinutes { get; init; } = 15;

    /// <summary>Set to false only for plain-http local development.</summary>
    public bool RefreshCookieSecure { get; init; } = true;

    /// <summary>PBKDF2 iterations for new password hashes (OWASP guidance for PBKDF2-HMAC-SHA512 is 210,000).</summary>
    [Range(100_000, 5_000_000)] public int PasswordHashIterations { get; init; } = 210_000;
}
