using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// Turning one slider position into a pair of power policies.
///
/// The boost tests matter most. This project shipped a hard-coded boost index of 3 under a
/// comment claiming "Aggressive", when Windows enumerates 3 as Efficient Enabled on the
/// hardware in question -- a mistake that applies cleanly, reports success, and quietly does
/// the wrong thing. Selection is by name here, and these tests are what keep it that way.
/// </summary>
public class PowerPlanRecipeTests
{
    private static PowerKnob BoostKnob(params string[] names)
    {
        var options = new List<PowerSettingOption>();
        for (uint i = 0; i < names.Length; i++) options.Add(new PowerSettingOption(i, names[i]));
        return new PowerKnob(Guid.NewGuid(), Guid.NewGuid(), "boost", "Processor boost mode", "", 0, 0, options);
    }

    /// <summary>The boost list exactly as Windows enumerates it on an HP Victus 15-fb2xxx.</summary>
    private static PowerKnob RealBoost() => BoostKnob(
        "Disabled", "Enabled", "Aggressive", "Efficient Enabled",
        "Efficient Aggressive", "Aggressive At Guaranteed", "Efficient Aggressive At Guaranteed");

    private static PowerKnob Range(string key, uint min, uint max, string units = "") =>
        new(Guid.NewGuid(), Guid.NewGuid(), key, key, units, min, max, Array.Empty<PowerSettingOption>());

    private static (uint Ac, uint Dc) Resolve(PowerPlanRecipe r, PowerKnob k) =>
        r.Resolve(new[] { k }).Select(x => (x.Ac, x.Dc)).Single();

    [Fact]
    public void MaximumPerformancePicksAggressiveByName_NotByIndex()
    {
        var (ac, _) = Resolve(PowerPlanRecipe.MaximumPerformance(), RealBoost());

        Assert.Equal(2u, ac);   // "Aggressive" is index 2 here, not 3
    }

    /// <summary>
    /// The same request on a machine that enumerates the list differently must still land on
    /// Aggressive. This is the whole reason selection is by name.
    /// </summary>
    [Fact]
    public void AggressiveIsFoundWhereverItSitsInTheList()
    {
        var reordered = BoostKnob("Disabled", "Efficient Enabled", "Enabled", "Aggressive");

        var (ac, _) = Resolve(PowerPlanRecipe.MaximumPerformance(), reordered);

        Assert.Equal(3u, ac);   // index differs, meaning does not
    }

    [Fact]
    public void MaximumBatteryDisablesBoost()
    {
        var (ac, dc) = Resolve(PowerPlanRecipe.MaximumBattery(), RealBoost());

        Assert.Equal(0u, ac);
        Assert.Equal(0u, dc);
    }

    /// <summary>
    /// Battery never boosts, whatever the plan is called. A performance plan left active while
    /// unplugged has to degrade rather than run the cell flat.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void BatteryRailNeverBoosts(int bias)
    {
        var (_, dc) = Resolve(new PowerPlanRecipe("p", bias), RealBoost());
        Assert.Equal(0u, dc);
    }

    /// <summary>A machine offering no "Aggressive" still gets something sensible.</summary>
    [Fact]
    public void FallsBackWhenAggressiveIsNotOffered()
    {
        var (ac, _) = Resolve(PowerPlanRecipe.MaximumPerformance(), BoostKnob("Disabled", "Enabled"));
        Assert.Equal(1u, ac);   // "Enabled"
    }

    [Fact]
    public void MaximumProcessorStateScalesWithBias()
    {
        var knob = Range("procmax", 0, 100, "%");

        Assert.Equal(50u, Resolve(PowerPlanRecipe.MaximumBattery(), knob).Ac);
        Assert.Equal(100u, Resolve(PowerPlanRecipe.MaximumPerformance(), knob).Ac);

        // The midpoint of the slider lands on the midpoint of the range, not one below it.
        Assert.Equal(75u, Resolve(PowerPlanRecipe.Balanced(), knob).Ac);
    }

    /// <summary>The battery rail stays capped even at maximum performance.</summary>
    [Fact]
    public void BatteryProcessorCeilingStaysBelowMains()
    {
        var (ac, dc) = Resolve(PowerPlanRecipe.MaximumPerformance(), Range("procmax", 0, 100, "%"));
        Assert.True(dc < ac, $"battery ceiling {dc} should stay under the mains ceiling {ac}");
    }

    /// <summary>Raising the idle floor buys clock-ramp latency and costs heat. It is never done.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void MinimumProcessorStateIsNeverRaised(int bias)
    {
        var (ac, dc) = Resolve(new PowerPlanRecipe("p", bias), Range("procmin", 0, 100, "%"));
        Assert.Equal(5u, ac);
        Assert.Equal(5u, dc);
    }

    /// <summary>An explicit value survives the slider moving underneath it.</summary>
    [Fact]
    public void OverridesWinOverTheBias()
    {
        var knob = Range("procmax", 0, 100, "%");
        var recipe = new PowerPlanRecipe("p", 100) { Overrides = { ["procmax"] = (60u, 40u) } };

        var (ac, dc) = Resolve(recipe, knob);

        Assert.Equal(60u, ac);
        Assert.Equal(40u, dc);
    }

    /// <summary>An override outside what the machine accepts is clamped, not written raw.</summary>
    [Fact]
    public void OverridesAreClampedToWhatTheMachineAccepts()
    {
        var knob = Range("procmax", 5, 100, "%");
        var recipe = new PowerPlanRecipe("p", 50) { Overrides = { ["procmax"] = (250u, 0u) } };

        var (ac, dc) = Resolve(recipe, knob);

        Assert.Equal(100u, ac);
        Assert.Equal(5u, dc);
    }

    [Fact]
    public void BiasIsClampedToItsRange()
    {
        Assert.Equal(0, new PowerPlanRecipe("p", -40).Bias);
        Assert.Equal(100, new PowerPlanRecipe("p", 9000).Bias);
    }

    /// <summary>Zero means "never", and the label has to say so rather than "0 seconds".</summary>
    [Fact]
    public void ZeroTimeoutReadsAsNever()
    {
        var knob = Range("video", 0, 3600, "seconds");
        Assert.Equal("never", knob.Describe(0));
        Assert.Equal("600 seconds", knob.Describe(600));
    }

    [Fact]
    public void EnumeratedValuesAreDescribedByTheirWindowsName()
    {
        Assert.Equal("Aggressive", RealBoost().Describe(2));
        Assert.Equal("Efficient Enabled", RealBoost().Describe(3));
    }
}
