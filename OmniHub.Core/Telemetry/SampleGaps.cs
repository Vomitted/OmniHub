namespace OmniHub.Core.Telemetry;

/// <summary>
/// Finds the holes in a series of timestamps.
///
/// This exists because the holes are real and large. thermal-2026-09-15.csv alone contains gaps
/// of 21,512 s, 3,000 s, 1,810 s, 1,539 s and 1,068 s -- the machine hung, or was asleep, or the
/// application was not running. A chart that joined the points either side of the largest would
/// draw a straight line across roughly a quarter of a one-day view, on data that does not exist.
/// That is the same fabrication this project refuses everywhere else, wearing a different hat.
///
/// The threshold is derived from the data rather than fixed. The streams tick at 2.31 s
/// (thermal), 5 s (network), about 70 s (poll timing) and 1 s (frame time), so a single constant
/// would be wrong for three of the four: too tight and every normal interval reads as a gap, too
/// loose and a real two-minute hole disappears.
/// </summary>
public static class SampleGaps
{
    /// <summary>
    /// How much larger than the typical interval a hole must be before it counts.
    ///
    /// A calibration knob rather than a constant. Three is comfortably above the jitter these
    /// loops actually show -- the thermal poll's own measured spread is a few hundred
    /// milliseconds around 2.31 s -- and comfortably below any hole worth drawing as one.
    /// </summary>
    public const double DefaultFactor = 3.0;

    /// <summary>
    /// The median interval between consecutive timestamps, or null with fewer than two.
    ///
    /// Median, not mean. A single six-hour hole drags a mean across a day's worth of two-second
    /// samples far enough to hide every other gap behind it, which is precisely backwards.
    /// </summary>
    public static TimeSpan? MedianInterval(IReadOnlyList<DateTime> timestamps)
    {
        if (timestamps.Count < 2) return null;

        var deltas = new List<double>(timestamps.Count - 1);
        for (int i = 1; i < timestamps.Count; i++)
        {
            double seconds = (timestamps[i] - timestamps[i - 1]).TotalSeconds;
            if (seconds > 0) deltas.Add(seconds);
        }

        if (deltas.Count == 0) return null;

        deltas.Sort();
        return TimeSpan.FromSeconds(deltas[deltas.Count / 2]);
    }

    /// <summary>Holes longer than an explicit threshold.</summary>
    public static IReadOnlyList<(DateTime From, DateTime To)> Find(
        IReadOnlyList<DateTime> timestamps, TimeSpan threshold)
    {
        var gaps = new List<(DateTime, DateTime)>();

        for (int i = 1; i < timestamps.Count; i++)
            if (timestamps[i] - timestamps[i - 1] > threshold)
                gaps.Add((timestamps[i - 1], timestamps[i]));

        return gaps;
    }

    /// <summary>
    /// Holes longer than <paramref name="factor"/> times the series' own median interval.
    ///
    /// Returns nothing when there is not enough data to establish what normal looks like. A
    /// two-sample series has one interval and no way to say whether it is typical, and guessing
    /// there would mark the only gap it has as either certain or impossible.
    /// </summary>
    public static IReadOnlyList<(DateTime From, DateTime To)> Find(
        IReadOnlyList<DateTime> timestamps, double factor = DefaultFactor)
    {
        if (MedianInterval(timestamps) is not { } median) return Array.Empty<(DateTime, DateTime)>();
        return Find(timestamps, median * factor);
    }
}
