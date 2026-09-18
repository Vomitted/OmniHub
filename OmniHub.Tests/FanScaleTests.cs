// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// The percent-to-raw scale, and the battery maths. Both are small pure functions whose
/// errors were previously invisible: the fan scale was documented as 0-255 PWM in three
/// places while the code used an RPM/100 target, and the runtime estimate divides two live
/// readings whose edge cases (on AC, zero draw) had never been exercised.
/// </summary>
public class FanScaleTests
{
    /// <summary>
    /// 0% must map to raw 0, not to MinRawLevel. Mapping it to the floor of the usable band
    /// meant every "silent" tick still commanded roughly 2000 RPM, so the curve's flat 0%
    /// segment through idle never actually produced silence.
    /// </summary>
    [Fact]
    public void ZeroPercent_MapsToTrueOff()
    {
        Assert.Equal(0, FanCalibration.Default.RawToPercent(0));
    }

    [Fact]
    public void RawReadback_MatchesTheDocumentedBand()
    {
        // 10-56 is the usable raw band, both ends measured by driving the fans directly: the
        // readback tracks the command down to 10 (1000 rpm) and pins at 56 above it. Its ends
        // must map to the ends of the percentage range.
        Assert.Equal(0, FanCalibration.Default.RawToPercent(10));
        Assert.Equal(100, FanCalibration.Default.RawToPercent(56));
    }

    [Fact]
    public void RawBelowTheBand_ClampsToZeroRatherThanGoingNegative()
    {
        // Subtracting MinRawLevel from a smaller raw value would underflow the byte cast.
        // The band now starts at 10, so 9 is the value just below it.
        Assert.Equal(0, FanCalibration.Default.RawToPercent(5));
        Assert.Equal(0, FanCalibration.Default.RawToPercent(9));
    }

    [Fact]
    public void RawAboveTheBand_ClampsToOneHundred()
    {
        Assert.Equal(100, FanCalibration.Default.RawToPercent(60));
        Assert.Equal(100, FanCalibration.Default.RawToPercent(255));
    }

    /// <summary>
    /// Anchors on the measured band rather than on remembered pairs.
    ///
    /// The previous expectations here (raw 31 = 32%, raw 46 = 74%) were recorded against a
    /// floor of 20. That floor turned out to be an assumption -- the EC tracks commands down
    /// to raw 10 -- so those pairs described a scale the hardware never had. These are derived
    /// from the measured 10-56 band instead: percent = (raw - 10) / 46.
    /// </summary>
    [Theory]
    [InlineData((byte)10, 0)]
    [InlineData((byte)21, 24)]
    [InlineData((byte)33, 50)]
    [InlineData((byte)45, 76)]
    [InlineData((byte)56, 100)]
    public void ObservedHardwareValues_RoundTripWithinRounding(byte raw, int expectedPercentApprox)
    {
        int actual = FanCalibration.Default.RawToPercent(raw);
        // Within 2 points: raw is a whole number, so the mapping is lossy by construction.
        Assert.InRange(actual, expectedPercentApprox - 2, expectedPercentApprox + 2);
    }

    [Fact]
    public void RawToPercent_IsMonotonic()
    {
        for (int raw = 0; raw < 255; raw++)
            Assert.True(FanCalibration.Default.RawToPercent((byte)(raw + 1)) >= FanCalibration.Default.RawToPercent((byte)raw));
    }

    // ---------- battery ----------

    [Fact]
    public void OnAc_RuntimeEstimateIsUnavailableRatherThanInvented()
    {
        var draw = new BatteryDraw(OnAc: true, Charging: false, DischargeMilliwatts: 0,
            ChargeMilliwatts: 0, RemainingCapacityMWh: 60000, MilliVolts: 17000);
        Assert.Null(BatterySaver.EstimateRuntime(draw));
    }

    [Fact]
    public void ZeroDraw_RuntimeEstimateIsUnavailable()
    {
        // Dividing by zero would produce Infinity and render as a nonsense duration.
        var draw = new BatteryDraw(false, false, 0, 0, 60000, 17000);
        Assert.Null(BatterySaver.EstimateRuntime(draw));
    }

    [Fact]
    public void RealDraw_ProducesCapacityOverDraw()
    {
        // 60000 mWh at 15000 mW is exactly four hours.
        var draw = new BatteryDraw(false, false, DischargeMilliwatts: 15000,
            ChargeMilliwatts: 0, RemainingCapacityMWh: 60000, MilliVolts: 17000);

        var runtime = BatterySaver.EstimateRuntime(draw);
        Assert.NotNull(runtime);
        Assert.Equal(4.0, runtime!.Value.TotalHours, precision: 3);
    }

    [Fact]
    public void ImplausiblyLongRuntime_IsSuppressed()
    {
        // A near-zero draw implies days of runtime, which is an artefact of a sensor that has
        // not settled rather than a fact worth showing.
        var draw = new BatteryDraw(false, false, DischargeMilliwatts: 1,
            ChargeMilliwatts: 0, RemainingCapacityMWh: 60000, MilliVolts: 17000);
        Assert.Null(BatterySaver.EstimateRuntime(draw));
    }

    [Fact]
    public void DescribeDraw_NeverClaimsAFigureItDoesNotHave()
    {
        Assert.Equal("Battery not readable", BatterySaver.DescribeDraw(null));

        var onAc = new BatteryDraw(true, false, 0, 0, 60000, 17000);
        Assert.Contains("AC", BatterySaver.DescribeDraw(onAc));

        var charging = new BatteryDraw(true, true, 0, 20000, 60000, 17000);
        Assert.Contains("Charging", BatterySaver.DescribeDraw(charging));
    }
}
