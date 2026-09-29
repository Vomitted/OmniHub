// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.RegularExpressions;
using OmniHub.Core.Theming;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The Frosted and Dot matrix looks set their own materials over whatever palette is chosen, and the
/// blur makes every look's surfaces translucent over something the application does not control. Both
/// are ways for text to stop being readable without a single line of the palettes changing, so both
/// are held here: every colour drawn as text, on every surface a look or the blur can put under it --
/// read from <see cref="LookSurfaces.Overrides"/>, the one function the application applies.
/// </summary>
public class LookSurfaceTests
{
    public static IEnumerable<object[]> Palettes() => ContrastTests.Palettes();

    private static string PaletteColour(string xaml, string key)
    {
        var m = Regex.Match(xaml, $@"x:Key=""{key}"">\s*(#[0-9A-Fa-f]{{6,8}})\s*<");
        Assert.True(m.Success, $"the palette declares no {key}");
        return m.Groups[1].Value;
    }

    /// <summary>Every text colour against every surface text lands on, under one look and blur setting.</summary>
    /// <summary>No blur, then the strength slider's whole range: both ends and three points between.</summary>
    private static readonly double?[] Strengths = { null, 0, 0.25, 0.5, 0.75, 1 };

    private static string Blurred(double? blur) => blur is { } b ? $" with the blur at {b:0.00}" : "";

    private static IEnumerable<string> Failures(string xaml, LookSurfaces.Material? look, double? blur)
    {
        var o = LookSurfaces.Overrides(look, blur, key => PaletteColour(xaml, key));
        string Get(string key) => o.TryGetValue(key, out var v) ? v : PaletteColour(xaml, key);
        string setting = $"{look switch { null => "no look", _ when look == LookSurfaces.Frosted => "Frosted", _ => "Dot matrix" }}{Blurred(blur)}";

        var surfaces = LookSurfaces.Surfaces(look, blur, Get);

        foreach (string textKey in ContrastTests.TextColors)
        {
            var text = Rgb.Parse(Get(textKey))!.Value;
            foreach (var (name, surface) in surfaces)
                if (!Contrast.IsReadable(text, surface))
                    yield return $"{setting}: {textKey} on {name} {surface} scores {Contrast.Ratio(text, surface):0.00}";
        }

        // Text on the accent fill, across both stops of its gradient.
        var onAccent = Rgb.Parse(Get("OnAccentColor"))!.Value;
        foreach (string stop in new[] { "AccentColor", "AccentColor2" })
        {
            var fill = Rgb.Parse(Get(stop))!.Value;
            if (!Contrast.IsReadable(onAccent, fill))
                yield return $"{setting}: OnAccentColor on {stop} scores {Contrast.Ratio(onAccent, fill):0.00}";
        }
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryTextColourStaysReadableUnderEveryLookAndTheBlur(string paletteFile)
    {
        string xaml = File.ReadAllText(Path.Combine(WpfTestHost.WpfDir, "Palettes", paletteFile));
        var failures = new List<string>();

        foreach (var look in new LookSurfaces.Material?[] { null, LookSurfaces.Frosted, LookSurfaces.DotMatrix })
            foreach (double? blur in Strengths)
                failures.AddRange(Failures(xaml, look, blur));

        Assert.True(failures.Count == 0, $"{paletteFile}:\n  " + string.Join("\n  ", failures));
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void FloatingWindowsGetASolidCard(string paletteFile)
    {
        // The tray flyout and the overlay float over the desktop, so they take a look's glass made
        // solid over the look's ground rather than the glass itself.
        string xaml = File.ReadAllText(Path.Combine(WpfTestHost.WpfDir, "Palettes", paletteFile));
        foreach (var look in LookSurfaces.All)
        {
            var o = LookSurfaces.Overrides(look, blur: null, key => PaletteColour(xaml, key));
            var card = LookSurfaces.Over(look.Panel, Rgb.Parse(look.Ground)!.Value);
            foreach (string textKey in ContrastTests.TextColors)
            {
                var text = Rgb.Parse(o.GetValueOrDefault(textKey) ?? PaletteColour(xaml, textKey))!.Value;
                Assert.True(Contrast.IsReadable(text, card), $"{paletteFile}: {textKey} on a floating card {card} scores {Contrast.Ratio(text, card):0.00}");
            }
        }
    }

    [Fact]
    public void TheBlurOnlyEverMakesTheSurfacesTranslucent()
    {
        // Text keys are never touched by the blur, and no key the blur sets is left opaque.
        var withLook = LookSurfaces.Overrides(LookSurfaces.Frosted, blur: null, _ => "#FF000000");
        var blurred = LookSurfaces.Overrides(LookSurfaces.Frosted, blur: LookSurfaces.DefaultBlurStrength, _ => "#FF000000");
        foreach (string key in withLook.Keys.Where(k => k.StartsWith("Text")))
            Assert.Equal(withLook[key], blurred[key]);
        foreach (string key in LookSurfaces.SurfaceKeys)
            Assert.False(blurred[key].StartsWith("#FF"), $"{key} is still opaque under the blur");

        // With no look and no blur, nothing is set: the palette is untouched.
        Assert.Empty(LookSurfaces.Overrides(null, blur: null, _ => "#FF000000"));
    }

    [Theory]
    [MemberData(nameof(Palettes))]
    public void DotMatrixsRedIsVisibleOnEveryTileItIsDrawnOn(string paletteFile)
    {
        // A graphic, never text: held to the 3 : 1 a graphic needs, on the tiles its dots sit on.
        string xaml = File.ReadAllText(Path.Combine(WpfTestHost.WpfDir, "Palettes", paletteFile));
        var red = Rgb.Parse(LookSurfaces.Signal)!.Value;
        foreach (double? blur in Strengths)
        {
            var o = LookSurfaces.Overrides(LookSurfaces.DotMatrix, blur, key => PaletteColour(xaml, key));
            string Get(string key) => o.TryGetValue(key, out var v) ? v : PaletteColour(xaml, key);
            foreach (var (name, tile) in LookSurfaces.Surfaces(LookSurfaces.DotMatrix, blur, Get).Where(s => s.Name is "PanelColor" or "PanelAltColor"))
                Assert.True(Contrast.Ratio(red, tile) >= 3, $"{paletteFile}: the red on {name} {tile}{Blurred(blur)} scores {Contrast.Ratio(red, tile):0.00}");
        }
    }

    [Fact]
    public void CompositingIsTheOrdinaryMix()
    {
        var black = new Rgb(0, 0, 0);
        Assert.Equal(new Rgb(0xFF, 0xFF, 0xFF), LookSurfaces.Over("#FFFFFFFF", black));
        Assert.Equal(new Rgb(0x80, 0x80, 0x80), LookSurfaces.Over("#80FFFFFF", black));
        Assert.Equal("#CC1A1A1A", LookSurfaces.WithOpacity("#FF1A1A1A", 0.8));
    }

    [Fact]
    public void TheDotFontDrawsEveryFigureAReadingNeeds()
    {
        foreach (char c in "0123456789.,°%-W ")
        {
            var g = DotFont.Glyph(c);
            Assert.NotNull(g);
            Assert.Equal(DotFont.Rows, g!.Count);
            Assert.All(g, row => Assert.Equal(g[0].Length, row.Length));   // a glyph is a rectangle
        }

        // "8" is the digit with every stroke: 17 dots in this font.
        Assert.Equal(17, DotFont.Cells("8").Count());
        Assert.Equal(5 + 1 + 5, DotFont.Columns("64"));      // two glyphs and the gap between
        Assert.Equal(DotFont.Columns("64"), DotFont.Columns("6x4"));   // an unknown mark is skipped, not drawn
    }
}
