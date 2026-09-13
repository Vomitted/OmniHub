using OmniHub.Core.Fan;

namespace OmniHub.Tests;

/// <summary>
/// The fan curve is the safety-critical component: the entire reason this app exists is that
/// the stock BIOS lets the fan idle at 0% while the machine is hot. These tests pin the
/// properties that make it safe, so a future tuning change cannot quietly remove them.
/// </summary>
public class FanCurveTests
{
    /// <summary>
    /// The ratchet bug: the fan reached a level and could never leave it.
    ///
    /// The ramp-down deadband compared against a reference that was rewritten every
    /// evaluation, so it asked "has the temperature fallen 4C since the last tick" rather than
    /// "since the level was set". A real cooldown falls a fraction of a degree per tick and
    /// never satisfies that, so the fan stayed at its peak indefinitely -- measured on this
    /// machine at 100% and 5600 rpm with the die at 76C.
    /// </summary>
    [Fact]
    public void FanStepsDownAsTheDieCoolsGradually()
    {
        var curve = FanCurve.CreateDefault();
        curve.Evaluate(92);                       // drive it up to full
        byte peak = curve.Evaluate(92);

        // Cool by half a degree per tick, the way real hardware does.
        byte level = peak;
        for (double t = 92; t >= 60; t -= 0.5) level = curve.Evaluate(t);

        Assert.True(level < peak, $"fan latched at {level}% while the die fell from 92C to 60C");
    }

    /// <summary>
    /// A transient spike must not leave the fan parked above the curve once the die settles.
    ///
    /// This is the case the continuous-cooldown test above cannot catch. There the temperature
    /// falls every tick, which feeds the deadband a fresh drop each time; here it spikes once
    /// and then holds perfectly flat, which is what a laptop actually does when a burst of
    /// light work ends. Taken from a real log: a two-second burst to 84C, then the die flat at
    /// 63.1C, where the default curve asks for about 25% -- and the fan held 70% for seventeen
    /// seconds because every step down had re-armed the deadband against itself.
    /// </summary>
    [Fact]
    public void FanReturnsToTheCurveWhenTemperatureSettlesFlatAfterASpike()
    {
        var curve = FanCurve.CreateDefault();

        curve.Evaluate(50);
        byte peak = curve.Evaluate(84);

        // Thirty ticks -- a minute at the service's two-second interval -- with the die
        // pinned at exactly the same temperature the whole time.
        byte level = peak;
        for (int tick = 0; tick < 30; tick++) level = curve.Evaluate(63.1);

        byte target = FanCurve.CreateDefault().Evaluate(63.1);

        Assert.True(level < peak, $"fan never left its peak of {peak}% on a flat 63.1C die");
        Assert.True(level <= target,
            $"fan settled at {level}% with the die flat at 63.1C, where the curve asks {target}%");
    }

    [Fact]
    public void FanDoesNotStepDownInsideTheDeadband()
    {
        // The deadband must still exist, or the fan chatters on every small fluctuation.
        var curve = FanCurve.CreateDefault();
        curve.Evaluate(85);
        byte atPeak = curve.Evaluate(85);
        byte justBelow = curve.Evaluate(83);      // 2C is inside the 4C deadband

        Assert.Equal(atPeak, justBelow);
    }

    [Fact]
    public void DefaultCurve_IsSilentThroughTrueIdle()
    {
        var curve = FanCurve.CreateDefault();
        Assert.Equal(0, curve.Evaluate(30));
        Assert.Equal(0, curve.Evaluate(40));
    }

    /// <summary>
    /// The floor is the point of the whole app. Above FloorTempC the curve must never
    /// evaluate to 0, whatever the lookup table says.
    /// </summary>
    [Fact]
    public void AboveFloorTemperature_NeverReturnsZero()
    {
        // A deliberately dangerous table: 0% everywhere, i.e. the stock-BIOS failure itself.
        var curve = new FanCurve(new[] { new CurvePoint(0, 0), new CurvePoint(100, 0) })
        {
            FloorTempC = 55,
            FloorLevelPercent = 15,
        };

        Assert.Equal(0, curve.Evaluate(50));   // below the floor, silence is allowed
        Assert.True(curve.Evaluate(60) >= 15); // above it, the floor must win
        Assert.True(curve.Evaluate(95) >= 15);
    }

    /// <summary>
    /// Monotonicity is what makes the predictive lead safe. FanService evaluates the curve
    /// against max(measured, forecast), which is only sound if a higher temperature can never
    /// produce a lower fan level -- otherwise a forecast could REDUCE cooling.
    /// </summary>
    [Fact]
    public void HigherTemperature_NeverProducesALowerLevel()
    {
        for (int t = 0; t <= 100; t++)
        {
            // A fresh curve each iteration: Evaluate carries hysteresis state, so reusing one
            // instance would test the ramp-down limiter rather than the table.
            var lower = FanCurve.CreateDefault().Evaluate(t);
            var higher = FanCurve.CreateDefault().Evaluate(t + 1);
            Assert.True(higher >= lower, $"level dropped between {t}C and {t + 1}C");
        }
    }

    [Fact]
    public void RampUp_IsImmediate()
    {
        var curve = FanCurve.CreateDefault();
        curve.Evaluate(40);
        // No deadband on the way up: heat is the thing being reacted to.
        Assert.True(curve.Evaluate(90) > 50);
    }

    /// <summary>Hysteresis: a small drop must NOT reduce the fan, or it chatters every poll.</summary>
    [Fact]
    public void SmallTemperatureDrop_DoesNotReduceTheFan()
    {
        var curve = FanCurve.CreateDefault();
        byte hot = curve.Evaluate(85);
        byte afterSmallDrop = curve.Evaluate(83); // inside RampDownDeadbandC (4)
        Assert.Equal(hot, afterSmallDrop);
    }

    /// <summary>And a large drop must step down gradually, never fall off a cliff.</summary>
    [Fact]
    public void LargeTemperatureDrop_StepsDownByAtMostMaxStep()
    {
        var curve = FanCurve.CreateDefault();
        byte hot = curve.Evaluate(90);
        byte afterBigDrop = curve.Evaluate(45); // well beyond the deadband

        Assert.True(hot - afterBigDrop <= 10, $"stepped down by {hot - afterBigDrop}, limit is 10");
        Assert.True(afterBigDrop < hot, "a genuine drop should still reduce the fan");
    }

    [Fact]
    public void CurveRequiresAtLeastTwoPoints()
    {
        Assert.Throws<ArgumentException>(() => new FanCurve(new[] { new CurvePoint(0, 0) }));
    }

    [Fact]
    public void PointsAreSortedRegardlessOfInputOrder()
    {
        var curve = new FanCurve(new[]
        {
            new CurvePoint(90, 100),
            new CurvePoint(0, 0),
            new CurvePoint(50, 40),
        });

        Assert.Equal(0, curve.Points[0].TempC);
        Assert.Equal(50, curve.Points[1].TempC);
        Assert.Equal(90, curve.Points[2].TempC);
    }

    [Fact]
    public void BeyondTheLastPoint_ClampsToMaximum()
    {
        var curve = FanCurve.CreateDefault();
        Assert.Equal(100, curve.Evaluate(120));
    }
}
