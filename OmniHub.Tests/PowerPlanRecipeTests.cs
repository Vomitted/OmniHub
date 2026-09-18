// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// Turning one slider position into a pair of power policies.
///
/// The processor knobs this used to resolve -- minimum state, maximum state and boost mode --
/// are gone, along with their tests. They are not missing coverage: writing them at all was the
/// defect, and PowerPlanSetupTests now asserts they cannot come back. What is left here is the
/// display, disk, sleep and ASPM policy, which is what the builder is for.
/// </summary>
public class PowerPlanRecipeTests
{
    private static PowerKnob Range(string key, uint min, uint max, string units = "") =>
        new(Guid.NewGuid(), Guid.NewGuid(), key, key, units, min, max, Array.Empty<PowerSettingOption>());

    private static (uint Ac, uint Dc) Resolve(PowerPlanRecipe r, PowerKnob k) =>
        r.Resolve(new[] { k }).Select(x => (x.Ac, x.Dc)).Single();

    /// <summary>An explicit value survives the slider moving underneath it.</summary>
    [Fact]
    public void OverridesWinOverTheBias()
    {
        var knob = Range("video", 0, 3600, "seconds");
        var recipe = new PowerPlanRecipe("p", 100) { Overrides = { ["video"] = (60u, 40u) } };

        var (ac, dc) = Resolve(recipe, knob);

        Assert.Equal(60u, ac);
        Assert.Equal(40u, dc);
    }

    /// <summary>An override outside what the machine accepts is clamped, not written raw.</summary>
    [Fact]
    public void OverridesAreClampedToWhatTheMachineAccepts()
    {
        var knob = Range("disk", 5, 100, "seconds");
        var recipe = new PowerPlanRecipe("p", 50) { Overrides = { ["disk"] = (250u, 0u) } };

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

    /// <summary>
    /// An enumerated setting reads back under the name Windows gave it, not as a bare index.
    /// The list here is the PCI Express ASPM one exactly as this machine enumerates it.
    /// </summary>
    [Fact]
    public void EnumeratedValuesAreDescribedByTheirWindowsName()
    {
        var aspm = new PowerKnob(Guid.NewGuid(), Guid.NewGuid(), "aspm", "PCI Express power saving", "",
            0, 0, new List<PowerSettingOption>
            {
                new(0, "Off"), new(1, "Moderate power savings"), new(2, "Maximum power savings"),
            });

        Assert.Equal("Off", aspm.Describe(0));
        Assert.Equal("Maximum power savings", aspm.Describe(2));
    }
}
