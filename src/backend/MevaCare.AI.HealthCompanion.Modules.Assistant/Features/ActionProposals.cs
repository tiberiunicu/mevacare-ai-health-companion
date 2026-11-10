using System.Text.Json;
using MevaCare.AI.HealthCompanion.Contracts.Assistant;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Features;

/// <summary>
/// The changes the assistant can PREPARE besides meals. The kind is the name of the tool that prepared it. Nothing is applied until the
/// signed-in user confirms the card (<see cref="ActionService.ConfirmAsync"/>), and what is applied is exactly the payload stored when
/// the card was shown, never anything the model says afterwards.
/// </summary>
public static class ActionKinds
{
    public const string Weight = "ProposeWeight";
    public const string Targets = "ProposeNutritionTargets";
    public const string Profile = "ProposeProfileUpdate";
    public const string DeleteMeal = "ProposeMealDeletion";

    public static readonly IReadOnlyList<string> All = [Weight, Targets, Profile, DeleteMeal];
}

public sealed record WeightPayload(decimal Kilograms, DateTimeOffset MeasuredAt, DateOnly LocalDate);

public sealed record TargetsPayload(NutritionTargetDto Before, NutritionTargetDto After);

public sealed record ProfilePayload(ProfileBasicsDto Before, ProfileBasicsDto After);

public sealed record DeleteMealPayload(Guid MealId, string MealType, DateOnly LocalDate, decimal Calories, IReadOnlyList<string> Items);

/// <param name="Kind">One of <see cref="ActionKinds"/>.</param>
/// <param name="State">Proposed, Committed (applied) or Dismissed.</param>
public sealed record ActionProposalDto(Guid Id, int AssistantMessageSequence, string State, string Kind, JsonElement Payload);

internal static class ActionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    public static ActionProposalDto? ToDto(ToolExecution execution)
    {
        if (execution.ResultJson is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(execution.ResultJson);
        return new ActionProposalDto(
            execution.Id, execution.AssistantMessageSequence, execution.Outcome.ToString(), execution.ToolName, document.RootElement.Clone());
    }
}

/// <summary>Lists, applies and dismisses the proposals prepared by <see cref="ActionKinds"/> tools.</summary>
public sealed class ActionService(
    AppDbContext db, INutritionAssistantApi nutrition, IProfileAssistantApi profile, IMeasurementsAssistantApi measurements)
{
    public async Task<IReadOnlyList<ActionProposalDto>> ListAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        if (!await db.Set<Conversation>().AnyAsync(c => c.Id == conversationId && c.UserId == userId, ct))
        {
            throw new NotFoundException("Conversation not found.");
        }

        var rows = await db.Set<ToolExecution>().AsNoTracking()
            .Where(t => t.ConversationId == conversationId && t.UserId == userId && ActionKinds.All.Contains(t.ToolName) && t.ResultJson != null)
            .OrderBy(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        return rows.Select(ActionJson.ToDto).OfType<ActionProposalDto>().ToList();
    }

    /// <summary>
    /// Applies the stored payload once. The proposal is claimed with one conditional UPDATE (Proposed to Committed), so a double click or
    /// two parallel requests apply it exactly once; confirming again returns the same card. If applying fails the claim is released.
    /// </summary>
    public async Task<ActionProposalDto> ConfirmAsync(Guid userId, Guid actionId, CancellationToken ct)
    {
        var execution = await LoadAsync(userId, actionId, ct);
        if (execution.Outcome == ToolOutcome.Committed)
        {
            return ActionJson.ToDto(execution)!;
        }

        if (execution.Outcome != ToolOutcome.Proposed)
        {
            throw new ConflictException("proposal_not_open", "This proposal was dismissed and can no longer be confirmed.");
        }

        var claimed = await db.Set<ToolExecution>()
            .Where(t => t.Id == actionId && t.UserId == userId && t.Outcome == ToolOutcome.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Outcome, ToolOutcome.Committed), ct);
        if (claimed == 0)
        {
            var current = await LoadAsync(userId, actionId, ct);
            return current.Outcome == ToolOutcome.Committed
                ? ActionJson.ToDto(current)!
                : throw new ConflictException("proposal_not_open", "This proposal was dismissed and can no longer be confirmed.");
        }

        try
        {
            await ApplyAsync(userId, execution.ToolName, execution.ResultJson!, ct);
        }
        catch
        {
            // Nothing was applied (or the request was cancelled): let the user try again or dismiss the card.
            await db.Set<ToolExecution>().Where(t => t.Id == actionId && t.Outcome == ToolOutcome.Committed)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Outcome, ToolOutcome.Proposed), CancellationToken.None);
            throw;
        }

        return ActionJson.ToDto(await LoadAsync(userId, actionId, ct))!;
    }

    public async Task DismissAsync(Guid userId, Guid actionId, CancellationToken ct)
    {
        var execution = await LoadAsync(userId, actionId, ct);
        if (execution.Outcome == ToolOutcome.Committed)
        {
            throw new ConflictException("proposal_committed", "This change was already applied.");
        }

        var updated = await db.Set<ToolExecution>()
            .Where(t => t.Id == actionId && t.UserId == userId && t.Outcome == ToolOutcome.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Outcome, ToolOutcome.Dismissed), ct);
        if (updated == 0 && (await LoadAsync(userId, actionId, ct)).Outcome == ToolOutcome.Committed)
        {
            throw new ConflictException("proposal_committed", "This change was already applied.");
        }
    }

    private async Task<ToolExecution> LoadAsync(Guid userId, Guid actionId, CancellationToken ct) =>
        await db.Set<ToolExecution>().AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == actionId && t.UserId == userId && ActionKinds.All.Contains(t.ToolName), ct)
        ?? throw new NotFoundException("Proposal not found.");

    private async Task ApplyAsync(Guid userId, string kind, string payloadJson, CancellationToken ct)
    {
        switch (kind)
        {
            case ActionKinds.Weight:
            {
                var payload = JsonSerializer.Deserialize<WeightPayload>(payloadJson, ActionJson.Options)!;
                await measurements.RecordWeightAsync(userId, payload.Kilograms, payload.MeasuredAt, ct);
                break;
            }

            case ActionKinds.Targets:
            {
                var payload = JsonSerializer.Deserialize<TargetsPayload>(payloadJson, ActionJson.Options)!;
                await nutrition.SetTargetsAsync(userId, payload.After, ct);
                break;
            }

            case ActionKinds.Profile:
            {
                var payload = JsonSerializer.Deserialize<ProfilePayload>(payloadJson, ActionJson.Options)!;
                await profile.SetBasicsAsync(userId, payload.After.HeightCm, payload.After.ActivityLevel, ct);
                break;
            }

            case ActionKinds.DeleteMeal:
            {
                var payload = JsonSerializer.Deserialize<DeleteMealPayload>(payloadJson, ActionJson.Options)!;
                try
                {
                    await nutrition.DeleteMealAsync(userId, payload.MealId, ct);
                }
                catch (NotFoundException)
                {
                    // Already gone (deleted on another page meanwhile): the wanted state is reached.
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unknown action kind '{kind}'.");
        }
    }
}
