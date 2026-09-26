// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Workspaces;

namespace OmniHub.Tests;

/// <summary>
/// The line under each sidebar item. Two promises: it states only what the shell established, and
/// every screen the application ships with has one.
/// </summary>
public class NavSummaryTests
{
    private static readonly NavFacts Everything = new()
    {
        LimitName = "Temperature",
        LimitPercent = 75.4,
        FanMode = "auto",
        CommandedPercent = 40,
        FanRpm = 2800,
        PackageWatts = 19.34,
        CpuClockGHz = 3.214,
        BatteryPercent = 100,
        OnAc = true,
        TimerResolution = true,
        DwmPriority = false,
        Theme = "Ember",
    };

    [Theory]
    [InlineData("dashboard", "temperature at 75%")]
    [InlineData("fans", "auto · 40% · 2800 rpm")]
    [InlineData("performance", "19.3 W · 3.21 GHz")]
    [InlineData("power", "100% · on AC")]
    [InlineData("system", "fine timer")]
    [InlineData("settings", "Ember")]
    public void EachScreenSaysWhatIsInForce(string panel, string expected)
    {
        Assert.Equal(expected, NavSummary.For(panel, Everything));
    }

    [Fact]
    public void AFactThatWasNotEstablishedIsLeftOutRatherThanGuessed()
    {
        // A board that did not report a fan speed, and a curve that has not commanded yet.
        var partial = new NavFacts { FanMode = "auto" };

        Assert.Equal("auto", NavSummary.For("fans", partial));
        Assert.Null(NavSummary.For("performance", partial));
        Assert.Null(NavSummary.For("dashboard", partial));
        Assert.Null(NavSummary.For("power", partial));
    }

    [Fact]
    public void NothingSwitchedOnIsStillAnAnswer()
    {
        Assert.Equal("Windows defaults", NavSummary.For("system", new NavFacts()));
        Assert.Equal("timer + DWM priority", NavSummary.For("system", new NavFacts { TimerResolution = true, DwmPriority = true }));
    }

    [Theory]
    [InlineData("Core current (EDC)", 99.2, "EDC at 99%")]
    [InlineData("Sustained power", 48, "sustained power at 48%")]
    public void ALimitKeepsItsNumberInTheSpaceThereIs(string name, double percent, string expected)
    {
        Assert.Equal(expected, NavSummary.For("dashboard", new NavFacts { LimitName = name, LimitPercent = percent }));
    }

    [Fact]
    public void EveryLineFitsTheSidebar()
    {
        // The text column is 149 px: a 211 px item, less 36 px of content margin and 26 px of icon.
        // Cascadia Mono advances 0.586 em, 6.15 px at 10.5 px, so twenty-four characters fit and
        // twenty-three leaves room. Past that the line is cut from the end, where the figure sits.
        var longest = new NavFacts
        {
            LimitName = "Sustained power", LimitPercent = 100, FanMode = "auto", CommandedPercent = 100,
            FanRpm = 5600, PackageWatts = 54.3, CpuClockGHz = 4.95, BatteryPercent = 100, OnAc = false,
            TimerResolution = true, DwmPriority = true, Theme = "OLED Black",
        };

        foreach (var panel in new[] { "dashboard", "fans", "performance", "power", "system", "settings" })
        {
            string line = NavSummary.For(panel, longest)!;
            Assert.True(line.Length <= 23, $"{panel}: \"{line}\" is {line.Length} characters");
        }
    }

    [Fact]
    public void APanelWithNothingToSayGetsNoLine()
    {
        Assert.Null(NavSummary.For("diagnostics", Everything));
        Assert.Null(NavSummary.For("metric.cpu", Everything));
    }

    [Fact]
    public void EveryScreenTheApplicationShipsWithHasALineExceptDiagnostics()
    {
        // Walks the real default layout, so a screen renamed or added there cannot silently lose
        // its line here -- the keys are strings, and strings that must match drift.
        var missing = WorkspaceLayout.Defaults().Workspaces
            .Select(w => w.Panels[0].Type)
            .Where(type => type != "diagnostics" && NavSummary.For(type, Everything) is null)
            .ToList();

        Assert.True(missing.Count == 0, $"no sidebar line for: {string.Join(", ", missing)}");
    }
}
