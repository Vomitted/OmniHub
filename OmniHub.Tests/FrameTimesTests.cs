// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class FrameTimesTests
{
    /// <summary>A steady run at a given rate, with an optional tail of slow frames.</summary>
    private static List<double> Run(int frames, double ms, params double[] slowTail)
    {
        var run = Enumerable.Repeat(ms, frames).ToList();
        run.AddRange(slowTail);
        return run;
    }

    [Fact]
    public void TheOnePercentLowIsNotThePercentileConvertedToARate()
    {
        // The decisive one, and the reason this class exists separately from anything that
        // collects frames. Nine hundred and ninety frames at 10 ms, then ten frames spread from
        // 40 to 130 ms. The 99th percentile lands on ONE frame near the start of that tail; the
        // one per cent low is the MEAN over all ten and is therefore worse.
        //
        // A tool quoting the percentile as the low reports a stutter milder than the one that
        // happened, every time, and nothing about the output looks wrong.
        var tail = new double[] { 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
        var stats = FrameTimes.Measure(Run(990, 10, tail))!;

        double lowFromPercentile = 1000.0 / stats.P99Ms;

        Assert.NotEqual(lowFromPercentile, stats.OnePercentLowFps, 1);

        // And the direction is not arbitrary: the mean of the tail is slower than the frame the
        // percentile picked, so the honest figure is the lower one.
        Assert.True(stats.OnePercentLowFps < lowFromPercentile,
                    $"1% low {stats.OnePercentLowFps:0.0} should be below {lowFromPercentile:0.0} from the percentile");

        // 85 ms mean over the worst ten frames is 11.8 fps.
        Assert.Equal(11.8, stats.OnePercentLowFps, 1);
    }

    [Fact]
    public void ASteadyRunReportsItsRateAndNoStutters()
    {
        var stats = FrameTimes.Measure(Run(600, 1000.0 / 144))!;

        Assert.Equal(144, stats.AverageFps, 0);
        Assert.Equal(144, stats.OnePercentLowFps, 0);
        Assert.Equal(0, stats.Stutters);
    }

    [Fact]
    public void TheAverageIsFramesOverTimeNotTheMeanOfTheRates()
    {
        // Averaging rates weights every frame equally however long it took, which flatters a
        // stuttering run: half at 5 ms and half at 50 ms averages to 110 fps by that method and
        // 36.4 by the honest one.
        var run = Run(500, 5).Concat(Run(500, 50)).ToList();
        var stats = FrameTimes.Measure(run)!;

        double meanOfRates = (1000.0 / 5 + 1000.0 / 50) / 2;

        Assert.Equal(36.4, stats.AverageFps, 1);
        Assert.True(stats.AverageFps < meanOfRates);
    }

    [Fact]
    public void AStutterIsMeasuredAgainstTheMedianNotTheMean()
    {
        // A handful of very long frames drags the mean far enough to hide the stutters that
        // produced it. Against the median, all five are still counted.
        var run = Run(995, 10, 500, 500, 500, 500, 500);
        var stats = FrameTimes.Measure(run)!;

        Assert.Equal(5, stats.Stutters);
        Assert.Equal(10, stats.MedianMs, 1);
    }

    [Fact]
    public void ARunTooShortToMeanAnythingIsNotSummarised()
    {
        // A one per cent low over fifty frames is one frame wearing the name of a statistic.
        Assert.Null(FrameTimes.Measure(Run(FrameTimes.MinimumFrames - 1, 10)));
        Assert.NotNull(FrameTimes.Measure(Run(FrameTimes.MinimumFrames, 10)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnIntervalThatIsNotAFrameIsDiscardedRatherThanDividedBy(double bad)
    {
        // Two presents sharing a timestamp happen at the edges of a trace, and dividing by one
        // gives infinity -- which would propagate into every figure here.
        var run = Run(500, 10);
        run.Add(bad);

        var stats = FrameTimes.Measure(run)!;

        Assert.Equal(500, stats.Frames);
        Assert.False(double.IsNaN(stats.AverageFps) || double.IsInfinity(stats.AverageFps));
    }

    [Fact]
    public void TheTenthOfAPercentLowIsNoBetterThanTheOnePercentLow()
    {
        // It is a subset of the same tail, so it can only be equal or worse. The other way round
        // would mean the tail was being taken from the wrong end.
        var stats = FrameTimes.Measure(Run(990, 10, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130))!;

        Assert.True(stats.PointOnePercentLowFps <= stats.OnePercentLowFps);
    }

    [Fact]
    public void PercentilesRiseWithTheirPercent()
    {
        var stats = FrameTimes.Measure(Run(990, 10, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130))!;

        Assert.True(stats.MedianMs <= stats.P95Ms);
        Assert.True(stats.P95Ms <= stats.P99Ms);
    }

    [Fact]
    public void OrderDoesNotMatter()
    {
        // Frames arrive in time order; the statistics are over the distribution. A dependency on
        // arrival order would make two identical runs disagree.
        var run = Run(400, 10).Concat(Run(400, 25)).ToList();
        var shuffled = run.OrderBy(_ => Guid.NewGuid()).ToList();

        Assert.Equal(FrameTimes.Measure(run), FrameTimes.Measure(shuffled));
    }

    [Fact]
    public void NoFramesAtAllIsNullRatherThanAThrow()
    {
        Assert.Null(FrameTimes.Measure(Array.Empty<double>()));
        Assert.Null(FrameTimes.Measure(null!));
    }
}
