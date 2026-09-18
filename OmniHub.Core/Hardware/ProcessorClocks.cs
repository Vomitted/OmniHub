// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;

namespace OmniHub.Core.Hardware;

/// <summary>One logical processor, as Windows reports it.</summary>
/// <param name="Number">The logical processor index.</param>
/// <param name="CurrentMhz">What it is running at now.</param>
/// <param name="MaxMhz">Its nominal maximum. Not the turbo ceiling.</param>
/// <param name="LimitMhz">The ceiling currently in force, which a power policy can lower.</param>
public readonly record struct CoreClock(int Number, int CurrentMhz, int MaxMhz, int LimitMhz);

/// <summary>
/// Per-logical-processor clocks, from Windows' own power information.
///
/// The application shows one CPU frequency today, from WMI's Win32_Processor.CurrentClockSpeed,
/// and that field's own source comment warns it "is not guaranteed to track the CPU's real-time
/// dynamic (Turbo Boost) frequency". So the single number on screen is both an average across
/// twelve logical processors and of uncertain freshness.
///
/// CallNtPowerInformation returns the real per-processor figures with no driver, no WMI query
/// and no elevation -- it is the same source Task Manager's per-core view uses. Twelve honest
/// numbers in place of one doubtful one, for a P/Invoke and a struct.
///
/// What it adds beyond detail: LimitMhz is the ceiling currently in force. When a power policy
/// or thermal event caps the processor, that value drops below MaxMhz and says so -- which is a
/// constraint the machine is genuinely under and one nothing in this application could see.
/// </summary>
public static class ProcessorClocks
{
    /// <summary>ProcessorInformation. The information class this call takes.</summary>
    private const int ProcessorInformation = 11;

    private const uint StatusSuccess = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        int informationLevel, IntPtr inputBuffer, uint inputBufferSize,
        IntPtr outputBuffer, uint outputBufferSize);

    /// <summary>
    /// Every logical processor, or an empty list when the call fails.
    ///
    /// Empty rather than a fabricated entry per core: this is a reading like any other, and a
    /// row of zeroes would be exactly the invented telemetry the project refuses.
    /// </summary>
    public static IReadOnlyList<CoreClock> Read()
    {
        int count = Environment.ProcessorCount;
        int size = Marshal.SizeOf<ProcessorPowerInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size * count);

        try
        {
            if (CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, buffer, (uint)(size * count))
                != StatusSuccess)
                return Array.Empty<CoreClock>();

            var clocks = new List<CoreClock>(count);

            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<ProcessorPowerInformation>(buffer + i * size);

                // A zero maximum means the entry was not filled in. Reporting it as a processor
                // running at nothing would be worse than leaving it out.
                if (info.MaxMhz == 0) continue;

                clocks.Add(new CoreClock(
                    (int)info.Number, (int)info.CurrentMhz, (int)info.MaxMhz, (int)info.MhzLimit));
            }

            return clocks;
        }
        catch
        {
            return Array.Empty<CoreClock>();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// The fastest logical processor right now, which is the figure a single readout should show.
    ///
    /// Maximum rather than mean. A lightly threaded load leaves most cores parked, so an average
    /// across twelve of them reports a low number for a processor that is in fact boosting hard
    /// on one -- which is the opposite of what somebody watching a clock wants to know.
    /// </summary>
    public static int? PeakMhz(IReadOnlyList<CoreClock> clocks) =>
        clocks.Count == 0 ? null : clocks.Max(c => c.CurrentMhz);

    /// <summary>
    /// True when Windows is holding the processor below its nominal maximum.
    ///
    /// A constraint the machine is genuinely under, reported by the platform rather than
    /// inferred from a clock that happens to look low.
    /// </summary>
    public static bool IsCapped(IReadOnlyList<CoreClock> clocks) =>
        clocks.Count > 0 && clocks.Any(c => c.LimitMhz > 0 && c.LimitMhz < c.MaxMhz);
}
