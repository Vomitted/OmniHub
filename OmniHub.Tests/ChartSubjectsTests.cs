// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class ChartSubjectsTests
{
    private static readonly DateTime At = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A sample with every reading present, each a value nothing else uses.</summary>
    private static ThermalSample Full() => new(
        At, TempC: 72.5, ForecastC: 74, Fan1Raw: 30, Fan2Raw: 29,
        CommandedPercent: 55, Throttling: false, Mode: "Auto", Sensor: "SmuDieTctl",
        GpuTempC: 61.5, GpuWatts: 42.25, PackageWatts: 38.75, Limit: "Temperature", LimitPercent: 96.5);

    /// <summary>A sample where nothing answered, which is an ordinary row in this log.</summary>
    private static ThermalSample Empty() => new(At, null, null, null, null, null, null, null, null);

    [Fact]
    public void EveryKeyIsUnique() =>
        Assert.Equal(ChartSubjects.All.Count, ChartSubjects.All.Select(s => s.Key).Distinct().Count());

    [Fact]
    public void EverySubjectDrawsSomething()
    {
        foreach (var subject in ChartSubjects.All) Assert.NotEmpty(subject.Lines);
    }

    [Fact]
    public void EverySubjectHasExactlyOnePrimaryLine()
    {
        // The primary decides the axis the chart is scaled and labelled by. None leaves it
        // unscaled; two means whichever was added last silently wins.
        foreach (var subject in ChartSubjects.All)
            Assert.Equal(1, subject.Lines.Count(l => l.Primary));
    }

    [Fact]
    public void AMissingReadingStaysMissingOnEveryLine()
    {
        // The rule the whole log is built on, applied at the last place it can be broken. A zero
        // here draws a flat line along the bottom, which reads as a measurement of nothing rather
        // than as nothing measured.
        var empty = Empty();

        foreach (var subject in ChartSubjects.All)
            foreach (var line in subject.Lines)
                Assert.Null(line.Select(empty));
    }

    [Theory]
    [InlineData("thermal", "die temp", 72.5)]
    [InlineData("thermal", "commanded", 55)]
    [InlineData("gpu", "gpu temp", 61.5)]
    [InlineData("gpu", "gpu power", 42.25)]
    [InlineData("power", "package", 38.75)]
    [InlineData("limit", "limit", 96.5)]
    public void EveryLineReadsTheFieldItClaimsTo(string key, string lineName, double expected)
    {
        // Every value in the fixture is distinct, so a line wired to the wrong field reads back
        // somebody else's number rather than a plausible one.
        var line = ChartSubjects.Find(key)!.Lines.Single(l => l.Name == lineName);

        Assert.Equal(expected, line.Select(Full()));
    }

    [Fact]
    public void APercentageLineIsPinnedToItsOwnScale()
    {
        // A percentage auto-scaled to its observed range makes a fan that moved between 40 and 45
        // look like it swung across the whole chart.
        foreach (var subject in ChartSubjects.All)
            foreach (var line in subject.Lines.Where(l => l.Unit == "%"))
            {
                Assert.Equal(0, line.Min);
                Assert.Equal(100, line.Max);
            }
    }

    [Fact]
    public void AnUnknownSubjectIsNullRatherThanAThrow() =>
        Assert.Null(ChartSubjects.Find("something-from-a-later-build"));
}
