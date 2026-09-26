// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// Where the labelled ticks go on a value axis, and how many decimals they need.
///
/// The time axis already puts its gridlines on round instants; the value axis split a padded
/// range into quarters and rounded the labels. A die temperature between 60 and 80 C became
/// ticks at 58.4, 64.2, 70.0, 75.8 and 81.6, printed as "58", "64", "70", "76", "82" -- so four of
/// five labels named a value their line was not at, and an empty chart labelled its lines at 0.25
/// and 0.75 as "0.3" and "0.8". Every chart in the application did this.
///
/// Here the range is widened to whole steps of 1, 2, 2.5 or 5 times a power of ten, so every
/// gridline sits on a value a person would write down, and every label says exactly that value.
/// In Core rather than in the chart because the chart cannot be tested.
/// </summary>
public static class ValueAxis
{
    public readonly record struct Scale(double Lo, double Hi, double Step)
    {
        /// <summary>Every tick from Lo to Hi inclusive, each an exact multiple of the step.</summary>
        public IReadOnlyList<double> Ticks()
        {
            var ticks = new List<double>();
            int n = (int)Math.Round((Hi - Lo) / Step);
            for (int i = 0; i <= n; i++) ticks.Add(Math.Round((Lo + i * Step) / Step) * Step);
            return ticks;
        }

        /// <summary>The fewest decimals that write every tick exactly.</summary>
        public int Decimals => DecimalsFor(Step);
    }

    private static readonly double[] Mantissas = { 1, 2, 2.5, 5, 10 };

    /// <summary>
    /// A scale covering <paramref name="lo"/> to <paramref name="hi"/> in about
    /// <paramref name="targetIntervals"/> round steps.
    /// </summary>
    public static Scale Nice(double lo, double hi, int targetIntervals = 5)
    {
        if (double.IsNaN(lo) || double.IsNaN(hi) || double.IsInfinity(lo) || double.IsInfinity(hi)) return new Scale(0, 1, 0.2);
        if (hi < lo) (lo, hi) = (hi, lo);

        // A flat window still needs a band to be drawn in; one unit either side of it.
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }

        double raw = (hi - lo) / Math.Max(1, targetIntervals);
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = Mantissas.Select(m => m * magnitude).First(s => s >= raw - 1e-12);

        // Tolerance on the floor and ceiling, so a bound that is already on a step is not pushed
        // out by a whole step because of floating-point residue.
        double niceLo = Math.Floor(lo / step + 1e-9) * step;
        double niceHi = Math.Ceiling(hi / step - 1e-9) * step;
        return new Scale(niceLo, niceHi, step);
    }

    /// <summary>
    /// The scale that wastes the least of the plot, trying every interval count up to
    /// <paramref name="maxIntervals"/> and preferring more gridlines when two fit equally well.
    ///
    /// One fixed count cannot suit every range. A fan band of 0 to 5,500 RPM in two intervals rounds
    /// its step up to 5,000 and draws the fan in the bottom half of a 0-to-10,000 scale; in three it
    /// is 2,000, and the scale ends at 6,000. Short plots can only label a few intervals, which is
    /// exactly where the rounding of a small count overshoots most.
    /// </summary>
    public static Scale Tightest(double lo, double hi, int maxIntervals)
    {
        Scale best = Nice(lo, hi, Math.Max(1, maxIntervals));
        for (int n = maxIntervals - 1; n >= 2; n--)
        {
            var candidate = Nice(lo, hi, n);
            if (candidate.Hi - candidate.Lo < best.Hi - best.Lo - 1e-9) best = candidate;
        }
        return best;
    }

    public static int DecimalsFor(double step)
    {
        for (int d = 0; d <= 6; d++)
        {
            double scaled = step * Math.Pow(10, d);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9 * Math.Max(1, scaled)) return d;
        }
        return 6;
    }
}
