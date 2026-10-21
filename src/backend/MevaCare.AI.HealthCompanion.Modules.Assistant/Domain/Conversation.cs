using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;

public enum MessageRole { User = 1, Assistant = 2 }

public enum MessageStatus
{
    Complete = 1,

    /// <summary>The provider failed (or returned nothing) before the answer was finished. Content may be partial.</summary>
    Failed = 2,

    /// <summary>The user stopped the answer or disconnected. Content is partial by design.</summary>
    Interrupted = 3,
}

public sealed class Conversation : AuditableEntity
{
    public const int MaxTitleLength = 120;

    private readonly List<ConversationMessage> _messages = [];

    private Conversation() { }

    public Conversation(Guid userId, string title)
    {
        UserId = userId;
        Title = title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }

    public Guid UserId { get; private set; }
    public string Title { get; private set; } = null!;
    public DateTime LastMessageAtUtc { get; private set; }
    public IReadOnlyCollection<ConversationMessage> Messages => _messages;

    public ConversationMessage AddUserMessage(string content, Guid? clientMessageId, int sequence, DateTime nowUtc)
    {
        var message = new ConversationMessage(Id, sequence, MessageRole.User, content, MessageStatus.Complete, false, clientMessageId, null, null, nowUtc);
        Attach(message, nowUtc);
        return message;
    }

    public ConversationMessage AddAssistantMessage(
        string content, MessageStatus status, bool isSafetyResponse, string? provider, string? model, int sequence, DateTime nowUtc)
    {
        var message = new ConversationMessage(Id, sequence, MessageRole.Assistant, content, status, isSafetyResponse, null, provider, model, nowUtc);
        Attach(message, nowUtc);
        return message;
    }

    private void Attach(ConversationMessage message, DateTime nowUtc)
    {
        _messages.Add(message);
        LastMessageAtUtc = nowUtc;
    }
}

public sealed class ConversationMessage : Entity
{
    private ConversationMessage() { }

    internal ConversationMessage(
        Guid conversationId, int sequence, MessageRole role, string content, MessageStatus status, bool isSafetyResponse,
        Guid? clientMessageId, string? provider, string? model, DateTime createdAtUtc)
    {
        ConversationId = conversationId;
        Sequence = sequence;
        Role = role;
        Content = content;
        Status = status;
        IsSafetyResponse = isSafetyResponse;
        ClientMessageId = clientMessageId;
        Provider = provider;
        Model = model;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid ConversationId { get; private set; }

    /// <summary>1-based position inside the conversation; the authoritative ordering.</summary>
    public int Sequence { get; private set; }

    public MessageRole Role { get; private set; }
    public string Content { get; private set; } = null!;
    public MessageStatus Status { get; private set; }

    /// <summary>True when the text is a fixed, pre-written safety answer rather than model output.</summary>
    public bool IsSafetyResponse { get; private set; }

    /// <summary>Client-generated id of a user message; makes retried sends idempotent.</summary>
    public Guid? ClientMessageId { get; private set; }

    public string? Provider { get; private set; }
    public string? Model { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
}
