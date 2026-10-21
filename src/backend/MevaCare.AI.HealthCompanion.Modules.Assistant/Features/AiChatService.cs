using System.Runtime.CompilerServices;
using System.Text;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Knowledge;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Safety;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

/// <summary>
/// A chat turn that has been validated and persisted but not yet answered. Splitting the work in two steps lets the
/// endpoint report every pre-flight problem (consent, validation, foreign conversation, duplicate) as a normal HTTP
/// error BEFORE the streaming response starts.
/// </summary>
public sealed class ChatTurn
{
    public required Guid UserId { get; init; }
    public required Conversation Conversation { get; init; }
    public required string UserText { get; init; }
    public required IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> History { get; init; }
    public required int NextSequence { get; init; }
    public required DateTimeOffset UserNow { get; init; }
    public EmergencyAssessment? Emergency { get; init; }
}

/// <summary>
/// The orchestrating assistant. One class, one LLM, no agents: it loads the authorised conversation context,
/// applies the deterministic safety screen, calls the provider through <see cref="IChatClient"/> (with the per-turn
/// tools wrapped in function invocation), streams the answer and persists every message, also when the provider fails
/// or the user disconnects.
/// </summary>
public sealed class AiChatService(
    AppDbContext db,
    IConsentService consents,
    IChatClient chat,
    INutritionAssistantApi nutrition,
    IHealthProfileReader profiles,
    MealParser mealParser,
    IOptions<AiOptions> options,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<AiChatService> logger,
    IKnowledgeSearch? knowledge = null)
{
    private readonly AiOptions _options = options.Value;

    public async Task<ChatTurn> PrepareTurnAsync(Guid userId, SendMessageRequest request, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.AiProcessing, ct);

        var text = request.Message.Trim();
        var now = clock.GetUtcNow().UtcDateTime;
        Conversation conversation;
        var history = new List<Microsoft.Extensions.AI.ChatMessage>();
        var nextSequence = 1;

        if (request.ConversationId is { } conversationId)
        {
            // The user id is part of the predicate: someone else's conversation is simply "not found".
            conversation = await db.Set<Conversation>().SingleOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
                ?? throw new NotFoundException("Conversation not found.");

            if (request.ClientMessageId is { } clientId
                && await db.Set<ConversationMessage>().AnyAsync(m => m.ConversationId == conversationId && m.ClientMessageId == clientId, ct))
            {
                throw new ConflictException("duplicate_message", "This message was already sent.");
            }

            var previous = await db.Set<ConversationMessage>().AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.Sequence)
                .Take(_options.HistoryMessageLimit)
                .ToListAsync(ct);
            nextSequence = (previous.Count == 0 ? 0 : previous[0].Sequence) + 1;
            history = BuildHistory(previous);
        }
        else
        {
            conversation = new Conversation(userId, TitleFrom(text));
            db.Add(conversation);
        }

        conversation.AddUserMessage(text, request.ClientMessageId, nextSequence, now);
        var emergency = EmergencyDetector.Assess(text);
        audit.Record(emergency is null ? "assistant.message_sent" : "assistant.safety_override", userId, emergency?.Category.ToString());

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Unique (conversation, sequence) or (conversation, client id): a concurrent or retried send.
            throw new ConflictException("duplicate_message", "This message was already sent.");
        }

        return new ChatTurn
        {
            UserId = userId,
            Conversation = conversation,
            UserText = text,
            History = history,
            NextSequence = nextSequence + 1,
            UserNow = request.ClientTime ?? clock.GetUtcNow(),
            Emergency = emergency,
        };
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatTurn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ChatStreamEvent.Conversation(turn.Conversation.Id, turn.Conversation.Title);

        if (turn.Emergency is { } emergency)
        {
            // Fixed answer, no model call: the guarantee must not depend on an LLM.
            var safetyMessage = await PersistAssistantAsync(turn, emergency.Response, MessageStatus.Complete, isSafety: true, model: null);
            yield return new ChatStreamEvent.Safety(emergency.Category.ToString());
            yield return new ChatStreamEvent.Delta(emergency.Response);
            yield return new ChatStreamEvent.Done(safetyMessage.Id);
            yield break;
        }

        var prompt = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(ChatRole.System, SystemPrompts.Build(_options.ToolsEnabled, turn.UserNow, knowledge is not null)),
        };
        prompt.AddRange(turn.History);
        prompt.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, turn.UserText));

        var chatOptions = new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens, Temperature = _options.Temperature };
        IChatClient client = chat;
        TurnTools? tools = null;
        if (_options.ToolsEnabled)
        {
            // The tools are bound to THIS user and conversation; the model never supplies a user id.
            tools = new TurnTools(turn.UserId, turn.Conversation.Id, turn.NextSequence, nutrition, profiles, mealParser, db, clock, logger, knowledge);
            chatOptions.Tools = [.. tools.Functions];
            client = new FunctionInvokingChatClient(chat)
            {
                MaximumIterationsPerRequest = _options.MaxToolIterations,
                AllowConcurrentInvocation = false,
                IncludeDetailedErrors = false,
            };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        var answer = new StringBuilder();
        var model = ChatClientFactory.ModelOf(_options);
        var persisted = false;
        var started = TimeProvider.System.GetTimestamp();
        var callNames = new Dictionary<string, string>();

        try
        {
            string? failureCode = null;
            await using var updates = client.GetStreamingResponseAsync(prompt, chatOptions, timeout.Token).GetAsyncEnumerator(timeout.Token);

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await updates.MoveNextAsync();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break; // the user stopped or disconnected; handled in finally as "Interrupted"
                }
                catch (OperationCanceledException)
                {
                    failureCode = "ai_timeout";
                    break;
                }
                catch (Exception ex)
                {
                    // Provider details (URLs, keys, status bodies) must never reach the client or the log message.
                    logger.LogWarning("AI provider call failed: {ExceptionType}", ex.GetType().Name);
                    failureCode = "ai_unavailable";
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                foreach (var content in updates.Current.Contents)
                {
                    if (content is FunctionCallContent call)
                    {
                        callNames[call.CallId] = call.Name;
                        yield return new ChatStreamEvent.Tool(call.Name, "running");
                    }
                    else if (content is FunctionResultContent result)
                    {
                        yield return new ChatStreamEvent.Tool(callNames.GetValueOrDefault(result.CallId, "tool"), result.Exception is null ? "done" : "failed");
                    }
                }

                while (tools is { PendingEvents.Count: > 0 })
                {
                    yield return tools.PendingEvents.Dequeue();
                }

                var piece = updates.Current.Text;
                if (string.IsNullOrEmpty(piece))
                {
                    continue;
                }

                answer.Append(piece);
                yield return new ChatStreamEvent.Delta(piece);
            }

            if (ct.IsCancellationRequested)
            {
                yield break; // finally block persists the partial answer
            }

            if (failureCode is null && answer.Length == 0 && tools is { ProposalCount: > 0 })
            {
                // The model produced only a tool call. The card is on screen, so give a short deterministic sentence.
                const string fallback = "I prepared this meal from what you wrote. Please check the items and confirm it below.";
                answer.Append(fallback);
                yield return new ChatStreamEvent.Delta(fallback);
            }

            if (failureCode is null && answer.Length == 0)
            {
                failureCode = "ai_empty_response";
            }

            var status = failureCode is null ? MessageStatus.Complete : MessageStatus.Failed;
            IReadOnlyList<KnowledgeCitation> citations = [];
            if (tools is not null && failureCode is null)
            {
                citations = tools.BuildCitations(answer.ToString(), out var unknownMarkers);
                if (unknownMarkers > 0)
                {
                    logger.LogWarning("The answer cited {Count} source number(s) that were never retrieved", unknownMarkers);
                }
            }

            var stored = await PersistAssistantAsync(turn, answer.ToString(), status, isSafety: false, model, CitationJson.Serialize(citations));
            persisted = true;
            if (citations.Count > 0)
            {
                yield return new ChatStreamEvent.Citations(citations);
            }

            logger.LogInformation(
                "Chat turn finished: status {Status}, {Characters} characters, {Tools} tool calls, {ElapsedMs} ms",
                status, answer.Length, callNames.Count, TimeProvider.System.GetElapsedTime(started).TotalMilliseconds);

            yield return failureCode is null
                ? new ChatStreamEvent.Done(stored.Id)
                : new ChatStreamEvent.Error(failureCode, ErrorText(failureCode), answer.Length > 0 ? stored.Id : null);
        }
        finally
        {
            if (!persisted && turn.Emergency is null)
            {
                await PersistAssistantAsync(turn, answer.ToString(), MessageStatus.Interrupted, isSafety: false, model);
            }
        }
    }

    private async Task<ConversationMessage> PersistAssistantAsync(
        ChatTurn turn, string content, MessageStatus status, bool isSafety, string? model, string? citationsJson = null)
    {
        // Persisting must survive a cancelled request: the user's message is already stored, the answer must be too.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = isSafety ? null : _options.Provider.ToString();
        var message = turn.Conversation.AddAssistantMessage(
            content, status, isSafety, provider, model, turn.NextSequence, clock.GetUtcNow().UtcDateTime, citationsJson);
        await db.SaveChangesAsync(cts.Token);
        return message;
    }

    private List<Microsoft.Extensions.AI.ChatMessage> BuildHistory(List<ConversationMessage> newestFirst)
    {
        var selected = new List<ConversationMessage>();
        var budget = _options.HistoryCharacterBudget;
        foreach (var message in newestFirst)
        {
            // Failed answers and empty partials add noise, not context.
            if (message.Role == MessageRole.Assistant && (message.Status == MessageStatus.Failed || message.Content.Length == 0))
            {
                continue;
            }

            if (budget - message.Content.Length < 0 && selected.Count > 0)
            {
                break;
            }

            budget -= message.Content.Length;
            selected.Add(message);
        }

        selected.Reverse();
        return selected
            .Select(m => new Microsoft.Extensions.AI.ChatMessage(
                m.Role == MessageRole.User ? ChatRole.User : ChatRole.Assistant, m.Content))
            .ToList();
    }

    private static string TitleFrom(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 60 ? collapsed : collapsed[..60].TrimEnd() + "…";
    }

    private static string ErrorText(string code) => code switch
    {
        "ai_timeout" => "The AI service took too long to answer.",
        "ai_empty_response" => "The AI service returned no answer.",
        _ => "The AI service is not available right now.",
    };
}
