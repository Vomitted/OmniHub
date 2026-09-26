// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Xml.Linq;
using OmniHub.Core.Theming;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// A palette built in the application, held to what the eight built by hand are held to.
/// </summary>
public class CustomPaletteTests
{
    // The shipped default, as the four colours somebody would have picked to arrive at it.
    private static CustomPalette Dark() => new(
        "Mine",
        Background: new Rgb(0x00, 0x00, 0x00),
        Panel: new Rgb(0x0E, 0x0E, 0x0E),
        Accent: new Rgb(0x4F, 0x9C, 0xF5),
        TextPrimary: new Rgb(0xF2, 0xF2, 0xF2));

    private static CustomPalette Light() => new(
        "Paper",
        Background: new Rgb(0xFA, 0xFA, 0xF7),
        Panel: new Rgb(0xEF, 0xEF, 0xEA),
        Accent: new Rgb(0x1B, 0x5E, 0xA8),
        TextPrimary: new Rgb(0x14, 0x14, 0x14));

    /// <summary>
    /// The colour keys a palette dictionary defines, read from a shipped one.
    ///
    /// Read rather than listed: the list this replaced was a copy, and it was missing AccentColor2 --
    /// so the build never produced it and the test agreed, while every gradient on a custom palette
    /// ended on a leftover colour.
    /// </summary>
    private static IEnumerable<string> Required =>
        XDocument.Load(Path.Combine(WpfTestHost.WpfDir, "Palettes", "Midnight.xaml"))
            .Descendants()
            .Where(e => e.Name.LocalName == "Color")
            .Select(e => e.Attribute(Xaml + "Key")!.Value);

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryKeyAPaletteNeedsIsProduced()
    {
        // A missing key does not fail loudly: the resource lookup falls through to whatever the
        // previous palette left behind, so one colour stays wrong until somebody notices.
        var built = Dark().Build();

        foreach (string key in Required)
            Assert.True(built.ContainsKey(key), $"no {key}");
    }

    [Fact]
    public void EveryValueIsAColourThatParsesBack()
    {
        foreach (var (key, hex) in Dark().Build())
            Assert.True(Rgb.Parse(hex) is not null, $"{key} is not a colour: {hex}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReasonablePaletteIsReadableOnEitherGround(bool dark)
    {
        var palette = dark ? Dark() : Light();

        Assert.Empty(palette.Problems());
        Assert.True(palette.IsReadable);
    }

    [Fact]
    public void TextTooCloseToItsGroundIsRefusedWithThePairNamed()
    {
        // Dark grey on black: the exact mistake somebody makes reaching for a subtle look.
        var palette = Dark() with { TextPrimary = new Rgb(0x30, 0x30, 0x30) };

        var problems = palette.Problems();

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("TextPrimaryColor"));

        // Naming the pair is the point. A message that only says the palette is unreadable tells
        // somebody to give up rather than which colour to change.
        Assert.Contains(problems, p => p.Contains("BackgroundColor") && p.Contains("4.5"));
    }

    [Fact]
    public void TheDerivedFaintTextIsCheckedTooNotJustTheChosenOne()
    {
        // The faint text is the chosen text more than half of the way to the ground, so a text
        // colour that passes on its own can still produce a derived one that does not. Checking
        // only what the user picked would let the editor approve a palette with an unreadable
        // footnote in it.
        var palette = Dark() with { TextPrimary = new Rgb(0x6A, 0x6A, 0x6A) };

        Assert.Contains(palette.Problems(), p => p.Contains("TextFaintColor"));
    }

    [Fact]
    public void AStatusColourIsMovedUntilItReadsOnTheChosenGround()
    {
        // On a light ground the canonical green is far too pale. Fixed values would be a guard
        // that only works on the dark palettes they were chosen against.
        var onLight = Rgb.Parse(Light().Build()["GoodColor"])!.Value;
        var onDark = Rgb.Parse(Dark().Build()["GoodColor"])!.Value;

        Assert.True(Contrast.IsReadable(onLight, Light().Background));
        Assert.True(Contrast.IsReadable(onDark, Dark().Background));

        // And it genuinely moved rather than being left alone.
        Assert.NotEqual(onLight, onDark);
    }

    [Fact]
    public void ContentOnTheAccentIsWhicheverSideReadsBetter()
    {
        // Not assumed to be white. Every shipped palette answers near-black here because every
        // accent in them is bright, and two controls were painting themselves white on it.
        var onBright = CustomPalette.OnAccent(new Rgb(0x4F, 0x9C, 0xF5));
        var onDarkAccent = CustomPalette.OnAccent(new Rgb(0x1B, 0x2E, 0x4A));

        Assert.True(Contrast.RelativeLuminance(onBright) < 0.2, "bright accent should take dark content");
        Assert.True(Contrast.RelativeLuminance(onDarkAccent) > 0.5, "dark accent should take light content");
    }

    [Fact]
    public void MixingIsBoundedAtBothEnds()
    {
        var a = new Rgb(0, 0, 0);
        var b = new Rgb(255, 255, 255);

        Assert.Equal(a, CustomPalette.Mix(a, b, 0));
        Assert.Equal(b, CustomPalette.Mix(a, b, 1));
        Assert.Equal(a, CustomPalette.Mix(a, b, -5));     // clamped, not extrapolated
        Assert.Equal(b, CustomPalette.Mix(a, b, 5));
    }

    [Fact]
    public void AColourThatCannotBeMadeReadableComesBackAnyway()
    {
        // Mid grey on mid grey has nowhere to go that helps within the step budget. Returning the
        // best effort and letting Problems report it beats returning something that looks fine.
        var ground = new Rgb(0x80, 0x80, 0x80);
        var result = CustomPalette.Readable(new Rgb(0x82, 0x82, 0x82), ground);

        // Whatever comes back is a colour; whether it passes is the caller's to report.
        Assert.InRange(Contrast.Ratio(result, ground), 1.0, 21.0);
    }

    [Fact]
    public void TheSamePaletteBuildsTheSameWayTwice()
    {
        // It is written to a file and re-read; a derivation that drifted between two calls would
        // make the saved palette differ from the one that was previewed.
        Assert.Equal(Dark().Build(), Dark().Build());
    }
}
