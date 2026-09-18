// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Vendors;

/// <summary>How much of a capability this machine actually offers.</summary>
public enum SupportState
{
    /// <summary>Readable and controllable.</summary>
    Full,

    /// <summary>
    /// Readable, but nothing here may change it.
    ///
    /// The state this release exists to make expressible. Until now a capability was present or
    /// absent, so a machine whose fans OmniHub can watch but must not command had no way to say
    /// so -- it appeared simply unsupported, which throws away the half that works.
    /// </summary>
    ReadOnly,

    /// <summary>Not available at all, for a reason that should be stated.</summary>
    Unavailable,
}

/// <summary>One thing this application might do, and whether it can do it here.</summary>
/// <param name="Name">What the user would call it.</param>
/// <param name="State">How much of it works.</param>
/// <param name="Detail">Why, in words worth showing. Never blank.</param>
public sealed record Capability(string Name, SupportState State, string Detail);

/// <summary>
/// The facts a machine's support can be decided from, gathered by whoever can gather them.
///
/// A record rather than a live object so the decision below is a pure function of stated inputs.
/// Every one of these comes from somewhere that needs a laptop -- WMI, PawnIO, a display driver --
/// and none of that should have to be present to check that the reasoning is right.
/// </summary>
public sealed record MachineFacts(
    string Manufacturer,
    string Product,
    string Baseboard,
    VendorTier FanTier,
    string? VendorUnavailableReason,
    bool SmuAvailable,
    string? SmuUnavailableReason,
    int FanCount,
    int DrivenFans,
    bool GpuPresent,
    bool GpuThermalSource);

/// <summary>
/// What OmniHub can do on this machine, and where it cannot, why not.
///
/// This used to be a list of missing things assembled inside the Dashboard. Two problems with
/// that, and the second is the one that matters for supporting other laptops.
///
/// It was only expressible as present or absent, so there was no way to state the true thing
/// about a machine this application can watch but must not command. And it lived in a view, so
/// the reasoning could not be tested without a window, on a project whose tests deliberately
/// cannot reference the application at all.
///
/// Every entry carries a reason. A capability listed as unavailable with no explanation tells
/// somebody their laptop is not good enough; the same line with a reason tells them whether it is
/// their driver, their hardware or this program.
/// </summary>
public static class MachineSupport
{
    /// <summary>Everything worth reporting, in the order a person would want to read it.</summary>
    public static IReadOnlyList<Capability> Describe(MachineFacts facts)
    {
        var all = new List<Capability> { FanControl(facts), Tuning(facts) };

        if (facts.DrivenFans > 0 && facts.FanCount > facts.DrivenFans)
            all.Add(UndrivenFans(facts));

        all.Add(Gpu(facts));
        return all;
    }

    /// <summary>Only what is not fully working -- the list the readiness card has always shown.</summary>
    public static IReadOnlyList<Capability> Shortfalls(MachineFacts facts) =>
        Describe(facts).Where(c => c.State != SupportState.Full).ToList();

    /// <summary>
    /// One line summarising the machine, for a headline.
    ///
    /// Says what works before what does not. A machine with no vendor interface is still doing
    /// most of its job, and leading with the failure misdescribes it.
    /// </summary>
    public static string Summarise(MachineFacts facts)
    {
        string machine = $"{facts.Manufacturer} {facts.Product}".Trim();
        if (machine.Length == 0) machine = "this machine";

        int limited = Shortfalls(facts).Count;
        return limited == 0
            ? $"Everything OmniHub offers works on {machine}."
            : $"Detected {machine}. {limited} capabilit{(limited == 1 ? "y is" : "ies are")} "
              + "limited here; everything else works normally.";
    }

    private static Capability FanControl(MachineFacts facts) => facts.FanTier switch
    {
        VendorTier.Verified => new Capability(
            "Fan control, GPU power, BIOS limits",
            SupportState.Full,
            "This board's control interface answered and has been verified."),

        // The case the tier system was built for, and the one that becomes common as other
        // vendors arrive: readings are trustworthy, commands are not yet.
        VendorTier.Reading => new Capability(
            "Fan control",
            SupportState.ReadOnly,
            $"Fan speeds and temperatures can be read on board {facts.Baseboard}, but writing to "
            + "its controller has not been verified on this model. OmniHub will not command a fan "
            + "it has not been shown is safe to command -- the same rule that stops it inventing a "
            + "temperature."),

        _ => new Capability(
            "Fan control, GPU power, BIOS limits",
            SupportState.Unavailable,
            facts.VendorUnavailableReason ?? "No vendor control interface was found on this machine."),
    };

    private static Capability Tuning(MachineFacts facts) => facts.SmuAvailable
        ? new Capability("Processor tuning and die temperature", SupportState.Full,
                         "The SMU is open, so Tctl is read directly and limits can be written.")
        : new Capability("Processor tuning and die temperature", SupportState.Unavailable,
                         facts.SmuUnavailableReason ?? "The SMU could not be opened.");

    private static Capability UndrivenFans(MachineFacts facts) => new(
        $"Fans {facts.DrivenFans + 1} to {facts.FanCount}",
        SupportState.Unavailable,
        $"This board reports {facts.FanCount} fans and OmniHub drives {facts.DrivenFans}. The extra "
        + "fans stay under firmware control. Extending the command payload has not been verified on "
        + "any machine, and guessing at it is not worth the risk to your cooling.");

    // Two different degrees of unavailable, and conflating them misdescribes every non-NVIDIA
    // machine: those get a name and a load figure, they just get no thermal sensor, because
    // Windows exposes none generically for a GPU.
    private static Capability Gpu(MachineFacts facts)
    {
        if (!facts.GpuPresent)
            return new Capability("GPU telemetry", SupportState.Unavailable,
                                  "No display adapter could be read.");

        return facts.GpuThermalSource
            ? new Capability("GPU telemetry", SupportState.Full,
                             "Temperature, power, clock and utilisation are all available.")
            : new Capability("GPU temperature and power", SupportState.ReadOnly,
                             "Windows reports GPU name and load for any adapter but exposes no thermal "
                             + "or power sensor. Those readings need a vendor library, which installs "
                             + "with the driver.");
    }
}
