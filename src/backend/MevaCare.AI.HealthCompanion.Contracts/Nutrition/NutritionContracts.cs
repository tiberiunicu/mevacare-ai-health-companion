namespace MevaCare.AI.HealthCompanion.Contracts.Nutrition;

/// <summary>
/// The five tracked nutrients. A null value means "not reported by the food source" and is never treated as zero.
/// </summary>
public sealed record NutrientValues(decimal? Calories, decimal? ProteinG, decimal? CarbohydratesG, decimal? FatG, decimal? FiberG)
{
    public static readonly NutrientValues None = new(null, null, null, null, null);
}

/// <summary>Nutrient totals plus honesty flags: totals ignore unknown values, so they may be incomplete.</summary>
public sealed record NutrientTotals(
    decimal Calories, decimal ProteinG, decimal CarbohydratesG, decimal FatG, decimal FiberG,
    bool IncompleteData, bool IncludesEstimates);

public sealed record NutritionTargetDto(
    decimal? Calories, decimal? ProteinG, decimal? CarbohydratesG, decimal? FatG, decimal? FiberG);

public sealed record MealItemDto(
    Guid Id, string Name, decimal QuantityGrams, string Preparation, bool IsEstimated, string? SourceLabel, Guid? FoodId,
    NutrientValues Nutrients);

public sealed record MealDto(
    Guid Id, string MealType, DateTime EatenAtUtc, DateOnly LocalDate, int UtcOffsetMinutes, string? Notes, string Origin,
    IReadOnlyList<MealItemDto> Items, NutrientTotals Totals);

public sealed record DailyNutritionSummaryDto(
    DateOnly Date, NutrientTotals Totals, NutritionTargetDto Targets, NutrientValues Remaining, IReadOnlyList<MealDto> Meals);

public sealed record DayTotalsDto(DateOnly Date, NutrientTotals Totals, int MealCount);

public sealed record NutritionRangeSummaryDto(
    DateOnly From, DateOnly To, IReadOnlyList<DayTotalsDto> Days, int DaysWithData, NutrientValues AveragePerLoggedDay,
    NutritionTargetDto Targets);

// ---- Meal proposals (the AI prepares, the user confirms) ----

/// <param name="Brand">A brand the user named ("Hochland"), kept apart from the generic <paramref name="FoodName"/> so that the match can try the product first and fall back to the generic food.</param>
public sealed record ParsedFoodItem(string FoodName, decimal Quantity, string Unit, string Preparation, string? OriginalText, string? Brand = null);

/// <param name="LocalDate">The day the user said the meal was eaten ("yesterday"), in their calendar; null means today.</param>
public sealed record ParsedMeal(string? MealType, IReadOnlyList<ParsedFoodItem> Items, DateOnly? LocalDate = null);

public sealed record FoodCandidateDto(Guid FoodId, string Description, string Preparation, string? Category, string SourceLabel);

/// <summary>One line of a proposal. Status: Matched, Unmatched (no food found) or NeedsQuantity (unit cannot be converted).</summary>
public sealed record ProposedItemDto(
    string ClientItemId,
    string InputText,
    string Status,
    FoodCandidateDto? Food,
    IReadOnlyList<FoodCandidateDto> Alternatives,
    decimal? QuantityGrams,
    string Preparation,
    bool PreparationAssumed,
    bool AmbiguousMatch,
    NutrientValues Nutrients,
    string? Note);

/// <param name="LocalDate">The day the meal was eaten when the user named one; null means "now".</param>
public sealed record MealProposalDto(
    string? MealType, IReadOnlyList<ProposedItemDto> Items, NutrientTotals Totals, IReadOnlyList<string> Warnings, DateOnly? LocalDate = null);

/// <summary>Read/prepare operations the AI assistant may use. Every call is scoped to the given user id.</summary>
public interface INutritionAssistantApi
{
    Task<DailyNutritionSummaryDto> GetDailySummaryAsync(Guid userId, DateOnly date, CancellationToken ct);

    Task<NutritionRangeSummaryDto> GetRangeSummaryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Matches parsed foods against the food database and calculates nutrients. Writes nothing.</summary>
    Task<MealProposalDto> PrepareMealAsync(Guid userId, ParsedMeal meal, CancellationToken ct);
}

public enum ProposalGateState
{
    Pending = 1,
    AlreadyCommitted = 2,
}

public sealed record ProposalGateResult(ProposalGateState State, Guid? MealId);

/// <summary>
/// Implemented by the Assistant module (it owns the proposals). The Nutrition module calls it when a meal is created
/// from a proposal so that the proposal is checked and marked in the same database transaction as the meal.
/// </summary>
public interface IMealProposalGate
{
    /// <summary>Throws NotFound when the proposal does not exist or is not the caller's; Conflict when it was dismissed.</summary>
    Task<ProposalGateResult> BeginCommitAsync(Guid userId, Guid proposalId, CancellationToken ct);

    /// <summary>Marks the proposal committed; persisted by the caller's next SaveChanges.</summary>
    void MarkCommitted(Guid proposalId, Guid mealId);
}

/// <summary>Minimal profile facts for the assistant. No date of birth, no free text.</summary>
public sealed record HealthProfileSummary(
    bool ProfileExists, int? AgeYears, string Sex, decimal? HeightCm, string ActivityLevel, IReadOnlyList<string> Goals);

public interface IHealthProfileReader
{
    /// <summary>Requires the HealthDataProcessing consent (throws ConsentRequiredException otherwise).</summary>
    Task<HealthProfileSummary> GetSummaryAsync(Guid userId, CancellationToken ct);
}
