// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Management;

namespace OmniHub.Core.Hardware;

public sealed record BatteryHealth(int ChargePercent, string Status, uint DesignCapacityMWh, uint FullChargeCapacityMWh, uint CycleCount);

/// <summary>
/// Reads real battery data from the standard Windows ACPI battery WMI classes --
/// the same pattern used for temperature (see SystemController.GetTemperatureC):
/// documented, non-reverse-engineered classes, queried with "SELECT *" rather than
/// a property list (Win32_ComputerSystemProduct threw "Invalid query" on a named-
/// property SELECT on this exact hardware, so that pattern is avoided everywhere
/// now as a precaution). CycleCount isn't exposed on every system, so it's read
/// independently and left at 0 rather than failing the whole read.
/// </summary>
public static class BatteryInfoReader
{
    public static BatteryHealth? Read()
    {
        try
        {
            int chargePercent = 0;
            string status = "Unknown";
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Battery"))
            {
                foreach (ManagementObject mo in searcher.Get())
                {
                    using var _ = mo;
                    chargePercent = Convert.ToInt32(mo["EstimatedChargeRemaining"] ?? 0);
                    status = DescribeStatus(Convert.ToInt32(mo["BatteryStatus"] ?? 0));
                }
            }

            uint designCapacity = 0, fullChargeCapacity = 0;
            using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryStaticData"))
                foreach (ManagementObject mo in searcher.Get())
                    using (mo) designCapacity = Convert.ToUInt32(mo["DesignedCapacity"] ?? 0u);

            using (var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryFullChargedCapacity"))
                foreach (ManagementObject mo in searcher.Get())
                    using (mo) fullChargeCapacity = Convert.ToUInt32(mo["FullChargedCapacity"] ?? 0u);

            uint cycleCount = 0;
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryCycleCount");
                foreach (ManagementObject mo in searcher.Get())
                    using (mo) cycleCount = Convert.ToUInt32(mo["CycleCount"] ?? 0u);
            }
            catch { /* not exposed on every system -- leave at 0 rather than fail the whole read */ }

            if (designCapacity == 0 && fullChargeCapacity == 0 && chargePercent == 0) return null;
            return new BatteryHealth(chargePercent, status, designCapacity, fullChargeCapacity, cycleCount);
        }
        catch { return null; }
    }

    private static string DescribeStatus(int code) => code switch
    {
        1 => "Discharging",
        2 => "On AC",
        3 => "Fully Charged",
        4 => "Low",
        5 => "Critical",
        6 => "Charging",
        7 => "Charging (High)",
        8 => "Charging (Low)",
        9 => "Charging (Critical)",
        11 => "Partially Charged",
        _ => "Unknown",
    };
}
