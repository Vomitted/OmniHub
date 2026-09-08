using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// The arithmetic behind run comparison.
///
/// This is the harness that exists because 1.1.1's regression could not be measured, so its own
/// numbers had better be right. The cases that matter are the ones where a reading is missing: a
/// run on a machine with no SMU has no wattage at all, and a summary that quietly reported 0 W
/// there would be exactly the fabricated telemetry this project refuses to show.
/// </summary>
public class LoadTestStatsTests
{
    private static LoadSample At(int seconds, double? temp = null, double? watts = null,
                                 double? ghz = null, bool throttling = false) =>
        new(TimeSpan.FromSeconds(seconds), temp, watts, ghz, null, null, throttling, "SmuDieTctl");

    [Fact]
    public void PercentileIsNearestRankSoItOnlyReportsMeasuredValues()
    {
        var values = new double[] { 10, 20, 30, 40 };

        // Not 25 -- no sample ever read 25. An interpolated percentile invents a measurement.
        Assert.Equal(20, RunStats.Percentile(values, 0.5));
        Assert.Equal(40, RunStats.Percentile(values, 0.9));
        Assert.Equal(10, RunStats.Percentile(values, 0));
        Assert.Equal(40, RunStats.Percentile(values, 1));
    }

    [Fact]
    public void PercentileHandlesEmptyAndSingle()
    {
        Assert.Null(RunStats.Percentile(Array.Empty<double>(), 0.5));
        Assert.Equal(7, RunStats.Percentile(new double[] { 7 }, 0.5));
        Assert.Equal(7, RunStats.Percentile(new double[] { 7 }, 1));
    }

    [Fact]
    public void PercentileDoesNotRequireSortedInput()
    {
        Assert.Equal(30, RunStats.Percentile(new double[] { 30, 10, 40, 20 }, 0.75));
    }

    [Fact]
    public void MissingReadingsAreAbsentFromTheSummaryRatherThanCountedAsZero()
    {
        // A machine with no SMU: temperature reads, wattage does not.
        var samples = new[] { At(2, temp: 70), At(4, temp: 80), At(6, temp: 90) };

        var s = RunStats.Summarise(samples);

        Assert.Equal(80, s.MedianTempC);
        Assert.Null(s.MedianWatts);      // not 0
        Assert.Null(s.MedianClockGhz);
        Assert.Null(s.MinClockGhz);
    }

    [Fact]
    public void TimeToThrottleIsFirstOccurrenceAndNullWhenNeverSeen()
    {
        Assert.Equal(TimeSpan.FromSeconds(6), RunStats.TimeToThrottle(new[]
        {
            At(2), At(4), At(6, throttling: true), At(8, throttling: true),
        }));

        Assert.Null(RunStats.TimeToThrottle(new[] { At(2), At(4) }));
    }

    [Fact]
    public void SummaryReportsTheClockFloorNotJustTheAverage()
    {
        // A run that started fast and settled low is the shape worth catching; a median alone
        // hides the floor the machine actually fell back to.
        var samples = new[]
        {
            At(2, temp: 60, ghz: 4.3), At(4, temp: 75, ghz: 4.3),
            At(6, temp: 88, ghz: 3.1), At(8, temp: 90, ghz: 2.4),
        };

        var s = RunStats.Summarise(samples);

        Assert.Equal(2.4, s.MinClockGhz);
        Assert.Equal(90, s.MaxTempC);
        Assert.Equal(TimeSpan.FromSeconds(8), s.Duration);
        Assert.Equal(4, s.SampleCount);
    }

    [Fact]
    public void ThrottledFractionCountsSamplesNotTime()
    {
        var s = RunStats.Summarise(new[]
        {
            At(2), At(4, throttling: true), At(6, throttling: true), At(8),
        });

        Assert.Equal(0.5, s.ThrottledFraction);
    }

    [Fact]
    public void EmptyRunSummarisesWithoutInventingAnything()
    {
        var s = RunStats.Summarise(Array.Empty<LoadSample>());

        Assert.Equal(0, s.SampleCount);
        Assert.Equal(TimeSpan.Zero, s.Duration);
        Assert.Null(s.MedianTempC);
        Assert.Null(s.MaxTempC);
        Assert.Null(s.TimeToThrottle);
        Assert.Equal(0, s.ThrottledFraction);
    }

    [Fact]
    public async Task RunAlwaysStopsItsLoadEvenWhenCancelled()
    {
        // The one failure this must not have: cancelling a run and leaving every core pinned.
        using var cts = new CancellationTokenSource();
        int taken = 0;

        var run = LoadTest.RunAsync(
            TimeSpan.FromMinutes(5),
            _ => { taken++; return At(0); },
            threads: 1,
            sampleInterval: TimeSpan.FromMilliseconds(20),
            token: cts.Token);

        await Task.Delay(200);
        cts.Cancel();

        // Completes rather than hanging, which is only true if the burner honours the token.
        var samples = await run;

        Assert.True(taken > 0, "the run should have sampled before cancellation");
        Assert.NotNull(samples);
    }
}
