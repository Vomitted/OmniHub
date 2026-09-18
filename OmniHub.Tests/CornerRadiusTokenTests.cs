// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Corners come from the palette, not from the markup.
///
/// The eight themes range from square corners to fourteen pixels, and the one shipped by default
/// is four. A hand-written radius therefore does not merely differ from the theme, it disagrees
/// with the card it is sitting inside -- a button at five beside a card at fourteen, or a rounded
/// chip in a palette whose whole point is that nothing is rounded.
///
/// Enforced the same way the contrast and control-sizing rules are, because this is the kind of
/// rule that is easy to restore and easy to break again: twenty-four elements had drifted before
/// this test existed, three of them added the same week by the panels that are supposed to be the
/// customizable part of the application.
/// </summary>
public class CornerRadiusTokenTests
{
    /// <summary>
    /// Styles whose radius is their shape rather than the theme.
    ///
    /// A scrollbar thumb is a rounded bar, a toggle switch is a capsule and a slider track is a
    /// thin rounded line. Making these follow the palette would square off a switch in the flat
    /// themes, which does not read as flat, it reads as broken. Every entry here is a deliberate
    /// exception and the list is meant to stay short.
    /// </summary>
    private static readonly string[] ShapeNotTheme =
    {
        "ScrollThumbStyle",
        "ToggleSwitchStyle",
        "OmniSliderStyle",
    };

    // Symmetric only. An asymmetric value such as "0,2,2,0" is a direction -- the nav rail, a
    // segmented control's outer end -- and not a corner the palette has an opinion about.
    private static readonly Regex Literal = new(
        @"CornerRadius=""\d+(\.\d+)?""|Property=""CornerRadius""\s+Value=""\d+(\.\d+)?""",
        RegexOptions.Compiled);

    private static readonly Regex Key = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void EveryCardAndControlCornerComesFromThePalette()
    {
        var offenders = new List<string>();

        foreach (string path in Directory.EnumerateFiles(WpfTestHost.WpfDir, "*.xaml", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(path);
            string owner = "";

            for (int i = 0; i < lines.Length; i++)
            {
                // The style a line belongs to is the nearest x:Key above it, which is how these
                // dictionaries are laid out.
                var key = Key.Match(lines[i]);
                if (key.Success) owner = key.Groups[1].Value;

                if (!Literal.IsMatch(lines[i])) continue;
                if (ShapeNotTheme.Contains(owner)) continue;

                offenders.Add($"{Path.GetFileName(path)}:{i + 1}"
                            + (owner.Length > 0 ? $" in {owner}" : "")
                            + $" -- {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These corners are written in the markup instead of coming from RadiusSm or RadiusMd:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every palette defines both radii.
    ///
    /// A theme missing one leaves the elements that use it falling back to whatever WPF defaults
    /// to, which is square -- so the failure looks like a handful of controls not being rounded
    /// rather than like a missing resource.
    /// </summary>
    [Fact]
    public void EveryPaletteDefinesBothRadii()
    {
        string palettes = Path.Combine(WpfTestHost.WpfDir, "Palettes");

        foreach (string path in Directory.EnumerateFiles(palettes, "*.xaml"))
        {
            string text = File.ReadAllText(path);

            Assert.True(text.Contains("x:Key=\"RadiusSm\""), $"{Path.GetFileName(path)} has no RadiusSm");
            Assert.True(text.Contains("x:Key=\"RadiusMd\""), $"{Path.GetFileName(path)} has no RadiusMd");
        }
    }

    /// <summary>
    /// Every palette defines the three figures density scales.
    ///
    /// Density works by reading these out of the palette, multiplying them and merging the result
    /// back over the top. A key that is missing, or named differently in one palette, means the
    /// lookup returns nothing and that dimension simply does not scale -- silently, on one theme,
    /// which is the hardest kind of thing to notice.
    ///
    /// This is a text check rather than a behavioural one because the code that does the merging
    /// lives in the application project, which this suite deliberately cannot reference. It
    /// catches the failure that is actually likely: a name that does not match.
    /// </summary>
    [Fact]
    public void EveryPaletteDefinesTheFiguresDensityScales()
    {
        string palettes = Path.Combine(WpfTestHost.WpfDir, "Palettes");

        foreach (string path in Directory.EnumerateFiles(palettes, "*.xaml"))
        {
            string text = File.ReadAllText(path);

            foreach (string key in new[] { "CardPadding", "TrackHeight", "MetricValueSize" })
                Assert.True(text.Contains($"x:Key=\"{key}\""),
                            $"{Path.GetFileName(path)} has no {key}, so density cannot scale it");
        }
    }
}
