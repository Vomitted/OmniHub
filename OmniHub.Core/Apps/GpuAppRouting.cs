// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using Microsoft.Win32;

namespace OmniHub.Core.Apps;

public enum AppGpuPreference { Auto = 0, PowerSaving = 1, HighPerformance = 2 }

public sealed record AppGpuRoute(string ExecutablePath, AppGpuPreference Preference);

/// <summary>
/// Per-application GPU preference (dGPU vs iGPU) via the same registry mechanism
/// Windows Settings > System > Display > Graphics uses -- HKCU\...\UserGpuPreferences,
/// one value per executable path, formatted "GpuPreference=N;". This is documented
/// Windows behavior, not a reverse-engineered vendor API.
/// </summary>
public static class GpuAppRouting
{
    private const string RegistryPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static IReadOnlyList<AppGpuRoute> GetAll()
    {
        var result = new List<AppGpuRoute>();
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key is null) return result;

        foreach (var valueName in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(valueName)) continue;
            var raw = key.GetValue(valueName) as string ?? "";
            result.Add(new AppGpuRoute(valueName, ParsePreference(raw)));
        }
        return result;
    }

    public static void SetPreference(string executablePath, AppGpuPreference preference)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        var existing = key.GetValue(executablePath) as string ?? "";
        key.SetValue(executablePath, WithPreference(existing, preference), RegistryValueKind.String);
    }

    // Windows' own Advanced Graphics settings can store more than just GpuPreference on
    // this same value (e.g. flip-model overrides), as "Key=Value;" pairs. Replaces just
    // the GpuPreference segment and preserves everything else -- a blind overwrite here
    // would silently destroy any of those other properties the user set outside OmniHub.
    private static string WithPreference(string existing, AppGpuPreference preference)
    {
        const string marker = "GpuPreference=";
        var idx = existing.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return existing + $"GpuPreference={(int)preference};";

        var start = idx + marker.Length;
        var end = existing.IndexOf(';', start);
        var before = existing[..idx];
        var after = end >= 0 ? existing[(end + 1)..] : "";
        return $"{before}GpuPreference={(int)preference};{after}";
    }

    public static void Remove(string executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        key?.DeleteValue(executablePath, throwOnMissingValue: false);
    }

    private static AppGpuPreference ParsePreference(string raw)
    {
        const string marker = "GpuPreference=";
        var idx = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return AppGpuPreference.Auto;
        var start = idx + marker.Length;
        var end = raw.IndexOf(';', start);
        var numStr = end > start ? raw[start..end] : raw[start..];
        return int.TryParse(numStr, out var n) && Enum.IsDefined(typeof(AppGpuPreference), n)
            ? (AppGpuPreference)n : AppGpuPreference.Auto;
    }
}
