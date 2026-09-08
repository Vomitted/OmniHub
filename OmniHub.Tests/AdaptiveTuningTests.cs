using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// The control law behind Adaptive mode, tested as the pure function it is.
///
/// The case that matters most is <see cref="IdleMachineIsNotGivenMorePower"/>. Without the
/// demand term the controller wound its limit up to the maximum during idle -- correctly, by
/// its own rules, because an idle die is always below target -- and the first work after a boot
/// then ran at full power and pinned the die onto the target with the fans at full speed. The
/// temperature looked like a misreading, because nothing connected it to a limit wound up
/// while the machine was doing nothing.
/// </summary>
public class AdaptiveTuningTests
{
    // The settings the bug was found under, so the regression is pinned to real numbers.
    private const int Target = 85;
    private const double Deadband = 3.0;
    private const double Threshold = 0.85;

    private static int Dir(double tempC, int watts, double? draw) =>
        AdaptiveTuning.Direction(tempC, watts, draw, Target, Deadband, Threshold);

    [Fact]
    public void IdleMachineIsNotGivenMorePower()
    {
        // Cool die, drawing 12 W against a 60 W limit: nowhere near power-limited. Raising the
        // limit cannot make this faster, and only guarantees the next burst runs at 60 W.
        Assert.Equal(-1, Dir(tempC: 45, watts: 60, draw: 12));
    }

    [Fact]
    public void UnusedHeadroomIsHandedBack()
    {
        // The same shape at a lower limit still walks down, so a limit wound up earlier unwinds
        // on its own instead of waiting for a workload to arrive and heat the machine first.
        Assert.Equal(-1, Dir(tempC: 50, watts: 40, draw: 9));
    }

    [Fact]
    public void PowerLimitedAndCoolClimbs()
    {
        // Drawing 38 W against 40 W with thermal room to spare: the case Adaptive exists for.
        Assert.Equal(1, Dir(tempC: 70, watts: 40, draw: 38));
    }

    [Fact]
    public void OverTargetBacksOffEvenWhenPowerLimited()
    {
        // Temperature outranks demand. A chip pressed against its limit and over target must
        // still lose power, or the controller would hold it there indefinitely.
        Assert.Equal(-1, Dir(tempC: 92, watts: 45, draw: 45));
    }

    [Fact]
    public void InsideDeadbandHolds()
    {
        Assert.Equal(0, Dir(tempC: Target, watts: 45, draw: 44));
        Assert.Equal(0, Dir(tempC: Target + 2, watts: 45, draw: 44));
        Assert.Equal(0, Dir(tempC: Target - 2, watts: 45, draw: 44));
    }

    [Fact]
    public void UnreadableDrawFallsBackToTemperatureOnly()
    {
        // No PM table readback is not evidence of an idle machine, so the demand term abstains
        // rather than pinning the limit to the floor on hardware that cannot report its draw.
        Assert.Equal(1, Dir(tempC: 60, watts: 40, draw: null));
        Assert.Equal(-1, Dir(tempC: 95, watts: 40, draw: null));
        Assert.Equal(0, Dir(tempC: Target, watts: 40, draw: null));
    }

    [Fact]
    public void DoesNotWindUpAcrossAnIdlePeriod()
    {
        // The reported bug as a sequence: an idle machine ticking for five minutes at the
        // controller's three-second interval must not arrive at the maximum limit.
        const int step = 3, min = 25, max = 60;
        int watts = 40;

        for (int tick = 0; tick < 100; tick++)
        {
            // Idle: cool, and drawing a fixed 10 W regardless of what it is allowed to draw.
            watts = Math.Clamp(watts + step * Dir(tempC: 46, watts: watts, draw: 10), min, max);
        }

        Assert.Equal(min, watts);
    }
}
