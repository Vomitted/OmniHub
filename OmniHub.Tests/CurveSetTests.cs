using OmniHub.Core.Fan;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The four stored curves, and the two independent switches that choose between them.
///
/// The property that matters is that either switch alone does exactly what its own name says,
/// because that is what makes each feature revertible without disturbing the other.
/// </summary>
public class CurveSetTests
{
    private static CurveRail Rail(byte level) =>
        new(new[] { new CurvePoint(40, level), new CurvePoint(80, level) }, FloorTempC: 55, FloorLevelPercent: level);

    private static readonly CurveSet Set = new(
        MainsFan1:   Rail(10),
        MainsFan2:   Rail(20),
        BatteryFan1: Rail(30),
        BatteryFan2: Rail(40));

    [Theory]
    [InlineData(false, false, 10)]   // mains, fan 1
    [InlineData(false, true,  20)]   // mains, fan 2
    [InlineData(true,  false, 30)]   // battery, fan 1
    [InlineData(true,  true,  40)]   // battery, fan 2
    public void WithBothFeaturesOnEachCellIsReachable(bool onBattery, bool fan2, byte expected)
    {
        var rail = CurveRails.Pick(Set, onBattery, separateBatteryCurve: true, fan2, separateFan2Curve: true);

        Assert.Equal(expected, rail.FloorLevelPercent);
    }

    [Fact]
    public void WithOnlyTheFanFeatureOnTheSecondFanUsesItsMainsCurveOnBattery()
    {
        // The rail feature is off, so there is one rail; asking for the second fan on battery must
        // give the second fan's only curve rather than falling all the way back to fan 1.
        var rail = CurveRails.Pick(Set, onBattery: true, separateBatteryCurve: false, fan2: true, separateFan2Curve: true);

        Assert.Equal(20, rail.FloorLevelPercent);
    }

    [Fact]
    public void WithOnlyTheRailFeatureOnTheSecondFanFollowsTheFirst()
    {
        var rail = CurveRails.Pick(Set, onBattery: true, separateBatteryCurve: true, fan2: true, separateFan2Curve: false);

        Assert.Equal(30, rail.FloorLevelPercent);
    }

    [Fact]
    public void WithNeitherFeatureOnEveryCellResolvesToTheOneCurve()
    {
        foreach (bool onBattery in new[] { false, true })
            foreach (bool fan2 in new[] { false, true })
                Assert.Equal(10, CurveRails.Pick(Set, onBattery, false, fan2, false).FloorLevelPercent);
    }

    [Fact]
    public void TheBuiltCurveCarriesTheChosenCellsFloor()
    {
        var curve = CurveRails.Build(Set, onBattery: true, separateBatteryCurve: true, fan2: true, separateFan2Curve: true);

        Assert.Equal(40, curve.FloorLevelPercent);
    }
}
