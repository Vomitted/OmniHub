// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Theming;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The contrast arithmetic itself, now that the palette editor depends on it as well as the
/// palette audit does.
/// </summary>
public class ContrastMathsTests
{
    private static readonly Rgb White = new(255, 255, 255);
    private static readonly Rgb Black = new(0, 0, 0);

    [Fact]
    public void TheTwoAnchorsTheStandardFixesExactly()
    {
        // A mistake in the formula would otherwise pass every palette at once, quietly.
        Assert.Equal(21.0, Contrast.Ratio(White, Black), 2);
        Assert.Equal(1.0, Contrast.Ratio(White, White), 6);
    }

    [Fact]
    public void TheRatioDoesNotCareWhichWayRound() =>
        Assert.Equal(Contrast.Ratio(White, Black), Contrast.Ratio(Black, White), 9);

    [Fact]
    public void GammaCorrectionIsApplied()
    {
        // Mid grey is 0.216 luminance under the standard curve and would be 0.5 under a plain
        // average. A naive implementation passes pairs an eye plainly cannot read.
        Assert.Equal(0.2159, Contrast.RelativeLuminance(new Rgb(128, 128, 128)), 3);
    }

    [Fact]
    public void ChannelsAreWeightedTheWayTheEyeSeesThem()
    {
        // Green reads far brighter than blue at the same value. A palette checker that weighted
        // them equally would rate a blue-on-black label as readable.
        Assert.True(Contrast.RelativeLuminance(new Rgb(0, 255, 0))
                  > Contrast.RelativeLuminance(new Rgb(0, 0, 255)));
    }

    [Theory]
    [InlineData("#FFFFFF", 255, 255, 255)]
    [InlineData("FFFFFF", 255, 255, 255)]
    [InlineData("#FF0A0D12", 10, 13, 18)]     // the palettes write AARRGGBB
    [InlineData("#0a0d12", 10, 13, 18)]
    public void BothHexFormsTheProjectUsesParse(string hex, byte r, byte g, byte b) =>
        Assert.Equal(new Rgb(r, g, b), Rgb.Parse(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#FFF")]
    [InlineData("not a colour")]
    [InlineData("#GGGGGG")]
    public void AnythingElseIsNullRatherThanAThrow(string? hex) => Assert.Null(Rgb.Parse(hex));

    [Fact]
    public void ReadableUsesTheProjectsOwnThreshold()
    {
        Assert.True(Contrast.IsReadable(White, Black));
        Assert.False(Contrast.IsReadable(new Rgb(0x30, 0x30, 0x30), Black));
    }

    [Fact]
    public void TheFailureMessageNamesBothColoursAndBothNumbers()
    {
        string message = Contrast.Describe("TextPrimary", new Rgb(0x30, 0x30, 0x30), "Background", Black);

        // "This palette is unreadable" tells somebody to give up; this tells them what to change.
        Assert.Contains("TextPrimary", message);
        Assert.Contains("Background", message);
        Assert.Contains("#303030", message);
        Assert.Contains("4.5", message);
    }
}
