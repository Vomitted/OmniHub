// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// The widgets' arithmetic. The rule that matters is the first one: a reading with no real full
/// scale gets no gauge, because an arc drawn against an invented maximum looks like a measurement.
/// </summary>
public class GaugeTests
{
    [Theory]
    [InlineData(45.0, null)]      // watts, gigahertz: nothing says where full is
    [InlineData(45.0, 0.0)]
    [InlineData(45.0, -90.0)]
    [InlineData(null, 90.0)]      // no reading
    [InlineData(double.NaN, 90.0)]
    public void NoRealScaleOrNoReadingMeansNoGauge(double? value, double? scale)
    {
        Assert.Null(Gauge.Fraction(value, scale));
    }

    [Theory]
    [InlineData(45.0, 90.0, 0.5)]
    [InlineData(95.0, 90.0, 1.0)]     // past the threshold is full, not broken
    [InlineData(-3.0, 90.0, 0.0)]
    public void AGaugeIsTheReadingAgainstItsScale(double value, double scale, double expected)
    {
        Assert.Equal(expected, Gauge.Fraction(value, scale)!.Value, 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(80.0)]
    public void AStoppedFanIsDrawnStill(double? rpm)
    {
        Assert.Null(Gauge.SecondsPerTurn(rpm));
    }

    [Fact]
    public void AFasterFanTurnsFasterWithinWhatTheEyeCanFollow()
    {
        double slow = Gauge.SecondsPerTurn(1500)!.Value;
        double fast = Gauge.SecondsPerTurn(5600)!.Value;

        Assert.True(fast < slow, $"5600 rpm took {fast} s per turn, 1500 rpm {slow} s");
        Assert.Equal(1.0, Gauge.SecondsPerTurn(3000)!.Value, 9);
        Assert.Equal(0.4, Gauge.SecondsPerTurn(20000)!.Value, 9);   // never a blur
        Assert.Equal(8.0, Gauge.SecondsPerTurn(150)!.Value, 9);     // never imperceptibly slow
    }
}
