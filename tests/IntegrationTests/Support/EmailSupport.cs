using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>Stand-in for the mail queue: keeps every message so a test can read the reset link. No mail server is involved.</summary>
public sealed class CapturingEmailQueue : IEmailQueue
{
    private readonly object _gate = new();
    private readonly List<EmailMessage> _sent = [];

    public bool IsConfigured { get; set; } = true;

    public void Enqueue(EmailMessage message)
    {
        lock (_gate)
        {
            _sent.Add(message);
        }
    }

    public IReadOnlyList<EmailMessage> To(string address)
    {
        lock (_gate)
        {
            return _sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    /// <summary>The one-time token inside the newest message to the address that contains a reset link.</summary>
    public string? LatestResetToken(string address)
    {
        var body = To(address).LastOrDefault(m => m.TextBody.Contains("/reset-password#token=", StringComparison.Ordinal))?.TextBody;
        if (body is null)
        {
            return null;
        }

        var start = body.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length;
        var end = body.IndexOfAny(['\r', '\n', ' '], start);
        return body[start..(end < 0 ? body.Length : end)];
    }
}
