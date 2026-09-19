// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;

namespace OmniHub.Daemon;

/// <summary>
/// One hardware monitoring chip as Linux presents it, and which channels it actually offers.
/// </summary>
/// <param name="Path">The chip's directory, e.g. /sys/class/hwmon/hwmon3.</param>
/// <param name="Name">What the kernel driver calls itself: k10temp, nct6798, hp_wmi, dell_smm.</param>
/// <param name="Pwm">PWM channel numbers that exist here, e.g. [1, 2] for pwm1 and pwm2.</param>
/// <param name="Fans">Tachometer channel numbers, from fanN_input.</param>
/// <param name="Temps">Temperature channel numbers, from tempN_input.</param>
public sealed record HwmonChip(
    string Path,
    string Name,
    IReadOnlyList<int> Pwm,
    IReadOnlyList<int> Fans,
    IReadOnlyList<int> Temps)
{
    /// <summary>Whether this chip can be commanded, as opposed to only read.</summary>
    public bool Controllable => Pwm.Count > 0;

    public override string ToString() =>
        $"{Name} ({System.IO.Path.GetFileName(Path)}): "
        + $"{Pwm.Count} pwm, {Fans.Count} tachometer{(Fans.Count == 1 ? "" : "s")}, {Temps.Count} temperature";
}

/// <summary>
/// The Linux hardware monitoring class, read as files.
///
/// This is why a Linux port is cheaper than the Windows one rather than dearer. On Windows this
/// project needed a signed kernel driver (PawnIO) to reach the SMU, a separate WMI interface per
/// vendor, and a raw embedded-controller path it deliberately refuses to ship because one wrong
/// byte on an HP Pavilion powers the machine off. On Linux the kernel has already done that
/// work: a driver written by somebody who had the chip in front of them exposes the fan as pwm1
/// and its tachometer as fan1_input, and reading a temperature is opening a file.
///
/// Every path here hangs off <see cref="Root"/> rather than being written out as a /sys literal.
/// That is not abstraction for its own sake -- it is the only reason any of this can be tested,
/// because the machine it was written on does not run Linux. A fake tree of directories and text
/// files in a temp folder exercises exactly the code the kernel will.
/// </summary>
public sealed class Hwmon
{
    /// <summary>Where the hardware monitoring class lives. Overridden in tests.</summary>
    public string Root { get; }

    public Hwmon(string root = "/sys/class/hwmon") => Root = root;

    /// <summary>
    /// Every chip the kernel is exposing, in the order it exposes them.
    ///
    /// Never throws. An absent /sys/class/hwmon means this is not Linux, or a kernel built
    /// without hwmon, and both are ordinary answers rather than failures: the caller reports
    /// that nothing was found and stops, which is what it would do for an empty directory too.
    /// </summary>
    public IReadOnlyList<HwmonChip> Chips()
    {
        if (!Directory.Exists(Root)) return Array.Empty<HwmonChip>();

        var found = new List<HwmonChip>();
        foreach (string dir in Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal))
        {
            // A chip with no name is still a chip. The name is a label for a person to read and
            // a key for a profile to be filed under, not a precondition for the channels working.
            string name = ReadText(Path.Combine(dir, "name")) ?? "unnamed";

            found.Add(new HwmonChip(
                dir, name,
                Channels(dir, "pwm", suffix: ""),
                Channels(dir, "fan", suffix: "_input"),
                Channels(dir, "temp", suffix: "_input")));
        }

        return found;
    }

    /// <summary>
    /// A number read from a sysfs attribute, or null when it could not be read as one.
    ///
    /// Null rather than zero, which is the rule the rest of this project runs on: a sensor that
    /// did not answer has not reported a low value. A fan whose tachometer failed, reported as
    /// 0 rpm, is indistinguishable from a stopped fan -- and a stopped fan on a hot machine is
    /// the exact fault this application exists to catch.
    /// </summary>
    public long? ReadNumber(string path)
    {
        string? text = ReadText(path);
        return text is not null
               && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;
    }

    /// <summary>The trimmed contents of a sysfs attribute, or null when it would not open.</summary>
    public string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Writes a number to a sysfs attribute, reporting whether it landed rather than throwing.
    ///
    /// Writing to hwmon needs root, and a refused write is the single most likely thing to happen
    /// to this code in the wild: it is what anybody gets for running the daemon without
    /// privileges. That deserves a reason they can act on rather than a stack trace, so the
    /// failure is a return value.
    /// </summary>
    public (bool Written, string Detail) WriteNumber(string path, long value)
    {
        try
        {
            File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));
            return (true, "written");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"permission denied writing {path}; hwmon control needs root");
        }
        catch (IOException ex)
        {
            // Usually EINVAL: the value is outside what the driver accepts, or this attribute is
            // read-only on this chip despite existing.
            return (false, $"the kernel refused the write to {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Which numbered channels of a kind exist, e.g. pwm1 and pwm2, or temp1_input and temp3_input.
    ///
    /// Numbering is not contiguous and must not be assumed to be. Drivers publish the channels the
    /// chip actually wires up, so a board with temp1 and temp3 and no temp2 is ordinary, and
    /// counting rather than enumerating would read the wrong sensor on it.
    /// </summary>
    private static IReadOnlyList<int> Channels(string dir, string prefix, string suffix)
    {
        var numbers = new List<int>();
        foreach (string file in Directory.EnumerateFiles(dir, prefix + "*"))
        {
            string stem = Path.GetFileName(file);
            if (suffix.Length > 0)
            {
                if (!stem.EndsWith(suffix, StringComparison.Ordinal)) continue;
                stem = stem[..^suffix.Length];
            }
            else if (stem.Contains('_'))
            {
                // pwm1_enable and pwm1_mode are attributes OF pwm1, not channels of their own.
                continue;
            }

            if (stem.Length > prefix.Length
                && int.TryParse(stem[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                numbers.Add(n);
        }

        numbers.Sort();
        return numbers;
    }
}
