using System.Management;

namespace OmniHub.Core.Hardware;

public sealed record SystemPerf(double CpuClockGHz, double CpuLoadPercent, double MemoryUsedGB, double MemoryTotalGB);

/// <summary>
/// Real CPU/RAM telemetry via standard Windows WMI classes -- same "documented,
/// not reverse-engineered" principle as the ACPI thermal-zone and battery readers.
/// No GPU load/VRAM/clock here deliberately: that needs vendor-specific APIs
/// (NVML for NVIDIA) this app doesn't have wired up, and a plausible-looking
/// fabricated number is worse than admitting the data isn't available.
///
/// CpuClockGHz caveat: Win32_Processor.CurrentClockSpeed is not guaranteed to
/// track the CPU's real-time dynamic (Turbo Boost) frequency -- on many
/// systems/drivers it reports a static nominal value instead, and there is no
/// portable WMI counter that reliably distinguishes the two. This app reports
/// whatever the OS itself returns for this field, unmodified; it is not
/// independently cross-checked against a second source the way the temperature
/// reading is against ACPI thermal zones.
/// </summary>
public static class SystemPerfReader
{
    public static SystemPerf? Read()
    {
        try
        {
            // Named columns, not SELECT *.
            //
            // This runs on the hardware poll's back, once per tick for as long as the dashboard
            // is open -- measured at 1.29 WMI queries a second. SELECT * makes the provider
            // populate and marshal a whole instance across the WMI boundary to read one
            // property, and Win32_Processor is a notoriously expensive provider to do that to.
            //
            // Worth knowing, since this codebase has been bitten once already: ModelProfile
            // deliberately uses SELECT * because Win32_ComputerSystemProduct rejects a property
            // list with "Invalid query" on some HP systems. That quirk is specific to that
            // class; these two take a column list normally. If one ever stops doing so, Read
            // returns null and the readouts say unavailable, which is the honest failure.
            double clockMHz = 0, loadPercent = 0;
            using (var searcher = new ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'"))
                foreach (ManagementObject mo in searcher.Get())
                    using (mo) loadPercent = Convert.ToDouble(mo["PercentProcessorTime"] ?? 0.0);

            using (var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed FROM Win32_Processor"))
                foreach (ManagementObject mo in searcher.Get())
                    using (mo) clockMHz = Convert.ToDouble(mo["CurrentClockSpeed"] ?? 0.0);

            // Memory comes from GlobalMemoryStatusEx rather than a third WMI query. It is the
            // same pair of numbers Win32_OperatingSystem reports, from the same kernel counters,
            // through a P/Invoke that costs effectively nothing -- and MemoryTools already wraps
            // it for the System tab, so this removes a round trip per tick and adds no new code.
            ulong totalBytes = Optimize.MemoryTools.TotalPhysicalBytes();
            ulong availableBytes = Optimize.MemoryTools.AvailablePhysicalBytes();

            // Both return 0 when the call fails. A zero total would make every derived figure
            // nonsense, so treat it as a failed read rather than report a machine with no RAM.
            if (totalBytes == 0) return null;

            const double BytesPerGB = 1024.0 * 1024.0 * 1024.0;
            double totalGB = totalBytes / BytesPerGB;
            double usedGB = (totalBytes - availableBytes) / BytesPerGB;

            return new SystemPerf(clockMHz / 1000.0, loadPercent, usedGB, totalGB);
        }
        catch { return null; }
    }
}
