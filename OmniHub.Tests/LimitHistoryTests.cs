// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Which limit was binding, over time.
///
/// Three of these encode a mistake the naive version makes, and each one produces a chart that
/// looks entirely reasonable while being wrong: a gap drawn as the longest confident band on the
/// screen, an idle machine reported as power-limited because something is always nearest its
/// limit, and a share quoted against a window that was mostly not measured.
/// </summary>
public class LimitHistoryTests
{
    private static readonly DateTime Base = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static LimitSample At(int seconds, string name, double percent = 99) =>
        new(Base.AddSeconds(seconds), name, percent);

    /// <summary>
    /// A run of samples at the real five-second cadence.
    ///
    /// Spelling the interval out matters: the first version of these fixtures spaced samples
    /// thirty and eighty seconds apart, which is past the gap threshold, and the tests failed
    /// because the implementation correctly refused to join across them. The fixtures were
    /// wrong, not the code -- but a fixture that cannot occur proves nothing either way.
    /// </summary>
    private static IEnumerable<LimitSample> Run(int fromSeconds, int toSeconds, string name)
    {
        for (int t = fromSeconds; t <= toSeconds; t += 5)
            yield return At(t, name);
    }

    /// <summary>Consecutive samples of one limit collapse into one stretch.</summary>
    [Fact]
    public void ConsecutiveSamplesOfOneLimitBecomeOneSegment()
    {
        var segments = LimitHistory.Segments(new[]
        {
            At(0, "Core current (EDC)"), At(5, "Core current (EDC)"), At(10, "Core current (EDC)"),
        });

        var segment = Assert.Single(segments);
        Assert.Equal("Core current (EDC)", segment.Name);
        Assert.Equal(TimeSpan.FromSeconds(10), segment.Duration);
    }

    /// <summary>A change of limit closes one stretch and opens the next.</summary>
    [Fact]
    public void AChangeOfLimitSplitsTheSegments()
    {
        var segments = LimitHistory.Segments(new[]
        {
            At(0, "Sustained power"), At(5, "Sustained power"),
            At(10, "Temperature"), At(15, "Temperature"),
        });

        Assert.Equal(2, segments.Count);
        Assert.Equal("Sustained power", segments[0].Name);
        Assert.Equal("Temperature", segments[1].Name);
    }

    /// <summary>
    /// A gap is not a stretch, however tempting it looks.
    ///
    /// Samples arrive only while a screen that reads the SMU is open, so an overnight gap is
    /// routine. Joining across it would draw the longest, most confident-looking band on the
    /// chart out of the period nothing was measured -- and it would be labelled with whatever
    /// limit happened to be binding when somebody last closed the window.
    /// </summary>
    [Fact]
    public void AGapIsNotDrawnAsASegment()
    {
        var segments = LimitHistory.Segments(new[]
        {
            At(0, "Temperature"), At(5, "Temperature"),
            At(3600, "Temperature"), At(3605, "Temperature"),
        });

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.Equal(TimeSpan.FromSeconds(5), s.Duration));
        Assert.Equal(TimeSpan.FromSeconds(10), LimitHistory.Covered(segments));
    }

    /// <summary>
    /// An idle machine is not limited by whatever happens to be nearest.
    ///
    /// Something is always closest to its limit. Reporting the maximum regardless would have a
    /// machine at eleven per cent of its sustained power described as power-limited, which is
    /// both false and the sort of false that sends somebody off raising a limit that was never
    /// binding.
    /// </summary>
    [Fact]
    public void BelowTheBindingThresholdNothingIsNamed()
    {
        var history = new LimitHistory();

        history.Record(Base, Snapshot(stapmPercent: 11));
        history.Record(Base.AddSeconds(5), Snapshot(stapmPercent: 99));

        var samples = history.Since(Base);

        Assert.Equal(LimitHistory.Unconstrained, samples[0].Name);
        Assert.Equal("Sustained power", samples[1].Name);
    }

    /// <summary>
    /// Shares are of the time measured, not of the window asked for.
    ///
    /// Four minutes of samples in a one-hour window is a true statement about four minutes. The
    /// denominator has to be the measured time, and the sentence has to say how much that was,
    /// or the reader takes a real number for an answer about the whole hour.
    /// </summary>
    [Fact]
    public void SharesAreOfTheMeasuredTimeAndTheCoverageIsStated()
    {
        var segments = LimitHistory.Segments(
            Run(0, 30, "Temperature").Concat(Run(35, 45, "Core current (EDC)")).ToList());

        var shares = LimitHistory.Shares(segments);

        Assert.Equal("Temperature", shares[0].Name);
        Assert.Equal(75.0, shares[0].Fraction, 1);   // 30 s of 40 s measured
        Assert.Equal(25.0, shares[1].Fraction, 1);   // 10 s of 40 s measured

        string text = LimitHistory.Describe(segments, TimeSpan.FromHours(1));
        Assert.Contains("out of the last 1 h", text);
        Assert.Contains("40 s measured", text);
    }

    /// <summary>The summary leads with the constraint that held the machine back most.</summary>
    [Fact]
    public void TheSummaryLeadsWithTheWorstOffender()
    {
        var segments = LimitHistory.Segments(
            Run(0, 20, LimitHistory.Unconstrained)
                .Concat(Run(25, 105, "Core current (EDC)")).ToList());

        string text = LimitHistory.Describe(segments, TimeSpan.FromMinutes(10));

        Assert.Contains("Core current (EDC) was the binding constraint", text);
        Assert.Contains("Nothing was binding for the other", text);
    }

    /// <summary>An unconstrained machine is described as such rather than left blank.</summary>
    [Fact]
    public void AnUnconstrainedMachineIsSaidPlainly()
    {
        var segments = LimitHistory.Segments(Run(0, 60, LimitHistory.Unconstrained).ToList());

        Assert.Contains("Nothing was holding the processor back",
                        LimitHistory.Describe(segments, TimeSpan.FromMinutes(10)));
    }

    /// <summary>Nothing recorded says so, rather than reporting an unconstrained machine.</summary>
    [Fact]
    public void NoHistoryIsNotAnUnconstrainedMachine() =>
        Assert.Contains("Nothing has been recorded yet",
                        LimitHistory.Describe(Array.Empty<LimitSegment>(), TimeSpan.FromHours(1)));

    /// <summary>A single sample has no duration, so it yields no segment.</summary>
    [Fact]
    public void OneSampleIsAnInstantNotAStretch() =>
        Assert.Empty(LimitHistory.Segments(new[] { At(0, "Temperature") }));

    /// <summary>
    /// The ring keeps the newest entries and drops the oldest.
    ///
    /// Asserted because the wrap-around index is the kind of arithmetic that works for the first
    /// pass and silently reorders the history on the second.
    /// </summary>
    [Fact]
    public void TheRingKeepsTheNewestSamplesInOrder()
    {
        var history = new LimitHistory();

        for (int i = 0; i < LimitHistory.Capacity + 100; i++)
            history.Record(Base.AddSeconds(i * 5), Snapshot(stapmPercent: 99));

        var samples = history.Since(DateTime.MinValue);

        Assert.Equal(LimitHistory.Capacity, samples.Count);

        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i].AtUtc > samples[i - 1].AtUtc, "the ring came back out of order");

        // The first 100 were overwritten, so the oldest kept sample is the 100th recorded.
        Assert.Equal(Base.AddSeconds(100 * 5), samples[0].AtUtc);
    }

    /// <summary>
    /// A snapshot whose tightest constraint is sustained power at a chosen percentage.
    ///
    /// Built with every other limit well clear, so the test controls which one binds.
    /// </summary>
    private static PowerSnapshot Snapshot(double stapmPercent) => new(
        StapmLimitWatts: 100, StapmWatts: stapmPercent,
        FastLimitWatts: 100, FastWatts: 1,
        SlowLimitWatts: 100, SlowWatts: 1,
        ApuSlowLimitWatts: 100, ApuSlowWatts: 1,
        TdcVddLimitAmps: 100, TdcVddAmps: 1,
        TdcSocLimitAmps: 100, TdcSocAmps: 1,
        EdcVddLimitAmps: 100, EdcVddAmps: 1,
        EdcSocLimitAmps: 100, EdcSocAmps: 1,
        ThermalLimitC: 100, CoreTempC: 1,
        SocThermalLimitC: 100, SocTempC: 1,
        GfxThermalLimitC: 100, GfxTempC: 1);
}
