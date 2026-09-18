// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Network;
using Xunit;

namespace OmniHub.Tests;

public class NetworkStatsTests
{
    /// <summary>
    /// The distinction the whole jitter figure exists for.
    ///
    /// Both connections here have a similar spread about their mean, so a standard deviation
    /// cannot separate them. They feel completely different to play on: the first changes its
    /// delay on every single packet, which is what makes a rollback netcode visibly correct over
    /// and over, while the second steps once and is then perfectly steady.
    /// </summary>
    [Fact]
    public void Jitter_SeparatesAlternatingFromAOneTimeStep()
    {
        var alternating = LatencyStats.From(4, new double[] { 40, 90, 40, 90 });
        var oneStep = LatencyStats.From(4, new double[] { 40, 40, 90, 90 });

        Assert.Equal(50, alternating.JitterMs, 3);
        Assert.True(oneStep.JitterMs < alternating.JitterMs / 2,
            $"a single step read as {oneStep.JitterMs}ms of jitter, nearly as bad as constant alternation");
    }

    /// <summary>
    /// Jitter is defined on the samples in the order they arrived, so the caller must not hand
    /// them over sorted. Sorting first computes the mean gap of the distribution, which is a
    /// different quantity that looks entirely plausible and is not jitter.
    /// </summary>
    [Fact]
    public void Jitter_DependsOnArrivalOrder()
    {
        var asMeasured = LatencyStats.From(4, new double[] { 40, 90, 40, 90 });
        var sorted = LatencyStats.From(4, new double[] { 40, 40, 90, 90 });

        Assert.NotEqual(asMeasured.JitterMs, sorted.JitterMs, 3);
    }

    [Fact]
    public void Loss_CountsProbesThatNeverReturned()
    {
        // Twenty sent, sixteen came back.
        var stats = LatencyStats.From(20, Enumerable.Repeat(30.0, 16).ToList());

        Assert.Equal(20, stats.Sent);
        Assert.Equal(16, stats.Received);
        Assert.Equal(20, stats.LossPercent, 3);
    }

    /// <summary>
    /// Nothing came back, so every derived figure must stay absent rather than default to a
    /// plausible-looking zero. A zero-millisecond connection is not what "no answer" means.
    /// </summary>
    [Fact]
    public void NothingReceived_IsEmptyRatherThanZeroLatency()
    {
        var stats = LatencyStats.From(10, Array.Empty<double>());

        Assert.True(stats.IsEmpty);
        Assert.Equal(100, stats.LossPercent, 3);
    }

    [Fact]
    public void P95_StaysInRangeForShortRuns()
    {
        var one = LatencyStats.From(1, new double[] { 42 });
        Assert.Equal(42, one.P95Ms, 3);

        var two = LatencyStats.From(2, new double[] { 10, 20 });
        Assert.InRange(two.P95Ms, 10, 20);
    }

    /// <summary>
    /// The regression this feature was built around.
    ///
    /// A load test whose download silently transferred nothing reported "latency changed by
    /// -0.9 ms" and therefore a clean connection. It had measured an idle link twice. A flat
    /// reading with no load behind it has to come back as inconclusive, because the alternative
    /// is handing someone a clean bill of health for a question that was never asked.
    /// </summary>
    [Fact]
    public void FlatLatencyWithNoThroughput_IsInconclusiveNotClean()
    {
        var idle = LatencyStats.From(10, new double[] { 6, 6, 5, 6, 7, 6, 6, 5, 6, 6 });
        var loaded = LatencyStats.From(10, new double[] { 5, 5, 6, 5, 5, 6, 5, 5, 5, 4 });

        var result = LoadedLatencyTest.Decide(idle, loaded, mbps: 0.0, pingTarget: "1.1.1.1");

        Assert.Equal(LoadVerdict.Inconclusive, result.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(result.Note));
    }

    [Fact]
    public void FlatLatencyWithRealThroughput_IsClean()
    {
        var idle = LatencyStats.From(10, new double[] { 6, 6, 5, 6, 7, 6, 6, 5, 6, 6 });
        var loaded = LatencyStats.From(10, new double[] { 7, 6, 6, 7, 6, 6, 7, 6, 6, 7 });

        var result = LoadedLatencyTest.Decide(idle, loaded, mbps: 94.0, pingTarget: "1.1.1.1");

        Assert.Equal(LoadVerdict.Clean, result.Verdict);
    }

    /// <summary>
    /// A latency rise proves queueing on its own, so it must outrank the throughput gate. A slow
    /// transfer that still bloated the queue has answered the question we came to ask, and
    /// discarding it for being slow would throw away a true positive.
    /// </summary>
    [Fact]
    public void LatencyRiseBeatsTheThroughputGate()
    {
        var idle = LatencyStats.From(10, new double[] { 6, 6, 5, 6, 7, 6, 6, 5, 6, 6 });
        var loaded = LatencyStats.From(10, new double[] { 180, 210, 190, 240, 200, 220, 195, 205, 215, 190 });

        var result = LoadedLatencyTest.Decide(idle, loaded, mbps: 1.2, pingTarget: "1.1.1.1");

        Assert.Equal(LoadVerdict.Bloated, result.Verdict);
        Assert.True(result.AddedMs > 150);
    }

    /// <summary>
    /// A physics floor keeps a distant server's latency honest: it is the difference between "your
    /// connection is bad" and "that server is on the other side of the planet". Oregon cannot be
    /// reached from Jakarta in under about 135 ms by anything, so a measured 206 ms is roughly
    /// 70 ms of real overhead and not 206 ms of fault.
    /// </summary>
    [Fact]
    public void PhysicsFloor_SeparatesDistanceFromOverhead()
    {
        var region = ServerRegions.All.Single(r => r.Name == "US West");
        var measured = new RegionLatency(region,
            new ProbeResult(region.Host, ProbeMethod.Tcp, LatencyStats.From(3, new double[] { 206, 210, 208 })));

        Assert.InRange(measured.PhysicsFloorMs, 120, 145);
        Assert.InRange(measured.OverheadMs, 55, 90);
    }

    [Fact]
    public void EveryRegionHasAPlausibleDistance()
    {
        Assert.NotEmpty(ServerRegions.All);
        foreach (var r in ServerRegions.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Host));
            // Half the earth's circumference is about 20,000 km; anything beyond that is a typo.
            Assert.InRange(r.ApproxKm, 1, 20_100);
        }
    }
}
