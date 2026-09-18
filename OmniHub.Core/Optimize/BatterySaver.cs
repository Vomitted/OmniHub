// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Management;

namespace OmniHub.Core.Optimize;

/// <summary>Live battery state, straight from ACPI. Milliwatts and milliwatt-hours as reported.</summary>
public sealed record BatteryDraw(
    bool OnAc,
    bool Charging,
    int DischargeMilliwatts,
    int ChargeMilliwatts,
    uint RemainingCapacityMWh,
    uint MilliVolts);

/// <summary>
/// Live battery draw, runtime estimate and display brightness.
///
/// Every field here is measured, not modelled. DischargeRate and RemainingCapacity come
/// straight from the ACPI battery via root\wmi BatteryStatus, in milliwatts and
/// milliwatt-hours, so the runtime estimate is a division of two real numbers rather than
/// the invented "time remaining" most tools show. On AC the discharge rate is genuinely 0,
/// and the estimate is reported as unavailable instead of guessed at.
/// </summary>
public static class BatterySaver
{
    /// <summary>Live draw. Null when the battery cannot be read at all.</summary>
    public static BatteryDraw? ReadDraw()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM BatteryStatus");
            foreach (ManagementObject mo in searcher.Get())
            {
                using var _ = mo;
                return new BatteryDraw(
                    OnAc: Convert.ToBoolean(mo["PowerOnline"] ?? false),
                    Charging: Convert.ToBoolean(mo["Charging"] ?? false),
                    DischargeMilliwatts: Convert.ToInt32(mo["DischargeRate"] ?? 0),
                    ChargeMilliwatts: Convert.ToInt32(mo["ChargeRate"] ?? 0),
                    RemainingCapacityMWh: Convert.ToUInt32(mo["RemainingCapacity"] ?? 0u),
                    MilliVolts: Convert.ToUInt32(mo["Voltage"] ?? 0u));
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Estimated runtime at the current draw, or null when there is nothing to divide by.
    /// Deliberately not smoothed or padded: it is capacity over draw at this instant, so it
    /// swings when load swings. An estimate that looks stable while the truth is moving is
    /// worse than one that visibly reflects what the machine is doing. Absurd results (a
    /// near-zero draw implying days) are returned as null rather than printed.
    /// </summary>
    public static TimeSpan? EstimateRuntime(BatteryDraw draw)
    {
        if (draw.OnAc || draw.DischargeMilliwatts <= 0 || draw.RemainingCapacityMWh == 0) return null;
        double hours = draw.RemainingCapacityMWh / (double)draw.DischargeMilliwatts;
        return hours > 48 ? null : TimeSpan.FromHours(hours);
    }

    // ---------- display brightness ----------
    // The panel is usually the single largest consumer on a laptop, which makes this the
    // most effective battery lever available without a driver.

    public static int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject mo in searcher.Get())
            {
                using var _ = mo;
                return Convert.ToInt32(mo["CurrentBrightness"] ?? 0);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Sets panel brightness, 0-100. Applies to every monitor exposing the WMI method;
    /// external displays generally do not, and are skipped rather than counted as failures.
    /// </summary>
    public static TuningResult SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        int applied = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject mo in searcher.Get())
            {
                using var _ = mo;
                try
                {
                    // First argument is a timeout in seconds the change may take; on an
                    // integrated panel the call is effectively synchronous.
                    mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                    applied++;
                }
                catch { /* monitor has no software brightness control; skip it */ }
            }
        }
        catch (Exception ex)
        {
            return new TuningResult(false, $"Brightness control unavailable: {ex.Message}");
        }

        return applied == 0
            ? new TuningResult(false, "No display accepted a brightness change.")
            : new TuningResult(true, $"Brightness set to {percent}% on {applied} display(s).");
    }

    /// <summary>
    /// One-click battery saver: dims the panel and hands the rest to the Eco profile.
    ///
    /// Brightness is done here rather than inside PerformanceProfile because it is the only
    /// lever in this app the user will *see* change. A profile silently dimming the screen
    /// would read as a fault, so it lives on the battery card next to the slider that shows
    /// the value, and the previous level is returned so the caller can offer it back.
    /// </summary>
    public static (TuningResult Result, int? PreviousBrightness) ApplyBatterySaver(int targetBrightness = 40)
    {
        int? previous = GetBrightness();
        var result = SetBrightness(targetBrightness);
        return (result, previous);
    }

    /// <summary>Formats live draw as watts, or says plainly when there is nothing to report.</summary>
    public static string DescribeDraw(BatteryDraw? draw)
    {
        if (draw is null) return "Battery not readable";
        if (draw.Charging && draw.ChargeMilliwatts > 0) return $"Charging at {draw.ChargeMilliwatts / 1000.0:0.0} W";
        if (draw.OnAc) return "On AC (no discharge)";
        if (draw.DischargeMilliwatts <= 0) return "On battery (draw not reported)";
        return $"Drawing {draw.DischargeMilliwatts / 1000.0:0.0} W";
    }
}
