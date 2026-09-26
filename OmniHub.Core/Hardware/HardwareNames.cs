// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Text.RegularExpressions;

namespace OmniHub.Core.Hardware;

/// <summary>
/// A processor's or a graphics card's name the way a person says it.
///
/// The names the platform reports are written for a spec sheet: "AMD Ryzen 7 7840HS w/ Radeon 780M
/// Graphics", "12th Gen Intel(R) Core(TM) i7-12700H", "NVIDIA GeForce RTX 4050 Laptop GPU". A card
/// headed with that wraps or is cut off mid-word; the part that identifies the chip is the model,
/// and that is what is kept. Only noise is removed -- vendor names, trademark marks, the integrated
/// graphics suffix, a clock speed -- so the result is still the reported name, shortened.
/// </summary>
public static partial class HardwareNames
{
    public static string? ShortCpu(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return null;

        string name = Cut(reported, " w/", " with Radeon", " @ ");
        name = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "");
        name = Generation().Replace(name, "");
        foreach (var noise in new[] { "AMD ", "Intel ", " Processor", " CPU" })
            name = name.Replace(noise, " ", StringComparison.OrdinalIgnoreCase);
        name = CoreCount().Replace(name, "");

        return Tidy(name);
    }

    public static string? ShortGpu(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return null;

        string name = reported;
        foreach (var noise in new[] { "NVIDIA ", "GeForce ", "AMD ", "(R)", "(TM)" })
            name = name.Replace(noise, " ", StringComparison.OrdinalIgnoreCase);
        if (name.TrimEnd().EndsWith(" GPU", StringComparison.OrdinalIgnoreCase))
            name = name.TrimEnd()[..^4];

        return Tidy(name);
    }

    private static string Cut(string name, params string[] from)
    {
        foreach (var marker in from)
        {
            int at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0) name = name[..at];
        }
        return name;
    }

    private static string? Tidy(string name)
    {
        string tidy = Spaces().Replace(name, " ").Trim();
        return tidy.Length == 0 ? null : tidy;
    }

    [GeneratedRegex(@"^\s*\d+(st|nd|rd|th)\s+Gen\s+", RegexOptions.IgnoreCase)]
    private static partial Regex Generation();

    [GeneratedRegex(@"\s\d+-Core\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoreCount();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
