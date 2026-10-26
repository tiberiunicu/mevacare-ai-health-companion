using MevaCare.AI.HealthCompanion.Contracts.Insights;

namespace MevaCare.AI.HealthCompanion.Modules.Insights.Analysis;

/// <summary>One observation of a metric on a local date.</summary>
internal readonly record struct Sample(DateOnly Date, double Value);

internal sealed record LinearFit(double SlopePerDay, double RSquared, int Points, int SpanDays);

/// <summary>Plain, inspectable statistics. No machine learning: every number can be recomputed by hand from the stored data.</summary>
internal static class TrendMath
{
    /// <summary>Ordinary least squares of value against calendar day. Null with fewer than two distinct days.</summary>
    public static LinearFit? Fit(IReadOnlyList<Sample> samples)
    {
        if (samples.Count < 2)
        {
            return null;
        }

        var first = samples.Min(s => s.Date);
        var xs = samples.Select(s => (double)(s.Date.DayNumber - first.DayNumber)).ToArray();
        var ys = samples.Select(s => s.Value).ToArray();
        var span = (int)(xs.Max() - xs.Min());
        if (span == 0)
        {
            return null;
        }

        var meanX = xs.Average();
        var meanY = ys.Average();
        var sxx = xs.Sum(x => (x - meanX) * (x - meanX));
        var sxy = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        var slope = sxy / sxx;
        var intercept = meanY - slope * meanX;
        var totalVariance = ys.Sum(y => (y - meanY) * (y - meanY));
        var residual = xs.Zip(ys, (x, y) => Math.Pow(y - (intercept + slope * x), 2)).Sum();
        var rSquared = totalVariance == 0 ? 0 : Math.Max(0, 1 - residual / totalVariance);
        return new LinearFit(slope, rSquared, samples.Count, span);
    }

    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Several entries on one day count once (their mean): a heavy logger must not outweigh a light one.</summary>
    public static IReadOnlyList<Sample> OnePerDay(IEnumerable<Sample> samples) =>
        samples.GroupBy(s => s.Date).OrderBy(g => g.Key).Select(g => new Sample(g.Key, g.Average(s => s.Value))).ToList();

    /// <summary>
    /// Confidence is a transparent rule of data volume, time span and fit, not a probability:
    /// high = 14+ points over 21+ days with R² ≥ 0.5, low = fewer than 5 points, under 10 days or R² under 0.15.
    /// </summary>
    public static string ConfidenceOf(LinearFit fit) =>
        fit.Points < 5 || fit.SpanDays < 10 || fit.RSquared < 0.15
            ? Confidence.Low
            : fit.Points >= 14 && fit.SpanDays >= 21 && fit.RSquared >= 0.5 ? Confidence.High : Confidence.Moderate;
}
