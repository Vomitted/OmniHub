// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Network;

/// <summary>
/// The result of one round of latency sampling.
///
/// Three numbers matter to a game and only one of them is the one people quote. Mean round-trip
/// time sets the floor on how stale your view of the world is, and past a certain distance it is
/// physics rather than a fault. Jitter and loss are what make a connection FEEL broken: a
/// rollback netcode mispredicts and visibly corrects on every jitter spike, and a lag-compensated
/// shooter cannot rewind to a state it never received. A 200 ms link with no jitter and no loss
/// plays consistently; a 40 ms link with 2% loss does not.
///
/// So all three are kept together and all three are shown together. Reporting the mean alone is
/// how a connection gets declared fine while it is actively ruining a match.
/// </summary>
public sealed record LatencyStats(
    int Sent,
    int Received,
    double MinMs,
    double AvgMs,
    double MaxMs,
    double P95Ms,
    double JitterMs)
{
    /// <summary>Percentage of probes that never came back. A timeout counts as lost.</summary>
    public double LossPercent => Sent == 0 ? 0 : 100.0 * (Sent - Received) / Sent;

    /// <summary>True when nothing came back at all, so every other field is meaningless.</summary>
    public bool IsEmpty => Received == 0;

    /// <summary>
    /// Nothing was measured. Deliberately distinct from a zero-latency result, because this
    /// project does not render an unavailable reading as a plausible number.
    /// </summary>
    public static LatencyStats None { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>
    /// Builds the statistics from raw samples.
    /// </summary>
    /// <param name="sent">How many probes were issued, including the ones that never returned.</param>
    /// <param name="roundTripsMs">Only the probes that came back, in the order they were taken.</param>
    public static LatencyStats From(int sent, IReadOnlyList<double> roundTripsMs)
    {
        if (roundTripsMs.Count == 0) return new LatencyStats(sent, 0, 0, 0, 0, 0, 0);

        var sorted = roundTripsMs.OrderBy(x => x).ToList();

        // Jitter as the mean absolute difference between CONSECUTIVE samples, which is the
        // RFC 3550 sense and the one a game actually experiences, rather than the standard
        // deviation about the mean.
        //
        // The two disagree in exactly the case that matters. A connection alternating
        // 40, 90, 40, 90 ms has a standard deviation of 25 ms and a consecutive-difference
        // jitter of 50 ms: every packet arrives at a different offset from the last, which is
        // what makes a rollback netcode correct on every frame. A connection that sits at 40 ms
        // and steps once to 90 ms has a similar standard deviation and a consecutive jitter
        // near zero, and it plays fine after the step. Standard deviation cannot tell those
        // apart. This can.
        //
        // Order therefore matters: this must be handed the samples as they were taken, not
        // sorted. Sorting them first would compute the mean gap of the DISTRIBUTION, which is
        // a different quantity that happens to look plausible.
        double jitter = 0;
        if (roundTripsMs.Count > 1)
        {
            double sum = 0;
            for (int i = 1; i < roundTripsMs.Count; i++)
                sum += Math.Abs(roundTripsMs[i] - roundTripsMs[i - 1]);
            jitter = sum / (roundTripsMs.Count - 1);
        }

        // Nearest-rank p95, clamped so a short run cannot index past either end. p95 rather than
        // the maximum because one stray sample is not a property of the connection, and the
        // maximum is always exactly one sample.
        int rank = Math.Clamp((int)Math.Ceiling(sorted.Count * 0.95) - 1, 0, sorted.Count - 1);

        return new LatencyStats(
            Sent: sent,
            Received: roundTripsMs.Count,
            MinMs: sorted[0],
            AvgMs: roundTripsMs.Average(),
            MaxMs: sorted[^1],
            P95Ms: sorted[rank],
            JitterMs: jitter);
    }
}
