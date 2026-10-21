using FluentValidation;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

public sealed record SendMessageRequest(Guid? ConversationId, string Message, Guid? ClientMessageId);

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public const int MaxMessageLength = 4000;

    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().Must(m => !string.IsNullOrWhiteSpace(m))
            .WithMessage("The message must not be blank.")
            .MaximumLength(MaxMessageLength);
    }
}

public sealed record ConversationSummaryDto(Guid Id, string Title, DateTime LastMessageAtUtc);

public sealed record ChatMessageDto(
    Guid Id, int Sequence, string Role, string Content, string Status, bool IsSafetyResponse, DateTime CreatedAtUtc);

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

    public sealed record Done(Guid MessageId) : ChatStreamEvent(Names.Done);

    public sealed record Error(string Code, string Message, Guid? MessageId) : ChatStreamEvent(Names.Error);

    public static class Names
    {
        public const string Conversation = "conversation";
        public const string Delta = "delta";
        public const string Safety = "safety";
        public const string Done = "done";
        public const string Error = "error";
    }
}

internal static class ChatMessageMapping
{
    public static ChatMessageDto ToDto(this ConversationMessage m) =>
        new(m.Id, m.Sequence, m.Role.ToString(), m.Content, m.Status.ToString(), m.IsSafetyResponse, m.CreatedAtUtc);
}
