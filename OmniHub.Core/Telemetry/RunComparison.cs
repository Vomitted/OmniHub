// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.Core.Telemetry;

/// <summary>
/// What one run of this machine looked like.
/// </summary>
/// <param name="Samples">How many thermal rows the range actually contained.</param>
/// <param name="Covered">How much time those rows account for, which is not the range's width.</param>
/// <param name="MeanTempC">The average the comparison is run on.</param>
/// <param name="P90TempC">The ninth decile: where it spent its hot time, rather than its worst instant.</param>
/// <param name="MaxTempC">The worst instant, kept because a single excursion can still matter.</param>
/// <param name="MedianFanPercent">Median measured fan duty, or null where the board reported none.</param>
public sealed record RunMetrics(
    int Samples,
    TimeSpan Covered,
    double? MeanTempC,
    double? P90TempC,
    double? MaxTempC,
    double? MedianFanPercent);

/// <summary>Two runs, the difference between them, and how much to believe it.</summary>
public sealed record RunComparisonResult(
    RunMetrics A,
    RunMetrics B,
    Interval? TemperatureDifference,
    Interval? FanDifference,
    string Verdict);

/// <summary>
/// Comparing two stretches of this machine's own history.
///
/// The whole release exists so a change can be shown to have helped, and this is where that
/// question gets answered -- from the trace already on disk, so it works backwards over the two
/// weeks already recorded rather than only forwards from the moment somebody thinks to press
/// record.
///
/// Three things it deliberately does not measure.
///
/// Throttling, although the column is right there. The flag it would come from is the one whose
/// validity is currently under investigation -- it may be an echo of the byte used to ask for it
/// -- and a comparison built on a reading that might not be a reading would inherit that doubt
/// silently. It comes back once the probe has settled it.
///
/// Package watts and clock floors, which the plan asked for, because nothing writes them to a
/// log. They can be added the day something does; deriving them from the readings that exist
/// would be exactly the fabrication this project forbids.
///
/// And any conclusion at all when the two runs are not comparable. Coverage differing more than
/// twofold is the usual reason -- twenty minutes against ninety is not a fair fight, and the
/// arithmetic is shown with the verdict withheld rather than quietly computed anyway.
/// </summary>
public static class RunComparison
{
    /// <summary>
    /// Fewest samples worth comparing.
    ///
    /// Roughly ten minutes at the two-second cadence. Below this the effective sample size of a
    /// correlated trace is small enough that the interval swamps any real difference, so the
    /// honest output is that there is not enough data rather than a very wide nothing.
    /// </summary>
    public const int MinimumSamples = 300;

    /// <summary>
    /// Beyond this ratio the two runs are not describing comparable things.
    ///
    /// Two is generous. It is here because the arithmetic will happily compare a twenty-minute
    /// run against a ninety-minute one and produce a number, and that number would mostly reflect
    /// how long each ran rather than what was changed between them.
    /// </summary>
    public const double MaxCoverageRatio = 2.0;

    /// <summary>
    /// Measures one run. Every figure is null where the samples did not supply it.
    ///
    /// The calibration is required rather than defaulted, and that is the point of it being a
    /// parameter at all. The log records the raw level the controller was given; turning that
    /// into a percentage needs the band, and the band used to be read from a process-wide static
    /// that startup assigned. So a caller measuring last month's run got this month's scale, and
    /// the moment anybody used the calibration tool every historical row was silently
    /// reinterpreted -- including the baseline half of an A/B comparison, which is precisely the
    /// figure somebody is trying to trust.
    ///
    /// ponytail: the log still does not record the band it was written under, so passing the
    /// current one remains an assumption -- just an explicit one made by the caller instead of an
    /// invisible one made here. Add a band column if per-machine calibration ever becomes common
    /// enough that runs start crossing it.
    /// </summary>
    public static RunMetrics Measure(IReadOnlyList<ThermalSample> samples, FanCalibration calibration)
    {
        var temperatures = samples.Where(s => s.TempC is not null).Select(s => s.TempC!.Value).ToList();

        var fanPercents = samples
            .Where(s => s.Fan1Raw is not null)
            .Select(s => (double)calibration.RawToPercent(s.Fan1Raw!.Value))
            .ToList();

        var stamps = samples.Select(s => s.AtUtc).ToList();
        TimeSpan median = SampleGaps.MedianInterval(stamps) ?? TimeSpan.Zero;

        return new RunMetrics(
            samples.Count,

            // Samples times their own cadence, not the range's width. A window with an hour's gap
            // in the middle covers what was sampled, and saying otherwise would make a sparse run
            // look like a complete one.
            median * samples.Count,

            temperatures.Count > 0 ? temperatures.Average() : null,
            Percentile(temperatures, 0.90),
            temperatures.Count > 0 ? temperatures.Max() : null,
            Percentile(fanPercents, 0.50));
    }

    /// <summary>Compares two runs, and refuses to conclude where a conclusion would mislead.</summary>
    public static RunComparisonResult Compare(
        IReadOnlyList<ThermalSample> a, IReadOnlyList<ThermalSample> b, FanCalibration calibration)
    {
        var metricsA = Measure(a, calibration);
        var metricsB = Measure(b, calibration);

        var tempsA = a.Where(s => s.TempC is not null).Select(s => s.TempC!.Value).ToList();
        var tempsB = b.Where(s => s.TempC is not null).Select(s => s.TempC!.Value).ToList();

        var fansA = a.Where(s => s.Fan1Raw is not null)
                     .Select(s => (double)calibration.RawToPercent(s.Fan1Raw!.Value)).ToList();
        var fansB = b.Where(s => s.Fan1Raw is not null)
                     .Select(s => (double)calibration.RawToPercent(s.Fan1Raw!.Value)).ToList();

        var temperature = RunDifference.DifferenceOfMeans(tempsA, tempsB);
        var fan = RunDifference.DifferenceOfMeans(fansA, fansB);

        return new RunComparisonResult(
            metricsA, metricsB, temperature, fan,
            Judge(metricsA, metricsB, temperature));
    }

    /// <summary>
    /// The sentence, which is allowed to say it does not know.
    ///
    /// "Indistinguishable" is the commonest honest answer when comparing a machine against
    /// itself, and it is written plainly rather than dressed up as a small improvement. A tool
    /// that always finds a difference finds one from noise about half the time.
    /// </summary>
    internal static string Judge(RunMetrics a, RunMetrics b, Interval? temperature)
    {
        if (a.Samples < MinimumSamples || b.Samples < MinimumSamples)
            return $"Not enough data to compare: {a.Samples} and {b.Samples} samples, against "
                 + $"{MinimumSamples} needed on each side. The numbers above are still what was "
                 + "measured.";

        double covered = Math.Max(a.Covered.TotalSeconds, 1);
        double other = Math.Max(b.Covered.TotalSeconds, 1);
        double ratio = Math.Max(covered, other) / Math.Min(covered, other);

        if (ratio > MaxCoverageRatio)
            return $"These two are not comparable: one covers {Humanise(a.Covered)} and the other "
                 + $"{Humanise(b.Covered)}, a difference of {ratio:0.#} times. The measurements are "
                 + "shown; the comparison is not, because it would mostly reflect the difference in "
                 + "length.";

        if (temperature is not { } interval)
            return "Neither run held enough temperature readings to compare. The measurements above "
                 + "are still what was recorded.";

        if (interval.StraddlesZero)
            return $"Indistinguishable. The difference in mean temperature lies between "
                 + $"{interval.Low:+0.0;-0.0} and {interval.High:+0.0;-0.0} C, which includes no "
                 + "difference at all -- so whatever changed between these two runs did not change "
                 + "this measurably.";

        string direction = interval.High < 0 ? "cooler" : "hotter";
        double low = Math.Abs(interval.High < 0 ? interval.High : interval.Low);
        double high = Math.Abs(interval.High < 0 ? interval.Low : interval.High);

        return $"The second run is {direction}, by somewhere between {low:0.0} and {high:0.0} C on "
             + "average. That interval is corrected for how strongly consecutive samples track each "
             + "other, which is why it is wider than the raw numbers suggest.";
    }

    /// <summary>An interpolated percentile, or null when there is nothing to take one of.</summary>
    internal static double? Percentile(IReadOnlyList<double> values, double fraction)
    {
        if (values.Count == 0) return null;

        var sorted = values.OrderBy(v => v).ToArray();

        if (sorted.Length == 1) return sorted[0];

        double position = fraction * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);

        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static string Humanise(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:0} min"
        : $"{span.TotalHours:0.#} h";
}
