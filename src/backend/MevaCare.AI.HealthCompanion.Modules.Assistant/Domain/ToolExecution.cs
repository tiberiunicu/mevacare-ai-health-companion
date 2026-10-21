using MevaCare.AI.HealthCompanion.Contracts.Abstractions;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;

/// <summary>Read tools only look things up; write tools prepare a change that needs the user's explicit confirmation.</summary>
public enum ToolKind { Read = 1, Write = 2 }

public enum ToolOutcome
{
    Succeeded = 1,

    /// <summary>The tool ran but failed (provider/database/unexpected error).</summary>
    Failed = 2,

    /// <summary>The arguments were invalid or the call was not allowed (for example, missing consent).</summary>
    Rejected = 3,

    /// <summary>A write tool prepared a proposal; nothing was written yet.</summary>
    Proposed = 4,

    /// <summary>The user confirmed the proposal and the change was written.</summary>
    Committed = 5,

    Dismissed = 6,
}

/// <summary>
/// Audit record of every tool call the model made: which tool, with which (validated) arguments and what happened.
/// For a prepared meal it also stores the proposal the user is asked to confirm. UserId is a plain column (not a foreign
/// key) because the row is already deleted with its conversation, which cascades from the user.
/// </summary>
public sealed class ToolExecution : Entity
{
    public const string PrepareMealTool = "PrepareMeal";

    private ToolExecution() { }

    public ToolExecution(
        Guid conversationId, Guid userId, int assistantMessageSequence, string toolName, ToolKind kind, string argumentsJson,
        ToolOutcome outcome, string? resultJson, string? errorCode, int durationMs, DateTime createdAtUtc)
    {
        ConversationId = conversationId;
        UserId = userId;
        AssistantMessageSequence = assistantMessageSequence;
        ToolName = toolName;
        Kind = kind;
        ArgumentsJson = argumentsJson;
        Outcome = outcome;
        ResultJson = resultJson;
        ErrorCode = errorCode;
        DurationMs = durationMs;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid ConversationId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Sequence of the assistant message produced in the same turn; lets the UI show a proposal under that message.</summary>
    public int AssistantMessageSequence { get; private set; }

    public string ToolName { get; private set; } = null!;
    public ToolKind Kind { get; private set; }
    public string ArgumentsJson { get; private set; } = null!;
    public ToolOutcome Outcome { get; private set; }

    /// <summary>Only kept for proposals (the data the user confirms); other tool results are not stored.</summary>
    public string? ResultJson { get; private set; }

    public string? ErrorCode { get; private set; }
    public int DurationMs { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid? CommittedMealId { get; private set; }

    public void MarkCommitted(Guid mealId)
    {
        Outcome = ToolOutcome.Committed;
        CommittedMealId = mealId;
    }

    public void Dismiss() => Outcome = ToolOutcome.Dismissed;
}
