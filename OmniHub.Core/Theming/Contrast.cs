// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Theming;

/// <summary>
/// An opaque colour, as the three channels a palette file writes.
///
/// Not the WPF type: this project's Core has no WPF reference, and the arithmetic below does not
/// need one. Alpha is absent rather than ignored -- a translucent colour has to be composited
/// against its real backdrop before any of this means anything, and pretending otherwise is how a
/// contrast check reports a pair as readable that plainly is not.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses #RRGGBB or #AARRGGBB, the two forms the palette files use.</summary>
    public static Rgb? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;

        string s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..];      // drop alpha; see the note above
        if (s.Length != 6) return null;

        try
        {
            return new Rgb(
                Convert.ToByte(s[..2], 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// WCAG contrast, and what this project treats as readable.
///
/// It lived in the test suite, which was the right place while palettes were only ever written by
/// hand and checked in. Once somebody can build one in the application, the same rule has to run
/// before it is saved -- and having two implementations of it would mean the editor and the test
/// could disagree about whether a palette is acceptable, which is worse than either answer.
/// </summary>
public static class Contrast
{
    /// <summary>
    /// WCAG 2.1 AA for normal text.
    ///
    /// The standard allows 3.0 for large text. This project does not use the concession: the
    /// figures that would qualify are the ones people read at a glance from across a desk, and
    /// the labels beside them are small.
    /// </summary>
    public const double MinimumRatio = 4.5;

    /// <summary>
    /// The perceived brightness of a colour, 0 to 1.
    ///
    /// The gamma-corrected form from the standard rather than a plain average. The difference is
    /// not academic: a naive average rates saturated greens and blues as far closer in brightness
    /// than an eye does, and it passes pairs that plainly are not readable.
    /// </summary>
    public static double RelativeLuminance(Rgb c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>The ratio between two colours, from 1 (identical) to 21 (black on white).</summary>
    public static double Ratio(Rgb a, Rgb b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        (double hi, double lo) = la > lb ? (la, lb) : (lb, la);

        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Whether a foreground is readable on a background.</summary>
    public static bool IsReadable(Rgb foreground, Rgb background) =>
        Ratio(foreground, background) >= MinimumRatio;

    /// <summary>
    /// The pair named, for a message somebody can act on.
    ///
    /// "This palette is unreadable" tells the user to give up. Naming which two colours, what they
    /// scored and what they needed tells them which one to change.
    /// </summary>
    public static string Describe(string foregroundName, Rgb foreground, string backgroundName, Rgb background) =>
        $"{foregroundName} {foreground} on {backgroundName} {background} "
        + $"scores {Ratio(foreground, background):0.0}, and needs {MinimumRatio:0.0}";
}
