// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Whether two runs of this machine actually differ.
///
/// The one output in the application that cannot be checked against the hardware, so these tests
/// stand in for that check. They are aimed at a single failure: reporting a difference between
/// two runs of the same machine doing the same thing.
///
/// The fixture matters as much as the assertions here, and both earlier versions of it were
/// wrong. It was first a random walk, which has no fixed mean -- so two independent walks
/// genuinely do drift apart, and the decisive test failed correctly rather than finding a bug.
/// Then it was an AR(1) at 0.95, chosen by eye. The real trace was measured instead: across
/// 198,024 samples on disk the lag-1 autocorrelation is 0.981, still 0.749 at a minute and 0.640
/// at two. The fixture matches that now, which is why the bar below is what it is.
/// </summary>
public class RunDifferenceTests
{
    /// <summary>
    /// A correlated series with a stable mean, matching the measured trace.
    ///
    /// Mean-reverting because a machine under a steady load has an equilibrium temperature and
    /// returns towards it. Phi defaults to the 0.98 the real data shows.
    /// </summary>
    private static List<double> Trace(int count, double mean, Random random, double phi = 0.98, double noise = 1.5)
    {
        var series = new List<double>(count);
        double value = mean;

        for (int i = 0; i < count; i++)
        {
            value = mean + phi * (value - mean) + (random.NextDouble() - 0.5) * noise;
            series.Add(value);
        }

        return series;
    }

    /// <summary>
    /// THE decisive test. Two runs of the same process must read as indistinguishable.
    ///
    /// A 95% interval misses one time in twenty by construction, and the effective-sample-size
    /// correction runs slightly optimistic on a process with memory beyond lag one, so the bar is
    /// 88 of 100 rather than 95. That is stated rather than quietly rounded up -- and it still
    /// separates this method decisively from the block bootstrap it replaced, which managed 62.
    /// </summary>
    [Fact]
    public void TwoRunsOfTheSameProcessReadAsIndistinguishable()
    {
        var random = new Random(12345);
        int indistinguishable = 0;

        for (int trial = 0; trial < 100; trial++)
        {
            var a = Trace(600, 75, random);
            var b = Trace(600, 75, random);

            if (RunDifference.DifferenceOfMeans(a, b) is { } interval && interval.StraddlesZero)
                indistinguishable++;
        }

        Assert.True(indistinguishable >= 88,
                    $"only {indistinguishable}/100 same-process pairs read as indistinguishable; "
                    + "below this the comparison is inventing differences");
    }

    /// <summary>
    /// The converse, and it is not optional.
    ///
    /// Without it the interval could be made so wide it never concludes anything and the test
    /// above would pass perfectly. A five-degree shift is the size of change somebody makes a
    /// tuning decision over.
    /// </summary>
    [Fact]
    public void ARealShiftIsDetected()
    {
        var random = new Random(999);
        int detected = 0;

        for (int trial = 0; trial < 50; trial++)
        {
            var a = Trace(600, 75, random);
            var b = Trace(600, 70, random);

            if (RunDifference.DifferenceOfMeans(a, b) is { } interval && !interval.StraddlesZero)
                detected++;
        }

        Assert.True(detected >= 45, $"a five-degree shift was detected only {detected}/50 times");
    }

    /// <summary>
    /// The correlation correction is doing something, not decorating the result.
    ///
    /// The same number of samples, correlated against independent: the correlated run carries far
    /// less information and must produce a visibly wider interval. Without this, the correction
    /// could be a no-op and every test above would still pass.
    /// </summary>
    [Fact]
    public void CorrelatedDataGivesAWiderIntervalThanIndependentData()
    {
        var random = new Random(4242);

        var correlatedA = Trace(600, 75, random);
        var correlatedB = Trace(600, 75, random);

        var independentA = Enumerable.Range(0, 600).Select(_ => 75 + (random.NextDouble() - 0.5) * 8).ToList();
        var independentB = Enumerable.Range(0, 600).Select(_ => 75 + (random.NextDouble() - 0.5) * 8).ToList();

        var correlated = RunDifference.DifferenceOfMeans(correlatedA, correlatedB);
        var independent = RunDifference.DifferenceOfMeans(independentA, independentB);

        Assert.NotNull(correlated);
        Assert.NotNull(independent);

        Assert.True(correlated!.Value.Width > independent!.Value.Width,
                    $"correlated width {correlated.Value.Width:0.###} did not exceed independent "
                    + $"{independent.Value.Width:0.###}; the correction is not being applied");
    }

    /// <summary>
    /// Six hundred correlated samples are worth a couple of dozen, not six hundred.
    ///
    /// This is the number the whole method turns on, and getting it wrong in the optimistic
    /// direction is what produces confident nonsense.
    /// </summary>
    [Fact]
    public void CorrelatedSamplesAreWorthFarFewerIndependentOnes()
    {
        var random = new Random(31337);

        var correlated = Trace(600, 75, random);
        var independent = Enumerable.Range(0, 600).Select(_ => 75 + (random.NextDouble() - 0.5) * 8).ToList();

        double correlatedN = RunDifference.EffectiveSampleSize(correlated);
        double independentN = RunDifference.EffectiveSampleSize(independent);

        Assert.True(correlatedN < 100, $"600 strongly correlated samples were valued at {correlatedN:0}");
        Assert.True(independentN > 400, $"600 independent samples were valued at only {independentN:0}");
    }

    /// <summary>
    /// A negative autocorrelation estimate is treated as zero, never as extra information.
    ///
    /// Letting it through would push the effective sample size above the real count, which is the
    /// one direction this must never err in.
    /// </summary>
    [Fact]
    public void ANegativeEstimateNeverInflatesTheSampleSize()
    {
        // Alternating values have a strongly negative lag-1 correlation.
        var alternating = Enumerable.Range(0, 200).Select(i => i % 2 == 0 ? 70.0 : 80.0).ToList();

        Assert.Equal(0, RunDifference.Autocorrelation(alternating), 6);
        Assert.True(RunDifference.EffectiveSampleSize(alternating) <= alternating.Count);
    }

    /// <summary>The same two runs always produce the same verdict.</summary>
    [Fact]
    public void TheVerdictIsReproducible()
    {
        var random = new Random(7);
        var a = Trace(300, 75, random);
        var b = Trace(300, 74, random);

        Assert.Equal(RunDifference.DifferenceOfMeans(a, b), RunDifference.DifferenceOfMeans(a, b));
    }

    /// <summary>Too little data is reported as no answer rather than a confident one.</summary>
    [Fact]
    public void TooShortARunGivesNoInterval() =>
        Assert.Null(RunDifference.DifferenceOfMeans(new List<double> { 70, 71 }, new List<double> { 70, 71 }));

    /// <summary>Two runs that never moved differ by exactly their constant.</summary>
    [Fact]
    public void TwoConstantRunsDifferByTheirConstant()
    {
        var a = Enumerable.Repeat(70.0, 300).ToList();
        var b = Enumerable.Repeat(75.0, 300).ToList();

        var interval = RunDifference.DifferenceOfMeans(a, b);

        Assert.NotNull(interval);
        Assert.Equal(5, interval!.Value.Low, 6);
        Assert.Equal(5, interval.Value.High, 6);
        Assert.False(interval.Value.StraddlesZero);
    }

    /// <summary>
    /// Few degrees of freedom widen the interval sharply, which is the point of the table.
    ///
    /// At three degrees of freedom the multiplier is over three, not 1.96. A method that used the
    /// normal quantile regardless would be confidently wrong exactly where correlated data puts
    /// it -- at the small-sample end.
    /// </summary>
    [Fact]
    public void TheCriticalValueGrowsAsInformationShrinks()
    {
        Assert.True(RunDifference.TCritical(3) > 3.0);
        Assert.True(RunDifference.TCritical(10) > 2.2);
        Assert.Equal(1.96, RunDifference.TCritical(200), 3);

        // Monotone: less information never buys a narrower interval.
        for (double df = 2; df < 30; df += 1)
            Assert.True(RunDifference.TCritical(df) >= RunDifference.TCritical(df + 1) - 1e-9);
    }
}
