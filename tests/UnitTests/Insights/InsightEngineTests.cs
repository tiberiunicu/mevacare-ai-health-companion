using MevaCare.AI.HealthCompanion.Contracts.Insights;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Modules.Insights.Analysis;

namespace MevaCare.AI.HealthCompanion.UnitTests.Insights;

public class TrendMathTests
{
    private static readonly DateOnly Start = new(2026, 9, 1);

    private static List<Sample> Line(int days, double start, double perDay) =>
        Enumerable.Range(0, days).Select(i => new Sample(Start.AddDays(i), start + perDay * i)).ToList();

    [Fact]
    public void A_straight_line_has_its_exact_slope_and_a_perfect_fit()
    {
        var fit = TrendMath.Fit(Line(30, 80, -0.05))!;

        Assert.Equal(-0.05, fit.SlopePerDay, 6);
        Assert.Equal(1, fit.RSquared, 6);
        Assert.Equal((30, 29), (fit.Points, fit.SpanDays));
    }

    [Fact]
    public void A_flat_series_has_no_slope_and_no_fit_quality_to_boast_about()
    {
        var fit = TrendMath.Fit(Line(10, 70, 0))!;

        Assert.Equal(0, fit.SlopePerDay, 9);
        Assert.Equal(0, fit.RSquared);
    }

    [Fact]
    public void Noise_lowers_the_fit_quality_but_the_slope_stays_close()
    {
        var noisy = Line(60, 80, -0.05).Select((s, i) => s with { Value = s.Value + (i % 2 == 0 ? 0.6 : -0.6) }).ToList();

        var fit = TrendMath.Fit(noisy)!;

        Assert.InRange(fit.SlopePerDay, -0.06, -0.04);
        Assert.InRange(fit.RSquared, 0.3, 0.95);
    }

    [Fact]
    public void Fewer_than_two_distinct_days_cannot_be_fitted()
    {
        Assert.Null(TrendMath.Fit([]));
        Assert.Null(TrendMath.Fit([new Sample(Start, 1)]));
        Assert.Null(TrendMath.Fit([new Sample(Start, 1), new Sample(Start, 2)]));
    }

    [Fact]
    public void Several_entries_on_one_day_count_once()
    {
        var daily = TrendMath.OnePerDay([new Sample(Start, 70), new Sample(Start, 72), new Sample(Start.AddDays(1), 71)]);

        Assert.Equal([71d, 71d], daily.Select(s => s.Value));
        Assert.Equal([Start, Start.AddDays(1)], daily.Select(s => s.Date));
    }

    [Fact]
    public void Median_and_standard_deviation_follow_their_textbook_definitions()
    {
        Assert.Equal(3, TrendMath.Median([5, 1, 3]));
        Assert.Equal(2.5, TrendMath.Median([4, 1, 2, 3]));
        Assert.Equal(2, TrendMath.StandardDeviation([2, 4, 4, 4, 5, 5, 7, 9]), 0); // sample deviation 2.14
        Assert.Equal(0, TrendMath.StandardDeviation([5]));
    }

    [Theory]
    [InlineData(4, 20, 0.9, Confidence.Low)] // too few points
    [InlineData(20, 8, 0.9, Confidence.Low)] // too short a span
    [InlineData(20, 28, 0.1, Confidence.Low)] // the line explains almost nothing
    [InlineData(8, 21, 0.4, Confidence.Moderate)]
    [InlineData(14, 21, 0.5, Confidence.High)]
    [InlineData(30, 40, 0.49, Confidence.Moderate)]
    public void Confidence_is_a_rule_of_volume_span_and_fit(int points, int span, double rSquared, string expected) =>
        Assert.Equal(expected, TrendMath.ConfidenceOf(new LinearFit(1, rSquared, points, span)));
}

public class InsightEngineTests
{
    private static readonly DateOnly To = new(2026, 10, 30);

    private static readonly NutritionTargetDto NoTargets = new(null, null, null, null, null);

    private static DateOnly Day(int daysBeforeTo) => To.AddDays(-daysBeforeTo);

    /// <summary>A weight series that moves by <paramref name="perWeek"/> kg per week, one entry every <paramref name="every"/> days.</summary>
    private static List<(DateOnly, decimal)> Weights(int fromDaysBefore, int toDaysBefore, decimal startKg, decimal perWeek, int every = 2)
    {
        var list = new List<(DateOnly, decimal)>();
        for (var d = fromDaysBefore; d >= toDaysBefore; d -= every)
        {
            list.Add((Day(d), startKg + perWeek / 7 * (fromDaysBefore - d)));
        }

        return list;
    }

    private static List<NutritionDay> Meals(int fromDaysBefore, int toDaysBefore, decimal calories, decimal protein = 100, bool incomplete = false) =>
        Enumerable.Range(toDaysBefore, fromDaysBefore - toDaysBefore + 1)
            .Select(d => new NutritionDay(Day(d), calories, protein, incomplete)).ToList();

    private static SleepRecoveryDayDto SleepDay(int daysBefore, string provider, int? asleep, decimal? recovery = null, decimal? hrv = null, decimal? rhr = null) =>
        new(Day(daysBefore), provider,
            asleep is null ? null : new SleepSessionDto(Guid.NewGuid(), default, default, 0, true, asleep, asleep + 30, 30, 200, 80, 100, 90, 92, 88, 15, 5),
            0,
            recovery is null && hrv is null && rhr is null ? null : new RecoveryDto(recovery, rhr, hrv));

    private static HealthInsightsDto Run(
        int days = 30, IReadOnlyList<(DateOnly, decimal)>? weights = null, IReadOnlyList<NutritionDay>? meals = null,
        NutritionTargetDto? targets = null, IReadOnlyList<SleepRecoveryDayDto>? sleep = null) =>
        InsightEngine.Analyse(new InsightInput(To, days, weights ?? [], meals ?? [], targets ?? NoTargets, sleep ?? []));

    private static InsightDto? Insight(HealthInsightsDto result, string code) => result.Insights.FirstOrDefault(i => i.Code == code);

    private static decimal Number(InsightDto insight, string key) => insight.Facts.Single(f => f.Key == key).Number!.Value;

    private static string Text(InsightDto insight, string key) => insight.Facts.Single(f => f.Key == key).Text!;

    // ---- trends ----

    [Fact]
    public void A_steady_loss_is_a_decreasing_weight_trend_with_the_rate_per_week_and_high_confidence()
    {
        var result = Run(weights: Weights(29, 0, 80, -0.5m, every: 1));

        var trend = result.Trends.Single(t => t.Metric == "weight_kg");
        Assert.Equal(Direction.Decreasing, trend.Direction);
        Assert.Equal(-0.5m, trend.ChangePerWeek);
        Assert.Equal(Confidence.High, trend.Confidence);
        Assert.Equal((30, 29), (trend.Points, trend.SpanDays));
        Assert.Equal(80m, trend.First);
        var insight = Insight(result, "weight_trend")!;
        Assert.Equal("decreasing", Text(insight, "direction"));
        Assert.Contains("decreasing by about 0.5 kg per week", insight.Statement);
        Assert.Contains("weight_varies_daily", insight.Caveats);
        Assert.DoesNotContain("low_confidence", insight.Caveats);
    }

    [Fact]
    public void A_change_smaller_than_the_stable_band_is_called_stable()
    {
        var result = Run(weights: Weights(29, 0, 80, 0.05m, every: 1));

        Assert.Equal(Direction.Stable, result.Trends.Single(t => t.Metric == "weight_kg").Direction);
        Assert.Contains("has been stable", Insight(result, "weight_trend")!.Statement);
    }

    [Theory]
    [InlineData(2, 10)] // too few points
    [InlineData(5, 5)] // enough points but only 5 days apart
    public void Too_little_data_is_reported_as_insufficient_not_guessed(int points, int spanDays)
    {
        var weights = Enumerable.Range(0, points).Select(i => (Day(spanDays - spanDays * i / Math.Max(points - 1, 1)), 80m - i)).ToList();

        var result = Run(weights: weights);

        var trend = result.Trends.Single(t => t.Metric == "weight_kg");
        Assert.Equal(Direction.InsufficientData, trend.Direction);
        Assert.Null(trend.ChangePerWeek);
        Assert.Equal("needs_3_points_over_7_days", trend.Note);
        Assert.Null(Insight(result, "weight_trend"));
        Assert.Contains(Insight(result, "data_gaps")!.Facts, f => f.Key == "weight_entries");
    }

    [Fact]
    public void A_weak_fit_keeps_the_direction_but_says_it_cannot_be_trusted()
    {
        var jumpy = Weights(29, 0, 80, -0.3m, every: 1).Select((w, i) => (w.Item1, w.Item2 + (i % 2 == 0 ? 2.5m : -2.5m))).ToList();

        var result = Run(weights: jumpy);

        Assert.Equal(Confidence.Low, result.Trends.Single(t => t.Metric == "weight_kg").Confidence);
        Assert.Contains("low_confidence", Insight(result, "weight_trend")!.Caveats);
    }

    [Fact]
    public void Metrics_without_any_data_do_not_appear_at_all()
    {
        var result = Run(weights: Weights(29, 0, 80, -0.5m, every: 1));

        Assert.Equal(["weight_kg"], result.Trends.Select(t => t.Metric));
        Assert.Equal(["weight_kg"], result.Comparisons.Select(c => c.Metric).Where(m => m == "weight_kg"));
    }

    // ---- comparisons ----

    [Fact]
    public void The_previous_period_of_the_same_length_is_compared_and_small_differences_are_noise()
    {
        var meals = Meals(59, 30, 2200).Concat(Meals(29, 0, 2000)).ToList();

        var calories = Run(meals: meals).Comparisons.Single(c => c.Metric == "calories_kcal");

        Assert.Equal((30, 30), (calories.CurrentDays, calories.PreviousDays));
        Assert.Equal((2000m, 2200m, -200m, -9.1m), (calories.CurrentAverage, calories.PreviousAverage, calories.Change, calories.ChangePercent));
        Assert.Equal((Direction.Decreasing, true), (calories.Direction, calories.Meaningful));

        var close = Run(meals: Meals(59, 30, 2050).Concat(Meals(29, 0, 2000)).ToList()).Comparisons.Single(c => c.Metric == "calories_kcal");
        Assert.Equal((Direction.Stable, false), (close.Direction, close.Meaningful));
    }

    [Fact]
    public void A_comparison_needs_three_days_in_each_period()
    {
        var meals = Meals(59, 57, 2200).Concat(Meals(29, 0, 2000)).ToList(); // only 3 days before, fine
        var sparse = Meals(59, 58, 2200).Concat(Meals(29, 0, 2000)).ToList(); // 2 days before

        Assert.NotEqual(Direction.InsufficientData, Run(meals: meals).Comparisons.Single(c => c.Metric == "calories_kcal").Direction);
        var result = Run(meals: sparse).Comparisons.Single(c => c.Metric == "calories_kcal");
        Assert.Equal(Direction.InsufficientData, result.Direction);
        Assert.Null(result.Change);
        Assert.Equal(2, result.PreviousDays);
    }

    // ---- the weight shift (why did it slow down?) ----

    [Fact]
    public void A_slowdown_names_what_else_changed_as_possible_factors_and_never_as_causes()
    {
        var weights = Weights(59, 30, 85, -0.6m).Concat(Weights(29, 0, 82.4m, -0.1m)).ToList();
        var meals = Meals(59, 30, 1800).Concat(Meals(29, 0, 2050)).ToList();
        var sleep = Enumerable.Range(0, 60).Select(d => SleepDay(d, "whoop", d < 30 ? 380 : 430)).ToList();

        var insight = Insight(Run(weights: weights, meals: meals, sleep: sleep), "weight_trend_shift")!;

        Assert.Equal("slowed", Text(insight, "shift"));
        Assert.Equal(Severity.Notable, insight.Severity);
        Assert.Equal(-0.6m, Number(insight, "previous_change_per_week"));
        Assert.Equal(-0.1m, Number(insight, "current_change_per_week"));
        Assert.Equal(250m, Number(insight, "calories_average_change"));
        Assert.Equal(-50m, Number(insight, "sleep_average_change"));
        Assert.Equal(2, Number(insight, "factors_count"));
        Assert.Contains("an association in your data, not proof of a cause", insight.Statement);
        Assert.Contains("association_not_cause", insight.Caveats);
        Assert.Equal(InsightCategory.Combined, insight.Category);
    }

    [Fact]
    public void A_slowdown_with_nothing_else_changing_says_so_instead_of_inventing_a_reason()
    {
        var weights = Weights(59, 30, 85, -0.6m).Concat(Weights(29, 0, 82.4m, -0.1m)).ToList();

        var insight = Insight(Run(weights: weights), "weight_trend_shift")!;

        Assert.Equal(0, Number(insight, "factors_count"));
        Assert.Contains("Nothing else in your data changed enough", insight.Statement);
    }

    [Theory]
    [InlineData(-0.6, 0.4, "reversed")]
    [InlineData(-0.3, -0.7, "accelerated")]
    [InlineData(0.5, 0.0, "slowed")] // gaining, then flat
    public void Other_shifts_are_recognised(double previous, double current, string expected)
    {
        var weights = Weights(59, 30, 85, (decimal)previous).Concat(Weights(29, 0, 85, (decimal)current)).ToList();

        Assert.Equal(expected, Text(Insight(Run(weights: weights), "weight_trend_shift")!, "shift"));
    }

    [Theory]
    [InlineData(-0.5, -0.4)] // a similar pace
    [InlineData(-0.05, -0.05)] // both flat
    [InlineData(-0.1, -0.02)] // previous pace too small to call a change
    public void A_similar_pace_is_not_reported_as_a_shift(double previous, double current)
    {
        var weights = Weights(59, 30, 85, (decimal)previous).Concat(Weights(29, 0, 85, (decimal)current)).ToList();

        Assert.Null(Insight(Run(weights: weights), "weight_trend_shift"));
    }

    [Fact]
    public void A_shift_is_not_reported_when_the_previous_period_has_too_little_data()
    {
        var weights = Weights(29, 0, 85, -0.1m);

        Assert.Null(Insight(Run(weights: weights), "weight_trend_shift"));
    }

    [Fact]
    public void Logging_gaps_are_named_when_the_averages_behind_a_shift_rest_on_few_days()
    {
        var weights = Weights(59, 30, 85, -0.6m).Concat(Weights(29, 0, 82.4m, -0.1m)).ToList();
        var meals = Meals(59, 55, 1800).Concat(Meals(29, 25, 2050)).ToList(); // 5 logged days in each 30

        var insight = Insight(Run(weights: weights, meals: meals), "weight_trend_shift")!;

        Assert.Contains("logging_gaps", insight.Caveats);
    }

    // ---- nutrition ----

    [Fact]
    public void Calories_far_from_the_target_are_notable_and_close_ones_are_just_information()
    {
        var far = Insight(Run(meals: Meals(29, 0, 1500), targets: new NutritionTargetDto(2000, null, null, null, null)), "calories_vs_target")!;
        var near = Insight(Run(meals: Meals(29, 0, 2050), targets: new NutritionTargetDto(2000, null, null, null, null)), "calories_vs_target")!;

        Assert.Equal((Severity.Notable, -25m), (far.Severity, Number(far, "deviation_percent")));
        Assert.Contains("25% below your target of 2000 kcal", far.Statement);
        Assert.Equal((Severity.Info, 2.5m), (near.Severity, Number(near, "deviation_percent")));
    }

    [Fact]
    public void Target_adherence_needs_a_target_and_at_least_five_logged_days()
    {
        Assert.Null(Insight(Run(meals: Meals(29, 0, 1500)), "calories_vs_target"));
        Assert.Null(Insight(Run(meals: Meals(29, 26, 1500), targets: new NutritionTargetDto(2000, null, null, null, null)), "calories_vs_target"));
    }

    [Fact]
    public void Incomplete_food_data_and_logging_gaps_are_flagged_on_the_adherence_insight()
    {
        var meals = Meals(29, 20, 1500, incomplete: true); // 10 of 30 days

        var insight = Insight(Run(meals: meals, targets: new NutritionTargetDto(2000, null, null, null, null)), "calories_vs_target")!;

        Assert.Contains("incomplete_food_data", insight.Caveats);
        Assert.Contains("logging_gaps", insight.Caveats);
    }

    [Fact]
    public void Protein_below_the_target_is_notable()
    {
        var insight = Insight(Run(meals: Meals(29, 0, 2000, protein: 60), targets: new NutritionTargetDto(null, 100, null, null, null)), "protein_vs_target")!;

        Assert.Equal(Severity.Notable, insight.Severity);
        Assert.Equal(-40m, Number(insight, "deviation_percent"));
    }

    [Fact]
    public void Logging_coverage_is_reported_only_when_it_is_low_and_the_window_is_long_enough()
    {
        var sparse = Run(meals: Meals(29, 20, 2000));
        Assert.Equal(10, Number(Insight(sparse, "nutrition_logging_coverage")!, "logged_days"));
        Assert.Contains("logging_gaps", sparse.Limitations);

        Assert.Null(Insight(Run(meals: Meals(29, 5, 2000)), "nutrition_logging_coverage"));
        Assert.Null(Insight(Run(days: 7, meals: Meals(6, 5, 2000)), "nutrition_logging_coverage")); // a week is too short to call it a pattern
    }

    // ---- sleep, recovery and providers ----

    [Fact]
    public void A_shorter_average_night_than_before_is_reported_per_provider_and_a_big_drop_is_notable()
    {
        var sleep = Enumerable.Range(0, 60).Select(d => SleepDay(d, "whoop", d < 30 ? 360 : 420)).ToList();

        var insight = Insight(Run(sleep: sleep), "sleep_duration_change")!;

        Assert.Equal(Severity.Notable, insight.Severity);
        Assert.Equal((360m, 420m, -60m), (Number(insight, "current_average"), Number(insight, "previous_average"), Number(insight, "change")));
        Assert.Contains("(WHOOP)", insight.Statement);
        Assert.Equal("whoop", Text(insight, "provider"));
    }

    [Fact]
    public void Two_providers_are_analysed_separately_and_never_blended()
    {
        var sleep = Enumerable.Range(0, 60).Select(d => SleepDay(d, "whoop", 400, recovery: d < 30 ? 70 : 50))
            .Concat(Enumerable.Range(0, 60).Select(d => SleepDay(d, "otherband", 400, recovery: 30)))
            .ToList();

        var result = Run(sleep: sleep);

        Assert.Equal(["otherband", "whoop"], result.Coverage.Providers.Select(p => p.Provider));
        var recovery = result.Comparisons.Where(c => c.Metric == "recovery_score").ToDictionary(c => c.Provider!);
        Assert.Equal(70m, recovery["whoop"].CurrentAverage);
        Assert.Equal(30m, recovery["otherband"].CurrentAverage);
        Assert.Equal(Direction.Stable, recovery["otherband"].Direction);
        Assert.Equal(["whoop", "otherband"], result.Comparisons.Where(c => c.Metric == "recovery_score").Select(c => c.Provider).OrderDescending());
        Assert.Contains("provider_scores_not_comparable", result.Limitations);
    }

    [Fact]
    public void Three_recovery_scores_far_below_ones_own_average_are_notable_and_labelled_as_the_providers_score()
    {
        var sleep = Enumerable.Range(0, 30).Select(d => SleepDay(d, "whoop", 420, recovery: d < 3 ? 25 : 70)).ToList();

        var insight = Insight(Run(sleep: sleep), "recovery_below_average")!;

        Assert.Equal(Severity.Notable, insight.Severity);
        Assert.Contains("provider_score", insight.Caveats);
        Assert.Contains("WHOOP's own score", insight.Statement);
        Assert.Equal(25m, Number(insight, "latest"));
    }

    [Fact]
    public void One_bad_day_is_not_a_pattern()
    {
        var sleep = Enumerable.Range(0, 30).Select(d => SleepDay(d, "whoop", 420, recovery: d == 0 ? 20 : 70)).ToList();

        Assert.Null(Insight(Run(sleep: sleep), "recovery_below_average"));
    }

    [Fact]
    public void Recovery_after_longer_nights_is_described_as_an_association_with_its_sample_size()
    {
        // alternating short and long nights; recovery follows with a 20 point gap
        var sleep = Enumerable.Range(0, 20).Select(d => d % 2 == 0 ? SleepDay(d, "whoop", 480, recovery: 80) : SleepDay(d, "whoop", 330, recovery: 60)).ToList();

        var insight = Insight(Run(sleep: sleep), "recovery_and_sleep")!;

        Assert.Equal(InsightCategory.Combined, insight.Category);
        Assert.Equal((80m, 60m), (Number(insight, "recovery_after_longer_sleep"), Number(insight, "recovery_after_shorter_sleep")));
        Assert.Contains("an association in your own data, not proof of a cause", insight.Statement);
        Assert.Contains("association_not_cause", insight.Caveats);
        Assert.DoesNotContain("small_sample", insight.Caveats);

        var small = Run(sleep: sleep.Take(12).ToList());
        Assert.Contains("small_sample", Insight(small, "recovery_and_sleep")!.Caveats);
        Assert.Null(Insight(Run(sleep: sleep.Take(8).ToList()), "recovery_and_sleep"));
    }

    [Fact]
    public void An_unrelated_pattern_produces_no_association()
    {
        var sleep = Enumerable.Range(0, 20).Select(d => SleepDay(d, "whoop", d % 2 == 0 ? 480 : 330, recovery: 70)).ToList();

        Assert.Null(Insight(Run(sleep: sleep), "recovery_and_sleep"));
    }

    [Fact]
    public void Very_irregular_sleep_is_described_with_its_range()
    {
        var sleep = Enumerable.Range(0, 10).Select(d => SleepDay(d, "whoop", d % 2 == 0 ? 240 : 520)).ToList();

        var insight = Insight(Run(sleep: sleep), "sleep_variability")!;

        Assert.Equal((240m, 520m, 10m), (Number(insight, "minimum"), Number(insight, "maximum"), Number(insight, "nights")));
    }

    [Fact]
    public void A_clear_hrv_trend_is_reported_but_a_weak_or_flat_one_is_not()
    {
        var rising = Enumerable.Range(0, 30).Select(d => SleepDay(d, "whoop", 420, hrv: 70 - d * 0.5m)).ToList(); // d=0 is the latest day
        var flat = Enumerable.Range(0, 30).Select(d => SleepDay(d, "whoop", 420, hrv: 60)).ToList();

        var insight = Insight(Run(sleep: rising), "hrv_trend")!;
        Assert.Equal("increasing", Text(insight, "direction"));
        Assert.Contains("provider_score", insight.Caveats);
        Assert.Null(Insight(Run(sleep: flat), "hrv_trend"));
    }

    // ---- gaps, ordering, determinism ----

    [Fact]
    public void With_no_data_at_all_the_only_insight_is_what_is_missing()
    {
        var result = Run();

        var gaps = Assert.Single(result.Insights);
        Assert.Equal("data_gaps", gaps.Code);
        Assert.Equal(["weight_entries", "nutrition_logged_days", "device_data"], gaps.Facts.Select(f => f.Key));
        Assert.Empty(result.Trends);
        Assert.Equal(["observational_only", "not_medical_advice"], result.Limitations);
        Assert.Equal((To.AddDays(-29), To), (result.From, result.To));
    }

    [Fact]
    public void Notable_insights_come_first_and_data_quality_notes_last()
    {
        var weights = Weights(59, 30, 85, -0.6m).Concat(Weights(29, 0, 82.4m, -0.1m)).ToList();
        var meals = Meals(29, 20, 2000); // 10 days: coverage note

        var result = Run(weights: weights, meals: meals);

        Assert.Equal("weight_trend_shift", result.Insights[0].Code);
        Assert.Equal(InsightCategory.DataQuality, result.Insights[^1].Category);
    }

    [Fact]
    public void The_same_data_always_gives_the_same_answer()
    {
        var weights = Weights(59, 30, 85, -0.6m).Concat(Weights(29, 0, 82.4m, -0.1m)).ToList();
        var meals = Meals(59, 30, 1800).Concat(Meals(29, 0, 2050)).ToList();

        var first = System.Text.Json.JsonSerializer.Serialize(Run(weights: weights, meals: meals));
        var second = System.Text.Json.JsonSerializer.Serialize(Run(weights: weights, meals: meals));

        Assert.Equal(first, second);
    }

    [Fact]
    public void No_statement_contains_a_causal_claim()
    {
        var weights = Weights(59, 30, 85, -0.6m).Concat(Weights(29, 0, 82.4m, -0.1m)).ToList();
        var meals = Meals(59, 30, 1800).Concat(Meals(29, 0, 2050)).ToList();
        var sleep = Enumerable.Range(0, 60).Select(d => SleepDay(d, "whoop", d % 2 == 0 ? 480 : 330, recovery: d % 2 == 0 ? 80 : 60)).ToList();

        var statements = Run(weights: weights, meals: meals, sleep: sleep, targets: new NutritionTargetDto(2000, 100, null, null, null)).Insights.Select(i => i.Statement);

        foreach (var statement in statements)
        {
            Assert.DoesNotMatch(@"(?i)\b(because|caused|causes|due to|result of|as a result|leads? to|you should|you must|diagnos)", statement);
        }
    }
}
