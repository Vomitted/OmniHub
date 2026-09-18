// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Optimize;
using Xunit;

namespace OmniHub.Tests;

public class DensityTests
{
    [Fact]
    public void NormalChangesNothing()
    {
        // The whole composition argument rests on this: Normal has to be the palette untouched,
        // or every theme quietly becomes a fourth thing.
        Assert.Equal(1.0, Density.Scale(UiDensity.Normal));
        Assert.Equal(20, Density.Padding(20, UiDensity.Normal));
        Assert.Equal(34, Density.FontSize(34, UiDensity.Normal));
        Assert.Equal(8, Density.TrackHeight(8, UiDensity.Normal));
    }

    [Fact]
    public void CompactIsSmallerAndRoomyIsLargerOnEveryDimension()
    {
        foreach (double value in new double[] { 4, 12, 20, 34 })
        {
            Assert.True(Density.Padding(value, UiDensity.Compact) < value);
            Assert.True(Density.Padding(value, UiDensity.Roomy) > value);
        }
    }

    [Theory]
    [InlineData(28, 22.4)]
    [InlineData(34, 27.2)]
    public void AFigureSizeScalesWhileItCan(double baseSize, double expected)
    {
        Assert.Equal(expected, Density.FontSize(baseSize, UiDensity.Compact), 3);
    }

    [Fact]
    public void AFigureNeverShrinksBelowReadable()
    {
        // A palette is free to pick a small base size, and Compact multiplies that rather than
        // replacing it -- so the two together can land somewhere nobody chose.
        Assert.Equal(Density.MinimumFontSize, Density.FontSize(12, UiDensity.Compact));
        Assert.Equal(Density.MinimumFontSize, Density.FontSize(1, UiDensity.Compact));
    }

    [Fact]
    public void ABarNeverShrinksBelowVisible()
    {
        Assert.Equal(Density.MinimumTrackHeight, Density.TrackHeight(2, UiDensity.Compact));
    }

    [Fact]
    public void APaletteThatAsksForNoBarStillGetsNoBar()
    {
        // The floor is there to stop a bar becoming invisible by accident, not to overrule a
        // palette that deliberately has none.
        foreach (var density in Enum.GetValues<UiDensity>())
            Assert.Equal(0, Density.TrackHeight(0, density));
    }

    [Fact]
    public void PaddingCanReachZeroButNotGoBelow()
    {
        // Unlike a figure, a tight card is not unreadable, so padding has no floor -- but a
        // negative padding is a layout that overlaps itself.
        Assert.Equal(0, Density.Padding(0, UiDensity.Compact));
        Assert.True(Density.Padding(1, UiDensity.Compact) >= 0);
    }

    [Fact]
    public void DensityAndThemeCompose()
    {
        // Compact on the roomiest palette is still roomier than Normal on the tightest. Picking
        // both is picking both, not having the second overrule the first.
        const double sandstone = 20, terminal = 8;

        Assert.True(Density.Padding(sandstone, UiDensity.Compact) > Density.Padding(terminal, UiDensity.Normal));
    }
}
