using System.Globalization;
using MevaCare.AI.HealthCompanion.Contracts.Insights;
using MevaCare.AI.HealthCompanion.Contracts.Measurements;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;

namespace MevaCare.AI.HealthCompanion.Modules.Insights.Analysis;

internal sealed record NutritionDay(DateOnly Date, decimal Calories, decimal ProteinG, bool IncompleteData);

/// <summary>Everything the engine needs, already loaded: it never touches the database, the clock or a model.</summary>
internal sealed record InsightInput(
    DateOnly To,
    int Days,
    IReadOnlyList<(DateOnly Date, decimal Kilograms)> Weights,
    IReadOnlyList<NutritionDay> NutritionDays,
    NutritionTargetDto Targets,
    IReadOnlyList<SleepRecoveryDayDto> SleepDays);

/// <param name="TrendBandPerWeek">A slope smaller than this per week counts as "stable".</param>
/// <param name="MeaningfulChange">A difference between two periods smaller than this is treated as noise.</param>
internal sealed record MetricSpec(string Key, string Unit, double TrendBandPerWeek, double MeaningfulChange, int Decimals);

/// <summary>
/// Turns stored measurements into trends, period comparisons and insights with plain rules. The thresholds below are product choices,
/// not clinical facts: they only decide what is worth mentioning, and every insight carries the numbers it is based on.
/// Association is never presented as cause, provider scores are analysed per provider, and weak data says so.
/// </summary>
internal static class InsightEngine
{
    public static readonly MetricSpec Weight = new("weight_kg", "kg", 0.1, 0.3, 2);
    public static readonly MetricSpec Calories = new("calories_kcal", "kcal", 50, 100, 0);
    public static readonly MetricSpec Protein = new("protein_g", "g", 3, 10, 1);
    public static readonly MetricSpec Sleep = new("sleep_minutes", "min", 10, 20, 0);
    public static readonly MetricSpec Recovery = new("recovery_score", "score", 2, 5, 1);
    public static readonly MetricSpec Hrv = new("hrv_ms", "ms", 1, 4, 1);
    public static readonly MetricSpec RestingHeartRate = new("resting_heart_rate_bpm", "bpm", 0.5, 2, 1);

    private const int MinPoints = 3;
    private const int MinSpanDays = 7;
    private const int MinDaysToCompare = 3;

    public static HealthInsightsDto Analyse(InsightInput input)
    {
        var current = new DateRange(input.To.AddDays(-(input.Days - 1)), input.To);
        var previous = new DateRange(current.Start.AddDays(-input.Days), current.Start.AddDays(-1));

        var series = Series.From(input);
        var trends = new List<TrendDto>();
        var comparisons = new List<PeriodComparisonDto>();
        var providers = input.SleepDays.Select(d => d.Provider).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        void AddMetric(MetricSpec spec, string? provider, IReadOnlyList<Sample> samples)
        {
            trends.Add(BuildTrend(spec, provider, samples.Where(s => current.Contains(s.Date)).ToList()));
            comparisons.Add(Compare(spec, provider, samples.Where(s => current.Contains(s.Date)).ToList(), samples.Where(s => previous.Contains(s.Date)).ToList()));
        }

        AddMetric(Weight, null, series.Weight);
        AddMetric(Calories, null, series.Calories);
        AddMetric(Protein, null, series.Protein);
        foreach (var provider in providers)
        {
            AddMetric(Sleep, provider, series.SleepMinutes(provider));
            AddMetric(Recovery, provider, series.RecoveryScore(provider));
            AddMetric(Hrv, provider, series.Hrv(provider));
            AddMetric(RestingHeartRate, provider, series.RestingHeartRate(provider));
        }

        // Trends and comparisons without any data are noise for the reader: keep only metrics that have something to say.
        trends = trends.Where(t => t.Points > 0).ToList();
        comparisons = comparisons.Where(c => c.CurrentDays > 0 || c.PreviousDays > 0).ToList();

        var coverage = new DataCoverageDto(
            series.Weight.Count(s => current.Contains(s.Date)),
            series.Calories.Count(s => current.Contains(s.Date)),
            input.Days,
            providers.Select(p => new ProviderCoverageDto(
                p,
                series.SleepMinutes(p).Count(s => current.Contains(s.Date)),
                series.RecoveryScore(p).Count(s => current.Contains(s.Date)))).ToList());

        var insights = new InsightRules(input, current, previous, series, trends, comparisons, providers, coverage).Build();
        var limitations = new List<string> { "observational_only", "not_medical_advice" };
        if (providers.Count > 0)
        {
            limitations.Add("provider_scores_not_comparable");
        }

        if (coverage.NutritionLoggedDays > 0 && coverage.NutritionLoggedDays * 2 < coverage.NutritionWindowDays)
        {
            limitations.Add("logging_gaps");
        }

        return new HealthInsightsDto(current.Start, current.End, input.Days, coverage, trends, comparisons, insights, limitations);
    }

    internal static TrendDto BuildTrend(MetricSpec spec, string? provider, IReadOnlyList<Sample> samples)
    {
        var daily = TrendMath.OnePerDay(samples);
        if (daily.Count == 0)
        {
            return new TrendDto(spec.Key, spec.Unit, provider, Direction.InsufficientData, Confidence.Low, 0, 0, null, null, null, null, null, "no_data");
        }

        var first = daily[0];
        var last = daily[^1];
        var span = last.Date.DayNumber - first.Date.DayNumber;
        var average = Round(daily.Average(s => s.Value), spec.Decimals);
        var fit = daily.Count >= MinPoints && span >= MinSpanDays ? TrendMath.Fit(daily) : null;
        if (fit is null)
        {
            return new TrendDto(
                spec.Key, spec.Unit, provider, Direction.InsufficientData, Confidence.Low, daily.Count, span, average,
                Round(first.Value, spec.Decimals), Round(last.Value, spec.Decimals), null, null, "needs_3_points_over_7_days");
        }

        var perWeek = fit.SlopePerDay * 7;
        var direction = Math.Abs(perWeek) < spec.TrendBandPerWeek ? Direction.Stable : perWeek > 0 ? Direction.Increasing : Direction.Decreasing;
        return new TrendDto(
            spec.Key, spec.Unit, provider, direction, TrendMath.ConfidenceOf(fit), daily.Count, span, average,
            Round(first.Value, spec.Decimals), Round(last.Value, spec.Decimals), Round(perWeek, Math.Max(spec.Decimals, 1)), Round(fit.RSquared, 2), null);
    }

    internal static PeriodComparisonDto Compare(MetricSpec spec, string? provider, IReadOnlyList<Sample> current, IReadOnlyList<Sample> previous)
    {
        var cur = TrendMath.OnePerDay(current);
        var prev = TrendMath.OnePerDay(previous);
        var curAverage = cur.Count == 0 ? (double?)null : cur.Average(s => s.Value);
        var prevAverage = prev.Count == 0 ? (double?)null : prev.Average(s => s.Value);
        if (cur.Count < MinDaysToCompare || prev.Count < MinDaysToCompare || curAverage is null || prevAverage is null)
        {
            return new PeriodComparisonDto(
                spec.Key, spec.Unit, provider, cur.Count, prev.Count, Round(curAverage, spec.Decimals), Round(prevAverage, spec.Decimals),
                null, null, Direction.InsufficientData, false);
        }

        var change = curAverage.Value - prevAverage.Value;
        var meaningful = Math.Abs(change) >= spec.MeaningfulChange;
        var direction = !meaningful ? Direction.Stable : change > 0 ? Direction.Increasing : Direction.Decreasing;
        decimal? percent = prevAverage.Value == 0 ? null : Round(change / prevAverage.Value * 100, 1);
        return new PeriodComparisonDto(
            spec.Key, spec.Unit, provider, cur.Count, prev.Count, Round(curAverage, spec.Decimals), Round(prevAverage, spec.Decimals),
            Round(change, Math.Max(spec.Decimals, 1)), percent, direction, meaningful);
    }

    internal static decimal? Round(double? value, int decimals) => value is null ? null : Round(value.Value, decimals);

    internal static decimal Round(double value, int decimals) => Math.Round((decimal)value, decimals, MidpointRounding.AwayFromZero);

    internal static string ProviderName(string provider) =>
        provider == "whoop" ? "WHOOP" : char.ToUpperInvariant(provider[0]) + provider[1..];

    internal static string Text(decimal value, int decimals = 1) => value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);

    internal static string Signed(decimal value, int decimals = 1) => (value > 0 ? "+" : "") + Text(value, decimals);
}

internal readonly record struct DateRange(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;
}

/// <summary>The input reshaped into one list of samples per metric (and provider).</summary>
internal sealed class Series
{
    private Dictionary<(string Metric, string Provider), List<Sample>> _byProvider = [];

    public IReadOnlyList<Sample> Weight { get; private init; } = [];
    public IReadOnlyList<Sample> Calories { get; private init; } = [];
    public IReadOnlyList<Sample> Protein { get; private init; } = [];
    public IReadOnlyDictionary<DateOnly, bool> IncompleteNutritionDays { get; private init; } = new Dictionary<DateOnly, bool>();

    public IReadOnlyList<Sample> SleepMinutes(string provider) => Of("sleep", provider);
    public IReadOnlyList<Sample> RecoveryScore(string provider) => Of("recovery", provider);
    public IReadOnlyList<Sample> Hrv(string provider) => Of("hrv", provider);
    public IReadOnlyList<Sample> RestingHeartRate(string provider) => Of("rhr", provider);

    private IReadOnlyList<Sample> Of(string metric, string provider) =>
        _byProvider.TryGetValue((metric, provider), out var list) ? list : [];

    public static Series From(InsightInput input)
    {
        var byProvider = new Dictionary<(string, string), List<Sample>>();
        void Add(string metric, string provider, DateOnly date, decimal? value)
        {
            if (value is null)
            {
                return;
            }

            if (!byProvider.TryGetValue((metric, provider), out var list))
            {
                byProvider[(metric, provider)] = list = [];
            }

            list.Add(new Sample(date, (double)value.Value));
        }

        foreach (var day in input.SleepDays)
        {
            Add("sleep", day.Provider, day.Date, day.Sleep?.AsleepMinutes);
            Add("recovery", day.Provider, day.Date, day.Recovery?.Score);
            Add("hrv", day.Provider, day.Date, day.Recovery?.HrvRmssdMilliseconds);
            Add("rhr", day.Provider, day.Date, day.Recovery?.RestingHeartRate);
        }

        return new Series
        {
            Weight = input.Weights.Select(w => new Sample(w.Date, (double)w.Kilograms)).ToList(),
            Calories = input.NutritionDays.Select(d => new Sample(d.Date, (double)d.Calories)).ToList(),
            Protein = input.NutritionDays.Select(d => new Sample(d.Date, (double)d.ProteinG)).ToList(),
            IncompleteNutritionDays = input.NutritionDays.ToDictionary(d => d.Date, d => d.IncompleteData),
            _byProvider = byProvider,
        };
    }
}

/// <summary>The insight rules. Each rule states its conditions, produces facts and names its caveats.</summary>
internal sealed class InsightRules(
    InsightInput input,
    DateRange current,
    DateRange previous,
    Series series,
    IReadOnlyList<TrendDto> trends,
    IReadOnlyList<PeriodComparisonDto> comparisons,
    IReadOnlyList<string> providers,
    DataCoverageDto coverage)
{
    private readonly List<InsightDto> _insights = [];

    private TrendDto? Trend(string metric, string? provider = null) => trends.FirstOrDefault(t => t.Metric == metric && t.Provider == provider);

    private PeriodComparisonDto? Comparison(string metric, string? provider = null) =>
        comparisons.FirstOrDefault(c => c.Metric == metric && c.Provider == provider);

    private bool LoggingGaps => coverage.NutritionLoggedDays * 2 < coverage.NutritionWindowDays;

    private static InsightFact Fact(string key, decimal? number, string? unit = null) => new(key, number, null, unit);

    private static InsightFact Fact(string key, string text) => new(key, null, text, null);

    private void Add(string code, string category, string severity, string statement, List<InsightFact> facts, params string?[] caveats) =>
        _insights.Add(new InsightDto(code, category, severity, statement, facts, caveats.Where(c => c is not null).Cast<string>().Distinct().ToList()));

    public IReadOnlyList<InsightDto> Build()
    {
        DataGaps();
        WeightTrend();
        WeightTrendShift();
        NutritionCoverage();
        TargetAdherence(InsightEngine.Calories, input.Targets.Calories, "calories_vs_target", 15, "kcal");
        TargetAdherence(InsightEngine.Protein, input.Targets.ProteinG, "protein_vs_target", 10, "g");
        foreach (var provider in providers)
        {
            SleepChange(provider);
            SleepVariability(provider);
            RecoveryBelowAverage(provider);
            RecoveryAndSleep(provider);
            DeviceTrend(provider, InsightEngine.Hrv, "hrv_trend", InsightCategory.Recovery, "HRV");
            DeviceTrend(provider, InsightEngine.RestingHeartRate, "resting_heart_rate_trend", InsightCategory.Recovery, "resting heart rate");
        }

        return _insights
            .OrderByDescending(i => i.Severity == Severity.Notable)
            .ThenBy(i => i.Category == InsightCategory.DataQuality ? 1 : 0)
            .ToList();
    }

    private void DataGaps()
    {
        var weight = Trend(InsightEngine.Weight.Key);
        var missing = new List<InsightFact>();
        if (weight is null || weight.Direction == Direction.InsufficientData)
        {
            missing.Add(Fact("weight_entries", weight?.Points ?? 0));
        }

        if (coverage.NutritionLoggedDays == 0)
        {
            missing.Add(Fact("nutrition_logged_days", 0));
        }

        if (providers.Count == 0)
        {
            missing.Add(Fact("device_data", "none"));
        }

        if (missing.Count > 0)
        {
            Add("data_gaps", InsightCategory.DataQuality, Severity.Info,
                "Some trends cannot be calculated yet because there is not enough data: " + string.Join(", ", missing.Select(m => m.Key.Replace('_', ' '))) + ".",
                missing);
        }
    }

    private void WeightTrend()
    {
        var trend = Trend(InsightEngine.Weight.Key);
        if (trend is null || trend.Direction == Direction.InsufficientData)
        {
            return;
        }

        var perWeek = trend.ChangePerWeek!.Value;
        var statement = trend.Direction == Direction.Stable
            ? $"Your weight has been stable: about {InsightEngine.Signed(perWeek, 2)} kg per week ({trend.Points} days with entries over {trend.SpanDays} days, {trend.Confidence} confidence)."
            : $"Your weight is {trend.Direction} by about {InsightEngine.Text(Math.Abs(perWeek), 2)} kg per week ({trend.Points} days with entries over {trend.SpanDays} days, {trend.Confidence} confidence).";
        Add("weight_trend", InsightCategory.Weight, Severity.Info, statement,
            [Fact("direction", trend.Direction), Fact("change_per_week", perWeek, "kg"), Fact("points", trend.Points), Fact("span_days", trend.SpanDays), Fact("confidence", trend.Confidence)],
            "weight_varies_daily", trend.Confidence == Confidence.Low ? "low_confidence" : null);
    }

    private void WeightTrendShift()
    {
        var currentTrend = Trend(InsightEngine.Weight.Key);
        if (currentTrend is null || currentTrend.Direction == Direction.InsufficientData)
        {
            return;
        }

        var previousSamples = series.Weight.Where(s => previous.Contains(s.Date)).ToList();
        var previousTrend = InsightEngine.BuildTrend(InsightEngine.Weight, null, previousSamples);
        if (previousTrend.Direction == Direction.InsufficientData)
        {
            return;
        }

        var p = previousTrend.ChangePerWeek!.Value;
        var c = currentTrend.ChangePerWeek!.Value;
        var band = (decimal)InsightEngine.Weight.TrendBandPerWeek;
        string? shift = null;
        if (Math.Abs(p) >= 0.15m)
        {
            if (Math.Sign(p) != Math.Sign(c) && Math.Abs(c) >= band)
            {
                shift = "reversed";
            }
            else if (Math.Abs(c) <= 0.5m * Math.Abs(p))
            {
                shift = "slowed";
            }
            else if (Math.Sign(p) == Math.Sign(c) && Math.Abs(c) >= 1.5m * Math.Abs(p))
            {
                shift = "accelerated";
            }
        }

        if (shift is null)
        {
            return;
        }

        var facts = new List<InsightFact>
        {
            Fact("shift", shift),
            Fact("previous_change_per_week", p, "kg"),
            Fact("current_change_per_week", c, "kg"),
        };
        var factorTexts = new List<string>();

        var calories = Comparison(InsightEngine.Calories.Key);
        if (calories is { Direction: not Direction.InsufficientData })
        {
            facts.Add(Fact("calories_logged_days_current", calories.CurrentDays));
            facts.Add(Fact("calories_logged_days_previous", calories.PreviousDays));
            if (calories.Meaningful)
            {
                facts.Add(Fact("calories_average_change", calories.Change, "kcal"));
                factorTexts.Add($"your average logged calories changed by {InsightEngine.Signed(calories.Change!.Value, 0)} kcal per logged day");
            }
        }

        foreach (var provider in providers)
        {
            var sleep = Comparison(InsightEngine.Sleep.Key, provider);
            if (sleep is { Meaningful: true })
            {
                facts.Add(Fact("sleep_average_change", sleep.Change, "min"));
                facts.Add(Fact("sleep_provider", provider));
                factorTexts.Add($"your average time asleep ({InsightEngine.ProviderName(provider)}) changed by {InsightEngine.Signed(sleep.Change!.Value, 0)} minutes");
                break;
            }
        }

        facts.Add(Fact("factors_count", factorTexts.Count));
        var statement = $"Your weight trend {shift}: about {InsightEngine.Signed(p, 2)} kg per week in the previous {input.Days} days, {InsightEngine.Signed(c, 2)} kg per week in the last {input.Days}."
            + (factorTexts.Count > 0
                ? " Over the same time " + string.Join(" and ", factorTexts) + " (an association in your data, not proof of a cause)."
                : " Nothing else in your data changed enough to point to a possible factor.");
        Add("weight_trend_shift", InsightCategory.Combined, Severity.Notable, statement, facts,
            "association_not_cause", "weight_varies_daily",
            previousTrend.Confidence == Confidence.Low || currentTrend.Confidence == Confidence.Low ? "low_confidence" : null,
            LoggingGaps ? "logging_gaps" : null);
    }

    private void NutritionCoverage()
    {
        if (input.Days < 14 || coverage.NutritionLoggedDays == 0 || !LoggingGaps)
        {
            return;
        }

        Add("nutrition_logging_coverage", InsightCategory.DataQuality, Severity.Info,
            $"You logged meals on {coverage.NutritionLoggedDays} of the last {coverage.NutritionWindowDays} days. Nutrition averages use only those days, so they may not describe the days you did not log.",
            [Fact("logged_days", coverage.NutritionLoggedDays), Fact("window_days", coverage.NutritionWindowDays)], "logging_gaps");
    }

    private void TargetAdherence(MetricSpec spec, decimal? target, string code, int notablePercent, string unit)
    {
        var trend = Trend(spec.Key);
        if (target is null or <= 0 || trend is null || trend.Points < 5 || trend.Average is null)
        {
            return;
        }

        var average = trend.Average.Value;
        var deviation = Math.Round((average - target.Value) / target.Value * 100, 1);
        var anyIncomplete = series.IncompleteNutritionDays.Where(d => current.Contains(d.Key)).Any(d => d.Value);
        var direction = deviation > 0 ? "above" : "below";
        var statement = Math.Abs(deviation) < 1
            ? $"Your average {spec.Key.Split('_')[0]} on logged days ({InsightEngine.Text(average, 0)} {unit}) matches your target ({InsightEngine.Text(target.Value, 0)} {unit})."
            : $"Your average {spec.Key.Split('_')[0]} on logged days is {InsightEngine.Text(average, 0)} {unit}, {InsightEngine.Text(Math.Abs(deviation))}% {direction} your target of {InsightEngine.Text(target.Value, 0)} {unit} ({trend.Points} logged days).";
        Add(code, InsightCategory.Nutrition, Math.Abs(deviation) > notablePercent ? Severity.Notable : Severity.Info, statement,
            [Fact("average", average, unit), Fact("target", target.Value, unit), Fact("deviation_percent", deviation, "%"), Fact("logged_days", trend.Points)],
            LoggingGaps ? "logging_gaps" : null, anyIncomplete ? "incomplete_food_data" : null);
    }

    private void SleepChange(string provider)
    {
        var sleep = Comparison(InsightEngine.Sleep.Key, provider);
        if (sleep is not { Meaningful: true })
        {
            return;
        }

        var change = sleep.Change!.Value;
        var name = InsightEngine.ProviderName(provider);
        Add("sleep_duration_change", InsightCategory.Sleep, change <= -30 ? Severity.Notable : Severity.Info,
            $"Your average time asleep ({name}) is {InsightEngine.Text(sleep.CurrentAverage!.Value, 0)} minutes, {InsightEngine.Text(Math.Abs(change), 0)} minutes {(change > 0 ? "more" : "less")} than in the previous {input.Days} days ({InsightEngine.Text(sleep.PreviousAverage!.Value, 0)} minutes).",
            [Fact("provider", provider), Fact("current_average", sleep.CurrentAverage, "min"), Fact("previous_average", sleep.PreviousAverage, "min"), Fact("change", change, "min"), Fact("current_days", sleep.CurrentDays), Fact("previous_days", sleep.PreviousDays)]);
    }

    private void SleepVariability(string provider)
    {
        var minutes = series.SleepMinutes(provider).Where(s => current.Contains(s.Date)).Select(s => s.Value).ToList();
        if (minutes.Count < 7)
        {
            return;
        }

        var deviation = TrendMath.StandardDeviation(minutes);
        if (deviation < 90)
        {
            return;
        }

        Add("sleep_variability", InsightCategory.Sleep, Severity.Info,
            $"Your time asleep ({InsightEngine.ProviderName(provider)}) varied a lot: between {InsightEngine.Text((decimal)minutes.Min(), 0)} and {InsightEngine.Text((decimal)minutes.Max(), 0)} minutes over {minutes.Count} nights (standard deviation {InsightEngine.Text((decimal)deviation, 0)} minutes).",
            [Fact("provider", provider), Fact("minimum", (decimal)minutes.Min(), "min"), Fact("maximum", (decimal)minutes.Max(), "min"), Fact("standard_deviation", (decimal)deviation, "min"), Fact("nights", minutes.Count)]);
    }

    private void RecoveryBelowAverage(string provider)
    {
        var scores = series.RecoveryScore(provider).Where(s => current.Contains(s.Date)).OrderBy(s => s.Date).ToList();
        if (scores.Count < 7)
        {
            return;
        }

        var average = scores.Average(s => s.Value);
        var lastThree = scores.TakeLast(3).ToList();
        if (!lastThree.All(s => s.Value <= average - 15))
        {
            return;
        }

        var name = InsightEngine.ProviderName(provider);
        Add("recovery_below_average", InsightCategory.Recovery, Severity.Notable,
            $"Your last three {name} recovery scores ({string.Join(", ", lastThree.Select(s => InsightEngine.Text((decimal)s.Value, 0)))}) are all at least 15 points below your {input.Days}-day average of {InsightEngine.Text((decimal)average, 0)}. This is {name}'s own score, compared only with your own history.",
            [Fact("provider", provider), Fact("average", (decimal)average, "score"), Fact("latest", (decimal)lastThree[^1].Value, "score"), Fact("days_with_score", scores.Count)],
            "provider_score");
    }

    private void RecoveryAndSleep(string provider)
    {
        var sleepByDate = series.SleepMinutes(provider).Where(s => current.Contains(s.Date)).ToDictionary(s => s.Date, s => s.Value);
        var pairs = series.RecoveryScore(provider)
            .Where(s => current.Contains(s.Date) && sleepByDate.ContainsKey(s.Date))
            .Select(s => (Sleep: sleepByDate[s.Date], Recovery: s.Value))
            .ToList();
        if (pairs.Count < 10)
        {
            return;
        }

        var median = TrendMath.Median(pairs.Select(p => p.Sleep).ToList());
        var shorter = pairs.Where(p => p.Sleep < median).Select(p => p.Recovery).ToList();
        var longer = pairs.Where(p => p.Sleep >= median).Select(p => p.Recovery).ToList();
        if (shorter.Count < 5 || longer.Count < 5)
        {
            return;
        }

        var difference = longer.Average() - shorter.Average();
        if (Math.Abs(difference) < 8)
        {
            return;
        }

        var name = InsightEngine.ProviderName(provider);
        Add("recovery_and_sleep", InsightCategory.Combined, Severity.Info,
            $"On days when you slept longer than your median ({InsightEngine.Text((decimal)median, 0)} minutes), your {name} recovery score averaged {InsightEngine.Text((decimal)longer.Average(), 0)} versus {InsightEngine.Text((decimal)shorter.Average(), 0)} on shorter nights ({pairs.Count} days). This is an association in your own data, not proof of a cause.",
            [Fact("provider", provider), Fact("median_sleep", (decimal)median, "min"), Fact("recovery_after_longer_sleep", (decimal)longer.Average(), "score"), Fact("recovery_after_shorter_sleep", (decimal)shorter.Average(), "score"), Fact("days", pairs.Count)],
            "association_not_cause", "provider_score", pairs.Count < 20 ? "small_sample" : null);
    }

    private void DeviceTrend(string provider, MetricSpec spec, string code, string category, string label)
    {
        var trend = Trend(spec.Key, provider);
        if (trend is null || trend.Direction is Direction.InsufficientData or Direction.Stable || trend.Confidence == Confidence.Low)
        {
            return;
        }

        var name = InsightEngine.ProviderName(provider);
        Add(code, category, Severity.Info,
            $"Your {label} ({name}) is {trend.Direction}, by about {InsightEngine.Text(Math.Abs(trend.ChangePerWeek!.Value))} {spec.Unit} per week ({trend.Points} days, {trend.Confidence} confidence).",
            [Fact("provider", provider), Fact("direction", trend.Direction), Fact("change_per_week", trend.ChangePerWeek, spec.Unit), Fact("points", trend.Points), Fact("confidence", trend.Confidence)],
            "provider_score");
    }
}
