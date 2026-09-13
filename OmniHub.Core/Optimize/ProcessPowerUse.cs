using System.Diagnostics;
using System.Management;

namespace OmniHub.Core.Optimize;

/// <summary>What one process is costing, as far as it can be attributed.</summary>
/// <param name="Pid">Process id.</param>
/// <param name="Name">Executable name without extension.</param>
/// <param name="GpuPercent">Sum of the GPU engines that do real work, capped at 100.</param>
/// <param name="TimerPercent">The Timer engine separately. See the note on <see cref="ProcessPowerUse"/>.</param>
/// <param name="CpuPercent">Share of the whole machine, so 100 means every core saturated.</param>
public sealed record ProcessDrain(int Pid, string Name, double GpuPercent, double TimerPercent, double CpuPercent)
{
    /// <summary>Rough ordering key. Not watts, and deliberately not presented as watts.</summary>
    public double Weight => GpuPercent + CpuPercent;
}

/// <summary>
/// Attributes GPU and CPU activity to individual processes, so "what is draining my battery" is a
/// readout rather than an investigation.
///
/// This exists because answering that question by hand took a dozen commands and turned up
/// something nobody would have guessed: the largest GPU consumer on this machine at the time was
/// the chat client being used to investigate it. That is not a footnote, it is the normal case.
/// What drains a laptop is rarely what anyone suspects, and it is almost never visible at the
/// moment someone thinks to look.
///
/// WHAT THIS IS AND IS NOT. It reports utilisation, not watts. Nothing on this platform exposes
/// per-process energy with any accuracy, and multiplying a percentage by a package power figure
/// would be inventing a number with a unit on it, which is exactly what this project refuses to
/// do everywhere else. Utilisation ranks the candidates honestly; it does not price them.
///
/// The Timer engine is reported SEPARATELY rather than folded into the GPU figure. It routinely
/// sits near 100% for a process doing almost no rendering, because it measures a scheduling queue
/// rather than shaded pixels, and summing it in produces a headline number that is alarming and
/// wrong. The 3D, Compute and video engines are the ones whose occupancy tracks power.
/// </summary>
public static class ProcessPowerUse
{
    /// <summary>
    /// Samples both processors over a window.
    ///
    /// A window is required rather than optional: CPU usage is the difference between two readings
    /// of a cumulative counter, and a single reading yields the process's whole-lifetime average,
    /// which for a long-running application is close to meaningless.
    /// </summary>
    public static async Task<List<ProcessDrain>> SampleAsync(TimeSpan window, CancellationToken ct = default)
    {
        var first = CpuTimes();
        var sw = Stopwatch.StartNew();

        try { await Task.Delay(window, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new List<ProcessDrain>(); }

        sw.Stop();
        var second = CpuTimes();
        var gpu = GpuByPid();

        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        int cores = Math.Max(1, Environment.ProcessorCount);

        var drains = new List<ProcessDrain>();
        foreach (var (pid, after) in second)
        {
            if (!first.TryGetValue(pid, out var before)) continue;   // started mid-window

            double cpu = elapsedMs <= 0 ? 0
                : (after.Cpu - before.Cpu).TotalMilliseconds / elapsedMs * 100.0 / cores;

            gpu.TryGetValue(pid, out var g);

            // Anything under a tenth of a percent on everything is noise, and would bury the rows
            // that matter under two hundred idle services.
            if (cpu < 0.1 && g.Work < 0.1 && g.Timer < 0.1) continue;

            drains.Add(new ProcessDrain(pid, after.Name,
                Math.Min(100, Math.Round(g.Work, 1)),
                Math.Min(100, Math.Round(g.Timer, 1)),
                Math.Round(Math.Max(0, cpu), 1)));
        }

        drains.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        return drains;
    }

    private static Dictionary<int, (string Name, TimeSpan Cpu)> CpuTimes()
    {
        var map = new Dictionary<int, (string, TimeSpan)>();

        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = (p.ProcessName, p.TotalProcessorTime); }

            // Protected and already-exited processes throw here. Both are ordinary: this runs
            // against every process on the machine, and skipping one is far better than failing
            // the whole sample because of it.
            catch (Exception) { }
            finally { p.Dispose(); }
        }

        return map;
    }

    /// <summary>
    /// GPU engine occupancy per process, split into real work and the Timer queue.
    ///
    /// Instance names look like pid_12912_luid_0x00000000_0x000132D1_phys_0_eng_4_engtype_Timer 0,
    /// so the pid and the engine type both come out of the name. A process appears once per engine
    /// per adapter, which is why these are summed rather than taken singly.
    /// </summary>
    private static Dictionary<int, (double Work, double Timer)> GpuByPid()
    {
        var map = new Dictionary<int, (double Work, double Timer)>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    string name = mo["Name"]?.ToString() ?? "";
                    if (!TryParsePid(name, out int pid)) continue;

                    double value = Convert.ToDouble(mo["UtilizationPercentage"] ?? 0);
                    if (value <= 0) continue;

                    bool isTimer = name.Contains("engtype_Timer", StringComparison.OrdinalIgnoreCase);

                    map.TryGetValue(pid, out var current);
                    map[pid] = isTimer
                        ? (current.Work, current.Timer + value)
                        : (current.Work + value, current.Timer);
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        return map;
    }

    /// <summary>Pulls the pid out of a GPU engine counter instance name.</summary>
    internal static bool TryParsePid(string instanceName, out int pid)
    {
        pid = 0;
        if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;

        int start = 4;
        int end = instanceName.IndexOf('_', start);
        if (end <= start) return false;

        return int.TryParse(instanceName.AsSpan(start, end - start), out pid);
    }
}
