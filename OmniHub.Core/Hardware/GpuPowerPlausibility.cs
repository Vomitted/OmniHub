// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

/// <summary>
/// Whether a GPU power reading is a measurement.
///
/// This exists because of one on this machine that is not. Sampled forty times in ten seconds, the
/// card reported exactly 312.13 W on ten of them -- the same value to the last digit each time,
/// while the surrounding samples sat between 14.96 and 15.05 W. It is not drift and it is not the
/// reader: nvidia-smi and NVML both produce it, in the same state, so it comes from the driver or
/// the card rather than from this application.
///
/// It is also impossible. The board reports its own ceiling as 75 W and its default as 60 W, so
/// 312 W is four times a limit the hardware enforces. It showed up mostly alongside a 1230 MHz
/// clock and 48 C, but not reliably enough to detect by the clock, and detecting it by the value
/// would be hard-coding one number that happens to be wrong on one machine.
///
/// So the rule is the card's own limit. A draw far above what the board is allowed to draw did not
/// happen, whatever the number is and whichever card reports it.
/// </summary>
public static class GpuPowerPlausibility
{
    /// <summary>
    /// How far above its enforced ceiling a board is still believed.
    ///
    /// Generous on purpose. A real card can exceed its sustained limit briefly, the limit NVML
    /// reports is not always the one in force, and the failure being caught here is off by a
    /// factor of four rather than by a few per cent. Half again is well clear of anything real and
    /// nowhere near anything wrong.
    /// </summary>
    public const double Tolerance = 1.5;

    /// <summary>
    /// Whether to believe a reading.
    ///
    /// A null limit means nothing to compare against, and the answer is then yes. That is
    /// deliberate: this refuses readings it can prove impossible, and a board whose ceiling cannot
    /// be read has not proved anything. Discarding those would turn one machine's driver quirk
    /// into missing data on every card whose limit NVML does not report.
    /// </summary>
    public static bool IsMeasurement(double watts, double? maxLimitWatts)
    {
        if (double.IsNaN(watts) || double.IsInfinity(watts) || watts < 0) return false;
        if (maxLimitWatts is not { } limit || limit <= 0) return true;

        return watts <= limit * Tolerance;
    }

    /// <summary>The reading, or null where it cannot have happened.</summary>
    public static double? Filter(double? watts, double? maxLimitWatts) =>
        watts is { } w && IsMeasurement(w, maxLimitWatts) ? w : null;
}
