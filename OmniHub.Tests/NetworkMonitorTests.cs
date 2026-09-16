using OmniHub.Core.Network;
using Xunit;

namespace OmniHub.Tests;

public class NetworkMonitorTests
{
    private static NetworkMonitor Feed(params double?[] samples)
    {
        var m = new NetworkMonitor();
        foreach (var s in samples) m.Record(s);
        return m;
    }

    /// <summary>
    /// A probe that never came back has to stay in the window as a hole, not be dropped.
    ///
    /// Dropping it would leave a window of nothing but successes, which reports a beautifully
    /// steady latency for a connection losing a quarter of its packets -- precisely the
    /// connection worth reporting.
    /// </summary>
    [Fact]
    public void LostProbesCountTowardLossRatherThanVanishing()
    {
        var m = Feed(20, null, 20, 20);

        Assert.Equal(4, m.Current.Sent);
        Assert.Equal(3, m.Current.Received);
        Assert.Equal(25, m.Current.LossPercent, 3);
    }

    /// <summary>
    /// Once the ring wraps, the samples must be read oldest-first rather than from index zero.
    ///
    /// Jitter is the mean gap between CONSECUTIVE samples, so reading the ring in storage order
    /// starts in the middle of the sequence and manufactures exactly one false jump per window,
    /// at the wrap point.
    ///
    /// The values ramp by one so the two orderings give different answers and the test can tell
    /// them apart. A first version fed a steady 20ms after one outlier, which reads as a sensible
    /// scenario and proves nothing whatsoever: the outlier had already scrolled out, so the window
    /// held nothing but 20s and every possible ordering of it yields a jitter of zero. A test that
    /// passes against the bug it names is worse than no test.
    /// </summary>
    [Fact]
    public void JitterIsComputedInArrivalOrderAcrossTheWrap()
    {
        var m = new NetworkMonitor();

        // Fill the ring exactly, then push it ten past the wrap. Arrival order is now a clean
        // 11,12,...,70 ramp; storage order would be 61..70 followed by 11..60, with a 50ms
        // cliff where the buffer happens to restart.
        for (int i = 1; i <= NetworkMonitor.WindowSize + 10; i++) m.Record(i);

        Assert.Equal(NetworkMonitor.WindowSize, m.Current.Sent);
        Assert.Equal(11, m.Current.MinMs, 3);
        Assert.Equal(NetworkMonitor.WindowSize + 10, m.Current.MaxMs, 3);

        // Every consecutive step is exactly 1 in arrival order. Storage order would average
        // close to 2, because of the single large jump at the seam.
        Assert.Equal(1.0, m.Current.JitterMs, 3);
    }

    /// <summary>The window is bounded: a monitor left running for days must not grow without limit.</summary>
    [Fact]
    public void WindowNeverExceedsItsSize()
    {
        var m = new NetworkMonitor();
        for (int i = 0; i < NetworkMonitor.WindowSize * 3; i++) m.Record(15);

        Assert.Equal(NetworkMonitor.WindowSize, m.Current.Sent);
    }

    /// <summary>
    /// Changing the target discards the window. The old samples describe a different host, and
    /// splicing them onto the new one would report a jitter spike nothing experienced.
    /// </summary>
    [Fact]
    public void ChangingTargetClearsTheWindow()
    {
        var m = Feed(20, 20, 20);
        Assert.False(m.Current.IsEmpty);

        m.Target = "8.8.8.8";

        Assert.True(m.Current.IsEmpty);
        Assert.Equal("8.8.8.8", m.Target);
    }

    /// <summary>Nothing has been measured yet, so nothing is claimed. Not a zero-latency connection.</summary>
    [Fact]
    public void ReportsNothingBeforeTheFirstProbe()
    {
        Assert.True(new NetworkMonitor().Current.IsEmpty);
    }

    /// <summary>
    /// The window the UI describes has to be the window that was actually sampled.
    ///
    /// The Network tab printed "IN THE LAST {WindowSize * 5 / 60} MINUTES" with the five
    /// written in -- the default probe interval -- while the real one is a setting clamped to
    /// 2..60 seconds. At the default the label was right by coincidence; at any other setting
    /// it stated a duration the window had never covered. The resume button had the same bug
    /// in a worse form: it restarted the loop at five seconds whatever the user had chosen.
    /// </summary>
    [Fact]
    public void TheWindowSpanFollowsTheIntervalActuallyInUse()
    {
        var monitor = new NetworkMonitor();

        // Before it ever runs it reports the default rather than zero, so a label rendered
        // ahead of the first probe still says something true.
        Assert.Equal(TimeSpan.FromSeconds(5), monitor.Interval);
        Assert.Equal(TimeSpan.FromSeconds(5) * NetworkMonitor.WindowSize, monitor.Window);

        monitor.Start(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(30), monitor.Interval);
            Assert.Equal(30.0 * NetworkMonitor.WindowSize / 60.0, monitor.Window.TotalMinutes, 3);
        }
        finally { monitor.Stop(); }
    }
}
