using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Features;

internal static class MealMapping
{
    public static MealDto ToDto(this Meal meal)
    {
        var items = meal.Items
            .Select(i => new MealItemDto(
                i.Id, i.Name, i.QuantityGrams, i.Preparation.ToString(), i.IsEstimated, i.SourceLabel, i.FoodId, i.Nutrients))
            .ToList();
        var totals = NutrientCalculator.Sum(meal.Items.Select(i => (i.Nutrients, i.IsEstimated)));
        return new MealDto(
            meal.Id, meal.Type.ToString(), meal.EatenAtUtc, meal.LocalDate, meal.UtcOffsetMinutes, meal.Notes, meal.Origin.ToString(),
            items, totals);
    }
}

/// <summary>
/// Creates, edits and deletes meals. Every query includes the caller's user id; nutrient values of database foods are
/// always calculated here from the verified data, never taken from the request or from an AI model.
/// </summary>
public sealed class MealService(
    AppDbContext db, IConsentService consents, IMealProposalGate proposals, IAuditLog audit, TimeProvider clock)
{
    public async Task<SaveMealResult> CreateAsync(Guid userId, SaveMealRequest request, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);

        if (request.ProposalId is { } proposalId)
        {
            var gate = await proposals.BeginCommitAsync(userId, proposalId, ct);
            if (gate is { State: ProposalGateState.AlreadyCommitted, MealId: { } existingId })
            {
                // A retry or double click: return the meal that was already created instead of creating another.
                return new SaveMealResult(await LoadAsync(userId, existingId, ct), Created: false);
            }
        }

        var (eatenAtUtc, localDate, offset) = ResolveTime(request.EatenAt);
        var type = request.Type ?? InferType(request.EatenAt?.DateTime ?? eatenAtUtc);
        var origin = request.ProposalId is null ? MealOrigin.Manual : MealOrigin.Assistant;
        var meal = new Meal(userId, type, eatenAtUtc, localDate, offset, request.Notes?.Trim(), origin, request.ProposalId);
        foreach (var item in await BuildItemsAsync(request.Items, ct))
        {
            meal.AddItem(item);
        }

        db.Add(meal);
        audit.Record("meal.created", userId, origin.ToString());
        if (request.ProposalId is { } id)
        {
            proposals.MarkCommitted(id, meal.Id);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (request.ProposalId is not null
            && ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        {
            // Two confirmations raced; the unique index on SourceProposalId let exactly one win.
            db.ChangeTracker.Clear();
            var winner = await db.Set<Meal>().AsNoTracking().Include(m => m.Items)
                .SingleAsync(m => m.UserId == userId && m.SourceProposalId == request.ProposalId, ct);
            return new SaveMealResult(winner.ToDto(), Created: false);
        }

        return new SaveMealResult(meal.ToDto(), Created: true);
    }

    public async Task<MealDto> UpdateAsync(Guid userId, Guid mealId, SaveMealRequest request, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var meal = await db.Set<Meal>().Include(m => m.Items).SingleOrDefaultAsync(m => m.Id == mealId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Meal not found.");

        var (eatenAtUtc, localDate, offset) = ResolveTime(request.EatenAt ?? new DateTimeOffset(meal.EatenAtUtc, TimeSpan.Zero).ToOffset(TimeSpan.FromMinutes(meal.UtcOffsetMinutes)));
        meal.Update(request.Type ?? meal.Type, eatenAtUtc, localDate, offset, request.Notes?.Trim());

        // Replacing items: removed ones are deleted (orphans), new ones inserted. Nutrient snapshots are recalculated.
        var newItems = await BuildItemsAsync(request.Items, ct);
        foreach (var old in meal.Items.ToList())
        {
            db.Remove(old);
        }

        meal.ReplaceItems(newItems);
        foreach (var item in newItems)
        {
            db.Entry(item).State = EntityState.Added;
        }

        audit.Record("meal.updated", userId);
        await db.SaveChangesAsync(ct);
        return meal.ToDto();
    }

    public async Task DeleteAsync(Guid userId, Guid mealId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var meal = await db.Set<Meal>().SingleOrDefaultAsync(m => m.Id == mealId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Meal not found.");
        db.Remove(meal);
        audit.Record("meal.deleted", userId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<MealDto> LoadAsync(Guid userId, Guid mealId, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var meal = await db.Set<Meal>().AsNoTracking().Include(m => m.Items).SingleOrDefaultAsync(m => m.Id == mealId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Meal not found.");
        return meal.ToDto();
    }

    private async Task<List<MealItem>> BuildItemsAsync(IReadOnlyList<MealItemInput> inputs, CancellationToken ct)
    {
        var foodIds = inputs.Where(i => i.FoodId is not null).Select(i => i.FoodId!.Value).Distinct().ToList();
        var foods = await db.Set<Food>().AsNoTracking().Include(f => f.Nutrients)
            .Where(f => foodIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

        var items = new List<MealItem>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input.FoodId is { } foodId)
            {
                if (!foods.TryGetValue(foodId, out var food))
                {
                    throw new BadRequestException("unknown_food", "One of the foods does not exist in the food database.");
                }

                // The food's own name and raw/cooked state are authoritative; the client only chooses which food and how many grams.
                items.Add(new MealItem(
                    food.Id, food.Description, input.QuantityGrams, food.Preparation, isEstimated: false, food.SourceLabel,
                    NutrientCalculator.ForQuantity(food, input.QuantityGrams)));
            }
            else
            {
                var e = input.Estimated!;
                items.Add(new MealItem(
                    null, input.Name.Trim(), input.QuantityGrams, input.Preparation, isEstimated: true, "Estimated by user",
                    new NutrientValues(e.Calories, e.ProteinG, e.CarbohydratesG, e.FatG, e.FiberG)));
            }
        }

        return items;
    }

    private (DateTime EatenAtUtc, DateOnly LocalDate, int OffsetMinutes) ResolveTime(DateTimeOffset? eatenAt)
    {
        var value = eatenAt ?? clock.GetUtcNow();
        return (value.UtcDateTime, DateOnly.FromDateTime(value.DateTime), (int)value.Offset.TotalMinutes);
    }

    public static MealType InferType(DateTime localTime) => localTime.Hour switch
    {
        >= 5 and < 11 => MealType.Breakfast,
        >= 11 and < 16 => MealType.Lunch,
        >= 17 and < 22 => MealType.Dinner,
        _ => MealType.Snack,
    };
}
