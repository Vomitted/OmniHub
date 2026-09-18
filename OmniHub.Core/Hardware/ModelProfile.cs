// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Management;

namespace OmniHub.Core.Hardware;

public sealed record ModelInfo(string Manufacturer, string Product, string BaseboardProduct);

/// <summary>
/// Identifies the running machine so fan-table quirks can eventually be
/// keyed per-model. For now this just reports identity; per-model overrides
/// go in /profiles/*.json as they're discovered via -Probe.
/// </summary>
public static class ModelProfile
{
    public static ModelInfo Detect()
    {
        string manufacturer = "", product = "", baseboard = "";

        // Deliberately "SELECT *" rather than naming columns: on at least some HP
        // systems, WMI's Win32_ComputerSystemProduct provider throws "Invalid query"
        // for a property-list SELECT against that class specifically (reproducible
        // even from PowerShell's own Get-WmiObject, so it's a provider quirk, not a
        // bug here) -- and Win32_ComputerSystemProduct.Manufacturer/.Product are
        // blank on at least the Victus 15 fb2xxx family anyway, where Manufacturer
        // lives on Win32_ComputerSystem instead. Each query is independently
        // try/caught so one flaky WMI class never blocks identifying the machine
        // from the rest, or crashes -Probe entirely.
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                using var _ = mo;
                manufacturer = mo["Manufacturer"]?.ToString() ?? "";
                product = mo["Model"]?.ToString() ?? "";
            }
        }
        catch { /* leave manufacturer/product blank -- identity is best-effort */ }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BaseBoard");
            foreach (ManagementObject mo in searcher.Get())
            {
                using var _ = mo;
                baseboard = mo["Product"]?.ToString() ?? "";
            }
        }
        catch { /* leave baseboard blank -- identity is best-effort */ }

        return new ModelInfo(manufacturer, product, baseboard);
    }
}
