using OmniHub.Core.Fan;
using Xunit;

namespace OmniHub.Tests;

public class ThermalSoakTests
{
    private static readonly DateTime Start = new(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Feeds a steady temperature for a number of seconds, two seconds at a time.</summary>
    private static ThermalSoak Run(ThermalSoak soak, double tempC, int seconds, DateTime from)
    {
        for (int t = 0; t <= seconds; t += 2) soak.Ingest(tempC, from.AddSeconds(t));
        return soak;
    }

    [Fact]
    public void IdlingNeverBuildsAResidue()
    {
        var soak = Run(new ThermalSoak(), tempC: 50, seconds: 600, from: Start);

        Assert.Equal(0, soak.DegreeMinutes, 6);
        Assert.Equal(0, soak.BoostPercent, 6);
    }

    /// <summary>
    /// The distinction the whole term exists for: a brief spike and a long load reach the same
    /// temperature and leave completely different amounts of heat in the machine.
    /// </summary>
    [Fact]
    public void ABriefSpikeLeavesFarLessResidueThanASustainedLoad()
    {
        var spike = Run(new ThermalSoak(), tempC: 82, seconds: 4, from: Start);
        var sustained = Run(new ThermalSoak(), tempC: 82, seconds: 300, from: Start);

        Assert.True(sustained.DegreeMinutes > spike.DegreeMinutes * 10,
            $"sustained load accumulated {sustained.DegreeMinutes:0.00} degree-minutes against "
            + $"{spike.DegreeMinutes:0.00} for a four-second spike");
        // A four-second spike still earns a little over 1%, because the term is an integral and
        // four seconds is not zero. The assertion is that it is NEGLIGIBLE beside the sustained
        // case, not that it is absent -- demanding zero would be demanding the wrong behaviour.
        Assert.True(spike.BoostPercent < sustained.BoostPercent / 5,
            $"a four-second spike justified {spike.BoostPercent:0.00}% against "
            + $"{sustained.BoostPercent:0.00}% for five minutes at the same temperature");
    }

    /// <summary>
    /// After a long load the residue must persist, because that is the whole point: the die has
    /// cooled and the heatsink has not.
    /// </summary>
    [Fact]
    public void ResidueOutlivesTheTemperatureThatCausedIt()
    {
        var soak = Run(new ThermalSoak(), tempC: 85, seconds: 300, from: Start);
        double peakBoost = soak.BoostPercent;
        double peakHeat = soak.DegreeMinutes;
        Assert.True(peakBoost > 5, $"a five-minute load at 85C only justified {peakBoost:0.0}%");

        // The machine drops to idle. The boost should still be doing something a minute later.
        var cooled = Start.AddSeconds(300);
        Run(soak, tempC: 50, seconds: 60, from: cooled);

        Assert.True(soak.BoostPercent > 0, "the residue vanished the moment the die cooled");

        // Decay is checked on the UNCAPPED accumulation, not on the boost.
        //
        // Five minutes at 85C banks far more heat than the 18% cap can express, so the boost sits
        // at the cap both before and after a minute of cooling and comparing the two shows
        // nothing. The first version of this asserted BoostPercent had fallen and failed against
        // entirely correct behaviour: the residue HAD decayed, from about 45 degree-minutes to 28,
        // and both of those still justify more boost than the cap allows. Asserting on a clamped
        // value is asserting on the clamp.
        Assert.True(soak.DegreeMinutes < peakHeat,
            $"accumulated heat did not decay: {peakHeat:0.0} then {soak.DegreeMinutes:0.0} degree-minutes");
    }

    /// <summary>And it must eventually go, or this becomes the cool-and-loud complaint.</summary>
    [Fact]
    public void ResidueDecaysToNothingGivenTime()
    {
        var soak = Run(new ThermalSoak(), tempC: 85, seconds: 300, from: Start);

        Run(soak, tempC: 45, seconds: 1800, from: Start.AddSeconds(300));

        Assert.True(soak.BoostPercent < 0.5,
            $"half an hour at idle still left a {soak.BoostPercent:0.00}% boost");
    }

    /// <summary>
    /// The boost is capped, so the soak term can never become the dominant input. An uncapped
    /// integral is how a controller ends up pinned at maximum for reasons nobody can reconstruct.
    /// </summary>
    [Fact]
    public void BoostIsBounded()
    {
        var soak = Run(new ThermalSoak(), tempC: 95, seconds: 7200, from: Start);

        Assert.True(soak.BoostPercent <= 18.0 + 1e-9,
            $"boost reached {soak.BoostPercent:0.0}%, above its own cap");
    }

    /// <summary>
    /// A gap is the machine having slept or the application having been closed. Integrating across
    /// it would invent an hour of accumulated heat out of an hour of the laptop being shut, and it
    /// would do so at exactly the moment the fan should be calm.
    /// </summary>
    [Fact]
    public void ALongGapClearsTheAccumulationRatherThanIntegratingAcrossIt()
    {
        var soak = Run(new ThermalSoak(), tempC: 85, seconds: 300, from: Start);
        Assert.True(soak.DegreeMinutes > 0);

        soak.Ingest(85, Start.AddHours(8));

        Assert.Equal(0, soak.DegreeMinutes, 6);
    }

    /// <summary>
    /// Accumulation follows real elapsed time, not tick count. The poll loop re-arms after each
    /// tick rather than running on a fixed period, and its real cadence was measured at 2.31s
    /// against a nominal 2s.
    /// </summary>
    [Fact]
    public void AccumulationFollowsElapsedTimeNotSampleCount()
    {
        var fast = new ThermalSoak();
        for (int t = 0; t <= 120; t += 1) fast.Ingest(80, Start.AddSeconds(t));

        var slow = new ThermalSoak();
        for (int t = 0; t <= 120; t += 4) slow.Ingest(80, Start.AddSeconds(t));

        // Four times the samples over the same two minutes must not mean four times the heat.
        //
        // Compared within a few percent rather than to a decimal place. This is a discrete
        // integration of a continuous decay, so sampling it at one second and at four cannot agree
        // exactly; they agreed to 23.58 against 23.86, about 1%. Demanding equality here would be
        // asserting that discretisation error does not exist, and the test would be failing for a
        // true statement.
        double spread = Math.Abs(fast.DegreeMinutes - slow.DegreeMinutes) / fast.DegreeMinutes;
        Assert.True(spread < 0.05,
            $"sampling rate changed the accumulation by {spread:P1} "
            + $"({fast.DegreeMinutes:0.00} against {slow.DegreeMinutes:0.00})");
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var soak = Run(new ThermalSoak(), tempC: 88, seconds: 300, from: Start);
        Assert.True(soak.BoostPercent > 0);

        soak.Reset();

        Assert.Equal(0, soak.DegreeMinutes, 6);
        Assert.Equal(0, soak.BoostPercent, 6);
    }
}
