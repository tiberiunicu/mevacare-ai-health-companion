using System.Globalization;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;

namespace MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;

/// <summary>
/// Turns WHOOP records into the provider-neutral normalized records. Pure functions, covered by unit tests.
/// Conversion rules (also documented in docs/integrations/whoop.md):
/// milliseconds → whole minutes (rounded), kilojoules → kilocalories (÷ 4.184), timestamps keep the record's own UTC offset,
/// unscored records keep their identity but carry no values, and WHOOP's own scores stay WHOOP-specific metrics.
/// </summary>
internal static class WhoopMapper
{
    public const string ScoredState = "SCORED";
    private const decimal KilojoulesPerKilocalorie = 4.184m;

    /// <summary>Parses the TZD form "+hh:mm", "-hh:mm" or "Z". Anything else is treated as UTC rather than guessed.</summary>
    public static TimeSpan ParseOffset(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset) || offset == "Z")
        {
            return TimeSpan.Zero;
        }

        if (offset.Length == 6 && (offset[0] == '+' || offset[0] == '-') && offset[3] == ':'
            && int.TryParse(offset.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(offset.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            && hours <= 14 && minutes < 60)
        {
            var span = new TimeSpan(hours, minutes, 0);
            return offset[0] == '-' ? -span : span;
        }

        return TimeSpan.Zero;
    }

    public static DateTimeOffset InOffset(DateTimeOffset moment, string? offset) => moment.ToOffset(ParseOffset(offset));

    public static NormalizedSleep? Map(WhoopSleep sleep)
    {
        if (string.IsNullOrWhiteSpace(sleep.Id) || sleep.End < sleep.Start)
        {
            return null;
        }

        var start = InOffset(sleep.Start, sleep.TimezoneOffset);
        var end = InOffset(sleep.End, sleep.TimezoneOffset);
        var stages = sleep.Score?.StageSummary;
        var scored = IsScored(sleep.ScoreState) && sleep.Score is not null && stages is not null;
        if (!scored)
        {
            return new NormalizedSleep(sleep.Id, start, end, sleep.Nap, false, null, null, null, null, null, null, null, null, null, null);
        }

        return new NormalizedSleep(
            sleep.Id, start, end, sleep.Nap, true,
            Minutes(stages!.TotalInBedTimeMilli), Minutes(stages.TotalAwakeTimeMilli), Minutes(stages.TotalLightSleepTimeMilli),
            Minutes(stages.TotalSlowWaveSleepTimeMilli), Minutes(stages.TotalRemSleepTimeMilli),
            Percent(sleep.Score!.SleepPerformancePercentage), Percent(sleep.Score.SleepEfficiencyPercentage), Percent(sleep.Score.SleepConsistencyPercentage),
            sleep.Score.RespiratoryRate is >= 0 and <= 80 ? sleep.Score.RespiratoryRate : null,
            stages.DisturbanceCount is >= 0 ? stages.DisturbanceCount : null);
    }

    /// <summary>A recovery has no time of its own: it belongs to the sleep it follows, so it is dated at that sleep's end.</summary>
    public static IReadOnlyList<NormalizedMeasurement> Map(WhoopRecovery recovery, DateTimeOffset sleepEnd)
    {
        if (!IsScored(recovery.ScoreState) || recovery.Score is null)
        {
            return [];
        }

        var id = recovery.CycleId.ToString(CultureInfo.InvariantCulture);
        var result = new List<NormalizedMeasurement>(3);
        if (recovery.Score.RecoveryScore is >= 0 and <= 100)
        {
            result.Add(new NormalizedMeasurement(HealthMetric.RecoveryScore, recovery.Score.RecoveryScore.Value, sleepEnd, id));
        }

        if (recovery.Score.RestingHeartRate is > 0 and < 250)
        {
            result.Add(new NormalizedMeasurement(HealthMetric.RestingHeartRate, recovery.Score.RestingHeartRate.Value, sleepEnd, id));
        }

        if (recovery.Score.HrvRmssdMilli is > 0 and < 1000)
        {
            result.Add(new NormalizedMeasurement(HealthMetric.HrvRmssd, Math.Round(recovery.Score.HrvRmssdMilli.Value, 2), sleepEnd, id));
        }

        return result;
    }

    /// <summary>Strain and energy of a physiological cycle, dated at the cycle's start. An unfinished cycle is stored and updated later.</summary>
    public static IReadOnlyList<NormalizedMeasurement> Map(WhoopCycle cycle)
    {
        if (!IsScored(cycle.ScoreState) || cycle.Score is null)
        {
            return [];
        }

        var id = cycle.Id.ToString(CultureInfo.InvariantCulture);
        var start = InOffset(cycle.Start, cycle.TimezoneOffset);
        var result = new List<NormalizedMeasurement>(2);
        if (cycle.Score.Strain is >= 0 and <= 21)
        {
            result.Add(new NormalizedMeasurement(HealthMetric.DayStrain, Math.Round(cycle.Score.Strain.Value, 2), start, id));
        }

        if (cycle.Score.Kilojoule is >= 0)
        {
            result.Add(new NormalizedMeasurement(HealthMetric.CaloriesBurned, ToKilocalories(cycle.Score.Kilojoule.Value), start, id));
        }

        return result;
    }

    public static NormalizedWorkout? Map(WhoopWorkout workout)
    {
        if (string.IsNullOrWhiteSpace(workout.Id) || workout.End < workout.Start)
        {
            return null;
        }

        var sport = string.IsNullOrWhiteSpace(workout.SportName) ? "activity" : workout.SportName.Trim();
        var start = InOffset(workout.Start, workout.TimezoneOffset);
        var end = InOffset(workout.End, workout.TimezoneOffset);
        if (!IsScored(workout.ScoreState) || workout.Score is null)
        {
            return new NormalizedWorkout(workout.Id, start, end, sport, false, null, null, null, null, null);
        }

        var score = workout.Score;
        return new NormalizedWorkout(
            workout.Id, start, end, sport, true,
            score.Strain is >= 0 and <= 21 ? Math.Round(score.Strain.Value, 2) : null,
            score.AverageHeartRate is > 0 and < 250 ? score.AverageHeartRate : null,
            score.MaxHeartRate is > 0 and < 250 ? score.MaxHeartRate : null,
            score.Kilojoule is >= 0 ? ToKilocalories(score.Kilojoule.Value) : null,
            score.DistanceMeter is >= 0 ? Math.Round(score.DistanceMeter.Value, 1) : null);
    }

    public static decimal ToKilocalories(decimal kilojoules) => Math.Round(kilojoules / KilojoulesPerKilocalorie, 1);

    private static bool IsScored(string? state) => string.Equals(state, ScoredState, StringComparison.Ordinal);

    private static int? Minutes(long? milliseconds) =>
        milliseconds is null or < 0 ? null : (int)Math.Round(milliseconds.Value / 60_000.0, MidpointRounding.AwayFromZero);

    private static decimal? Percent(decimal? value) => value is >= 0 and <= 100 ? Math.Round(value.Value, 1) : null;
}
