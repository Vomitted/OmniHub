// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.RegularExpressions;
using OmniHub.Core.Theming;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Two rules the finished Studio look rests on, both of which failed silently before: a card has to be
/// visible against its ground in every palette, and motion has to stay quick however much of the
/// interface moves.
/// </summary>
public class SurfaceAndMotionTests
{
    public static IEnumerable<object[]> Palettes() => ContrastTests.Palettes();

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryPaletteDrawsItsCardsWhereAnEyeCanSeeThem(string paletteFile)
    {
        string xaml = File.ReadAllText(Path.Combine(WpfTestHost.WpfDir, "Palettes", paletteFile));
        Rgb Colour(string key) =>
            Rgb.Parse(Regex.Match(xaml, $@"x:Key=""{key}"">\s*(#[0-9A-Fa-f]{{6,8}})\s*<").Groups[1].Value)
            ?? throw new Xunit.Sdk.XunitException($"{paletteFile} does not define {key}");

        double ground = Contrast.Lightness(Colour("BackgroundColor"));
        double card = Contrast.Lightness(Colour("PanelColor"));

        Assert.True(card >= Contrast.MinimumCardLightness,
            $"{paletteFile}: cards at L* {card:0.0}, below {Contrast.MinimumCardLightness} -- black on black");
        Assert.True(card - ground >= Contrast.MinimumCardStep,
            $"{paletteFile}: cards {card - ground:0.0} above the ground, needs {Contrast.MinimumCardStep}");
    }

    [Fact]
    public void LightnessRunsFromBlackToWhite()
    {
        Assert.Equal(0, Contrast.Lightness(new Rgb(0, 0, 0)), 6);
        Assert.Equal(100, Contrast.Lightness(new Rgb(255, 255, 255)), 1);
        Assert.InRange(Contrast.Lightness(Rgb.Parse("#151515")!.Value), 6.5, 7.1);   // the reference's card
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(12)]
    [InlineData(40)]
    public void AnEntranceFinishesInsideItsBudgetHoweverManyEnter(int count)
    {
        int last = MotionBudget.Delay(count - 1, count) + MotionBudget.EnterMs;
        Assert.True(last <= MotionBudget.EntranceMs, $"{count} elements take {last} ms");

        for (int i = 1; i < count; i++)
            Assert.True(MotionBudget.Delay(i, count) >= MotionBudget.Delay(i - 1, count), "entrances run in order");
    }

    [Fact]
    public void AFewElementsKeepTheirFullStep()
    {
        Assert.Equal(0, MotionBudget.Delay(0, 5));
        Assert.Equal(MotionBudget.StepMs * 3, MotionBudget.Delay(3, 5));
    }
}
