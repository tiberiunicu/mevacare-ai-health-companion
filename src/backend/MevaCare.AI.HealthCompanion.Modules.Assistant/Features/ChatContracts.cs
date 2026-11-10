using FluentValidation;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

/// <param name="ClientTime">The user's current local time with UTC offset; lets the assistant know today's date. Defaults to UTC.</param>
public sealed record SendMessageRequest(Guid? ConversationId, string Message, Guid? ClientMessageId, DateTimeOffset? ClientTime = null);

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public const int MaxMessageLength = 4000;

    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().Must(m => !string.IsNullOrWhiteSpace(m))
            .WithMessage("The message must not be blank.")
            .MaximumLength(MaxMessageLength);
        RuleFor(x => x.ClientTime).Must(t => t is null || Math.Abs((t.Value - DateTimeOffset.UtcNow).TotalHours) <= 26)
            .WithMessage("The client time is implausible.");
    }
}

public sealed record ConversationSummaryDto(Guid Id, string Title, DateTime LastMessageAtUtc);

public sealed record ChatMessageDto(
    Guid Id, int Sequence, string Role, string Content, string Status, bool IsSafetyResponse, DateTime CreatedAtUtc,
    IReadOnlyList<MevaCare.AI.HealthCompanion.Contracts.Knowledge.KnowledgeCitation> Citations);

/// <summary>
/// Events streamed to the browser over Server-Sent Events. The SSE event name is <see cref="Names"/>.
/// <c>conversation</c> comes first, then zero or more <c>delta</c>, then exactly one of <c>done</c> or <c>error</c>.
/// </summary>
public abstract record ChatStreamEvent([property: System.Text.Json.Serialization.JsonIgnore] string EventName)
{
    public sealed record Conversation(Guid ConversationId, string Title) : ChatStreamEvent(Names.Conversation);

    public sealed record Delta(string Text) : ChatStreamEvent(Names.Delta);

    /// <summary>The answer is a fixed safety message, not model output. Sent before its text.</summary>
    public sealed record Safety(string Category) : ChatStreamEvent(Names.Safety);

    /// <summary>A tool started or finished (Status: running, done, failed).</summary>
    public sealed record Tool(string Name, string Status) : ChatStreamEvent(Names.Tool);

    /// <summary>A meal prepared by the assistant that the user must confirm; nothing is saved yet.</summary>
    public sealed record Proposal(ProposalDto Card) : ChatStreamEvent(Names.Proposal);

    /// <summary>A change other than a meal (weight, targets, profile, meal deletion) that the user must confirm; nothing is applied yet.</summary>
    public sealed record Action(ActionProposalDto Card) : ChatStreamEvent(Names.Action);

    /// <summary>The approved sources used for this answer (cited ones first flagged). Sent before <c>done</c>.</summary>
    public sealed record Citations(IReadOnlyList<MevaCare.AI.HealthCompanion.Contracts.Knowledge.KnowledgeCitation> Items) : ChatStreamEvent(Names.Citations);

    public sealed record Done(Guid MessageId) : ChatStreamEvent(Names.Done);

    public sealed record Error(string Code, string Message, Guid? MessageId) : ChatStreamEvent(Names.Error);

    public static class Names
    {
        public const string Conversation = "conversation";
        public const string Delta = "delta";
        public const string Safety = "safety";
        public const string Citations = "citations";
        public const string Tool = "tool";
        public const string Proposal = "proposal";
        public const string Action = "action";
        public const string Done = "done";
        public const string Error = "error";
    }
}

internal static class ChatMessageMapping
{
    public static ChatMessageDto ToDto(this ConversationMessage m) =>
        new(m.Id, m.Sequence, m.Role.ToString(), m.Content, m.Status.ToString(), m.IsSafetyResponse, m.CreatedAtUtc, CitationJson.Parse(m.CitationsJson));
}

internal static class CitationJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string? Serialize(IReadOnlyList<MevaCare.AI.HealthCompanion.Contracts.Knowledge.KnowledgeCitation> citations) =>
        citations.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(citations, Options);

    public static IReadOnlyList<MevaCare.AI.HealthCompanion.Contracts.Knowledge.KnowledgeCitation> Parse(string? json) =>
        string.IsNullOrEmpty(json)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<MevaCare.AI.HealthCompanion.Contracts.Knowledge.KnowledgeCitation>>(json, Options) ?? [];
}
