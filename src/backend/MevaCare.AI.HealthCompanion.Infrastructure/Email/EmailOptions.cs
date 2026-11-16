using System.ComponentModel.DataAnnotations;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Email;

/// <summary>SMTP settings. Leave <see cref="Host"/> empty to switch e-mail off (messages are then only counted in the log, never shown).</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public string Host { get; init; } = "";

    [Range(1, 65535)] public int Port { get; init; } = 587;

    /// <summary>STARTTLS on port 587 (true) or implicit TLS on port 465 (false).</summary>
    public bool UseStartTls { get; init; } = true;

    public string Username { get; init; } = "";

    /// <summary>Keep it in user-secrets or an environment variable (Email__Password), never in a file of the repository.</summary>
    public string Password { get; init; } = "";

    /// <summary>The sender address. With Gmail this must be the signed-in account or one of its aliases.</summary>
    public string FromAddress { get; init; } = "";

    public string FromName { get; init; } = "MevaCare";

    public bool Enabled => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
