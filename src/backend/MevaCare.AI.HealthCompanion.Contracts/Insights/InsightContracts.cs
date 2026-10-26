namespace MevaCare.AI.HealthCompanion.Contracts.Insights;

/// <summary>Direction of a trend or a period comparison. Always paired with the evidence that produced it.</summary>
public static class Direction
{
    public const string InsufficientData = "insufficient_data";
    public const string Increasing = "increasing";
    public const string Decreasing = "decreasing";
    public const string Stable = "stable";
}

/// <summary>How much the numbers behind a trend can be trusted (data volume, time span, fit). A rule, not a model.</summary>
public static class Confidence
{
    public const string Low = "low";
    public const string Moderate = "moderate";
    public const string High = "high";
}

public static class Severity
{
    public const string Info = "info";

    /// <summary>Worth the user's attention. Never an alarm and never a diagnosis.</summary>
    public const string Notable = "notable";
}

public static class InsightCategory
{
    public const string Weight = "weight";
    public const string Nutrition = "nutrition";
    public const string Sleep = "sleep";
    public const string Recovery = "recovery";
    public const string Combined = "combined";
    public const string DataQuality = "data_quality";
}

/// <summary>One number or word behind an insight, with its unit. Insights are made of facts, not of prose.</summary>
public sealed record InsightFact(string Key, decimal? Number, string? Text, string? Unit);

/// <param name="Metric">weight_kg, calories_kcal, protein_g, sleep_minutes, recovery_score, hrv_ms, resting_heart_rate_bpm.</param>
/// <param name="Provider">The device provider for provider-specific metrics (recovery, HRV, sleep), otherwise null.</param>
/// <param name="ChangePerWeek">Least-squares slope in metric units per week; null when the data is insufficient.</param>
/// <param name="Note">Machine code explaining a missing or weak result, for example "needs_3_points_over_7_days".</param>
public sealed record TrendDto(
    string Metric,
    string Unit,
    string? Provider,
    string Direction,
    string Confidence,
    int Points,
    int SpanDays,
    decimal? Average,
    decimal? First,
    decimal? Last,
    decimal? ChangePerWeek,
    decimal? RSquared,
    string? Note);

/// <summary>The same metric over the current period and over the period of the same length right before it.</summary>
public sealed record PeriodComparisonDto(
    string Metric,
    string Unit,
    string? Provider,
    int CurrentDays,
    int PreviousDays,
    decimal? CurrentAverage,
    decimal? PreviousAverage,
    decimal? Change,
    decimal? ChangePercent,
    string Direction,
    bool Meaningful);

/// <param name="Code">Stable identifier (weight_trend_shift, calories_vs_target …); the UI and the model phrase it from the facts.</param>
/// <param name="Statement">A deterministic English sentence built from the facts, used by the assistant tool and API consumers.</param>
/// <param name="Caveats">Codes of the reasons the statement must not be over-read (association_not_cause, low_confidence, logging_gaps …).</param>
public sealed record InsightDto(
    string Code,
    string Category,
    string Severity,
    string Statement,
    IReadOnlyList<InsightFact> Facts,
    IReadOnlyList<string> Caveats);

public sealed record DataCoverageDto(
    int WeightEntries,
    int NutritionLoggedDays,
    int NutritionWindowDays,
    IReadOnlyList<ProviderCoverageDto> Providers);

public sealed record ProviderCoverageDto(string Provider, int DaysWithSleep, int DaysWithRecovery);

public sealed record HealthInsightsDto(
    DateOnly From,
    DateOnly To,
    int Days,
    DataCoverageDto Coverage,
    IReadOnlyList<TrendDto> Trends,
    IReadOnlyList<PeriodComparisonDto> Comparisons,
    IReadOnlyList<InsightDto> Insights,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Read access to the computed insights, for the assistant. Everything is calculated deterministically from stored data
/// of the caller; nothing is persisted and nothing comes from a model.
/// </summary>
public interface IInsightsAssistantApi
{
    /// <summary>
    /// Analyses the period [to - days + 1, to] and compares it with the period of the same length before it.
    /// Requires the HealthDataProcessing consent. <paramref name="days"/> must be 7 to 90.
    /// </summary>
    Task<HealthInsightsDto> GetInsightsAsync(Guid userId, int days, DateOnly to, CancellationToken ct);
}
