using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Modules.Integrations.Providers.Whoop;

namespace MevaCare.AI.HealthCompanion.UnitTests.Integrations;

public class WhoopMapperTests
{
    private static readonly DateTimeOffset Wake = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("+03:00", 180)]
    [InlineData("-05:00", -300)]
    [InlineData("+05:30", 330)]
    [InlineData("-09:30", -570)]
    [InlineData("Z", 0)]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("garbage", 0)]
    [InlineData("+25:00", 0)]
    [InlineData("+03:75", 0)]
    [InlineData("0300", 0)]
    public void The_time_zone_designator_is_parsed_and_anything_else_is_treated_as_UTC(string? text, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), WhoopMapper.ParseOffset(text));

    private static WhoopSleep Sleep(string state = "SCORED", string offset = "+03:00", bool nap = false, WhoopSleepScore? score = null) => new(
        "sleep-1", Wake.AddHours(-8), Wake, offset, nap, state,
        score ?? new WhoopSleepScore(
            new WhoopStageSummary(28_800_000, 1_800_000, 15_000_000, 6_000_000, 6_000_000, 7), 15.5m, 91m, 88m, 92.456m));

    [Fact]
    public void A_scored_sleep_becomes_minutes_in_the_records_own_time_zone()
    {
        var result = WhoopMapper.Map(Sleep())!;

        Assert.Equal("sleep-1", result.ExternalId);
        Assert.True(result.IsScored);
        Assert.Equal(TimeSpan.FromHours(3), result.End.Offset);
        Assert.Equal(Wake, result.End.ToUniversalTime());
        Assert.Equal(new DateOnly(2026, 10, 20), DateOnly.FromDateTime(result.End.DateTime)); // 09:00 local
        Assert.Equal((480, 30, 250, 100, 100), (result.TimeInBedMinutes, result.AwakeMinutes, result.LightMinutes, result.DeepMinutes, result.RemMinutes));
        Assert.Equal(91m, result.PerformancePercent);
        Assert.Equal(92.5m, result.EfficiencyPercent); // rounded to one decimal
        Assert.Equal(7, result.DisturbanceCount);
        Assert.False(result.IsNap);
    }

    [Theory]
    [InlineData("PENDING_SCORE")]
    [InlineData("UNSCORABLE")]
    [InlineData("scored")] // the documented values are upper case; anything else is not trusted
    [InlineData(null)]
    public void A_sleep_that_is_not_scored_keeps_its_identity_but_carries_no_values(string? state)
    {
        var result = WhoopMapper.Map(Sleep(state!))!;

        Assert.False(result.IsScored);
        Assert.Null(result.LightMinutes);
        Assert.Null(result.PerformancePercent);
        Assert.Equal("sleep-1", result.ExternalId);
    }

    [Fact]
    public void A_scored_sleep_without_a_score_object_is_treated_as_unscored()
    {
        var sleep = Sleep() with { Score = null };

        Assert.False(WhoopMapper.Map(sleep)!.IsScored);
    }

    [Fact]
    public void Naps_are_flagged()
    {
        Assert.True(WhoopMapper.Map(Sleep(nap: true))!.IsNap);
    }

    [Fact]
    public void Records_that_end_before_they_start_or_have_no_id_are_dropped()
    {
        Assert.Null(WhoopMapper.Map(Sleep() with { End = Wake.AddDays(-1) }));
        Assert.Null(WhoopMapper.Map(Sleep() with { Id = " " }));
        Assert.Null(WhoopMapper.Map(new WhoopWorkout("", Wake, Wake.AddHours(1), "+00:00", "run", "SCORED", null)));
        Assert.Null(WhoopMapper.Map(new WhoopWorkout("w", Wake, Wake.AddHours(-1), "+00:00", "run", "SCORED", null)));
    }

    [Theory]
    [InlineData(29_999, 0)]
    [InlineData(30_000, 1)]
    [InlineData(60_000, 1)]
    [InlineData(89_999, 1)]
    [InlineData(90_000, 2)]
    public void Milliseconds_are_rounded_to_whole_minutes(long milliseconds, int minutes)
    {
        var score = new WhoopSleepScore(new WhoopStageSummary(milliseconds, 0, 0, 0, 0, 0), null, null, null, null);

        Assert.Equal(minutes, WhoopMapper.Map(Sleep(score: score))!.TimeInBedMinutes);
    }

    [Fact]
    public void Implausible_sleep_values_are_dropped_not_stored()
    {
        var score = new WhoopSleepScore(new WhoopStageSummary(-5, 0, 0, 0, 0, -1), 500m, 150m, -3m, 101m);

        var result = WhoopMapper.Map(Sleep(score: score))!;

        Assert.Null(result.TimeInBedMinutes);
        Assert.Null(result.DisturbanceCount);
        Assert.Null(result.RespiratoryRate);
        Assert.Null(result.PerformancePercent);
        Assert.Null(result.ConsistencyPercent);
        Assert.Null(result.EfficiencyPercent);
    }

    // ---- recovery ----

    private static WhoopRecovery Recovery(string state = "SCORED", WhoopRecoveryScore? score = null) =>
        new(101, "sleep-1", state, score ?? new WhoopRecoveryScore(66m, 52m, 61.5678m, false));

    [Fact]
    public void A_recovery_is_dated_at_the_end_of_its_sleep_and_keeps_the_providers_scale_in_its_metrics()
    {
        var sleepEnd = Wake.ToOffset(TimeSpan.FromHours(3));

        var result = WhoopMapper.Map(Recovery(), sleepEnd);

        Assert.Equal(
            [(HealthMetric.RecoveryScore, 66m), (HealthMetric.RestingHeartRate, 52m), (HealthMetric.HrvRmssd, 61.57m)],
            result.Select(m => (m.Metric, m.Value)));
        Assert.All(result, m =>
        {
            Assert.Equal("101", m.ExternalId); // keyed by the cycle: (source, metric, 101) is unique, so a re-import updates
            Assert.Equal(sleepEnd, m.MeasuredAt);
            Assert.Equal(TimeSpan.FromHours(3), m.MeasuredAt.Offset);
        });
    }

    [Theory]
    [InlineData("PENDING_SCORE")]
    [InlineData("UNSCORABLE")]
    public void A_recovery_that_is_not_scored_produces_nothing(string state) =>
        Assert.Empty(WhoopMapper.Map(Recovery(state), Wake));

    [Fact]
    public void Recovery_values_outside_physical_ranges_are_skipped_individually()
    {
        var result = WhoopMapper.Map(Recovery(score: new WhoopRecoveryScore(150m, 0m, 61m, null)), Wake);

        Assert.Equal([HealthMetric.HrvRmssd], result.Select(m => m.Metric));
    }

    // ---- cycle and workout ----

    [Fact]
    public void A_cycle_gives_day_strain_and_kilocalories_converted_from_kilojoules()
    {
        var cycle = new WhoopCycle(7, Wake, null, "-05:00", "SCORED", new WhoopCycleScore(12.3456m, 4184m, 66, 150));

        var result = WhoopMapper.Map(cycle);

        Assert.Equal([(HealthMetric.DayStrain, 12.35m), (HealthMetric.CaloriesBurned, 1000m)], result.Select(m => (m.Metric, m.Value)));
        Assert.All(result, m => Assert.Equal(TimeSpan.FromHours(-5), m.MeasuredAt.Offset));
        Assert.Equal(1000m, WhoopMapper.ToKilocalories(4184m));
        Assert.Equal(375.1m, WhoopMapper.ToKilocalories(1569.3m));
    }

    [Fact]
    public void Unscored_cycles_and_impossible_strain_produce_nothing()
    {
        Assert.Empty(WhoopMapper.Map(new WhoopCycle(7, Wake, null, "Z", "PENDING_SCORE", null)));
        Assert.Equal([HealthMetric.CaloriesBurned], WhoopMapper.Map(new WhoopCycle(7, Wake, null, "Z", "SCORED", new WhoopCycleScore(22m, 100m, 1, 1))).Select(m => m.Metric));
    }

    [Fact]
    public void A_workout_is_mapped_with_converted_energy_and_a_default_sport_name()
    {
        var score = new WhoopWorkoutScore(8.2463m, 123, 146, 1569.34m, 1772.77035916m);

        var result = WhoopMapper.Map(new WhoopWorkout("w-1", Wake, Wake.AddHours(1), "+03:00", " ", "SCORED", score))!;

        Assert.Equal("activity", result.SportName);
        Assert.True(result.IsScored);
        Assert.Equal((8.25m, 123, 146, 375.1m, 1772.8m), (result.Strain, result.AverageHeartRate, result.MaxHeartRate, result.Calories, result.DistanceMeters));
        Assert.Equal(TimeSpan.FromHours(3), result.Start.Offset);
    }

    [Fact]
    public void An_unscored_workout_keeps_only_its_identity()
    {
        var result = WhoopMapper.Map(new WhoopWorkout("w-1", Wake, Wake.AddHours(1), "Z", "running", "PENDING_SCORE", null))!;

        Assert.False(result.IsScored);
        Assert.Null(result.Strain);
        Assert.Equal("running", result.SportName);
    }
}
