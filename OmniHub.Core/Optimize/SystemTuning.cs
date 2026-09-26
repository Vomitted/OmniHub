// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

/// <summary>Result of an optimisation action. Never reports success without a measured value.</summary>
public sealed record TuningResult(bool Applied, string Detail);

/// <summary>
/// Windows scheduling and compositor tuning. Two real levers, both documented APIs.
///
/// Everything here queries the system back after writing and reports what the OS actually
/// granted, rather than what was requested. That distinction matters: the timer resolution
/// you ask for is frequently not the one you get (another process may already hold a finer
/// one, and since Windows 10 2004 the granted value is per-process anyway), and an optimiser
/// that prints the requested number is just lying politely.
/// </summary>
public static class SystemTuning
{
    // Resolutions are in 100-nanosecond units: 10000 = 1ms, 5000 = 0.5ms.
    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSetTimerResolution(uint desired, bool set, out uint current);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableMMCSS(bool enable);

    // NTSTATUS is not "0 means success". The NT convention is that any value with the sign
    // bit clear (>= 0) succeeded -- 0 is STATUS_SUCCESS, but positive codes are informational
    // or warning statuses where the call still did its job. Testing != 0 therefore reports a
    // working call as a refusal. Only negative values are genuine errors.
    private static bool NtFailed(int status) => status < 0;

    /// <summary>Timer resolution currently granted to this process, in milliseconds.</summary>
    public static double CurrentTimerResolutionMs()
    {
        if (NtFailed(NtQueryTimerResolution(out _, out _, out uint current))) return double.NaN;
        return current / 10000.0;
    }

    /// <summary>
    /// Coarsest resolution the timer runs at, in milliseconds -- where it sits when nothing has
    /// asked for better, and the far end of the scale the System page draws the timer on.
    /// </summary>
    public static double CoarsestTimerResolutionMs()
    {
        // "minimum" is minimum precision: the largest interval. Same inverted naming as below.
        if (NtFailed(NtQueryTimerResolution(out uint minimum, out _, out _))) return double.NaN;
        return minimum / 10000.0;
    }

    /// <summary>Finest resolution this system will grant, in milliseconds.</summary>
    public static double BestTimerResolutionMs()
    {
        // "maximum" here means maximum precision, i.e. the smallest interval. The naming in
        // the native API reads the opposite way round to what you would expect.
        if (NtFailed(NtQueryTimerResolution(out _, out uint maximum, out _))) return double.NaN;
        return maximum / 10000.0;
    }

    /// <summary>
    /// Requests the finest timer resolution the system supports. Raising it makes the
    /// scheduler wake more often, which smooths frame pacing and input handling and costs
    /// measurable battery. This is a trade, not a free win, and the UI says so.
    /// </summary>
    public static TuningResult ApplyHighResolutionTimer()
    {
        if (NtFailed(NtQueryTimerResolution(out _, out uint maximum, out _)))
            return new TuningResult(false, "Could not query the system timer.");

        if (NtFailed(NtSetTimerResolution(maximum, true, out _)))
            return new TuningResult(false, "The system refused the timer resolution request.");

        double granted = CurrentTimerResolutionMs();
        return double.IsNaN(granted)
            ? new TuningResult(false, "Applied, but the result could not be read back.")
            : new TuningResult(true, $"Timer resolution now {granted:0.###} ms");
    }

    /// <summary>Releases this process's request, letting the system return to its default cadence.</summary>
    public static TuningResult ReleaseHighResolutionTimer()
    {
        // No query first. When SetResolution is FALSE the DesiredResolution argument is
        // ignored outright, so querying for a value we will not use only created a path where
        // a failed query aborted the release and silently left the timer held.
        if (NtFailed(NtSetTimerResolution(0, false, out _)))
            return new TuningResult(false, "The system refused to release the timer request.");

        // Reports the SYSTEM-wide resolution, which is not necessarily back to default: the
        // request is per-process and another process may still be holding a fine one. Worded
        // so that a still-low number does not read as a failed release.
        double now = CurrentTimerResolutionMs();
        return new TuningResult(true, double.IsNaN(now)
            ? "Released."
            : $"Released. System-wide resolution is {now:0.###} ms.");
    }

    /// <summary>
    /// Asks DWM to run composition on the multimedia class scheduler, raising the priority
    /// of the compositor thread. Helps most with stutter under heavy CPU load.
    /// </summary>
    public static TuningResult SetMmcss(bool enable)
    {
        try
        {
            int hr = DwmEnableMMCSS(enable);
            return hr == 0
                ? new TuningResult(true, enable
                    ? "DWM composition raised to MMCSS priority."
                    : "DWM composition returned to normal priority.")
                : new TuningResult(false, $"DWM refused the request (0x{hr:X8}).");
        }
        catch (DllNotFoundException)
        {
            return new TuningResult(false, "dwmapi.dll unavailable on this system.");
        }
    }
}
