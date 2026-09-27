// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class SplineTests
{
    [Fact]
    public void TheCurvePassesThroughEveryReading()
    {
        var points = new[] { (0.0, 10.0), (10.0, 30.0), (20.0, 15.0), (30.0, 40.0) };
        var cubics = Spline.Through(points);

        Assert.Equal(3, cubics.Count);
        for (int i = 0; i < cubics.Count; i++) Assert.Equal(points[i + 1], cubics[i].End);
    }

    [Fact]
    public void NoStretchBulgesPastTheTwoReadingsItJoins()
    {
        // A sharp peak between two lows: an unclamped Catmull-Rom would carry the curve above 30 on
        // the way in and below 15 on the way out. Every control point must sit within its segment.
        var points = new[] { (0.0, 0.0), (10.0, 30.0), (20.0, 15.0), (30.0, 15.0), (40.0, 0.0) };
        var cubics = Spline.Through(points);

        for (int i = 0; i < cubics.Count; i++)
        {
            double lo = Math.Min(points[i].Item2, points[i + 1].Item2);
            double hi = Math.Max(points[i].Item2, points[i + 1].Item2);
            foreach (var y in new[] { cubics[i].C1.Y, cubics[i].C2.Y })
                Assert.InRange(y, lo, hi);
        }
    }

    [Fact]
    public void FewerThanTwoReadingsDrawNothing()
    {
        Assert.Empty(Spline.Through(Array.Empty<(double, double)>()));
        Assert.Empty(Spline.Through(new[] { (0.0, 5.0) }));
    }
}
