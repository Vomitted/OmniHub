// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Vendors;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// What this application tells somebody it can do on their laptop.
///
/// Worth testing because it is the first thing a person sees on a machine that is not the one
/// this was written for, and because getting it wrong is not a cosmetic failure: a capability
/// reported as unavailable with no reason tells somebody their hardware is not good enough, when
/// the truth is usually that they are missing a driver.
/// </summary>
public class MachineSupportTests
{
    /// <summary>This machine: HP, verified, SMU open, NVIDIA present.</summary>
    private static MachineFacts Victus(VendorTier tier = VendorTier.Verified) => new(
        Manufacturer: "HP",
        Product: "Victus by HP Gaming Laptop 15-fb2xxx",
        Baseboard: "8C2F",
        FanTier: tier,
        VendorUnavailableReason: null,
        SmuAvailable: true,
        SmuUnavailableReason: null,
        FanCount: 2,
        DrivenFans: 2,
        GpuPresent: true,
        GpuThermalSource: true);

    /// <summary>A machine nobody here owns: no vendor interface, no SMU, no GPU thermal source.</summary>
    private static MachineFacts Stranger() => Victus(VendorTier.Detected) with
    {
        Manufacturer = "Lenovo",
        Product = "Legion 5 16IRX9",
        Baseboard = "LNVNB161216",
        VendorUnavailableReason = "This machine does not expose HP's WMI control interface.",
        SmuAvailable = false,
        SmuUnavailableReason = "PawnIO is not installed.",
        GpuThermalSource = false,
    };

    [Fact]
    public void AFullySupportedMachineReportsNoShortfalls()
    {
        Assert.Empty(MachineSupport.Shortfalls(Victus()));
        Assert.Contains("Everything OmniHub offers works", MachineSupport.Summarise(Victus()));
    }

    [Fact]
    public void EveryCapabilityCarriesAReason()
    {
        // The rule this class exists to keep. An entry without a reason is a verdict on somebody's
        // laptop with no way to act on it.
        foreach (var facts in new[] { Victus(), Victus(VendorTier.Reading), Stranger() })
            foreach (var capability in MachineSupport.Describe(facts))
            {
                Assert.False(string.IsNullOrWhiteSpace(capability.Detail),
                             $"{capability.Name} on {facts.Product} gives no reason");
                Assert.False(string.IsNullOrWhiteSpace(capability.Name));
            }
    }

    [Fact]
    public void AReadableButUnverifiedMachineIsNotReportedAsUnsupported()
    {
        // The distinction the whole tier system exists for. Before it, a machine OmniHub could
        // watch but must not command looked identical to one it could do nothing with, which
        // throws away the half that works.
        var fans = Assert.Single(
            MachineSupport.Describe(Victus(VendorTier.Reading)),
            c => c.Name.Contains("Fan control"));

        Assert.Equal(SupportState.ReadOnly, fans.State);

        // And it says which board, because the answer is specific to the board rather than to
        // the brand.
        Assert.Contains("8C2F", fans.Detail);
    }

    [Fact]
    public void AMachineWithNoVendorInterfaceStillGetsAReasonRatherThanASilence()
    {
        var fans = Assert.Single(
            MachineSupport.Describe(Stranger()),
            c => c.Name.Contains("Fan control"));

        Assert.Equal(SupportState.Unavailable, fans.State);
        Assert.Contains("HP's WMI control interface", fans.Detail);
    }

    [Fact]
    public void TheSummaryLeadsWithWhatWorks()
    {
        // A machine with no vendor interface is still doing most of its job -- temperatures,
        // battery, the Windows-side controls. Leading with the failure misdescribes it.
        string summary = MachineSupport.Summarise(Stranger());

        Assert.Contains("Lenovo Legion 5 16IRX9", summary);
        Assert.Contains("everything else works normally", summary);
    }

    [Fact]
    public void UndrivenFansAreNamedOnlyWhenThereAreSome()
    {
        // Two fans driven out of two is the normal case and should say nothing at all.
        Assert.DoesNotContain(MachineSupport.Describe(Victus()), c => c.Name.StartsWith("Fans "));

        var four = Victus() with { FanCount = 4 };
        var extra = Assert.Single(MachineSupport.Describe(four), c => c.Name.StartsWith("Fans "));

        Assert.Equal("Fans 3 to 4", extra.Name);
        Assert.Equal(SupportState.Unavailable, extra.State);
    }

    [Fact]
    public void AFanCountThatWasNeverReadDoesNotInventMissingFans()
    {
        // FanCount stays zero when the vendor call failed, and zero fans reported is not
        // evidence of fans going undriven. Reporting "Fans 1 to 0" would be this application
        // inventing a shortfall out of a failed read, which is the same sin as inventing a
        // temperature.
        var unknown = Stranger() with { FanCount = 0, DrivenFans = 0 };

        Assert.DoesNotContain(MachineSupport.Describe(unknown), c => c.Name.StartsWith("Fans "));
    }

    [Fact]
    public void AGpuWithoutAThermalSensorIsDistinguishedFromNoGpuAtAll()
    {
        // Conflating these misdescribed every non-NVIDIA machine: those get a name and a load
        // figure, they just get no thermal sensor, because Windows exposes none generically.
        // StartsWith rather than Contains: the fan entry is called "Fan control, GPU power, BIOS
        // limits" on a machine with no vendor interface, and a looser predicate matches both.
        var noSensor = Assert.Single(MachineSupport.Describe(Stranger()), c => c.Name.StartsWith("GPU"));
        Assert.Equal(SupportState.ReadOnly, noSensor.State);

        var none = Assert.Single(
            MachineSupport.Describe(Stranger() with { GpuPresent = false }), c => c.Name.StartsWith("GPU"));
        Assert.Equal(SupportState.Unavailable, none.State);

        Assert.NotEqual(noSensor.Detail, none.Detail);
    }

    [Fact]
    public void AnUnnamedMachineIsStillDescribable()
    {
        // ModelProfile.Detect returns blanks rather than throwing when WMI declines, and this
        // machine's own Win32_ComputerSystemProduct fields are blank, so it is a real case.
        string summary = MachineSupport.Summarise(
            Stranger() with { Manufacturer = "", Product = "" });

        Assert.Contains("this machine", summary);
        Assert.DoesNotContain("Detected .", summary);
    }
}
