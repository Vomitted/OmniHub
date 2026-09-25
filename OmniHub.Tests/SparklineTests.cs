// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class SparklineTests
{
    private static Sparkline Filled(int capacity, params double?[] values)
    {
        var s = new Sparkline(capacity);
        foreach (var v in values) s.Push(v);
        return s;
    }

    [Fact]
    public void NothingIsDrawnUntilThereAreTwoReadings()
    {
        Assert.Empty(new Sparkline().Segments(100, 20));
        Assert.Empty(Filled(10, 50).Segments(100, 20));
    }

    [Fact]
    public void AWindowOfNothingButUnavailableReadingsDrawsNothing()
    {
        // Not a flat line along the bottom, which is what plotting a missing reading as zero
        // would produce and what it would look like.
        Assert.Empty(Filled(10, null, null, null).Segments(100, 20));
    }

    [Fact]
    public void TheLowestSampleSitsAtTheBottomAndTheHighestAtTheTop()
    {
        var segments = Filled(2, 40, 80).Segments(100, 20);

        var points = Assert.Single(segments);
        Assert.Equal(2, points.Count);
        Assert.Equal((0.0, 20.0), points[0]);      // 40 C, the minimum, at the bottom edge
        Assert.Equal((100.0, 0.0), points[1]);     // 80 C, the maximum, at the top
    }

    [Fact]
    public void AWindowThatNeverMovedDrawsDownTheMiddle()
    {
        var points = Assert.Single(Filled(3, 60, 60, 60).Segments(90, 20));

        Assert.All(points, p => Assert.Equal(10.0, p.Y));
    }

    [Fact]
    public void AnUnavailableReadingBreaksTheLineRatherThanClosingOverIt()
    {
        // The claim a joined line would make -- that the value passed smoothly between the two
        // ends -- is exactly the claim the missing sample failed to support.
        var segments = Filled(5, 40, 50, null, 70, 80).Segments(100, 20);

        Assert.Equal(2, segments.Count);
        Assert.Equal(2, segments[0].Count);
        Assert.Equal(2, segments[1].Count);

        // The gap keeps its place: the second run starts at slot 3 of 5, not immediately after
        // the first.
        Assert.Equal(75.0, segments[1][0].X, 6);
    }

    [Fact]
    public void ALoneReadingBesideAGapIsNotASegment()
    {
        var segments = Filled(5, 40, null, 60, null, 80).Segments(100, 20);

        Assert.Empty(segments);
    }

    [Fact]
    public void TheWindowKeepsTheNewestSamplesOnceItIsFull()
    {
        var s = Filled(3, 1, 2, 3, 4, 5);

        Assert.Equal(new double?[] { 3, 4, 5 }, s.Samples);
    }

    [Fact]
    public void APartlyFilledWindowGrowsFromTheLeftRatherThanStretching()
    {
        // Two samples in a window of ten occupy the first ninth of the box, not all of it. A
        // sparkline that stretched would redraw every point on every tick and imply a history
        // that had not been taken yet.
        var points = Assert.Single(Filled(10, 40, 80).Segments(90, 20));

        Assert.Equal(0.0, points[0].X, 6);
        Assert.Equal(10.0, points[1].X, 6);
    }

    [Fact]
    public void AFullWindowSpansTheBox()
    {
        var points = Assert.Single(Filled(4, 1, 2, 3, 4).Segments(90, 20));

        Assert.Equal(0.0, points[0].X, 6);
        Assert.Equal(90.0, points[^1].X, 6);
    }

    [Fact]
    public void ACapacityTooSmallToHoldALineIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sparkline(1));
    }

    private static IReadOnlyList<double> Heights(Sparkline line, params double?[] values)
    {
        foreach (var v in values) line.Push(v);
        return Assert.Single(line.Segments(90, 20)).Select(p => p.Y).ToList();
    }

    /// <summary>
    /// Sensor noise stays a ripple.
    ///
    /// The instrument bar drew an idle die wandering between 67.1 and 68.6 C as a jagged run of
    /// full-height spikes, because the box stood for whatever range the window happened to hold.
    /// With a ten-degree floor, one and a half degrees uses about fifteen per cent of the height.
    /// </summary>
    [Fact]
    public void NoiseSmallerThanTheFloorStaysARipple()
    {
        var ys = Heights(new Sparkline(capacity: 4, minSpan: 10), 67.1, 68.6, 67.1, 68.6);

        Assert.True(ys.Max() - ys.Min() <= 20 * 0.16,
                    $"1.5 of a 10-wide band should use about 15% of a 20px box; it used {ys.Max() - ys.Min():0.0}px");
    }

    [Fact]
    public void ASwingLargerThanTheFloorStillFillsTheBox()
    {
        // The floor only ever widens the band. A real thirty-degree climb is exactly what the trace
        // is for, and it keeps the whole height.
        var ys = Heights(new Sparkline(capacity: 3, minSpan: 10), 50, 65, 80);

        Assert.Equal(20.0, ys.Max(), 6);
        Assert.Equal(0.0, ys.Min(), 6);
    }

    [Fact]
    public void AFlatWindowStillDrawsDownTheMiddleWithAFloor()
    {
        var ys = Heights(new Sparkline(capacity: 3, minSpan: 10), 60, 60, 60);
        Assert.All(ys, y => Assert.Equal(10.0, y, 6));
    }

    [Fact]
    public void ANegativeFloorIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Sparkline(minSpan: -1));
    }
}
