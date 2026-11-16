namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>A plain-text e-mail. Put nothing in it except what the recipient needs (for example a one-time link); never health data.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>
/// Sends e-mail without making the caller wait: <see cref="Enqueue"/> returns at once and a background service delivers the message
/// (with retries). That matters for security: a request that sends mail for an existing account must not take longer than one that
/// does not, or the response time would reveal which addresses have an account.
/// </summary>
public interface IEmailQueue
{
    /// <summary>False when no mail server is configured: features that depend on e-mail (password reset) switch themselves off.</summary>
    bool IsConfigured { get; }

    void Enqueue(EmailMessage message);
}
