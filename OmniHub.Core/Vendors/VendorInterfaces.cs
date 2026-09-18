// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Vendors;

/// <summary>How much anybody actually knows about a candidate interface.</summary>
public enum Provenance
{
    /// <summary>Exercised on a real machine by this project.</summary>
    VerifiedHere,

    /// <summary>
    /// Named in another project's published source, and not checked here.
    ///
    /// Not a lesser kind of fact -- the people who wrote those drivers had the hardware, which is
    /// more than this project can say. It is a different kind: a claim inherited rather than
    /// measured, and worth labelling as such so nobody later mistakes the list for evidence.
    /// </summary>
    Published,
}

/// <summary>A vendor control interface worth looking for, and where the knowledge of it came from.</summary>
/// <param name="Vendor">The manufacturer string this belongs to, as WMI reports it.</param>
/// <param name="Interface">What the interface is called, for a person to read.</param>
/// <param name="WmiNamespace">Where to look.</param>
/// <param name="WmiClass">What to look for.</param>
/// <param name="Provenance">How this project came to know about it.</param>
/// <param name="Source">Who to credit, and where to check.</param>
public sealed record VendorInterface(
    string Vendor,
    string Interface,
    string WmiNamespace,
    string WmiClass,
    Provenance Provenance,
    string Source);

/// <summary>
/// The interfaces this application knows to look for, and what finding one does and does not mean.
///
/// FINDING ONE IS EVIDENCE. NOT FINDING ONE PROVES NOTHING. A machine may expose its controls
/// under a class this list does not name, or through something that is not WMI at all -- Dell's is
/// SMM calls -- and most of the laptop world has no vendor interface whatsoever and is driven
/// through the embedded controller instead. So a negative result is reported as "none of the
/// interfaces this build knows about", never as "your laptop has none".
///
/// Only the HP entry has been exercised on hardware by this project. The rest are transcribed from
/// the published source of drivers written by people who did have the machines, and are labelled
/// <see cref="Provenance.Published"/> so the difference stays visible. That labelling is the
/// point: this is a set of things to go and look for, not a set of things known to be there, and a
/// list flattening the two would be exactly the confident-sounding unverified claim this project
/// refuses everywhere else.
/// </summary>
public static class VendorInterfaces
{
    public static IReadOnlyList<VendorInterface> Known { get; } = new[]
    {
        new VendorInterface(
            "HP", "HP BIOS WMI (hpqBIntM)", "root\\wmi", "hpqBIntM",
            Provenance.VerifiedHere,
            "Driven by this application on an HP Victus 15 fb2xxx, board 8C2F."),

        new VendorInterface(
            "LENOVO", "Lenovo Gamezone WMI", "root\\wmi", "LENOVO_GAMEZONE_DATA",
            Provenance.Published,
            "LenovoLegionLinux (johnfanv2), which drives Legion fan curves and power modes "
            + "through it. Not checked here."),

        new VendorInterface(
            "LENOVO", "Lenovo other-method WMI", "root\\wmi", "LENOVO_OTHER_METHOD",
            Provenance.Published,
            "LenovoLegionLinux, used for fan-table access on newer Legion firmware. Not checked here."),

        new VendorInterface(
            "ASUS", "ASUS System Control Interface", "root\\wmi", "AsusAtkWmi_WMNB",
            Provenance.Published,
            "The Linux asus-wmi driver and G-Helper both reach ASUS laptops through this device's "
            + "DEVS and DSTS methods. The class name is the usual one; not checked here."),
    };

    /// <summary>
    /// Which known interfaces are present, given what this machine actually exposes.
    ///
    /// Pure, and takes the list of classes rather than going and finding them, so the matching can
    /// be tested without a laptop -- which matters more here than usual, since every machine this
    /// is written for is one nobody on the project has.
    /// </summary>
    public static IReadOnlyList<VendorInterface> Present(IEnumerable<string> wmiClasses)
    {
        var have = new HashSet<string>(wmiClasses, StringComparer.OrdinalIgnoreCase);
        return Known.Where(k => have.Contains(k.WmiClass)).ToList();
    }

    /// <summary>
    /// What to say about a machine after looking.
    ///
    /// Deliberately careful about the empty case. "No vendor interface was found" invites the
    /// reading "your laptop cannot be supported", which is wrong twice over: this list is short,
    /// and the great majority of laptops are driven through the embedded controller rather than
    /// through any vendor interface at all.
    /// </summary>
    public static string Describe(string manufacturer, IReadOnlyList<VendorInterface> present)
    {
        if (present.Count > 0)
            return $"Found {string.Join(", ", present.Select(p => p.Interface))}.";

        string who = string.IsNullOrWhiteSpace(manufacturer) ? "This machine" : manufacturer;

        // Naming the vendor when one of its interfaces was expected is the useful half: it is the
        // difference between "we do not know your brand" and "we know it and did not find the
        // interface", which point at different things to check.
        bool expected = Known.Any(k => k.Vendor.Equals(manufacturer, StringComparison.OrdinalIgnoreCase));

        return expected
            ? $"{who} is a make OmniHub knows interfaces for, but none of them answered here. "
              + "Different models within a brand expose different things."
            : $"{who} exposes none of the vendor interfaces this build knows to look for. That is "
              + "not the same as having none -- most laptops are driven through the embedded "
              + "controller instead, which needs a register map for the board rather than a "
              + "vendor interface.";
    }
}
