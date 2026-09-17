using System.Runtime.InteropServices;

namespace OmniHub.Core.Hardware;

/// <summary>
/// CPU and memory telemetry. Null fields mean the reading could not be taken.
/// </summary>
/// <param name="CpuClockGHz">Peak current clock across the logical processors, or null.</param>
/// <param name="CpuLoadPercent">Busy time since the previous read, or null before there is one.</param>
public sealed record SystemPerf(double? CpuClockGHz, double? CpuLoadPercent, double MemoryUsedGB, double MemoryTotalGB);

/// <summary>
/// Busy time from the kernel's own tick counters.
///
/// This replaces a WMI query whose cost nobody had ever measured. The source note recorded the
/// rate -- 1.29 queries a second while the dashboard is open -- and stopped there; the query
/// itself measures 283 ms. Called once per poll tick, that is roughly fourteen per cent of one
/// processor spent continuously asking how busy the processor is, which is the largest single
/// cost in this application and the most ironic.
///
/// GetSystemTimes is the same source Task Manager reads: three counters, one syscall, no
/// provider to spin up and no instance to marshal across the WMI boundary.
/// </summary>
public sealed class CpuLoad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong Ticks => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private readonly object _gate = new();
    private ulong _idle, _kernel, _user;
    private bool _seeded;

    /// <summary>
    /// Busy percentage since this sampler's previous call, or null when there has not been one.
    ///
    /// Null on the first call rather than zero. These are cumulative counters since boot, so a
    /// single reading says nothing about now -- and reporting zero would put an idle machine on
    /// screen at the exact moment somebody opened the dashboard to find out why theirs was not.
    ///
    /// The previous sample is held per instance, not in a static. It used to be shared, which was
    /// invisible while the dashboard was the only caller and wrong the moment a second one
    /// appeared: each call consumes the window since ANY caller's last call, so a second readout
    /// on its own timer would report busy time over whatever few milliseconds happened to have
    /// passed since the first one asked. Both numbers would be arithmetically correct and neither
    /// would be a measurement of what its own label claimed.
    /// </summary>
    public double? Percent()
    {
        lock (_gate)
        {
            if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
                return null;

            ulong nowIdle = idle.Ticks, nowKernel = kernel.Ticks, nowUser = user.Ticks;

            if (!_seeded)
            {
                (_idle, _kernel, _user, _seeded) = (nowIdle, nowKernel, nowUser, true);
                return null;
            }

            double? percent = Compute(nowIdle - _idle, nowKernel - _kernel, nowUser - _user);
            (_idle, _kernel, _user) = (nowIdle, nowKernel, nowUser);

            return percent;
        }
    }

    /// <summary>
    /// The arithmetic, separated so it can be tested without a machine to be busy on.
    ///
    /// Kernel time already includes idle time -- that is the trap in this calculation, and
    /// forgetting it gives an idle machine a load near fifty per cent, which looks entirely
    /// plausible. Total is kernel plus user; busy is total less idle.
    /// </summary>
    internal static double? Compute(ulong idleDelta, ulong kernelDelta, ulong userDelta)
    {
        ulong total = kernelDelta + userDelta;

        // No elapsed time at all: two reads inside one tick of the clock. Nothing happened that
        // can be divided, and zero per cent would be a claim rather than an absence.
        if (total == 0) return null;

        // A counter that went backwards is not a negative load. It should not happen, and if it
        // does the honest output is no reading rather than a number derived from an impossibility.
        if (idleDelta > total) return null;

        return (total - idleDelta) * 100.0 / total;
    }
}

/// <summary>
/// Real CPU and RAM telemetry, without WMI.
///
/// Both of the WMI queries this used to make have been replaced by the sources they were
/// standing in for. The load came from Win32_PerfFormattedData_PerfOS_Processor, measured at
/// 283 ms; it now comes from the kernel counters Task Manager uses. The clock came from
/// Win32_Processor.CurrentClockSpeed at 10.6 ms, which this file's own comment warned "is not
/// guaranteed to track the CPU's real-time dynamic (Turbo Boost) frequency" -- it now comes from
/// the per-processor clocks, which do, and which report the peak rather than a nominal figure.
///
/// No GPU load, VRAM or clock here deliberately: that lives in GpuTelemetry, where it names its
/// own source.
/// </summary>
public sealed class SystemPerfReader
{
    // One sampler per reader, so two readouts on two timers each measure their own window.
    private readonly CpuLoad _load = new();

    public SystemPerf? Read()
    {
        try
        {
            // The peak across logical processors, not the mean. A lightly threaded load leaves
            // most cores parked, so an average across twelve reports a low number for a
            // processor that is in fact boosting hard on one.
            var clocks = ProcessorClocks.Read();
            double? clockGHz = clocks.Count > 0 ? ProcessorClocks.PeakMhz(clocks) / 1000.0 : null;

            // Memory comes from GlobalMemoryStatusEx rather than a WMI query. It is the same
            // pair of numbers Win32_OperatingSystem reports, from the same kernel counters,
            // through a P/Invoke that costs effectively nothing -- and MemoryTools already wraps
            // it for the System tab, so this adds no new code.
            ulong totalBytes = Optimize.MemoryTools.TotalPhysicalBytes();
            ulong availableBytes = Optimize.MemoryTools.AvailablePhysicalBytes();

            // Both return 0 when the call fails. A zero total would make every derived figure
            // nonsense, so treat it as a failed read rather than report a machine with no RAM.
            if (totalBytes == 0) return null;

            const double BytesPerGB = 1024.0 * 1024.0 * 1024.0;

            return new SystemPerf(
                clockGHz,
                _load.Percent(),
                (totalBytes - availableBytes) / BytesPerGB,
                totalBytes / BytesPerGB);
        }
        catch { return null; }
    }
}
