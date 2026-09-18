// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Diagnostics;

namespace OmniHub.Core.Hardware;

/// <summary>
/// What this application costs the machine it is measuring.
///
/// A tool that exists to find what is draining a laptop should be willing to say what it is drawing
/// itself, and on this machine it has been suspected of both drain and hangs more than once. A
/// number on screen settles that far better than an argument about it.
///
/// The processor figure is a share of ONE core, matching how every other CPU figure in this
/// application is expressed and how Task Manager reports a single process. Dividing by the core
/// count instead would report a busy application as four per cent on a twelve-thread part and read
/// as nothing at all.
/// </summary>
public sealed class SelfUsage
{
    private readonly Process _process = Process.GetCurrentProcess();

    private TimeSpan _lastCpu;
    private DateTime _lastAtUtc;
    private bool _seeded;

    /// <summary>
    /// Busy time since this sampler's previous call, as a percentage of one core.
    ///
    /// Null on the first call rather than zero, for the reason CpuLoad gives: processor time is
    /// cumulative since the process started, so one reading describes the whole run rather than
    /// now, and reporting zero would put an idle figure on screen at the moment somebody opened
    /// the panel to find out why theirs was not.
    ///
    /// Per instance, not static. That distinction was learned here the hard way -- a shared
    /// previous sample means each caller consumes the window since whichever caller asked last,
    /// so a second readout on its own timer measures a few milliseconds and reports it as a rate.
    /// </summary>
    public double? CpuPercent()
    {
        var now = DateTime.UtcNow;

        _process.Refresh();
        var cpu = _process.TotalProcessorTime;

        if (!_seeded)
        {
            (_lastCpu, _lastAtUtc, _seeded) = (cpu, now, true);
            return null;
        }

        double? percent = Percent(cpu - _lastCpu, now - _lastAtUtc);
        (_lastCpu, _lastAtUtc) = (cpu, now);

        return percent;
    }

    /// <summary>
    /// The arithmetic, separated so it can be tested without a process to be busy in.
    /// </summary>
    internal static double? Percent(TimeSpan cpuUsed, TimeSpan elapsed)
    {
        // Two reads inside one tick of the clock. Nothing happened that can be divided, and zero
        // per cent would be a claim rather than an absence.
        if (elapsed <= TimeSpan.Zero) return null;

        // A counter that went backwards is not negative work. It should not happen to a monotonic
        // total, and if it does the honest output is no reading.
        if (cpuUsed < TimeSpan.Zero) return null;

        return cpuUsed.TotalMilliseconds / elapsed.TotalMilliseconds * 100.0;
    }

    /// <summary>
    /// Memory this process is holding in RAM, in megabytes.
    ///
    /// The working set rather than the private or committed size, because the question being
    /// answered is what this application is costing the machine right now, and that is the figure
    /// Task Manager shows beside it.
    /// </summary>
    public double MemoryMegabytes()
    {
        _process.Refresh();
        return _process.WorkingSet64 / (1024.0 * 1024.0);
    }
}
