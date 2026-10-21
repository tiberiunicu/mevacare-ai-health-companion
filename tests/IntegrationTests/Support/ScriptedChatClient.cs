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
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, IEnumerable<ChatResponseUpdate>>> _queue = new();

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public ChatOptions? LastOptions { get; private set; }

    /// <summary>Scripts the NEXT model call (use several in a row for tool-calling flows: tool call, extraction, final answer).</summary>
    public void Enqueue(Func<IReadOnlyList<ChatMessage>, IEnumerable<ChatResponseUpdate>> script) => _queue.Enqueue(script);

    public void EnqueueToolCall(string tool, object arguments, string callId = "call-1") =>
        Enqueue(_ => [new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, tool, ToDictionary(arguments))])]);

    public void EnqueueText(params string[] chunks) =>
        Enqueue(_ => chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c)));

    private static Dictionary<string, object?> ToDictionary(object arguments) =>
        System.Text.Json.JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

    public void Reset()
    {
        _queue.Clear();
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
        var updates = _queue.Count > 0 ? _queue.Dequeue()(Calls[^1]) : _script();
        foreach (var update in updates)
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
