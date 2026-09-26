// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// The fan loop's recent decisions, as the Fans page reads them back: in order, bounded, and with a
/// note only where a tick was not simply the curve.
/// </summary>
public class FanTickLogTests
{
    private static FanTick Tick(double measured, byte level, double? filtered = null, string? error = null,
                                bool ceiling = false, bool commanded = true, double lead = 0, double? effective = null) =>
        new(measured, effective ?? filtered ?? measured, TemperatureSource.SmuDieTctl, ceiling, commanded, level, lead, error, filtered);

    [Fact]
    public void TheNewestTickComesFirst()
    {
        var log = new FanTickLog(capacity: 3);
        var start = new DateTime(2026, 9, 25, 16, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 5; i++) log.Add(start.AddSeconds(2 * i), Tick(60 + i, (byte)(10 * i)));

        var newest = log.Newest();

        // Five in, three kept: the two oldest are gone, and the order runs back from the latest.
        Assert.Equal(new byte[] { 40, 30, 20 }, newest.Select(e => e.Tick.CommandedPercent));
        Assert.Equal(start.AddSeconds(8), newest[0].AtUtc);
    }

    [Fact]
    public void AnEmptyLogIsAnEmptyList()
    {
        Assert.Empty(new FanTickLog().Newest());
    }

    [Theory]
    [InlineData(72.0, null, "")]                              // on the curve: nothing to say
    [InlineData(81.1, 74.3, "spike set aside")]
    [InlineData(66.0, 71.5, "fall not yet trusted")]
    public void TheNoteSaysOnlyWhatWasNotTheCurve(double measured, double? filtered, string expected)
    {
        Assert.Equal(expected, Tick(measured, 40, filtered).Note());
    }

    [Fact]
    public void FailuresTheCeilingAndAForecastAreNamed()
    {
        Assert.Equal("failed: WMI timeout", Tick(70, 40, error: "WMI timeout").Note());
        Assert.Equal("sensor on its ceiling, forced to full", Tick(85, 100, ceiling: true).Note());
        Assert.Equal("nothing commanded yet", Tick(70, 0, commanded: false).Note());
        Assert.Equal("forecast +2.1 C", Tick(70, 45, filtered: 70, lead: 10, effective: 72.1).Note());
    }

    [Fact]
    public async Task TheRealLoopRecordsEveryTickAndNamesTheSpike()
    {
        // Six steady readings, one spike, then steady again: the spike must be in the log, set
        // aside, and every tick the loop made must be there.
        double[] temps = { 70, 70, 70, 70, 70, 70, 84, 70, 70 };
        int next = 0;
        TemperatureReading Read() =>
            new(temps[Math.Min(Interlocked.Increment(ref next) - 1, temps.Length - 1)], TemperatureSource.SmuDieTctl);

        int ticks = 0;
        using var service = new FanService(new ScriptedFanBackend(), Read, FanCurve.CreateDefault(), TimeSpan.FromMilliseconds(5));
        service.OnTick += (_, _) => Interlocked.Increment(ref ticks);

        service.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref ticks) < temps.Length && DateTime.UtcNow < deadline) await Task.Delay(5);
        service.Stop();

        var recorded = service.Recent.Newest().Reverse().Take(temps.Length).ToList();

        Assert.Equal(temps, recorded.Select(e => e.Tick.MeasuredC));
        Assert.Equal("spike set aside", recorded[6].Tick.Note());
        Assert.All(recorded.Take(6), e => Assert.Equal("", e.Tick.Note()));
    }
}
