// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The Dashboard's three presets as the writes they make, shared by every look's Dashboard. Moved out
/// of DashboardView unchanged when the Frosted and Dot matrix Dashboards needed the same three buttons:
/// two copies of a hardware path is how one of them ends up doing something the other does not.
///
/// The GPU half is the BIOS's own TGP and Dynamic Boost flags. Nothing here touches the Windows power
/// plan's processor settings.
/// </summary>
internal static class PresetActions
{
    /// <summary>Each preset, and what it sends in full -- the three buttons are not a mood.</summary>
    public static readonly (string Name, GpuPowerLevel Gpu, FanControlMode Fans, string Note)[] All =
    {
        ("Eco", GpuPowerLevel.Eco, FanControlMode.Auto, "Custom TGP off, Dynamic Boost off, fans on the curve."),
        ("Balanced", GpuPowerLevel.Balanced, FanControlMode.Auto, "Custom TGP on, Dynamic Boost off, fans on the curve."),
        ("Performance", GpuPowerLevel.Performance, FanControlMode.Max, "Custom TGP on, Dynamic Boost on, fans held at maximum."),
    };

    /// <summary>
    /// Sends a preset. Every call in it (SetPowerPreset, SetMaxFan, Stop's RestoreAutomaticControl) is
    /// a synchronous BIOS/WMI call, so the whole chain runs off the UI thread -- called from a button
    /// click it blocked the window for the length of the chain, which read as freezing.
    /// </summary>
    public static Task Apply(HardwareContext ctx, FanService service, AppSettings settings, GpuPowerLevel gpu, FanControlMode fans) =>
        Task.Run(() =>
        {
            try { ctx.Gpu.SetPowerPreset(gpu); } catch { }
            try
            {
                if (fans == FanControlMode.Auto) { ctx.System.SetMaxFan(false); service.Start(); }
                else if (fans == FanControlMode.Max) { if (service.IsRunning) service.Stop(); ctx.System.SetMaxFan(true); }
                settings.FanControlMode = fans;
                settings.Save();
            }
            catch { }
        });
}
