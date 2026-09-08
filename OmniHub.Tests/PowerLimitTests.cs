using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Which constraint the processor is actually up against.
///
/// This is the arithmetic behind telling someone that raising their power limit will not help,
/// because they are at 99% of core current and 60% of power. Getting it wrong would not look
/// like a failure; it would look like confident advice pointing at the wrong knob.
/// </summary>
public class PowerLimitTests
{
    /// <summary>A snapshot with everything slack, which individual tests tighten one field of.</summary>
    private static PowerSnapshot Snapshot(
        double stapm = 10, double stapmLimit = 50,
        double fast = 10, double fastLimit = 60,
        double edc = 10, double edcLimit = 140,
        double tdc = 10, double tdcLimit = 100,
        double temp = 50, double tempLimit = 95) =>
        new(StapmLimitWatts: stapmLimit, StapmWatts: stapm,
            FastLimitWatts: fastLimit, FastWatts: fast,
            SlowLimitWatts: 54, SlowWatts: 10,
            ApuSlowLimitWatts: 54, ApuSlowWatts: 10,
            TdcVddLimitAmps: tdcLimit, TdcVddAmps: tdc,
            TdcSocLimitAmps: 20, TdcSocAmps: 1,
            EdcVddLimitAmps: edcLimit, EdcVddAmps: edc,
            EdcSocLimitAmps: 20, EdcSocAmps: 1,
            ThermalLimitC: tempLimit, CoreTempC: temp,
            SocThermalLimitC: 95, SocTempC: 40,
            GfxThermalLimitC: 95, GfxTempC: 40);

    [Fact]
    public void ReportsAllFiveConstraintsInAStableOrder()
    {
        var names = Snapshot().Limits().Select(l => l.Name).ToArray();

        Assert.Equal(
            new[] { "Sustained power", "Boost power", "Core current (EDC)", "Core current (TDC)", "Temperature" },
            names);
    }

    [Fact]
    public void EachConstraintIsAPercentageOfItsOwnLimit()
    {
        var limits = Snapshot(stapm: 25, stapmLimit: 50, temp: 76, tempLimit: 95).Limits();

        Assert.Equal(50, limits[0].Percent, 3);   // 25 of 50 W
        Assert.Equal(80, limits[4].Percent, 3);   // 76 of 95 C
    }

    [Fact]
    public void CurrentLimitedIsDistinguishableFromPowerLimited()
    {
        // The case the readout exists for: pressed hard against core current while power is
        // barely half spent. Raising the power limit here would achieve nothing.
        var s = Snapshot(stapm: 30, stapmLimit: 50, edc: 138.6, edcLimit: 140);

        var tightest = s.TightestLimit();
        Assert.Equal("Core current (EDC)", tightest.Name);
        Assert.True(tightest.Percent > 98);

        Assert.Equal(60, s.Limits()[0].Percent, 3);   // and sustained power still has room
    }

    [Fact]
    public void TightestAgreesWithTheHighestOfTheFive()
    {
        var s = Snapshot(stapm: 49, stapmLimit: 50, temp: 60, tempLimit: 95);
        Assert.Equal(s.Limits().Max(l => l.Percent), s.TightestLimit().Percent, 6);
    }

    [Fact]
    public void AnUnreportedLimitIsZeroRatherThanADivisionByZero()
    {
        // A PM table carrying no limit for a field must not produce infinity or NaN, which would
        // then win MaxBy and be reported as the machine's binding constraint.
        var limits = Snapshot(stapmLimit: 0, fastLimit: 0, edcLimit: 0, tdcLimit: 0, tempLimit: 0).Limits();

        Assert.All(limits, l =>
        {
            Assert.Equal(0, l.Percent);
            Assert.False(double.IsNaN(l.Percent) || double.IsInfinity(l.Percent));
        });
    }
}
