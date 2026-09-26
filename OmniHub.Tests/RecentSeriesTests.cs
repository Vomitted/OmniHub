// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class RecentSeriesTests
{
    private static readonly DateTime T0 = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AMissingReadingAddsNothingSoTheChartSeesAGapNotAZero()
    {
        var series = new RecentSeries(TimeSpan.FromMinutes(5));

        series.Add(T0, 60);
        series.Add(T0.AddSeconds(2), null);
        series.Add(T0.AddSeconds(4), double.NaN);
        series.Add(T0.AddSeconds(6), 62);

        Assert.Equal(new[] { 60.0, 62.0 }, series.Points.Select(p => p.Value));
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
