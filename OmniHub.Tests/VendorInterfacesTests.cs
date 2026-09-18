// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Vendors;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Looking for a vendor's control interface, and being careful about what not finding one means.
///
/// The trap this guards is a sentence, not a bug. "No vendor interface was found" reads as "your
/// laptop cannot be supported", and that is wrong twice over: the list is short, and most laptops
/// have no vendor interface at all and are driven through the embedded controller instead.
/// </summary>
public class VendorInterfacesTests
{
    [Fact]
    public void OnlyWhatWasActuallyExercisedHereIsMarkedVerified()
    {
        // The labelling is the substance of this class. One machine exists to test on, so exactly
        // one entry may claim to have been verified on hardware; everything else is somebody
        // else's published work, credited as such.
        var verified = VendorInterfaces.Known.Where(k => k.Provenance == Provenance.VerifiedHere).ToList();

        var only = Assert.Single(verified);
        Assert.Equal("hpqBIntM", only.WmiClass);
        Assert.Contains("8C2F", only.Source);
    }

    [Fact]
    public void EveryEntryCreditsWhereItCameFrom()
    {
        // A register address or a class name is a fact about hardware and can be used freely, but
        // the work of finding it was real. An unsourced entry is also unfalsifiable: nobody later
        // can tell whether it was measured, read somewhere, or guessed.
        foreach (var entry in VendorInterfaces.Known)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Source), $"{entry.Interface} credits nobody");
            Assert.False(string.IsNullOrWhiteSpace(entry.WmiClass));
            Assert.False(string.IsNullOrWhiteSpace(entry.WmiNamespace));

            if (entry.Provenance == Provenance.Published)
                Assert.Contains("not checked here", entry.Source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AMachineExposingAKnownClassIsRecognised()
    {
        var present = VendorInterfaces.Present(new[] { "Win32_Bios", "hpqBIntM", "MSAcpi_ThermalZoneTemperature" });

        var found = Assert.Single(present);
        Assert.Equal("HP", found.Vendor);
        Assert.Contains("Found HP BIOS WMI", VendorInterfaces.Describe("HP", present));
    }

    [Fact]
    public void MatchingIgnoresCaseBecauseWmiDoes()
    {
        Assert.Single(VendorInterfaces.Present(new[] { "HPQBINTM" }));
        Assert.Single(VendorInterfaces.Present(new[] { "lenovo_gamezone_data" }));
    }

    [Fact]
    public void FindingNothingIsNotReportedAsHavingNothing()
    {
        // The sentence this class exists to get right.
        string said = VendorInterfaces.Describe("Framework", VendorInterfaces.Present(new[] { "Win32_Bios" }));

        Assert.Contains("this build knows to look for", said);
        Assert.Contains("embedded controller", said);

        // And specifically NOT a verdict on the hardware.
        Assert.DoesNotContain("not supported", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AKnownBrandWithNoAnswerIsDistinguishedFromAnUnknownOne()
    {
        // Different things to go and check. A Lenovo with no Gamezone class is probably a model
        // that does not have it; a Framework was never expected to.
        string lenovo = VendorInterfaces.Describe("LENOVO", Array.Empty<VendorInterface>());
        string other = VendorInterfaces.Describe("Framework", Array.Empty<VendorInterface>());

        Assert.Contains("knows interfaces for", lenovo);
        Assert.NotEqual(lenovo, other);
    }

    [Fact]
    public void AMachineThatDidNotNameItselfIsStillDescribable()
    {
        // ModelProfile returns blanks rather than throwing when WMI declines, and this machine's
        // own Win32_ComputerSystemProduct fields are blank, so it is a real case.
        string said = VendorInterfaces.Describe("", Array.Empty<VendorInterface>());

        Assert.StartsWith("This machine", said);
    }

    [Fact]
    public void TwoInterfacesOnOneMachineAreBothReported()
    {
        // Lenovo exposes more than one, and which are present differs by firmware generation --
        // so reporting only the first would throw away the part that says which generation.
        var present = VendorInterfaces.Present(new[] { "LENOVO_GAMEZONE_DATA", "LENOVO_OTHER_METHOD" });

        Assert.Equal(2, present.Count);
        Assert.Contains("Gamezone", VendorInterfaces.Describe("LENOVO", present));
        Assert.Contains("other-method", VendorInterfaces.Describe("LENOVO", present));
    }
}
