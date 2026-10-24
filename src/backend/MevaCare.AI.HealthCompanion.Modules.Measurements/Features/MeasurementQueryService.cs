using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Measurements.Domain;
using Microsoft.EntityFrameworkCore;

namespace MevaCare.AI.HealthCompanion.Modules.Measurements.Features;

public sealed record AddWeightRequest(decimal Kilograms, DateTimeOffset? MeasuredAt);

public sealed class AddWeightRequestValidator : AbstractValidator<AddWeightRequest>
{
    public AddWeightRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Kilograms).InclusiveBetween(20m, 500m).WithMessage("The weight must be between 20 and 500 kg.");
        RuleFor(x => x.MeasuredAt).Must(t =>
            {
                var now = clock.GetUtcNow();
                return t is null || (t.Value <= now.AddDays(1) && t.Value >= now.AddYears(-30));
            })
            .WithMessage("The time must be within the last 30 years and not more than a day in the future.");
    }
}

/// <summary>Manual entries and historical queries over everything the user has stored, whatever its source.</summary>
public sealed class MeasurementQueryService(AppDbContext db, IConsentService consents, IAuditLog audit, TimeProvider clock)
    : IMeasurementsAssistantApi
{
    public const int MaxRangeDays = 366;

    // ---- weight ----

    public async Task<WeightPointDto> AddWeightAsync(Guid userId, AddWeightRequest request, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var source = await new MeasurementIngestService(db).GetOrCreateSourceAsync(userId, HealthMetrics.ManualProvider, ct);
        var measurement = new HealthMeasurement(
            userId, source.Id, HealthMetric.Weight, Math.Round(request.Kilograms, 2), request.MeasuredAt ?? clock.GetUtcNow(), externalId: null);
        db.Add(measurement);
        audit.Record("measurement.weight_added", userId);
        await db.SaveChangesAsync(ct);
        return new WeightPointDto(
            measurement.Id, measurement.MeasuredAtUtc, measurement.LocalDate, measurement.UtcOffsetMinutes, measurement.Value, HealthMetrics.ManualProvider);
    }

    public async Task DeleteManualWeightAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        var manualSource = await db.Set<MeasurementSource>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.UserId == userId && s.Provider == HealthMetrics.ManualProvider, ct);
        var measurement = manualSource is null
            ? null
            : await db.Set<HealthMeasurement>().SingleOrDefaultAsync(m => m.Id == id && m.UserId == userId && m.SourceId == manualSource.Id, ct);
        if (measurement is null)
        {
            throw new NotFoundException("The measurement was not found, or it was imported and cannot be deleted here.");
        }

        db.Remove(measurement);
        audit.Record("measurement.weight_deleted", userId);
        await db.SaveChangesAsync(ct);
    }

    public async Task<WeightHistoryDto> GetWeightHistoryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await PrepareAsync(userId, from, to, ct);
        var (first, last) = (from, to);
        var rows = await (
                from m in db.Set<HealthMeasurement>().AsNoTracking()
                join s in db.Set<MeasurementSource>().AsNoTracking() on m.SourceId equals s.Id
                where m.UserId == userId && m.Metric == HealthMetric.Weight && m.LocalDate >= first && m.LocalDate <= last
                orderby m.MeasuredAtUtc
                select new { m.Id, m.MeasuredAtUtc, m.LocalDate, m.UtcOffsetMinutes, m.Value, s.Provider })
            .ToListAsync(ct);
        var points = rows.Select(r => new WeightPointDto(r.Id, r.MeasuredAtUtc, r.LocalDate, r.UtcOffsetMinutes, r.Value, r.Provider)).ToList();
        decimal? latest = points.Count > 0 ? points[^1].Kilograms : null;
        decimal? change = points.Count > 1 ? Math.Round(points[^1].Kilograms - points[0].Kilograms, 2) : null;
        return new WeightHistoryDto(from, to, points, latest, change);
    }

    // ---- sleep and recovery ----

    public async Task<SleepRecoveryRangeDto> GetSleepRecoveryAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await PrepareAsync(userId, from, to, ct);
        var (first, last) = (from, to);
        var sleeps = await (
                from s in db.Set<SleepSession>().AsNoTracking()
                join src in db.Set<MeasurementSource>().AsNoTracking() on s.SourceId equals src.Id
                where s.UserId == userId && s.LocalDate >= first && s.LocalDate <= last
                select new { Sleep = s, src.Provider })
            .ToListAsync(ct);
        var metrics = await (
                from m in db.Set<HealthMeasurement>().AsNoTracking()
                join src in db.Set<MeasurementSource>().AsNoTracking() on m.SourceId equals src.Id
                where m.UserId == userId && m.LocalDate >= first && m.LocalDate <= last
                    && (m.Metric == HealthMetric.RecoveryScore || m.Metric == HealthMetric.RestingHeartRate || m.Metric == HealthMetric.HrvRmssd)
                select new { m.Metric, m.Value, m.LocalDate, src.Provider })
            .ToListAsync(ct);

        var days = new Dictionary<(DateOnly, string), (List<SleepSession> Sleeps, Dictionary<HealthMetric, decimal> Metrics)>();
        (List<SleepSession>, Dictionary<HealthMetric, decimal>) Slot(DateOnly date, string provider)
        {
            if (!days.TryGetValue((date, provider), out var slot))
            {
                slot = ([], []);
                days[(date, provider)] = slot;
            }

            return slot;
        }

        foreach (var row in sleeps)
        {
            Slot(row.Sleep.LocalDate, row.Provider).Item1.Add(row.Sleep);
        }

        foreach (var row in metrics)
        {
            Slot(row.LocalDate, row.Provider).Item2[row.Metric] = row.Value;
        }

        var result = days
            .OrderBy(d => d.Key.Item1).ThenBy(d => d.Key.Item2, StringComparer.Ordinal)
            .Select(d =>
            {
                var main = d.Value.Sleeps.Where(s => !s.IsNap).OrderByDescending(s => s.EndUtc - s.StartUtc).FirstOrDefault();
                var naps = d.Value.Sleeps.Where(s => s.IsNap && s.IsScored).Sum(s => s.AsleepMinutes ?? 0);
                var m = d.Value.Metrics;
                var recovery = m.Count == 0
                    ? null
                    : new RecoveryDto(
                        m.TryGetValue(HealthMetric.RecoveryScore, out var score) ? score : (decimal?)null,
                        m.TryGetValue(HealthMetric.RestingHeartRate, out var rhr) ? rhr : (decimal?)null,
                        m.TryGetValue(HealthMetric.HrvRmssd, out var hrv) ? hrv : (decimal?)null);
                return new SleepRecoveryDayDto(d.Key.Item1, d.Key.Item2, main is null ? null : ToDto(main), naps, recovery);
            })
            .ToList();

        var averages = result
            .GroupBy(d => d.Provider)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var asleep = g.Where(d => d.Sleep?.AsleepMinutes is not null).Select(d => d.Sleep!.AsleepMinutes!.Value).ToList();
                var withRecovery = g.Where(d => d.Recovery is not null).Select(d => d.Recovery!).ToList();
                return new ProviderAveragesDto(
                    g.Key,
                    asleep.Count,
                    asleep.Count > 0 ? (int)Math.Round(asleep.Average()) : null,
                    withRecovery.Count,
                    Average(withRecovery.Select(r => r.Score)),
                    Average(withRecovery.Select(r => r.RestingHeartRate)),
                    Average(withRecovery.Select(r => r.HrvRmssdMilliseconds)));
            })
            .ToList();

        return new SleepRecoveryRangeDto(from, to, result, averages);
    }

    // ---- activity ----

    public async Task<ActivityRangeDto> GetActivityAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await PrepareAsync(userId, from, to, ct);
        var (first, last) = (from, to);
        var metrics = await (
                from m in db.Set<HealthMeasurement>().AsNoTracking()
                join src in db.Set<MeasurementSource>().AsNoTracking() on m.SourceId equals src.Id
                where m.UserId == userId && m.LocalDate >= first && m.LocalDate <= last
                    && (m.Metric == HealthMetric.DayStrain || m.Metric == HealthMetric.CaloriesBurned)
                select new { m.Metric, m.Value, m.LocalDate, src.Provider })
            .ToListAsync(ct);
        var workouts = await (
                from w in db.Set<Workout>().AsNoTracking()
                join src in db.Set<MeasurementSource>().AsNoTracking() on w.SourceId equals src.Id
                where w.UserId == userId && w.LocalDate >= first && w.LocalDate <= last
                select new { Workout = w, src.Provider })
            .ToListAsync(ct);

        var keys = metrics.Select(m => (m.LocalDate, m.Provider)).Concat(workouts.Select(w => (w.Workout.LocalDate, w.Provider))).Distinct();
        var days = keys
            .OrderBy(k => k.Item1).ThenBy(k => k.Item2, StringComparer.Ordinal)
            .Select(k =>
            {
                var dayMetrics = metrics.Where(m => m.LocalDate == k.Item1 && m.Provider == k.Item2).ToList();
                return new ActivityDayDto(
                    k.Item1,
                    k.Item2,
                    dayMetrics.Where(m => m.Metric == HealthMetric.DayStrain).Select(m => (decimal?)m.Value).FirstOrDefault(),
                    dayMetrics.Where(m => m.Metric == HealthMetric.CaloriesBurned).Select(m => (decimal?)m.Value).FirstOrDefault(),
                    workouts
                        .Where(w => w.Workout.LocalDate == k.Item1 && w.Provider == k.Item2)
                        .OrderBy(w => w.Workout.StartUtc)
                        .Select(w => ToDto(w.Workout))
                        .ToList());
            })
            .ToList();
        return new ActivityRangeDto(from, to, days);
    }

    // ---- helpers ----

    private async Task PrepareAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await consents.EnsureGrantedAsync(userId, ConsentType.HealthDataProcessing, ct);
        if (to < from)
        {
            throw new BadRequestException("invalid_range", "The end date must not be before the start date.");
        }

        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            throw new BadRequestException("range_too_long", $"The range can span at most {MaxRangeDays} days.");
        }
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var present = values.Where(v => v is not null).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 1);
    }

    private static SleepSessionDto ToDto(SleepSession s) => new(
        s.Id, s.StartUtc, s.EndUtc, s.UtcOffsetMinutes, s.IsScored, s.AsleepMinutes, s.TimeInBedMinutes, s.AwakeMinutes, s.LightMinutes,
        s.DeepMinutes, s.RemMinutes, s.PerformancePercent, s.EfficiencyPercent, s.ConsistencyPercent, s.RespiratoryRate, s.DisturbanceCount);

    private static WorkoutDto ToDto(Workout w) => new(
        w.Id, w.SportName, w.StartUtc, w.EndUtc, w.UtcOffsetMinutes, w.IsScored, w.Strain, w.AverageHeartRate, w.MaxHeartRate, w.Calories,
        w.DistanceMeters);
}
