// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Fan;

/// <summary>
/// The control temperature with brief spikes set aside: the median of the last five readings.
///
/// Ryzen's Tctl jumps several degrees for a second or two with every burst of boost and falls back
/// as quickly. The curve raises the fan the instant the temperature does, which is right, and lowers
/// it once the temperature has dropped four degrees below the reading that raised it. A one-tick
/// spike therefore raised the level and set that anchor high, the very next ordinary reading was
/// already four degrees below it, and the fan came straight back down: a sawtooth on every spike.
/// On this machine the log showed the command swinging between 43% and 76% within twenty seconds,
/// chasing heat that was gone before the fan -- whose spin-up is measured at about six seconds --
/// could have reached speed to remove it.
///
/// Measured before adopting this, by replaying 98 hours of this machine's own thermal log through
/// the real curve: reversals of ten points or more fell from 44 an hour to 15, total fan movement
/// by 57%, and the mean commanded level was unchanged (33.5% against 33.0%), so the machine is not
/// cooled less, only more steadily. In sustained heat -- three ticks at or above the temperature
/// where the curve asks for 75% -- the filtered command arrived at most two ticks later, and never
/// failed to arrive. An exponential average was tried too and rejected: it smoothed real plateaus
/// away and in places never reached the level at all.
///
/// Two things are never delayed. A reading at or above the curve's full-speed temperature passes
/// straight through, so the top of the curve responds exactly as it always has. And until five
/// readings exist there is no median, so the latest reading is used rather than an invented history.
/// </summary>
public sealed class SpikeFilter
{
    public const int Window = 5;

    private readonly double[] _ring = new double[Window];
    private int _count;
    private int _next;

    /// <param name="celsius">The latest control temperature.</param>
    /// <param name="bypassAtC">At or above this the reading passes unfiltered; the curve's full-speed point.</param>
    public double Next(double celsius, double bypassAtC)
    {
        // Not a reading. Passed on as it is, so whatever the caller already does with a failed read
        // still happens, and not kept, so one bad sample cannot poison the next five medians.
        if (double.IsNaN(celsius)) return celsius;

        _ring[_next] = celsius;
        _next = (_next + 1) % Window;
        if (_count < Window) _count++;

        if (celsius >= bypassAtC || _count < Window) return celsius;

        var sorted = (double[])_ring.Clone();
        Array.Sort(sorted);
        return sorted[Window / 2];
    }

    /// <summary>Forgets the history, for a loop that is starting again after a gap.</summary>
    public void Reset()
    {
        _count = 0;
        _next = 0;
    }
}
