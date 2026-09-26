// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Optimize;

/// <summary>How much room the interface gives each thing on it.</summary>
public enum UiDensity
{
    /// <summary>More on screen, at the cost of breathing room.</summary>
    Compact,

    /// <summary>The palette's own figures, unchanged.</summary>
    Normal,

    /// <summary>Fewer things, larger.</summary>
    Roomy,
}

/// <summary>
/// Density as a multiplier over whatever the palette already says.
///
/// A multiplier rather than three sets of numbers, because the eight palettes already disagree
/// about padding and figure size on purpose -- one of them is a terminal and another is a
/// sandstone-coloured thing with twenty pixels of padding. Replacing those with three fixed
/// densities would flatten a deliberate difference; scaling them keeps it and moves it.
///
/// So density and theme compose: Compact on Sandstone is still roomier than Normal on Terminal,
/// which is what somebody picking both of them meant.
/// </summary>
public static class Density
{
    /// <summary>
    /// A fifth smaller and a quarter larger.
    ///
    /// Deliberately mild. These numbers multiply a padding, a bar height and a figure size at
    /// once, and a range wide enough to be dramatic on the figure makes the padding collapse or
    /// the bars fat before it gets there.
    /// </summary>
    public static double Scale(UiDensity density) => density switch
    {
        UiDensity.Compact => 0.8,
        UiDensity.Roomy => 1.25,
        _ => 1.0,
    };

    /// <summary>Smallest figure size worth rendering, in device-independent pixels.</summary>
    public const double MinimumFontSize = 11;

    /// <summary>Smallest bar height that still reads as a bar rather than as a rule.</summary>
    public const double MinimumTrackHeight = 2;

    /// <summary>
    /// Scales a figure size, never below the point where it stops being readable.
    ///
    /// The floor matters more than it looks: a palette is free to choose a small base size, and
    /// the compact multiplier applies on top of that rather than instead of it, so the two
    /// together can land somewhere no one chose.
    /// </summary>
    public static double FontSize(double baseSize, UiDensity density) =>
        Math.Max(MinimumFontSize, baseSize * Scale(density));

    /// <summary>Shortest table row that still holds a line of 12px text without clipping it.</summary>
    public const double MinimumRowHeight = 18;

    /// <summary>Scales a table row, never below the height its text needs.</summary>
    public static double RowHeight(double baseHeight, UiDensity density) =>
        Math.Max(MinimumRowHeight, baseHeight * Scale(density));

    /// <summary>Scales a track height, never below where it stops being visible.</summary>
    public static double TrackHeight(double baseHeight, UiDensity density) =>
        baseHeight <= 0 ? baseHeight : Math.Max(MinimumTrackHeight, baseHeight * Scale(density));

    /// <summary>
    /// Scales a padding.
    ///
    /// No floor: zero padding is a legitimate choice a palette can make, and unlike a font size
    /// there is nothing unreadable about a tight card.
    /// </summary>
    public static double Padding(double basePadding, UiDensity density) =>
        Math.Max(0, basePadding * Scale(density));
}
