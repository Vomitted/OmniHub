// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Apps;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The per-application GPU preference list (TECHNICAL-v5-ui.md, section 14): which registry values are
/// routes, how the page's search and preference filter narrow them, and that changing a preference
/// keeps whatever else Windows stored on the same value.
/// </summary>
public class GpuAppRoutingTests
{
    private static readonly AppGpuRoute[] Routes =
    {
        new(@"C:\Games\Alpha\game.exe", AppGpuPreference.HighPerformance),
        new(@"C:\Games\Beta\game.exe", AppGpuPreference.PowerSaving),
        new(@"C:\Tools\editor.exe", AppGpuPreference.PowerSaving),
        new(@"Microsoft.Example_8wekyb3d8bbwe!App", AppGpuPreference.Auto),
    };

    [Fact]
    public void WindowsGlobalDirectXOptionsAreNotAnApplication()
    {
        Assert.False(GpuAppRouting.IsRoute("DirectXUserGlobalSettings"));
        Assert.False(GpuAppRouting.IsRoute(" "));
        Assert.True(GpuAppRouting.IsRoute(@"C:\Games\Alpha\game.exe"));
        Assert.True(GpuAppRouting.IsRoute("Microsoft.Example_8wekyb3d8bbwe!App"));   // a Store app, named by its ID
    }

    [Fact]
    public void TheFilterNarrowsByPreferenceAndByAnyPartOfThePath()
    {
        Assert.Equal(4, GpuAppRouting.Filter(Routes, "", null).Count);
        Assert.Equal(2, GpuAppRouting.Filter(Routes, "", AppGpuPreference.PowerSaving).Count);
        Assert.Single(GpuAppRouting.Filter(Routes, "", AppGpuPreference.HighPerformance));

        // The folder tells two builds of one game apart, so the search looks at the whole path.
        Assert.Equal(2, GpuAppRouting.Filter(Routes, "game.exe", null).Count);
        Assert.Single(GpuAppRouting.Filter(Routes, "beta", null));
        Assert.Single(GpuAppRouting.Filter(Routes, "  GAMES\\ALPHA ", null));

        // Both at once.
        Assert.Single(GpuAppRouting.Filter(Routes, "game", AppGpuPreference.PowerSaving));
        Assert.Empty(GpuAppRouting.Filter(Routes, "editor", AppGpuPreference.HighPerformance));
    }

    [Fact]
    public void ChangingThePreferenceKeepsEverythingElseWindowsStored()
    {
        Assert.Equal("GpuPreference=2;", GpuAppRouting.WithPreference("", AppGpuPreference.HighPerformance));
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=1;",
                     GpuAppRouting.WithPreference("SwapEffectUpgradeEnable=1;GpuPreference=2;", AppGpuPreference.PowerSaving));
        Assert.Equal("GpuPreference=0;SwapEffectUpgradeEnable=1;",
                     GpuAppRouting.WithPreference("GpuPreference=2;SwapEffectUpgradeEnable=1;", AppGpuPreference.Auto));
    }

    [Fact]
    public void APreferenceIsReadFromWhereverItSitsInTheValue()
    {
        Assert.Equal(AppGpuPreference.HighPerformance, GpuAppRouting.ParsePreference("GpuPreference=2;"));
        Assert.Equal(AppGpuPreference.PowerSaving, GpuAppRouting.ParsePreference("SwapEffectUpgradeEnable=1;GpuPreference=1;"));
        Assert.Equal(AppGpuPreference.Auto, GpuAppRouting.ParsePreference("SwapEffectUpgradeEnable=1;"));
        Assert.Equal(AppGpuPreference.Auto, GpuAppRouting.ParsePreference("GpuPreference=7;"));   // not a preference Windows defines
    }
}
