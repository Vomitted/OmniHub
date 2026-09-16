using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Finding the holes, so a chart can draw them as holes.
///
/// The numbers here are the real ones. thermal-2026-09-15.csv contains gaps of 21,512 s,
/// 3,000 s, 1,810 s, 1,539 s and 1,068 s -- the machine hung, or slept, or the application was
/// not running. A chart joining the points either side of the largest would draw a straight line
/// across about a quarter of a one-day view, on data that does not exist, which is the same
/// fabrication this project refuses everywhere else.
/// </summary>
public class SampleGapsTests
{
    /// <summary>A 2.31 s cadence with the jitter the poll loop actually shows.</summary>
    private static List<DateTime> Cadence(int count, DateTime start, double seconds = 2.31)
    {
        var stamps = new List<DateTime>(count);
        var t = start;
        var rng = new Random(1);

        for (int i = 0; i < count; i++)
        {
            stamps.Add(t);
            // +/- 0.3 s, which is roughly what the measured interval varies by in practice.
            t = t.AddSeconds(seconds + (rng.NextDouble() - 0.5) * 0.6);
        }

        return stamps;
    }

    /// <summary>
    /// The real hole is found and the ordinary jitter is not. Both halves matter: a threshold
    /// tight enough to flag normal intervals would draw a broken line through a healthy day.
    /// </summary>
    [Fact]
    public void ARealHoleIsFoundAndOrdinaryJitterIsNot()
    {
        var start = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        var stamps = Cadence(600, start);

        // The 21,512-second hole from 15 September.
        var after = stamps[^1].AddSeconds(21512);
        stamps.AddRange(Cadence(600, after));

        var gaps = SampleGaps.Find(stamps);

        Assert.Single(gaps);
        Assert.Equal(21512, (gaps[0].To - gaps[0].From).TotalSeconds, 0);
    }

    /// <summary>
    /// Median, not mean. One six-hour hole drags a mean across a day of two-second samples far
    /// enough to hide every other gap behind it, which is exactly backwards.
    /// </summary>
    [Fact]
    public void TheTypicalIntervalIgnoresTheOutlier()
    {
        var start = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        var stamps = Cadence(500, start);
        stamps.AddRange(Cadence(500, stamps[^1].AddHours(6)));

        var median = SampleGaps.MedianInterval(stamps);

        Assert.NotNull(median);
        Assert.InRange(median!.Value.TotalSeconds, 2.0, 2.7);
    }

    /// <summary>All five of the real holes, and nothing else.</summary>
    [Fact]
    public void EveryRealHoleInADayIsFound()
    {
        var start = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        var stamps = Cadence(200, start);

        foreach (int hole in new[] { 21512, 3000, 1810, 1539, 1068 })
        {
            stamps.AddRange(Cadence(200, stamps[^1].AddSeconds(hole)));
        }

        Assert.Equal(5, SampleGaps.Find(stamps).Count);
    }

    /// <summary>
    /// Two samples give one interval and no way to say whether it is typical. Guessing there
    /// would mark the only gap the series has as either certain or impossible.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TooLittleDataYieldsNoGaps(int count)
    {
        var start = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Empty(SampleGaps.Find(Cadence(count, start)));
    }

    [Fact]
    public void MedianIsNullBelowTwoSamples()
    {
        var start = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        Assert.Null(SampleGaps.MedianInterval(Array.Empty<DateTime>()));
        Assert.Null(SampleGaps.MedianInterval(new[] { start }));
    }

    /// <summary>An explicit threshold is honoured as given, for callers that know their cadence.</summary>
    [Fact]
    public void AnExplicitThresholdIsUsedVerbatim()
    {
        var start = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        var stamps = new List<DateTime> { start, start.AddSeconds(10), start.AddSeconds(30) };

        Assert.Single(SampleGaps.Find(stamps, TimeSpan.FromSeconds(15)));
        Assert.Equal(2, SampleGaps.Find(stamps, TimeSpan.FromSeconds(5)).Count);
        Assert.Empty(SampleGaps.Find(stamps, TimeSpan.FromSeconds(60)));
    }
}
