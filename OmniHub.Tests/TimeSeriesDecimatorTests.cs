using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Reducing 40,000 points to a thousand pixels without losing what happened.
///
/// The first test is the one that matters. Largest-triangle-three-buckets is the algorithm
/// usually reached for here, it renders more prettily than this does, and it drops real
/// extremes -- it preserves the SHAPE of a series, not its values. On thermal data a dropped
/// extreme is an excursion that did not happen, which is the same class of error as a smoother
/// that overshoots. If anyone ever swaps min/max for something prettier, that test fails.
/// </summary>
public class TimeSeriesDecimatorTests
{
    private static readonly DateTime Start = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    private static List<TimePoint> Ramp(int count, double value = 50, double seconds = 2)
    {
        var points = new List<TimePoint>(count);
        for (int i = 0; i < count; i++)
            points.Add(new TimePoint(Start.AddSeconds(i * seconds), value));
        return points;
    }

    private static readonly TimeSpan NoGaps = TimeSpan.FromDays(365);

    /// <summary>
    /// A single-sample spike in the middle of 40,000 flat points survives being reduced to 200
    /// columns. This is the whole reason the algorithm is min/max.
    /// </summary>
    [Fact]
    public void ASingleSampleSpikeSurvivesDecimation()
    {
        var points = Ramp(40_000);
        points[19_999] = new TimePoint(points[19_999].AtUtc, 97.5);

        var segments = TimeSeriesDecimator.Decimate(
            points, Start, points[^1].AtUtc, targetColumns: 200, gapThreshold: NoGaps);

        var drawn = segments.SelectMany(s => s).ToList();

        Assert.Contains(drawn, p => Math.Abs(p.Value - 97.5) < 0.001);
    }

    /// <summary>The lowest point survives too. A dip is as real as a spike.</summary>
    [Fact]
    public void ASingleSampleTroughSurvivesDecimation()
    {
        var points = Ramp(40_000);
        points[7_777] = new TimePoint(points[7_777].AtUtc, 4.25);

        var segments = TimeSeriesDecimator.Decimate(
            points, Start, points[^1].AtUtc, targetColumns: 200, gapThreshold: NoGaps);

        Assert.Contains(segments.SelectMany(s => s), p => Math.Abs(p.Value - 4.25) < 0.001);
    }

    /// <summary>
    /// A hole becomes two segments with nothing joining them. A renderer that drew one line
    /// through this would be drawing across a day the machine was not running.
    /// </summary>
    [Fact]
    public void AHoleSplitsTheSeriesIntoSeparateSegments()
    {
        var points = Ramp(100);
        var after = points[^1].AtUtc.AddSeconds(21_512);   // the real 15 September hole
        for (int i = 0; i < 100; i++) points.Add(new TimePoint(after.AddSeconds(i * 2), 60));

        var segments = TimeSeriesDecimator.Decimate(
            points, Start, points[^1].AtUtc, targetColumns: 400, gapThreshold: TimeSpan.FromSeconds(10));

        Assert.Equal(2, segments.Count);

        // No segment straddles the hole.
        foreach (var segment in segments)
            for (int i = 1; i < segment.Count; i++)
                Assert.True((segment[i].AtUtc - segment[i - 1].AtUtc).TotalSeconds < 21_000);
    }

    /// <summary>At most two points per column per segment, which is the budget being bought.</summary>
    [Fact]
    public void TheColumnBudgetIsRespected()
    {
        var rng = new Random(7);
        var points = Ramp(40_000);
        for (int i = 0; i < points.Count; i++)
            points[i] = new TimePoint(points[i].AtUtc, 40 + rng.NextDouble() * 50);

        var segments = TimeSeriesDecimator.Decimate(
            points, Start, points[^1].AtUtc, targetColumns: 250, gapThreshold: NoGaps);

        Assert.True(TimeSeriesDecimator.Count(segments) <= 2 * 250 + segments.Count,
                    $"drew {TimeSeriesDecimator.Count(segments)} points for a 250-column budget");
    }

    /// <summary>
    /// A series with fewer points than columns comes back unchanged, not doubled. This is the
    /// live case: sixty samples on a two-minute dashboard window.
    /// </summary>
    [Fact]
    public void ASparseSeriesPassesThroughUnchanged()
    {
        var points = Ramp(60);

        var segments = TimeSeriesDecimator.Decimate(
            points, Start, points[^1].AtUtc, targetColumns: 500, gapThreshold: NoGaps);

        Assert.Single(segments);
        Assert.Equal(points.Count, segments[0].Count);
        Assert.Equal(points.Select(p => p.AtUtc), segments[0].Select(p => p.AtUtc));
    }

    /// <summary>
    /// Within a bucket the two extremes are emitted in the order they occurred, so the drawn
    /// line never runs backwards in time.
    /// </summary>
    [Fact]
    public void PointsNeverRunBackwardsInTime()
    {
        var rng = new Random(11);
        var points = Ramp(5_000);
        for (int i = 0; i < points.Count; i++)
            points[i] = new TimePoint(points[i].AtUtc, rng.NextDouble() * 100);

        var segments = TimeSeriesDecimator.Decimate(
            points, Start, points[^1].AtUtc, targetColumns: 120, gapThreshold: NoGaps);

        foreach (var segment in segments)
            for (int i = 1; i < segment.Count; i++)
                Assert.True(segment[i].AtUtc >= segment[i - 1].AtUtc);
    }

    /// <summary>
    /// Empty input yields no segments at all, not one empty segment. A renderer must never be
    /// handed a figure with no points in it.
    /// </summary>
    [Fact]
    public void EmptyInputYieldsNoSegments()
    {
        Assert.Empty(TimeSeriesDecimator.Decimate(
            Array.Empty<TimePoint>(), Start, Start.AddHours(1), 100, NoGaps));

        // Everything outside the window is the same case by a different route.
        Assert.Empty(TimeSeriesDecimator.Decimate(
            Ramp(10), Start.AddDays(5), Start.AddDays(6), 100, NoGaps));
    }

    /// <summary>
    /// A NaN or an infinity is dropped rather than drawn. They reach here from a division by a
    /// zero sample count, and a chart that plots one renders nothing at all from that point on.
    /// </summary>
    [Fact]
    public void NonFiniteValuesAreDropped()
    {
        var points = Ramp(10);
        points[3] = new TimePoint(points[3].AtUtc, double.NaN);
        points[6] = new TimePoint(points[6].AtUtc, double.PositiveInfinity);

        var drawn = TimeSeriesDecimator
            .Decimate(points, Start, points[^1].AtUtc, 100, NoGaps)
            .SelectMany(s => s)
            .ToList();

        Assert.Equal(8, drawn.Count);
        Assert.All(drawn, p => Assert.True(double.IsFinite(p.Value)));
    }
}
