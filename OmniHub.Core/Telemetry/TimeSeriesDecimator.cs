namespace OmniHub.Core.Telemetry;

/// <summary>One value at one instant. The unit a chart is drawn from.</summary>
public readonly record struct TimePoint(DateTime AtUtc, double Value);

/// <summary>
/// Reduces a series to something a chart can draw, without losing anything that happened.
///
/// A day of thermal trace is about 40,000 points and a chart is perhaps a thousand pixels wide,
/// so something has to go. WHICH something is the entire question.
///
/// This uses min/max bucketing: one bucket per pixel column, and each bucket contributes its
/// lowest and highest value in the order they occurred. The alternative usually reached for --
/// largest-triangle-three-buckets -- renders beautifully and DROPS REAL EXTREMES, because it is
/// a shape-preserving summary rather than a value-preserving one. On this data a dropped extreme
/// is a thermal excursion that did not happen, which is the same error as the Catmull-Rom
/// overshoot the old chart control went out of its way to avoid, wearing a different hat.
/// Min/max costs one extra point per column and cannot hide a spike.
///
/// Gaps come back as SEPARATE SEGMENTS rather than as a NaN sentinel inside one list. WPF
/// mishandles NaN inside a figure, and this codebase already demonstrates that NaN sentinels get
/// forgotten at boundaries -- Reading.PreciseTemperatureC needs an explicit IsNaN guard at every
/// consumer, and gained one only after the consumers were found to be missing it. A list of
/// lists cannot be forgotten: a renderer that ignores the segmentation draws nothing at all,
/// which is a visible failure rather than an invisible lie.
/// </summary>
public static class TimeSeriesDecimator
{
    /// <summary>
    /// Buckets <paramref name="points"/> into at most <paramref name="targetColumns"/> columns
    /// across [from, to], split wherever consecutive points are more than
    /// <paramref name="gapThreshold"/> apart.
    ///
    /// Points are assumed sorted by time; TelemetryHistory guarantees that. Anything outside the
    /// window is dropped before bucketing, so the column width is the window's own, not the
    /// data's -- two series over the same window therefore share an x scale exactly.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TimePoint>> Decimate(
        IReadOnlyList<TimePoint> points,
        DateTime fromUtc,
        DateTime toUtc,
        int targetColumns,
        TimeSpan gapThreshold)
    {
        var segments = new List<IReadOnlyList<TimePoint>>();
        if (points.Count == 0 || targetColumns < 1 || toUtc <= fromUtc) return segments;

        long windowTicks = (toUtc - fromUtc).Ticks;
        long columnTicks = Math.Max(1, windowTicks / targetColumns);

        var current = new List<TimePoint>();

        // The bucket being accumulated: its index, and the extremes seen in it so far.
        long bucket = long.MinValue;
        TimePoint min = default, max = default;
        bool haveBucket = false;

        void FlushBucket()
        {
            if (!haveBucket) return;

            // One point, or a flat bucket: emit it once. This is what makes a sparse series --
            // fewer points than columns -- pass through unchanged rather than doubling.
            if (min.AtUtc == max.AtUtc) current.Add(min);
            else if (min.AtUtc < max.AtUtc) { current.Add(min); current.Add(max); }
            else { current.Add(max); current.Add(min); }

            haveBucket = false;
        }

        void FlushSegment()
        {
            FlushBucket();
            if (current.Count > 0) segments.Add(current.ToArray());
            current = new List<TimePoint>();
        }

        DateTime? previous = null;

        foreach (var p in points)
        {
            if (p.AtUtc < fromUtc || p.AtUtc > toUtc) continue;
            if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

            // A hole ends the segment. The renderer then draws two lines with nothing between
            // them, which is the honest picture of a machine that was not running.
            if (previous is { } prev && p.AtUtc - prev > gapThreshold) FlushSegment();
            previous = p.AtUtc;

            long index = (p.AtUtc.Ticks - fromUtc.Ticks) / columnTicks;

            if (!haveBucket || index != bucket)
            {
                FlushBucket();
                bucket = index;
                min = max = p;
                haveBucket = true;
                continue;
            }

            if (p.Value < min.Value) min = p;
            if (p.Value > max.Value) max = p;
        }

        FlushSegment();
        return segments;
    }

    /// <summary>
    /// Total points across every segment. What a renderer is actually being asked to draw.
    /// </summary>
    public static int Count(IReadOnlyList<IReadOnlyList<TimePoint>> segments)
    {
        int n = 0;
        foreach (var s in segments) n += s.Count;
        return n;
    }
}
