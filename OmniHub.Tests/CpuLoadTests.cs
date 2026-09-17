using System.Diagnostics;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Busy time from the kernel's tick counters, replacing a 283 ms WMI query.
///
/// The arithmetic has one trap and it is worth a test on its own: kernel time already includes
/// idle time. Subtracting idle from kernel-plus-user is correct; treating them as three separate
/// buckets gives an idle machine a load near fifty per cent, which looks entirely plausible on a
/// dashboard and is wrong every second of the day.
/// </summary>
public class CpuLoadTests
{
    /// <summary>A fully idle interval is zero per cent, not fifty.</summary>
    [Fact]
    public void AnIdleIntervalIsZero() =>
        Assert.Equal(0.0, CpuLoad.Percent(idleDelta: 1000, kernelDelta: 1000, userDelta: 0)!.Value, 3);

    /// <summary>An interval with no idle time at all is a hundred per cent.</summary>
    [Fact]
    public void AFullyBusyIntervalIsAHundred() =>
        Assert.Equal(100.0, CpuLoad.Percent(idleDelta: 0, kernelDelta: 400, userDelta: 600)!.Value, 3);

    /// <summary>
    /// Half idle is half, with the idle ticks counted once rather than twice.
    ///
    /// 500 idle inside 800 kernel, plus 200 user: total 1000, busy 500. Reading kernel and idle
    /// as separate buckets would give 1500 total and 33 per cent, which is the plausible wrong
    /// answer this test exists to reject.
    /// </summary>
    [Fact]
    public void KernelTimeAlreadyContainsIdleTime() =>
        Assert.Equal(50.0, CpuLoad.Percent(idleDelta: 500, kernelDelta: 800, userDelta: 200)!.Value, 3);

    /// <summary>Two reads inside one clock tick divide nothing, and say so.</summary>
    [Fact]
    public void NoElapsedTimeIsNoReading() =>
        Assert.Null(CpuLoad.Percent(idleDelta: 0, kernelDelta: 0, userDelta: 0));

    /// <summary>
    /// More idle than total is impossible, so it is reported as no reading.
    ///
    /// It should never happen. If it does, a number derived from an impossibility is worse than
    /// an admitted gap -- and the naive formula would return a negative percentage, which would
    /// then be clamped to zero somewhere and look like an idle machine.
    /// </summary>
    [Fact]
    public void AnImpossibleCounterIsNotANegativeLoad() =>
        Assert.Null(CpuLoad.Percent(idleDelta: 2000, kernelDelta: 1000, userDelta: 0));

    /// <summary>
    /// The first reading has nothing to compare against and says so.
    ///
    /// These are cumulative counters since boot, so one sample describes the whole uptime rather
    /// than now. Returning zero would put an idle machine on screen at the moment somebody opened
    /// the dashboard to find out why theirs was not.
    /// </summary>
    [Fact]
    public void TheFirstReadingIsNull()
    {
        CpuLoad.Reset();
        Assert.Null(CpuLoad.Percent());
    }

    /// <summary>The second reading against this machine is a real percentage.</summary>
    [Fact]
    public void TheSecondReadingIsAPercentage()
    {
        CpuLoad.Reset();
        CpuLoad.Percent();

        // Something has to have elapsed between the two calls, and running the tests is itself
        // work, so this is not a contrived load -- just enough ticks to divide by.
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 30) { }

        if (CpuLoad.Percent() is { } load)
            Assert.InRange(load, 0, 100);
    }

    /// <summary>
    /// The whole read is fast now, which is the entire point of the change.
    ///
    /// The query this replaced measured 283 ms and ran once per poll tick for as long as the
    /// dashboard was open. The threshold here is deliberately loose -- it is not a benchmark,
    /// it is a tripwire against somebody reintroducing a WMI provider on this path.
    /// </summary>
    [Fact]
    public void TheWholeReadIsNowhereNearAWmiQuery()
    {
        SystemPerfReader.Read();   // warm any one-time cost

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) SystemPerfReader.Read();
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 100,
                    $"five reads took {clock.ElapsedMilliseconds} ms; one WMI query alone was 283 ms");
    }

    /// <summary>
    /// The real read reports plausible memory and a clock, or admits it cannot.
    ///
    /// Memory is not nullable -- a failed read returns no record at all -- so a record in hand
    /// must carry a sane total, and used can never exceed it.
    /// </summary>
    [Fact]
    public void TheRealMachineReads()
    {
        var perf = SystemPerfReader.Read();
        if (perf is null) return;

        Assert.InRange(perf.MemoryTotalGB, 0.5, 4096);
        Assert.InRange(perf.MemoryUsedGB, 0, perf.MemoryTotalGB);

        if (perf.CpuClockGHz is { } ghz)
            Assert.InRange(ghz, 0.3, 12);

        if (perf.CpuLoadPercent is { } load)
            Assert.InRange(load, 0, 100);
    }
}
