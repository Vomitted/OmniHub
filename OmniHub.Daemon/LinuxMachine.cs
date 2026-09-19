// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;
using OmniHub.Core.Hardware;

namespace OmniHub.Daemon;

/// <summary>
/// The one thing here that needs the C library rather than a file.
///
/// Writing to hwmon needs root, and finding that out by attempting the write and catching the
/// refusal would mean the diagnosis arrived after the fans had already been half taken over.
/// Asking first lets every command that needs privileges say so before it starts.
/// </summary>
internal static class LibC
{
    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    /// <summary>
    /// The EFFECTIVE user id, not the real one, because that is the id the kernel checks when
    /// this process opens a sysfs attribute for writing. They differ under sudo and under a
    /// setuid binary, and the real uid would answer the wrong question in both cases.
    /// </summary>
    public static bool IsRoot
    {
        get
        {
            // A machine where libc cannot be called is not a machine where this is root. Answering
            // true on failure would let a command past its own privilege check and into a write
            // that then fails somewhere less explicable.
            try { return geteuid() == 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }
}

/// <summary>
/// What this Linux machine is, and where its real temperature comes from.
///
/// The two things every command here needs before it can do anything honest: the board name, so a
/// profile can be filed under it and a refusal can say which machine it is refusing about, and a
/// die sensor, so the fan curve is not driven by the coarse ACPI zone.
/// </summary>
public static class LinuxMachine
{
    /// <summary>
    /// Manufacturer, product and baseboard, from the kernel's DMI export.
    ///
    /// The same three strings ModelProfile reads out of WMI on Windows, filed under the same key,
    /// so a board profile written on one platform is the board profile on the other. That is not a
    /// convenience: the baseboard is what "verified on this machine" is verified ABOUT, and two
    /// different keys for one laptop would mean evidence gathered on Linux could never promote the
    /// Windows build, or the reverse.
    ///
    /// Unreadable fields come back empty rather than as a placeholder. Some DMI fields need root;
    /// a board named "unknown" would be filed under a profile shared with every other machine that
    /// could not answer, which is how one laptop's measurements end up applied to another's.
    /// </summary>
    public static ModelInfo Identify(string dmiRoot = "/sys/class/dmi/id")
    {
        string Field(string name)
        {
            try { return File.ReadAllText(Path.Combine(dmiRoot, name)).Trim(); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        return new ModelInfo(Field("sys_vendor"), Field("product_name"), Field("board_name"));
    }

    /// <summary>
    /// A reader for the processor's own die temperature, or null when this machine exposes none.
    ///
    /// This is the part of the Linux port that is simply free. On Windows, reading Tctl means
    /// installing PawnIO, loading a signed bytecode module, and decoding an SMU mailbox whose
    /// layout changes between processor generations -- and when that driver loses the race at boot
    /// the whole session falls back to a zone that pins at 85 C and holds both fans at maximum.
    /// On Linux the k10temp driver has already done all of it and the answer is a file.
    ///
    /// Labels are preferred over channel numbers because the numbering is not stable across driver
    /// versions and the labels are. Tdie before Tctl deliberately: Tctl carries a per-model offset
    /// that exists to bias a fan curve, so on parts publishing both, the unmodified junction
    /// temperature is the honest one.
    /// </summary>
    /// <returns>A delegate returning degrees Celsius, or null if no die sensor was found.</returns>
    public static Func<double?>? DieSensor(Hwmon hwmon)
    {
        var chips = hwmon.Chips();

        // Ordered by how directly each names the die. A miss falls through to the next.
        foreach (string label in new[] { "Tdie", "Tctl", "Package id 0" })
        {
            foreach (HwmonChip chip in chips)
            {
                foreach (int n in chip.Temps)
                {
                    if (!string.Equals(hwmon.ReadText(Path.Combine(chip.Path, $"temp{n}_label")),
                                       label, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string valuePath = Path.Combine(chip.Path, $"temp{n}_input");
                    return () => hwmon.ReadNumber(valuePath) is { } milli ? milli / 1000.0 : null;
                }
            }
        }

        // Nothing labelled. On a good many laptops k10temp publishes temp1_input with no label at
        // all and that channel is Tctl, so the driver's name stands in as the evidence the label
        // did not provide. Named drivers only: guessing that some unidentified chip's temp1 is a
        // die sensor is how a fan curve ends up tracking the chipset.
        foreach (HwmonChip chip in chips)
        {
            if (chip.Name is not ("k10temp" or "zenpower" or "coretemp")) continue;
            if (chip.Temps.Count == 0) continue;

            string valuePath = Path.Combine(chip.Path, $"temp{chip.Temps[0]}_input");
            return () => hwmon.ReadNumber(valuePath) is { } milli ? milli / 1000.0 : null;
        }

        return null;
    }

    /// <summary>
    /// The chip whose fans this daemon would drive, or null when none can be driven.
    ///
    /// Prefers a chip with both PWM channels and tachometers, because those two together are what
    /// makes control verifiable: a duty cycle whose effect nobody can read back cannot be
    /// confirmed to have done anything. A PWM-only chip is still returned when that is all there
    /// is, since an unverifiable control path is not the same as an absent one -- it simply never
    /// gets past the write gate.
    /// </summary>
    public static HwmonChip? ControllableChip(Hwmon hwmon)
    {
        var chips = hwmon.Chips().Where(c => c.Controllable).ToList();
        return chips.FirstOrDefault(c => c.Fans.Count > 0) ?? chips.FirstOrDefault();
    }
}
