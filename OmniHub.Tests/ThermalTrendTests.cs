// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;

namespace OmniHub.Tests;

/// <summary>
/// Regression tests for the trend estimator. Every case here corresponds to a bug that was
/// actually shipped and then caught by reading a log, not to a hypothetical -- which is the
/// point: this code feeds a fan curve, and each of its failures was silent.
/// </summary>
public class ThermalTrendTests
{
    private static ThermalTrend FeedSteady(double tempC, int samples, double intervalSeconds = 2.0)
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < samples; i++)
        {
            trend.Ingest(tempC, t);
            t = t.AddSeconds(intervalSeconds);
        }
        return trend;
    }

    [Fact]
    public void SteadyTemperature_ForecastsThatSameTemperature()
    {
        var trend = FeedSteady(70, 12);
        Assert.True(trend.HasEnoughData);
        Assert.InRange(trend.ForecastC(10), 69.0, 71.0);
        Assert.InRange(trend.VelocityCPerSec, -0.05, 0.05);
    }

    /// <summary>
    /// THE TIMEBASE BUG. The window start time was captured once, on the very first sample
    /// ever, and never rolled with the ring buffer -- so the span it was divided over grew
    /// without bound while the sample count stayed pinned at the window size. After a few
    /// hundred polls the filter had effectively stopped tracking.
    ///
    /// A steady rise must be measured as the same rate after 500 samples as after 10.
    /// </summary>
    [Fact]
    public void LongRun_DoesNotDegradeTheVelocityEstimate()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 500 samples of a clean +0.5 C/s ramp: about 17 minutes of real polling.
        double temp = 40;
        for (int i = 0; i < 500; i++)
        {
            trend.Ingest(temp, t);
            t = t.AddSeconds(2.0);
            temp += 1.0; // 1 C per 2 s == 0.5 C/s
        }

        Assert.InRange(trend.VelocityCPerSec, 0.4, 0.6);
    }

    /// <summary>
    /// THE DIVERGENCE BUG. With a quadratic term at a 10-second horizon, an acceleration
    /// estimate pinned at its clamp contributed tens of degrees, and the forecast alternated
    /// between both clamp rails while the die was thermally steady. Because the fan curve
    /// consumes max(measured, forecast), that drove the fan to 100% on noise alone.
    ///
    /// Feeds the real pathological input: whole-degree quantisation with multi-degree jumps,
    /// which is exactly what the ACPI sensor produces.
    /// </summary>
    [Fact]
    public void QuantisedNoisyInput_ForecastStaysNearReality()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Steps taken from an actual load run: 1 C quantisation, several-degree jumps, no
        // sustained trend.
        double[] observed = { 83, 85, 85, 83, 85, 83, 80, 83, 85, 85, 83, 85, 83, 85 };
        foreach (var temp in observed)
        {
            trend.Ingest(temp, t);
            t = t.AddSeconds(2.4);
        }

        // Must stay in the neighbourhood of the readings. The old filter produced 25 and 110
        // for this very input.
        Assert.InRange(trend.ForecastC(10), 70.0, 100.0);
    }

    [Fact]
    public void RisingTemperature_ForecastLeadsTheReading()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double temp = 60;
        for (int i = 0; i < 10; i++)
        {
            trend.Ingest(temp, t);
            t = t.AddSeconds(2.0);
            temp += 1.0;
        }

        // Leading is the whole purpose: the fan should ramp before the heat lands.
        Assert.True(trend.ForecastC(10) > trend.FilteredTempC);
    }

    [Fact]
    public void FallingTemperature_ForecastTrailsTheReading()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        double temp = 90;
        for (int i = 0; i < 10; i++)
        {
            trend.Ingest(temp, t);
            t = t.AddSeconds(2.0);
            temp -= 1.0;
        }

        Assert.True(trend.ForecastC(10) < trend.FilteredTempC);
    }

    /// <summary>A gap far longer than the poll interval means the machine slept. Extrapolating
    /// across that discontinuity would invent a trend, so the filter must restart.</summary>
    [Fact]
    public void LargeTimeGap_ResetsRatherThanInventingATrend()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
        {
            trend.Ingest(50 + i, t);
            t = t.AddSeconds(2.0);
        }
        Assert.True(trend.HasEnoughData);

        trend.Ingest(50, t.AddHours(3)); // resumed from sleep
        Assert.False(trend.HasEnoughData);
        Assert.Equal(0, trend.VelocityCPerSec);
    }

    [Fact]
    public void BeforeThreeSamples_ReportsNotEnoughData()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        trend.Ingest(60, t);
        trend.Ingest(60, t.AddSeconds(2));
        Assert.False(trend.HasEnoughData);

        // With too little history the forecast must be the current reading, never an
        // extrapolation from noise.
        Assert.Equal(60, trend.ForecastC(10));
    }

    [Fact]
    public void ForecastNeverDepartsFurtherThanTheDeltaClamp()
    {
        var trend = new ThermalTrend();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A violent ramp of 5 C per sample. The velocity clamp and the delta clamp together
        // must keep the projection physically plausible.
        double temp = 40;
        for (int i = 0; i < 10; i++)
        {
            trend.Ingest(temp, t);
            t = t.AddSeconds(2.0);
            temp += 5.0;
        }

        double delta = trend.ForecastC(30) - trend.FilteredTempC;
        Assert.InRange(delta, 0, 12.001); // MaxForecastDeltaC
    }
}
