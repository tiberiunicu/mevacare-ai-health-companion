using System.Threading.Channels;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Email;

/// <summary>In-memory queue: a restart loses messages not yet delivered, which is acceptable for password-reset mail (the person asks again).</summary>
public sealed class EmailQueue(IOptions<EmailOptions> options) : IEmailQueue
{
    private readonly Channel<EmailMessage> _channel = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public bool IsConfigured => options.Value.Enabled;

    public void Enqueue(EmailMessage message) => _channel.Writer.TryWrite(message);

    internal ChannelReader<EmailMessage> Reader => _channel.Reader;
}
