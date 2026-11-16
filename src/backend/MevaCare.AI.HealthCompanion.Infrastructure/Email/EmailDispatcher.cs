using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MevaCare.AI.HealthCompanion.Infrastructure.Email;

/// <summary>Takes messages from the queue and delivers them, retrying with a growing pause. A message that still fails is dropped and logged without its content.</summary>
public sealed class EmailDispatcher(EmailQueue queue, IEmailTransport transport, ILogger<EmailDispatcher> logger) : BackgroundService
{
    /// <summary>Pauses before the second and third attempt; tests shorten them.</summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await DeliverAsync(message, stoppingToken);
        }
    }

    public async Task DeliverAsync(EmailMessage message, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await transport.SendAsync(message, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= RetryDelays.Length)
                {
                    logger.LogError(ex, "An e-mail could not be delivered after {Attempts} attempts and was dropped.", attempt + 1);
                    return;
                }

                logger.LogWarning(ex, "Sending an e-mail failed (attempt {Attempt}); trying again.", attempt + 1);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }
    }
}
