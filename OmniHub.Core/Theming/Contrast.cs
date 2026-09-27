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
    /// How far apart two colours that name different components must be.
    ///
    /// A colour per component is only a code if the colours can be told apart. OLED Black gave the
    /// processor #22D3EE and the graphics card #2DD4BF, 27 apart by <see cref="Difference"/>: on screen,
    /// one cyan, and the live chart's two lines could not be told apart. CIE76 flatters the cyans, so
    /// the floor sits above that pair rather than at the textbook "clearly different".
    /// </summary>
    public const double MinimumComponentDifference = 30;

    /// <summary>
    /// The perceptual distance between two colours: CIE76, the straight-line distance in CIE L*a*b*
    /// under D65. Unlike <see cref="Ratio"/>, which only compares brightness, this separates two
    /// colours of the same brightness and different hue -- which is what telling components apart is.
    /// </summary>
    public static double Difference(Rgb a, Rgb b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    /// <summary>
    /// How light a card must be, and how far above its ground, to be seen as a card. Measured off the
    /// reference (cards L* 6.8 on 3.6). Near black a display crushes both ends together, so a step
    /// alone is not enough: OLED Black stepped its cards 3.2 above a true-black ground and they read
    /// as black holes with an outline.
    /// </summary>
    public const double MinimumCardLightness = 5.5, MinimumCardStep = 2.3;

    /// <summary>CIE L*, 0 (black) to 100 (white): lightness as an eye grades it.</summary>
    public static double Lightness(Rgb c) => Lab(c).L;

    private static (double L, double A, double B) Lab(Rgb c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;

        double r = Linear(c.R), g = Linear(c.G), bl = Linear(c.B);
        double x = F((0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047);
        double y = F(0.2126 * r + 0.7152 * g + 0.0722 * bl);
        double z = F((0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883);
        return (116 * y - 16, 500 * (x - y), 200 * (y - z));
    }

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
