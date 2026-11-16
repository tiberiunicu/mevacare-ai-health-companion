using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Infrastructure.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.UnitTests.Identity;

public sealed class EmailDispatcherTests
{
    private static readonly EmailMessage Message = new("someone@example.test", "Subject", "Body");

    private sealed class ScriptedTransport(int failures) : IEmailTransport
    {
        public int Attempts { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken ct)
        {
            Attempts++;
            return Attempts <= failures ? throw new IOException("mail server unreachable") : Task.CompletedTask;
        }
    }

    private static EmailDispatcher Dispatcher(ScriptedTransport transport) =>
        new(new EmailQueue(Options.Create(new EmailOptions())), transport, NullLogger<EmailDispatcher>.Instance)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero],
        };

    [Fact]
    public async Task A_message_that_goes_through_is_sent_once()
    {
        var transport = new ScriptedTransport(failures: 0);

        await Dispatcher(transport).DeliverAsync(Message, default);

        Assert.Equal(1, transport.Attempts);
    }

    [Fact]
    public async Task A_temporary_failure_is_retried_until_it_works()
    {
        var transport = new ScriptedTransport(failures: 2);

        await Dispatcher(transport).DeliverAsync(Message, default);

        Assert.Equal(3, transport.Attempts);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dropped_after_three_attempts_without_crashing_the_service()
    {
        var transport = new ScriptedTransport(failures: 100);

        await Dispatcher(transport).DeliverAsync(Message, default);

        Assert.Equal(3, transport.Attempts);
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("smtp.example.test", "", false)]
    [InlineData("", "me@example.test", false)]
    [InlineData("smtp.example.test", "me@example.test", true)]
    public void E_mail_counts_as_configured_only_with_a_host_and_a_sender(string host, string from, bool expected)
    {
        var queue = new EmailQueue(Options.Create(new EmailOptions { Host = host, FromAddress = from }));

        Assert.Equal(expected, queue.IsConfigured);
    }
}
