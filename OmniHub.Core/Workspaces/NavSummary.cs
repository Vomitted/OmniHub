// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;

namespace OmniHub.Core.Workspaces;

/// <summary>
/// The facts the sidebar can state, gathered by the shell from what it already holds.
///
/// Every field is optional. A fact the shell could not establish is left out of the line rather
/// than guessed, so a machine without an SMU simply has a shorter Performance line.
/// </summary>
public sealed record NavFacts
{
    public string? LimitName { get; init; }
    public double? LimitPercent { get; init; }

    /// <summary>The fan mode in words: "auto curve", "BIOS", "max".</summary>
    public string? FanMode { get; init; }

    /// <summary>What the curve last sent, only when it is steering.</summary>
    public int? CommandedPercent { get; init; }

    public double? FanRpm { get; init; }
    public double? PackageWatts { get; init; }
    public double? CpuClockGHz { get; init; }
    public int? BatteryPercent { get; init; }
    public bool? OnAc { get; init; }
    public bool TimerResolution { get; init; }
    public bool DwmPriority { get; init; }
    public string? Theme { get; init; }
}

/// <summary>
/// The second line under each sidebar item: what is in force for that subject, right now.
///
/// The sidebar used to name seven places and state one fact, the fan mode, in a card of its own.
/// What was actually in force -- the fan level, the power being drawn, the battery, which Windows
/// tweaks were on -- was spread over four pages and visible from none of the others. Each item now
/// carries its own, so the index of the application is also a summary of the machine.
///
/// Keyed on a workspace's first panel, the same key its icon comes from, and composed here rather
/// than in the window so that the wording is under test.
///
/// Every line fits the sidebar's text column, twenty-three characters of the monospace face,
/// because a line cut short loses its end -- and the end is where the number is.
/// </summary>
public static class NavSummary
{
    private const string Dot = " · ";

    /// <summary>The line for a workspace led by <paramref name="panelType"/>, or null when there is nothing to say.</summary>
    public static string? For(string panelType, NavFacts facts) => panelType switch
    {
        "dashboard" => facts.LimitName is { Length: > 0 } limit && facts.LimitPercent is { } pct
            ? $"{Short(limit)} at {pct.ToString("0", CultureInfo.InvariantCulture)}%"
            : null,

        "fans" => Join(
            facts.FanMode,
            facts.CommandedPercent is { } c ? $"{c}%" : null,
            facts.FanRpm is { } rpm ? $"{rpm.ToString("0", CultureInfo.InvariantCulture)} rpm" : null),

        "performance" => Join(
            facts.PackageWatts is { } w ? $"{w.ToString("0.0", CultureInfo.InvariantCulture)} W" : null,
            facts.CpuClockGHz is { } ghz ? $"{ghz.ToString("0.00", CultureInfo.InvariantCulture)} GHz" : null),

        "power" => Join(
            facts.BatteryPercent is { } b ? $"{b}%" : null,
            facts.OnAc switch { true => "on AC", false => "on battery", _ => null }),

        // Named rather than counted: "2 of 2 on" says less than the two names, and there are two.
        // The switch, not a resolution: which interval Windows actually granted is a reading the
        // System page takes, and this line has not taken it.
        "system" => (facts.TimerResolution, facts.DwmPriority) switch
        {
            (true, true) => "timer + DWM priority",
            (true, false) => "fine timer",
            (false, true) => "DWM priority",
            _ => "Windows defaults",
        },

        "settings" => facts.Theme,

        _ => null,
    };

    /// <summary>
    /// A limit's name as the sidebar can hold it: the acronym where the name carries one, the
    /// name in lower case otherwise. "Core current (EDC)" at 99% has to show the 99.
    /// </summary>
    private static string Short(string name)
    {
        int open = name.IndexOf('('), close = name.IndexOf(')');
        if (open >= 0 && close > open + 1) return name[(open + 1)..close];

        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        return present.Length == 0 ? null : string.Join(Dot, present);
    }
}
