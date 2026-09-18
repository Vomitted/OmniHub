// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Comparing two stretches of this machine's history.
///
/// The arithmetic is the easy half and lives in RunDifference. What is tested here is the set of
/// cases where a comparison is available and wrong to make: two runs of wildly different lengths,
/// two runs too short to say anything about, and the ordinary case where a machine is compared
/// against itself and the only honest answer is that nothing is distinguishable.
/// </summary>
public class RunComparisonTests
{
    private static readonly DateTime Base = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A run at the real two-second cadence, mean-reverting like the measured trace.</summary>
    [Fact]
    public void TheBandIsActuallyUsedRatherThanAssumed()
    {
        // The defect this parameter exists to fix: fan percentages used to be derived through a
        // process-wide band that startup assigned, so measuring an old run applied today's scale
        // to yesterday's rows. Calibrating the fans silently rescaled every historical figure,
        // including the baseline half of an A/B comparison -- which is the exact number somebody
        // runs a comparison in order to trust.
        //
        // Identical samples, two bands. If the argument were ignored and a constant used, these
        // would agree, and this test is the only thing standing between that and looking correct.
        var samples = Run(200, 75, new Random(7), fanRaw: 30);

        var measured = RunComparison.Measure(samples, new FanCalibration(10, 56, 56));
        var wider = RunComparison.Measure(samples, new FanCalibration(10, 120, 120));

        Assert.NotNull(measured.MedianFanPercent);
        Assert.NotNull(wider.MedianFanPercent);

        // Raw 30 is a long way up a band ending at 56 and barely off the floor of one ending at
        // 120, so the same reading means very different things about how hard the fan is working.
        Assert.True(measured.MedianFanPercent > wider.MedianFanPercent,
                    $"raw 30 read as {measured.MedianFanPercent}% on a 10-56 band and "
                    + $"{wider.MedianFanPercent}% on a 10-120 one; the band was ignored");
    }

    private static List<ThermalSample> Run(int count, double meanTemp, Random random, byte fanRaw = 25)
    {
        var samples = new List<ThermalSample>(count);
        double value = meanTemp;

        for (int i = 0; i < count; i++)
        {
            value = meanTemp + 0.98 * (value - meanTemp) + (random.NextDouble() - 0.5) * 1.5;

            samples.Add(new ThermalSample(
                Base.AddSeconds(i * 2), value, null, fanRaw, fanRaw, 40, false, "Auto", "SmuDieTctl"));
        }

        return samples;
    }

    /// <summary>Coverage is the samples' own cadence, not the width of the window asked for.</summary>
    [Fact]
    public void CoverageComesFromTheSamplesNotTheWindow()
    {
        var metrics = RunComparison.Measure(Run(300, 75, new Random(1)), FanCalibration.Default);

        Assert.Equal(300, metrics.Samples);
        Assert.Equal(TimeSpan.FromSeconds(600), metrics.Covered);
    }

    /// <summary>Every figure is null when the samples did not supply it, rather than zero.</summary>
    [Fact]
    public void AbsentReadingsAreNullNotZero()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(i => new ThermalSample(Base.AddSeconds(i * 2), null, null, null, null, null, null, "Auto", "x"))
            .ToList();

        var metrics = RunComparison.Measure(samples, FanCalibration.Default);

        Assert.Equal(10, metrics.Samples);
        Assert.Null(metrics.MeanTempC);
        Assert.Null(metrics.P90TempC);
        Assert.Null(metrics.MaxTempC);
        Assert.Null(metrics.MedianFanPercent);
    }

    /// <summary>The ninth decile sits below the worst instant, which is the point of having both.</summary>
    [Fact]
    public void TheNinthDecileIsNotTheMaximum()
    {
        var samples = Run(300, 70, new Random(5)).ToList();

        // One brief excursion, the kind a p90 should mostly ignore and a maximum should not.
        samples[150] = samples[150] with { TempC = 95 };

        var metrics = RunComparison.Measure(samples, FanCalibration.Default);

        Assert.Equal(95, metrics.MaxTempC!.Value, 3);
        Assert.True(metrics.P90TempC < 80, $"p90 was dragged to {metrics.P90TempC:0.0} by one sample");
    }

    /// <summary>
    /// An interval containing no difference is reported as indistinguishable, in plain words.
    ///
    /// Tested through the judgement with an interval handed to it, rather than by comparing two
    /// generated runs and hoping. A 95% interval misses one time in twenty by construction, so a
    /// single draw asserting a probabilistic outcome is a test that fails for the right reason
    /// roughly every twentieth time somebody touches the seed -- which is how a correct test gets
    /// deleted. The coverage property belongs over a hundred trials and is asserted in
    /// RunDifferenceTests, where it is.
    /// </summary>
    [Fact]
    public void AnIntervalContainingNoDifferenceIsReportedAsIndistinguishable()
    {
        var metrics = new RunMetrics(600, TimeSpan.FromMinutes(20), 75, 78, 85, 30);

        string verdict = RunComparison.Judge(metrics, metrics, new Interval(-0.4, 0.6));

        Assert.Contains("Indistinguishable", verdict);
        Assert.Contains("includes no difference at all", verdict);
    }

    /// <summary>The same machine twice produces one of the honest verdicts and full metrics.</summary>
    [Fact]
    public void ComparingTwoRealRunsProducesAVerdictAndBothSetsOfMetrics()
    {
        var random = new Random(4242);

        var result = RunComparison.Compare(Run(600, 75, random), Run(600, 75, random), FanCalibration.Default);

        Assert.False(string.IsNullOrWhiteSpace(result.Verdict));
        Assert.NotNull(result.TemperatureDifference);
        Assert.NotNull(result.A.MeanTempC);
        Assert.NotNull(result.B.MeanTempC);
        Assert.Equal(600, result.A.Samples);
    }

    /// <summary>A real shift is named, with its direction and its range.</summary>
    [Fact]
    public void ARealImprovementIsNamedWithItsRange()
    {
        var random = new Random(77);

        var result = RunComparison.Compare(Run(600, 80, random), Run(600, 72, random), FanCalibration.Default);

        Assert.Contains("cooler", result.Verdict);
        Assert.False(result.TemperatureDifference!.Value.StraddlesZero);
    }

    /// <summary>
    /// Runs of very different lengths get their numbers and no conclusion.
    ///
    /// The arithmetic would happily produce one, and it would mostly reflect the difference in
    /// length rather than anything that was changed between them.
    /// </summary>
    [Fact]
    public void RunsOfDifferentLengthsAreNotCompared()
    {
        var random = new Random(9);

        var result = RunComparison.Compare(Run(400, 75, random), Run(2000, 75, random), FanCalibration.Default);

        Assert.Contains("not comparable", result.Verdict);
        Assert.Contains("times", result.Verdict);

        // The measurements survive; only the verdict is withheld.
        Assert.NotNull(result.A.MeanTempC);
        Assert.NotNull(result.B.MeanTempC);
    }

    /// <summary>Too little data is said plainly rather than answered with a very wide nothing.</summary>
    [Fact]
    public void TooShortARunIsReportedAsSuch()
    {
        var random = new Random(3);

        var result = RunComparison.Compare(Run(50, 75, random), Run(50, 75, random), FanCalibration.Default);

        Assert.Contains("Not enough data", result.Verdict);
        Assert.Contains("still what was measured", result.Verdict);
    }

    /// <summary>
    /// Throttling is deliberately absent from the metrics.
    ///
    /// The flag it would come from may be an echo of the byte used to ask for it, which is under
    /// investigation elsewhere. A comparison built on it would inherit that doubt without saying
    /// so, and this record having no such field is what keeps that from happening quietly.
    /// </summary>
    [Fact]
    public void ThrottlingIsNotAmongTheMetrics() =>
        Assert.DoesNotContain(
            typeof(RunMetrics).GetProperties(),
            p => p.Name.Contains("Throttl", StringComparison.OrdinalIgnoreCase));

    /// <summary>The percentile interpolates, and has nothing to say about an empty run.</summary>
    [Fact]
    public void ThePercentileInterpolatesAndToleratesNothing()
    {
        Assert.Null(RunComparison.Percentile(Array.Empty<double>(), 0.9));
        Assert.Equal(70, RunComparison.Percentile(new[] { 70.0 }, 0.9)!.Value, 6);
        Assert.Equal(20, RunComparison.Percentile(new[] { 0, 10.0, 20, 30, 40 }, 0.5)!.Value, 6);
    }
}
