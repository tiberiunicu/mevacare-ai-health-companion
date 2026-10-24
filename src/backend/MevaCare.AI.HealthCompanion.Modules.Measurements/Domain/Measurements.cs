using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;

namespace MevaCare.AI.HealthCompanion.Modules.Measurements.Domain;

/// <summary>
/// Where a user's measurements come from ("manual", "whoop", ...). One row per user and provider; every measurement
/// points to it, so provenance is never lost and one provider's data can be removed without touching the rest.
/// </summary>
public sealed class MeasurementSource : AuditableEntity
{
    private MeasurementSource() { }

    public MeasurementSource(Guid userId, string provider)
    {
        UserId = userId;
        Provider = provider;
    }

    public Guid UserId { get; private set; }

    /// <summary>Lower-case provider key.</summary>
    public string Provider { get; private set; } = "";
}

/// <summary>
/// A single-value measurement. UserId is a plain column (no foreign key of its own): the row is deleted together with its
/// source, which cascades from the user, and SQL Server does not allow two cascade paths.
/// </summary>
public sealed class HealthMeasurement : AuditableEntity
{
    private HealthMeasurement() { }

    public HealthMeasurement(Guid userId, Guid sourceId, HealthMetric metric, decimal value, DateTimeOffset measuredAt, string? externalId)
    {
        UserId = userId;
        SourceId = sourceId;
        Metric = metric;
        Unit = HealthMetrics.UnitOf(metric);
        ExternalId = externalId;
        Apply(value, measuredAt);
    }

    public Guid UserId { get; private set; }
    public Guid SourceId { get; private set; }
    public HealthMetric Metric { get; private set; }
    public decimal Value { get; private set; }
    public string Unit { get; private set; } = "";
    public DateTime MeasuredAtUtc { get; private set; }

    /// <summary>The source's own UTC offset (minutes), kept so the local day can always be reconstructed.</summary>
    public int UtcOffsetMinutes { get; private set; }

    /// <summary>The calendar day in the source's local time. Daily views group by this.</summary>
    public DateOnly LocalDate { get; private set; }

    /// <summary>Id of the record at the provider; null for manual entries. (SourceId, Metric, ExternalId) is unique.</summary>
    public string? ExternalId { get; private set; }

    public void Apply(decimal value, DateTimeOffset measuredAt)
    {
        Value = value;
        MeasuredAtUtc = measuredAt.UtcDateTime;
        UtcOffsetMinutes = (int)measuredAt.Offset.TotalMinutes;
        LocalDate = DateOnly.FromDateTime(measuredAt.DateTime);
    }
}

public sealed class SleepSession : AuditableEntity
{
    private SleepSession() { }

    public SleepSession(Guid userId, Guid sourceId, NormalizedSleep sleep)
    {
        UserId = userId;
        SourceId = sourceId;
        ExternalId = sleep.ExternalId;
        Apply(sleep);
    }

    public Guid UserId { get; private set; }
    public Guid SourceId { get; private set; }
    public string ExternalId { get; private set; } = "";
    public DateTime StartUtc { get; private set; }
    public DateTime EndUtc { get; private set; }
    public int UtcOffsetMinutes { get; private set; }

    /// <summary>The local date on which the sleep ended (the morning the person woke up). The "night of Tuesday" is the sleep that ends on Tuesday.</summary>
    public DateOnly LocalDate { get; private set; }

    public bool IsNap { get; private set; }

    /// <summary>False while the provider has not scored the session yet; the stage values are then null.</summary>
    public bool IsScored { get; private set; }

    public int? TimeInBedMinutes { get; private set; }
    public int? AwakeMinutes { get; private set; }
    public int? LightMinutes { get; private set; }
    public int? DeepMinutes { get; private set; }
    public int? RemMinutes { get; private set; }
    public decimal? PerformancePercent { get; private set; }
    public decimal? EfficiencyPercent { get; private set; }
    public decimal? ConsistencyPercent { get; private set; }
    public decimal? RespiratoryRate { get; private set; }
    public int? DisturbanceCount { get; private set; }

    public int? AsleepMinutes => IsScored && LightMinutes is not null && DeepMinutes is not null && RemMinutes is not null
        ? LightMinutes + DeepMinutes + RemMinutes
        : null;

    public void Apply(NormalizedSleep sleep)
    {
        StartUtc = sleep.Start.UtcDateTime;
        EndUtc = sleep.End.UtcDateTime;
        UtcOffsetMinutes = (int)sleep.End.Offset.TotalMinutes;
        LocalDate = DateOnly.FromDateTime(sleep.End.DateTime);
        IsNap = sleep.IsNap;
        IsScored = sleep.IsScored;
        TimeInBedMinutes = sleep.TimeInBedMinutes;
        AwakeMinutes = sleep.AwakeMinutes;
        LightMinutes = sleep.LightMinutes;
        DeepMinutes = sleep.DeepMinutes;
        RemMinutes = sleep.RemMinutes;
        PerformancePercent = sleep.PerformancePercent;
        EfficiencyPercent = sleep.EfficiencyPercent;
        ConsistencyPercent = sleep.ConsistencyPercent;
        RespiratoryRate = sleep.RespiratoryRate;
        DisturbanceCount = sleep.DisturbanceCount;
    }
}

public sealed class Workout : AuditableEntity
{
    private Workout() { }

    public Workout(Guid userId, Guid sourceId, NormalizedWorkout workout)
    {
        UserId = userId;
        SourceId = sourceId;
        ExternalId = workout.ExternalId;
        Apply(workout);
    }

    public Guid UserId { get; private set; }
    public Guid SourceId { get; private set; }
    public string ExternalId { get; private set; } = "";
    public string SportName { get; private set; } = "";
    public DateTime StartUtc { get; private set; }
    public DateTime EndUtc { get; private set; }
    public int UtcOffsetMinutes { get; private set; }

    /// <summary>The local date on which the workout started.</summary>
    public DateOnly LocalDate { get; private set; }

    public bool IsScored { get; private set; }
    public decimal? Strain { get; private set; }
    public int? AverageHeartRate { get; private set; }
    public int? MaxHeartRate { get; private set; }
    public decimal? Calories { get; private set; }
    public decimal? DistanceMeters { get; private set; }

    public void Apply(NormalizedWorkout workout)
    {
        SportName = workout.SportName.Length <= 80 ? workout.SportName : workout.SportName[..80];
        StartUtc = workout.Start.UtcDateTime;
        EndUtc = workout.End.UtcDateTime;
        UtcOffsetMinutes = (int)workout.Start.Offset.TotalMinutes;
        LocalDate = DateOnly.FromDateTime(workout.Start.DateTime);
        IsScored = workout.IsScored;
        Strain = workout.Strain;
        AverageHeartRate = workout.AverageHeartRate;
        MaxHeartRate = workout.MaxHeartRate;
        Calories = workout.Calories;
        DistanceMeters = workout.DistanceMeters;
    }
}
