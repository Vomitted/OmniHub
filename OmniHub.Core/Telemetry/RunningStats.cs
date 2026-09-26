// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// The lowest, highest and mean value of one reading since the application started, or since
/// the last reset.
///
/// These are HWiNFO's semantics, deliberately, not the trace's. A trend line covers the last few
/// minutes and can say nothing about the spike during the game an hour ago; a session maximum
/// can, and "what did it reach while I was away" is most of the reason to open a sensor table.
///
/// A missing reading is not a zero. An unavailable sample leaves the figures exactly as they were,
/// so a GPU that was asleep for an hour does not drag its mean towards nothing, and a reading that
/// has never answered has no minimum rather than a minimum of zero.
/// </summary>
public sealed class RunningStats
{
    private double _sum;

    /// <summary>The lowest value seen, or null before the first.</summary>
    public double? Min { get; private set; }

    /// <summary>The highest value seen, or null before the first.</summary>
    public double? Max { get; private set; }

    /// <summary>How many values have been counted.</summary>
    public long Count { get; private set; }

    /// <summary>The mean of every value counted, or null before the first.</summary>
    public double? Mean => Count == 0 ? null : _sum / Count;

    /// <summary>Counts one sample. Null, NaN and infinity are not values and are ignored.</summary>
    public void Add(double? value)
    {
        if (value is not { } v || !double.IsFinite(v)) return;

        Min = Min is { } lo ? Math.Min(lo, v) : v;
        Max = Max is { } hi ? Math.Max(hi, v) : v;
        _sum += v;
        Count++;
    }

    /// <summary>Forgets everything, as a fresh start would.</summary>
    public void Reset()
    {
        Min = Max = null;
        _sum = 0;
        Count = 0;
    }
}
