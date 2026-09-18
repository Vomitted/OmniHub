// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;
using System.Text;

namespace OmniHub.Core.Hardware;

/// <summary>
/// GPU telemetry from NVML, the library nvidia-smi is itself a front end for.
///
/// nvidia-smi answers every question this application asks and costs a process launch to do it:
/// measured at 58 to 61 ms on this machine, which is why the result has to be cached for three
/// seconds and fetched on a background single-flight. NVML is the same data through a direct
/// call, in microseconds, from a library the driver already installs into System32. No package,
/// no redistributable, no elevation.
///
/// The awkward part is not the call, it is the lifetime.
///
/// nvidia-smi starts, asks, and exits, so it holds nothing. NVML stays initialised, and on a
/// laptop with switchable graphics an initialised NVML can keep the discrete GPU from powering
/// down -- which would turn a battery saving into a battery cost, on the application whose own
/// battery screen is one tab away. So the handle is released the moment the machine goes to
/// battery, and re-acquired if it comes back. The existing rule that the card is not woken on
/// battery at all is unchanged and is what does most of the work.
///
/// Every field is separately optional. A driver can refuse power reporting on one board and
/// answer everything else, and that must render as one unavailable reading rather than as no
/// GPU at all.
/// </summary>
public static class Nvml
{
    private const string Library = "nvml.dll";

    /// <summary>NVML_SUCCESS. Every other code means this particular question went unanswered.</summary>
    private const int Ok = 0;

    /// <summary>NVML_TEMPERATURE_GPU: the die, as opposed to a board sensor.</summary>
    private const uint TemperatureGpu = 0;

    /// <summary>NVML_CLOCK_SM: the shader clock, matching what nvidia-smi's clocks.sm reports.</summary>
    private const uint ClockSm = 1;

    /// <summary>NVML_DEVICE_NAME_V2_BUFFER_SIZE.</summary>
    private const int NameBufferSize = 96;

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport(Library, EntryPoint = "nvmlInit_v2")]
    private static extern int Init();

    [DllImport(Library, EntryPoint = "nvmlShutdown")]
    private static extern int ShutdownNative();

    [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int GetHandle(uint index, out IntPtr device);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetName")]
    private static extern int GetName(IntPtr device, StringBuilder name, uint length);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int GetTemperature(IntPtr device, uint sensor, out uint celsius);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int GetPowerUsage(IntPtr device, out uint milliwatts);

    // The board's own ceiling, in milliwatts. Read so a draw far above it can be refused --
    // see GpuPowerPlausibility for the reading on this machine that made that necessary.
    //
    // Which call to ask took two wrong answers, and both failed silently, which is why the guard
    // now has a test of its own against real hardware:
    //
    //   nvmlDeviceGetPowerManagementLimit      present, returns NOT_SUPPORTED on this card --
    //                                          the same N/A nvidia-smi prints for power.limit
    //   nvmlDeviceGetMaxPowerManagementLimit   not exported by this driver at all, so the
    //                                          P/Invoke threw and was caught
    //   nvmlDeviceGetEnforcedPowerLimit        answers 75000 mW, which is the figure nvidia-smi
    //                                          reports as power.max_limit
    //
    // The constraints call is kept as a fallback because it answered too (min 5000, max 75000) and
    // a driver that drops one export is exactly what this whole class already guards against.
    [DllImport(Library, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    private static extern int GetEnforcedPowerLimit(IntPtr device, out uint milliwatts);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")]
    private static extern int GetPowerLimitConstraints(IntPtr device, out uint minMilliwatts, out uint maxMilliwatts);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetClockInfo")]
    private static extern int GetClockInfo(IntPtr device, uint type, out uint megahertz);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int GetUtilization(IntPtr device, out Utilization utilisation);

    private static readonly object Gate = new();
    private static bool _initialised;

    /// <summary>
    /// Set once the library has been found missing or refused to start.
    ///
    /// Sticky, because the cost worth avoiding is the repeated one: a DllNotFoundException per
    /// refresh on every machine without an NVIDIA driver is an exception thrown and caught twice
    /// a second, forever, to learn something that cannot change while the process is running.
    /// </summary>
    private static bool _unusable;

    /// <summary>Why NVML is not being used, in words fit to show. Null when it is working.</summary>
    public static string? UnavailableReason { get; private set; }

    /// <summary>True when NVML is currently initialised.</summary>
    public static bool IsInUse { get { lock (Gate) return _initialised; } }

    /// <summary>
    /// One sample of the first NVIDIA GPU, or null when NVML cannot answer.
    ///
    /// Null is the caller's signal to fall back, not an assertion that there is no GPU.
    /// </summary>
    public static GpuReading? TryRead()
    {
        lock (Gate)
        {
            if (_unusable) return null;

            try
            {
                if (!_initialised)
                {
                    int started = Init();
                    if (started != Ok)
                    {
                        MarkUnusable($"NVML would not initialise (code {started}).");
                        return null;
                    }

                    _initialised = true;
                }

                if (GetHandle(0, out IntPtr device) != Ok) return null;

                // The name is the one field worth failing the whole read over: without it there
                // is nothing to label the reading with, and an unnamed GPU reading is the sort
                // of thing that gets attributed to the wrong adapter.
                var name = new StringBuilder(NameBufferSize);
                if (GetName(device, name, NameBufferSize) != Ok) return null;

                return new GpuReading(
                    name.ToString(),
                    GetTemperature(device, TemperatureGpu, out uint celsius) == Ok ? celsius : null,

                    // Milliwatts. Dividing by a thousand is the entire unit conversion, and
                    // getting it wrong would report a 45 W board at 45,000 W -- which is exactly
                    // what the cross-check against nvidia-smi in the tests is there to catch.
                    //
                    // Filtered against the board's own ceiling, because this card reports a fixed
                    // 312.13 W in about a quarter of its samples and that is four times a limit it
                    // enforces. The cross-check caught it; it is not a reader bug, since nvidia-smi
                    // reports the same value in the same state.
                    GpuPowerPlausibility.Filter(
                        GetPowerUsage(device, out uint milliwatts) == Ok ? milliwatts / 1000.0 : null,
                        PowerCeilingWatts(device)),

                    GetClockInfo(device, ClockSm, out uint megahertz) == Ok ? (int)megahertz : null,
                    GetUtilization(device, out Utilization used) == Ok ? (int)used.Gpu : null,
                    "NVIDIA",
                    GpuSource.Nvml);
            }
            catch (DllNotFoundException)
            {
                MarkUnusable("No NVIDIA driver on this machine, so nvml.dll is not present.");
                return null;
            }
            catch (EntryPointNotFoundException ex)
            {
                MarkUnusable($"This NVIDIA driver's nvml.dll does not export {ex.Message}.");
                return null;
            }
            catch (Exception ex)
            {
                MarkUnusable($"NVML could not be used ({ex.GetType().Name}).");
                return null;
            }
        }
    }

    /// <summary>
    /// Releases NVML, so nothing here can hold the discrete GPU awake.
    ///
    /// Called when the machine goes to battery. An initialised NVML holds a handle on the driver,
    /// and on switchable graphics that can be enough to keep the card from powering down -- which
    /// would make this optimisation cost battery life on a laptop, for a reading the application
    /// deliberately does not take on battery anyway.
    ///
    /// Safe to call when nothing is initialised, and leaves the library usable afterwards: this
    /// is a release, not the sticky refusal that a missing driver produces.
    /// </summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (!_initialised) return;

            try { ShutdownNative(); } catch { }
            _initialised = false;
        }
    }

    private static void MarkUnusable(string reason)
    {
        _unusable = true;
        _initialised = false;
        UnavailableReason = reason;
    }

    /// <summary>
    /// The board's power ceiling in watts, or null when it cannot be read.
    ///
    /// Cached: it is a property of the card rather than of the moment, and it is consulted on
    /// every reading. A card that will not report it simply has no ceiling to check against, which
    /// GpuPowerPlausibility treats as "believe the reading" rather than as a failure.
    /// </summary>
    private static double? _ceilingWatts;
    private static bool _ceilingRead;

    private static double? PowerCeilingWatts(IntPtr device)
    {
        if (_ceilingRead) return _ceilingWatts;

        _ceilingRead = true;

        try
        {
            if (GetEnforcedPowerLimit(device, out uint milliwatts) == Ok && milliwatts > 0)
                return _ceilingWatts = milliwatts / 1000.0;
        }
        catch (EntryPointNotFoundException) { }

        try
        {
            if (GetPowerLimitConstraints(device, out uint _, out uint maxMilliwatts) == Ok && maxMilliwatts > 0)
                return _ceilingWatts = maxMilliwatts / 1000.0;
        }
        catch (EntryPointNotFoundException) { }

        return _ceilingWatts = null;
    }
}
