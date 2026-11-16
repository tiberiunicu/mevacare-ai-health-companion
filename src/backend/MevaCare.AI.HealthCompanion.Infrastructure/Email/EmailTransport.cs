using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Email;

/// <summary>Delivers one message. A seam so the dispatcher can be tested without a mail server.</summary>
public interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class SmtpEmailTransport(IOptions<EmailOptions> options) : IEmailTransport
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var o = options.Value;
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.TextBody };

        using var client = new SmtpClient { Timeout = 20_000 };
        await client.ConnectAsync(o.Host, o.Port, o.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
        if (!string.IsNullOrEmpty(o.Username))
        {
            await client.AuthenticateAsync(o.Username, o.Password, ct);
        }

        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}

/// <summary>Used when no mail server is configured. Logs that a message was dropped, never its recipient or content (personal data, one-time links).</summary>
public sealed class LoggingEmailTransport(ILogger<LoggingEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogWarning("An e-mail was not sent because no mail server is configured (Email:Host).");
        return Task.CompletedTask;
    }
}
