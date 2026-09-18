// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

public sealed record TrimResult(int ProcessesTrimmed, long BytesReleased);

/// <summary>
/// Per-process levers: priority for the app you are actually using, and working-set trimming
/// for the ones you are not.
///
/// Both are genuinely modest, and the UI says so. Windows' scheduler is already good at this;
/// raising a foreground game above Normal helps mainly when something else is competing hard,
/// and trimming a working set does not "free" memory so much as push pages onto the standby
/// list where Windows can reuse them. Neither is the dramatic win that tools in this category
/// like to imply.
///
/// What this deliberately does NOT do: suspend, kill or deprioritise services and background
/// processes wholesale. That is the "game booster" behaviour that breaks audio stacks,
/// anti-cheat and Windows Update, and the damage surfaces hours later somewhere unrelated.
/// </summary>
public static class ProcessTuning
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>The process owning the foreground window, or null if it cannot be resolved.
    /// The caller owns the returned Process and must dispose it.</summary>
    public static Process? ForegroundProcess()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            return Process.GetProcessById((int)pid);
        }
        catch { return null; }
    }

    /// <summary>
    /// Raises the foreground process to AboveNormal. Deliberately not High or RealTime:
    /// those starve the very threads a game depends on -- audio, input, the compositor --
    /// and RealTime can make a machine unresponsive outright.
    /// </summary>
    public static TuningResult PrioritiseForeground()
    {
        using var process = ForegroundProcess();
        if (process is null) return new TuningResult(false, "Could not identify the foreground application.");

        try
        {
            string name = process.ProcessName;
            if (process.PriorityClass is ProcessPriorityClass.AboveNormal or ProcessPriorityClass.High)
                return new TuningResult(true, $"{name} is already above normal priority.");

            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            return new TuningResult(true, $"{name} raised to above-normal priority until it exits.");
        }
        catch (InvalidOperationException) { return new TuningResult(false, "That process exited before it could be changed."); }
        catch (Exception ex) { return new TuningResult(false, $"Could not change priority: {ex.Message}"); }
    }

    /// <summary>
    /// Trims the working set of background processes, excluding the foreground app and this
    /// one. Measured rather than asserted: each process's working set is read before and
    /// after, and only the real difference is summed.
    ///
    /// Protected and system processes reject the call; those are skipped silently because a
    /// refusal there is expected, not an error.
    /// </summary>
    public static TrimResult TrimBackgroundWorkingSets()
    {
        int trimmed = 0;
        long released = 0;

        int foregroundId = 0;
        using (var fg = ForegroundProcess()) { if (fg is not null) foregroundId = fg.Id; }
        int selfId = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // PID 0 and 4 are the Idle and System processes; touching them is meaningless.
                if (process.Id == foregroundId || process.Id == selfId || process.Id <= 4) continue;

                long before = process.WorkingSet64;
                // Below ~20 MB there is nothing worth reclaiming, and the page faults the
                // process takes faulting its data back in cost more than the trim saves.
                if (before < 20L * 1024 * 1024) continue;

                if (!EmptyWorkingSet(process.Handle)) continue;

                process.Refresh();
                long after = process.WorkingSet64;
                if (after < before)
                {
                    released += before - after;
                    trimmed++;
                }
            }
            catch { /* protected process, or it exited mid-iteration */ }
            finally { process.Dispose(); }
        }

        return new TrimResult(trimmed, released);
    }
}
