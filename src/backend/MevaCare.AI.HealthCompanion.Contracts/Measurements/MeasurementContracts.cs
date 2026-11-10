namespace MevaCare.AI.HealthCompanion.Contracts.Measurements;

/// <summary>
/// The metrics stored as single values. Metrics whose value is a score defined by one provider
/// (see <see cref="HealthMetrics.IsProviderScore"/>) must never be compared across providers.
/// </summary>
public enum HealthMetric
{
    /// <summary>Body weight, kilograms. Physical quantity: comparable across sources.</summary>
    Weight = 1,

    /// <summary>Resting heart rate, beats per minute. Physical quantity (the way it is derived differs by provider).</summary>
    RestingHeartRate = 2,

    /// <summary>Heart-rate variability as RMSSD in milliseconds. Not comparable with SDNN-based values from other devices.</summary>
    HrvRmssd = 3,

    /// <summary>The provider's own 0-100 recovery score. Provider-specific scale and algorithm.</summary>
    RecoveryScore = 4,

    /// <summary>The provider's own day strain (WHOOP: 0-21 scale). Provider-specific.</summary>
    DayStrain = 5,

    /// <summary>Energy burned over the provider's physiological day, kilocalories (includes resting metabolism).</summary>
    CaloriesBurned = 6,
}

public static class HealthMetrics
{
    public static bool IsProviderScore(HealthMetric metric) => metric is HealthMetric.RecoveryScore or HealthMetric.DayStrain;

    public static string UnitOf(HealthMetric metric) => metric switch
    {
        HealthMetric.Weight => "kg",
        HealthMetric.RestingHeartRate => "bpm",
        HealthMetric.HrvRmssd => "ms",
        HealthMetric.RecoveryScore => "score",
        HealthMetric.DayStrain => "score",
        HealthMetric.CaloriesBurned => "kcal",
        _ => "",
    };

    public const string ManualProvider = "manual";
}

// ---- Provider-neutral records produced by integrations and consumed by the Measurements module ----

/// <param name="MeasuredAt">The source's own moment with its UTC offset, so the local day is preserved.</param>
/// <param name="ExternalId">Id of the record in the provider; (source, metric, external id) is unique, which makes imports idempotent.</param>
public sealed record NormalizedMeasurement(HealthMetric Metric, decimal Value, DateTimeOffset MeasuredAt, string ExternalId);

/// <summary>A sleep session. Stage values are only present when the provider has scored the session.</summary>
public sealed record NormalizedSleep(
    string ExternalId,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsNap,
    bool IsScored,
    int? TimeInBedMinutes,
    int? AwakeMinutes,
    int? LightMinutes,
    int? DeepMinutes,
    int? RemMinutes,
    decimal? PerformancePercent,
    decimal? EfficiencyPercent,
    decimal? ConsistencyPercent,
    decimal? RespiratoryRate,
    int? DisturbanceCount);

public sealed record NormalizedWorkout(
    string ExternalId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string SportName,
    bool IsScored,
    decimal? Strain,
    int? AverageHeartRate,
    int? MaxHeartRate,
    decimal? Calories,
    decimal? DistanceMeters);

public sealed record NormalizedHealthData(
    IReadOnlyList<NormalizedMeasurement> Measurements,
    IReadOnlyList<NormalizedSleep> Sleeps,
    IReadOnlyList<NormalizedWorkout> Workouts)
{
    public static NormalizedHealthData Empty { get; } = new([], [], []);

    public int Count => Measurements.Count + Sleeps.Count + Workouts.Count;
}

public sealed record IngestCounts(int Added, int Updated)
{
    public int Total => Added + Updated;
}

/// <summary>Implemented by the Measurements module; used by integrations to store normalized data with provenance.</summary>
public interface IMeasurementIngest
{
    /// <summary>
    /// Stores or updates the records for one user from one provider. Idempotent: repeating the same call changes nothing.
    /// The caller is responsible for the consent checks.
    /// </summary>
    Task<IngestCounts> UpsertAsync(Guid userId, string provider, NormalizedHealthData data, CancellationToken ct);

    /// <summary>Deletes everything imported from a provider for the user (not manual entries). Returns the number of removed records.</summary>
    Task<int> DeleteProviderDataAsync(Guid userId, string provider, CancellationToken ct);
}

// ---- Read models (HTTP API and assistant tools) ----

public sealed record WeightPointDto(Guid Id, DateTime MeasuredAtUtc, DateOnly LocalDate, int UtcOffsetMinutes, decimal Kilograms, string Provider);

public sealed record WeightHistoryDto(
    DateOnly From, DateOnly To, IReadOnlyList<WeightPointDto> Points, decimal? LatestKilograms, decimal? ChangeKilograms);

public sealed record SleepSessionDto(
    Guid Id, DateTime StartUtc, DateTime EndUtc, int UtcOffsetMinutes, bool IsScored,
    int? AsleepMinutes, int? InBedMinutes, int? AwakeMinutes, int? LightMinutes, int? DeepMinutes, int? RemMinutes,
    decimal? PerformancePercent, decimal? EfficiencyPercent, decimal? ConsistencyPercent, decimal? RespiratoryRate, int? DisturbanceCount);

public sealed record RecoveryDto(decimal? Score, decimal? RestingHeartRate, decimal? HrvRmssdMilliseconds);

/// <summary>One day for one provider. The "night" of a day is the longest non-nap sleep that ENDED on that local date.</summary>
public sealed record SleepRecoveryDayDto(DateOnly Date, string Provider, SleepSessionDto? Sleep, int NapMinutes, RecoveryDto? Recovery);

public sealed record ProviderAveragesDto(
    string Provider, int DaysWithSleep, int? AverageAsleepMinutes, int DaysWithRecovery, decimal? AverageRecoveryScore,
    decimal? AverageRestingHeartRate, decimal? AverageHrvRmssdMilliseconds);

public sealed record SleepRecoveryRangeDto(
    DateOnly From, DateOnly To, IReadOnlyList<SleepRecoveryDayDto> Days, IReadOnlyList<ProviderAveragesDto> Averages);

public sealed record WorkoutDto(
    Guid Id, string SportName, DateTime StartUtc, DateTime EndUtc, int UtcOffsetMinutes, bool IsScored,
    decimal? Strain, int? AverageHeartRate, int? MaxHeartRate, decimal? Calories, decimal? DistanceMeters);

public sealed record ActivityDayDto(DateOnly Date, string Provider, decimal? DayStrain, decimal? CaloriesBurned, IReadOnlyList<WorkoutDto> Workouts);

public sealed record ActivityRangeDto(DateOnly From, DateOnly To, IReadOnlyList<ActivityDayDto> Days);

/// <summary>Read access for the assistant's tools. Every method re-checks the HealthDataProcessing consent and filters by user.</summary>
public interface IMeasurementsAssistantApi
{
    Task<WeightHistoryDto> GetWeightHistoryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct);

    Task<SleepRecoveryRangeDto> GetSleepRecoveryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Stores a manual weight entry. Called only after the user confirmed it; range checks (20 to 500 kg) apply.</summary>
    Task<WeightPointDto> RecordWeightAsync(Guid userId, decimal kilograms, DateTimeOffset measuredAt, CancellationToken ct);
}
