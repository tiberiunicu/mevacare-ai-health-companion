using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>
/// A scriptable fake <see cref="IChatClient"/>. Tests decide what the "model" streams (or how it fails) and can
/// inspect exactly which messages the application sent to it.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private Func<IEnumerable<ChatResponseUpdate>> _script = () => throw new InvalidOperationException("No chat script configured for this test.");

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public ChatOptions? LastOptions { get; private set; }

    public void Reset()
    {
        Calls.Clear();
        LastOptions = null;
        _script = () => throw new InvalidOperationException("No chat script configured for this test.");
    }

    /// <summary>The model streams these chunks and finishes normally.</summary>
    public void Reply(params string[] chunks) =>
        _script = () => chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c));

    /// <summary>The model fails before producing anything.</summary>
    public void Fail(Exception exception) => _script = () => Throwing(exception, []);

    /// <summary>The model streams some chunks and then the connection breaks.</summary>
    public void ReplyThenFail(Exception exception, params string[] chunks) => _script = () => Throwing(exception, chunks);

    /// <summary>The provider answers successfully but with no text at all (malformed/empty response).</summary>
    public void ReplyNothing() => _script = () => [new ChatResponseUpdate(ChatRole.Assistant, (string?)null)];

    private static IEnumerable<ChatResponseUpdate> Throwing(Exception exception, string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        throw exception;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        LastOptions = options;
        await Task.Yield();
        foreach (var update in _script())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
