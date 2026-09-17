using OmniHub.Core.Fan;
using Xunit;

namespace OmniHub.Tests;

public class CurveRailTests
{
    private static readonly CurveRail Mains = new(
        new[] { new CurvePoint(40, 20), new CurvePoint(80, 80) }, FloorTempC: 55.0, FloorLevelPercent: 15);

    private static readonly CurveRail Battery = new(
        new[] { new CurvePoint(40, 5), new CurvePoint(80, 40) }, FloorTempC: 70.0, FloorLevelPercent: 8);

    [Fact]
    public void MainsRailIsUsedOnMainsEvenWithTwoCurvesEnabled()
    {
        Assert.Equal(Mains, CurveRails.Pick(onBattery: false, separateBatteryCurve: true, Mains, Battery));
    }

    [Fact]
    public void MainsRailIsUsedOnBatteryWhileTheFeatureIsOff()
    {
        // The regression this guards: a battery curve left behind from a time the toggle was on
        // must not come back into force once the user has switched it off.
        Assert.Equal(Mains, CurveRails.Pick(onBattery: true, separateBatteryCurve: false, Mains, Battery));
    }

    [Fact]
    public void BatteryRailIsUsedOnBatteryWhenEnabled()
    {
        Assert.Equal(Battery, CurveRails.Pick(onBattery: true, separateBatteryCurve: true, Mains, Battery));
    }

    [Fact]
    public void TheFloorComesFromTheRailInForce()
    {
        var curve = CurveRails.Build(onBattery: true, separateBatteryCurve: true, Mains, Battery);

        Assert.Equal(70.0, curve.FloorTempC);
        Assert.Equal(8, curve.FloorLevelPercent);
    }

    [Fact]
    public void TheBuiltCurveEvaluatesTheRailInForce()
    {
        var mains = CurveRails.Build(onBattery: false, separateBatteryCurve: true, Mains, Battery);
        var battery = CurveRails.Build(onBattery: true, separateBatteryCurve: true, Mains, Battery);

        // 80 C sits on the last point of both rails, so this reads the curve itself rather than
        // the floor: the battery rail is the quieter of the two and must evaluate lower.
        Assert.True(battery.Evaluate(80) < mains.Evaluate(80));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void ARailThatCannotMakeACurveFallsBackToTheDefault(int? pointCount)
    {
        var points = pointCount is null
            ? null
            : Enumerable.Range(0, pointCount.Value).Select(i => new CurvePoint(40 + i * 10, 20)).ToArray();

        // FanCurve's constructor rejects fewer than two points. Reaching it from a hand-edited or
        // half-written settings file would take the fan loop down, so the fallback is the point.
        var curve = CurveRails.Build(
            onBattery: true, separateBatteryCurve: true, Mains,
            new CurveRail(points, FloorTempC: 60.0, FloorLevelPercent: 12));

        Assert.Equal(FanCurve.DefaultPoints, curve.Points);

        // The floor is still the rail's own -- only the points were unusable.
        Assert.Equal(60.0, curve.FloorTempC);
        Assert.Equal(12, curve.FloorLevelPercent);
    }
}
