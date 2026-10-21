using System.Text.Json;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

/// <summary>A meal prepared by the assistant, waiting for (or already past) the user's decision.</summary>
public sealed record ProposalDto(Guid Id, int AssistantMessageSequence, string State, Guid? MealId, MealProposalDto Proposal);

internal static class ProposalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ProposalDto? ToDto(ToolExecution execution) =>
        execution.ResultJson is null
            ? null
            : new ProposalDto(
                execution.Id,
                execution.AssistantMessageSequence,
                execution.Outcome.ToString(),
                execution.CommittedMealId,
                JsonSerializer.Deserialize<MealProposalDto>(execution.ResultJson, Options)!);
}

/// <summary>
/// Lets the Nutrition module create a meal from a proposal inside one transaction: it checks that the proposal is the
/// caller's and still open, and marks it committed together with the new meal. Confirming twice returns the same meal.
/// </summary>
public sealed class ProposalGate(AppDbContext db) : IMealProposalGate
{
    public async Task<ProposalGateResult> BeginCommitAsync(Guid userId, Guid proposalId, CancellationToken ct)
    {
        var execution = await db.Set<ToolExecution>()
            .SingleOrDefaultAsync(t => t.Id == proposalId && t.UserId == userId && t.ToolName == ToolExecution.PrepareMealTool, ct)
            ?? throw new NotFoundException("Proposal not found.");

        return execution.Outcome switch
        {
            ToolOutcome.Committed => new ProposalGateResult(ProposalGateState.AlreadyCommitted, execution.CommittedMealId),
            ToolOutcome.Proposed => new ProposalGateResult(ProposalGateState.Pending, null),
            _ => throw new ConflictException("proposal_not_open", "This proposal was dismissed and can no longer be confirmed."),
        };
    }

    public void MarkCommitted(Guid proposalId, Guid mealId) =>
        db.ChangeTracker.Entries<ToolExecution>().Single(e => e.Entity.Id == proposalId).Entity.MarkCommitted(mealId);
}

public sealed class ProposalService(AppDbContext db)
{
    public async Task<IReadOnlyList<ProposalDto>> ListAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        if (!await db.Set<Conversation>().AnyAsync(c => c.Id == conversationId && c.UserId == userId, ct))
        {
            throw new NotFoundException("Conversation not found.");
        }

        var rows = await db.Set<ToolExecution>().AsNoTracking()
            .Where(t => t.ConversationId == conversationId && t.UserId == userId && t.ToolName == ToolExecution.PrepareMealTool && t.ResultJson != null)
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        return rows.Select(ProposalJson.ToDto).OfType<ProposalDto>().ToList();
    }

    public async Task DismissAsync(Guid userId, Guid proposalId, CancellationToken ct)
    {
        var execution = await db.Set<ToolExecution>()
            .SingleOrDefaultAsync(t => t.Id == proposalId && t.UserId == userId && t.ToolName == ToolExecution.PrepareMealTool, ct)
            ?? throw new NotFoundException("Proposal not found.");

        if (execution.Outcome == ToolOutcome.Committed)
        {
            throw new ConflictException("proposal_committed", "This meal was already saved.");
        }

        execution.Dismiss();
        await db.SaveChangesAsync(ct);
    }
}
