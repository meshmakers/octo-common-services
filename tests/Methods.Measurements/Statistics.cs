namespace Meshmakers.Octo.Services.Methods.Measurements;

internal static class Statistics
{
    /// <summary>Nearest-rank percentile of an unsorted sample (milliseconds).</summary>
    public static double Percentile(IReadOnlyCollection<double> samples, double percentile)
    {
        var sorted = samples.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public static string Summarize(IReadOnlyCollection<double> samples) =>
        $"n={samples.Count} p50={Percentile(samples, 50):F2} ms p95={Percentile(samples, 95):F2} ms " +
        $"p99={Percentile(samples, 99):F2} ms max={samples.Max():F2} ms";
}
