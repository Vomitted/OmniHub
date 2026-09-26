// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class RecentSeriesTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AMissingReadingIsABreakNeverAZeroAndOneBreakCoversARun()
    {
        var series = new RecentSeries(TimeSpan.FromMinutes(5));

        series.Add(T0.AddSeconds(-2), null);          // nothing before the first reading
        series.Add(T0, 60);
        series.Add(T0.AddSeconds(2), null);
        series.Add(T0.AddSeconds(4), double.NaN);
        series.Add(T0.AddSeconds(6), 62);

        Assert.Equal(new[] { 60.0, double.NaN, 62.0 }, series.Points.Select(p => p.Value));
    }

    [Fact]
    public void ABreakEndsTheLineEvenWhereTheSpacingWouldHaveBridgedIt()
    {
        // Thirty seconds apart, as the slow readings arrive from the tray, with a generous gap rule:
        // the GPU missing for one reading must still split the line in two.
        var series = new RecentSeries(TimeSpan.FromMinutes(30));
        series.Add(T0, 40);
        series.Add(T0.AddSeconds(30), 41);
        series.Add(T0.AddSeconds(60), null);
        series.Add(T0.AddSeconds(90), 42);
        series.Add(T0.AddSeconds(120), 43);

        var segments = TimeSeriesDecimator.Decimate(series.Points, T0, T0.AddSeconds(120), 100, TimeSpan.FromSeconds(95));

        Assert.Equal(2, segments.Count);
        Assert.All(segments.SelectMany(s => s), p => Assert.True(double.IsFinite(p.Value)));
    }

    [Fact]
    public void PointsOlderThanTheWindowFallOffTheFront()
    {
        var series = new RecentSeries(TimeSpan.FromSeconds(10));

        for (int s = 0; s <= 30; s += 2) series.Add(T0.AddSeconds(s), s);

        Assert.Equal(T0.AddSeconds(20), series.Points[0].AtUtc);
        Assert.Equal(T0.AddSeconds(30), series.Points[^1].AtUtc);
    }

    [Fact]
    public void AWindowWithNoLengthIsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentSeries(TimeSpan.Zero));
}
