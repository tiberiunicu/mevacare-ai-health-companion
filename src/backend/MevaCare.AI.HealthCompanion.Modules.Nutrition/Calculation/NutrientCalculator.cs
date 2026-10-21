using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Modules.Nutrition.Domain;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.Calculation;

/// <summary>
/// The only place where nutrient arithmetic happens. Nutrition numbers are always computed by this deterministic code
/// from verified per-100-g data; an LLM is never asked to calculate or to supply them.
/// </summary>
public static class NutrientCalculator
{
    public const decimal MaxItemGrams = 10_000m;

    /// <summary>Values for <paramref name="grams"/> of a food. Nutrients missing from the food stay null (unknown).</summary>
    public static NutrientValues ForQuantity(IReadOnlyDictionary<NutrientType, decimal> per100g, decimal grams)
    {
        decimal? Scale(NutrientType type) =>
            per100g.TryGetValue(type, out var amount) ? Round(amount * grams / 100m) : null;

        return new NutrientValues(
            Scale(NutrientType.Calories),
            Scale(NutrientType.Protein),
            Scale(NutrientType.Carbohydrates),
            Scale(NutrientType.Fat),
            Scale(NutrientType.Fiber));
    }

    public static NutrientValues ForQuantity(Food food, decimal grams) =>
        ForQuantity(food.Nutrients.ToDictionary(n => n.Type, n => n.AmountPer100g), grams);

    /// <summary>
    /// Adds up item values. Unknown values contribute nothing but raise <c>IncompleteData</c>, so a total is never
    /// presented as complete when it is not. Estimated (user-entered) items raise <c>IncludesEstimates</c>.
    /// </summary>
    public static NutrientTotals Sum(IEnumerable<(NutrientValues Values, bool IsEstimated)> items)
    {
        decimal calories = 0, protein = 0, carbs = 0, fat = 0, fiber = 0;
        var incomplete = false;
        var estimated = false;

        foreach (var (values, isEstimated) in items)
        {
            calories += values.Calories ?? 0;
            protein += values.ProteinG ?? 0;
            carbs += values.CarbohydratesG ?? 0;
            fat += values.FatG ?? 0;
            fiber += values.FiberG ?? 0;
            incomplete |= values.Calories is null || values.ProteinG is null || values.CarbohydratesG is null
                || values.FatG is null || values.FiberG is null;
            estimated |= isEstimated;
        }

        return new NutrientTotals(Round1(calories), Round1(protein), Round1(carbs), Round1(fat), Round1(fiber), incomplete, estimated);
    }

    /// <summary>Target minus consumed (negative = exceeded). Null when there is no target for that nutrient.</summary>
    public static NutrientValues Remaining(NutritionTargetDto targets, NutrientTotals totals) => new(
        targets.Calories is { } c ? Round1(c - totals.Calories) : null,
        targets.ProteinG is { } p ? Round1(p - totals.ProteinG) : null,
        targets.CarbohydratesG is { } k ? Round1(k - totals.CarbohydratesG) : null,
        targets.FatG is { } f ? Round1(f - totals.FatG) : null,
        targets.FiberG is { } i ? Round1(i - totals.FiberG) : null);

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Round1(decimal value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}
