// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// Where the labelled ticks go on a time axis, and what they say.
///
/// Round instants, not evenly spaced offsets from wherever the window happens to start. A tick
/// at 14:37 followed by one at 15:07 is arithmetically correct and useless: reading a chart
/// means finding "about half past two", and that only works if the gridlines land on the
/// instants a person already thinks in.
///
/// In Core rather than in the control because OmniHub.Tests cannot reference OmniHub.App, so
/// anything in the WPF layer is untestable by construction. The control draws what this returns.
/// </summary>
public static class TimeAxis
{
    /// <summary>
    /// The steps a human reads. Nothing between them, deliberately.
    ///
    /// A generic "nice number" algorithm would offer 20 s and 4 h, which are arithmetically
    /// reasonable and not how anybody thinks about time. This ladder is the set of intervals
    /// that clocks and calendars are actually divided into.
    /// </summary>
    private static readonly TimeSpan[] Ladder =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3),
        TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
        TimeSpan.FromDays(14), TimeSpan.FromDays(28),
    };

    /// <summary>
    /// The step whose tick count lands closest to <paramref name="targetCount"/> over the span.
    ///
    /// Closest rather than "first that fits": a rule that took the first step producing no more
    /// than the target would jump from eight ticks to three across one ladder entry, which reads
    /// as the axis suddenly losing detail for no visible reason.
    /// </summary>
    public static TimeSpan Step(TimeSpan span, int targetCount = 6)
    {
        if (span <= TimeSpan.Zero || targetCount < 1) return Ladder[0];

        TimeSpan best = Ladder[^1];
        double bestDistance = double.MaxValue;

        foreach (var candidate in Ladder)
        {
            double count = span.TotalSeconds / candidate.TotalSeconds;
            double distance = Math.Abs(count - targetCount);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Round instants across [from, to], aligned to the chosen step.
    ///
    /// Alignment is against absolute ticks rather than against the window start. DateTime tick
    /// zero is midnight on a date boundary, so every step on the ladder divides it: thirty-minute
    /// ticks land on the hour and the half hour, six-hour ticks on 00:00, 06:00, 12:00 and 18:00,
    /// and day ticks on midnight. No special case per unit is needed.
    /// </summary>
    public static IReadOnlyList<DateTime> Ticks(DateTime fromUtc, DateTime toUtc, int targetCount = 6)
    {
        var ticks = new List<DateTime>();
        if (toUtc <= fromUtc) return ticks;

        TimeSpan step = Step(toUtc - fromUtc, targetCount);
        long stepTicks = step.Ticks;

        // Round the start UP to the next boundary, so the first tick is inside the window.
        long first = ((fromUtc.Ticks + stepTicks - 1) / stepTicks) * stepTicks;

        for (long t = first; t <= toUtc.Ticks; t += stepTicks)
            ticks.Add(new DateTime(t, DateTimeKind.Utc));

        return ticks;
    }

    /// <summary>
    /// How to write a tick for a window of this length.
    ///
    /// The label says only what changes across the window. Printing the date on every tick of a
    /// two-minute view wastes the width that the readings need, and printing only the time on a
    /// two-week view leaves the reader unable to tell Tuesday from Friday.
    /// </summary>
    public static string LabelFormat(TimeSpan span) =>
        span <= TimeSpan.FromMinutes(5) ? "HH:mm:ss"
        : span <= TimeSpan.FromHours(24) ? "HH:mm"
        : span <= TimeSpan.FromDays(7) ? "ddd HH:mm"
        : "MMM d";
}
