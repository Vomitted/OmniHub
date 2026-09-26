// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// The figures beside every reading in the sensor table: lowest, mean and highest since the
/// application started. The rule that matters most is the one a naive version breaks: a reading
/// that did not answer is not a zero.
/// </summary>
public class RunningStatsTests
{
    [Fact]
    public void TheFiguresAreTheLowestMeanAndHighestOfWhatWasCounted()
    {
        var stats = new RunningStats();
        foreach (var v in new double?[] { 62, 81.1, 58, 67 }) stats.Add(v);

        Assert.Equal(58.0, stats.Min);
        Assert.Equal(81.1, stats.Max);
        Assert.Equal(67.025, stats.Mean!.Value, 9);
        Assert.Equal(4, stats.Count);
    }

    [Fact]
    public void AMissingReadingLeavesTheFiguresAlone()
    {
        // A GPU asleep for an hour must not drag its mean towards zero, and must not set a minimum
        // of zero it never measured.
        var stats = new RunningStats();
        stats.Add(45);
        stats.Add(null);
        stats.Add(double.NaN);
        stats.Add(double.PositiveInfinity);
        stats.Add(47);

        Assert.Equal(45.0, stats.Min);
        Assert.Equal(47.0, stats.Max);
        Assert.Equal(46.0, stats.Mean);
        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public void AReadingThatNeverAnsweredHasNoFigures()
    {
        var stats = new RunningStats();
        stats.Add(null);

        Assert.Null(stats.Min);
        Assert.Null(stats.Max);
        Assert.Null(stats.Mean);
    }

    [Fact]
    public void ResetStartsAgain()
    {
        var stats = new RunningStats();
        stats.Add(90);
        stats.Reset();
        stats.Add(60);

        Assert.Equal(60.0, stats.Min);
        Assert.Equal(60.0, stats.Max);
        Assert.Equal(60.0, stats.Mean);
        Assert.Equal(1, stats.Count);
    }
}
