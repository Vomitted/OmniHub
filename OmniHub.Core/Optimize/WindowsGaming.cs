// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using Microsoft.Win32;

namespace OmniHub.Core.Optimize;

/// <summary>One toggle's current state. A null <see cref="Enabled"/> means the value is
/// absent, which for these settings means "Windows default", not "off".</summary>
public sealed record GamingToggle(string Id, string Name, string Description, bool? Enabled, bool RequiresReboot);

/// <summary>
/// Three documented Windows settings that genuinely affect game performance. All are ordinary
/// registry values the Settings app writes too -- nothing here is a reverse-engineered trick,
/// and every one can be changed back from Windows' own UI.
///
/// Two rules this class holds to:
///
///   * A missing value means "Windows default", which is NOT the same as "off". Reporting a
///     default as disabled would let the UI claim credit for a change it never made, and
///     would make "restore" ambiguous. Absent is reported as null throughout.
///
///   * Nothing is written without reading back afterwards. On a policy-managed machine a
///     write can succeed against a virtualised key while the effective value never changes,
///     and a toggle that flips in the UI without flipping on the machine is worse than one
///     that refuses.
/// </summary>
public static class WindowsGaming
{
    // HKLM: machine-wide, needs Administrator, and only takes effect after a reboot because
    // the graphics scheduler is set up during driver initialisation.
    private const string HagsKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string HagsValue = "HwSchMode"; // 2 = enabled, 1 = disabled

    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameModeValue = "AutoGameModeEnabled";

    private const string GameDvrKey = @"System\GameConfigStore";
    private const string GameDvrValue = "GameDVR_Enabled";

    public const string IdHags = "hags";
    public const string IdGameMode = "gamemode";
    public const string IdGameDvr = "gamedvr";

    public static IReadOnlyList<GamingToggle> ReadAll() => new[]
    {
        new GamingToggle(IdHags, "Hardware-accelerated GPU scheduling",
            "Lets the GPU manage its own memory scheduling. Can reduce latency; the effect varies by driver and title. Needs a restart.",
            ReadHags(), RequiresReboot: true),

        new GamingToggle(IdGameMode, "Game Mode",
            "Windows deprioritises background work while a game is in the foreground.",
            ReadDword(GameBarKey, GameModeValue), RequiresReboot: false),

        new GamingToggle(IdGameDvr, "Background game recording",
            "Game DVR records continuously in the background. Turning it off frees GPU and disk that nothing is watching.",
            ReadDword(GameDvrKey, GameDvrValue), RequiresReboot: false),
    };

    public static TuningResult Set(string id, bool enable)
    {
        try
        {
            return id switch
            {
                IdHags => WriteHags(enable),
                IdGameMode => WriteUserDword(GameBarKey, GameModeValue, enable, "Game Mode"),
                IdGameDvr => WriteUserDword(GameDvrKey, GameDvrValue, enable, "Background game recording"),
                _ => new TuningResult(false, $"Unknown setting '{id}'."),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new TuningResult(false, "Access denied writing that setting; Administrator rights are required.");
        }
        catch (Exception ex)
        {
            return new TuningResult(false, ex.Message);
        }
    }

    // ---------- HAGS ----------

    private static bool? ReadHags()
    {
        // HwSchMode uses 2/1 rather than 1/0, so it cannot share the generic DWORD reader.
        using var key = Registry.LocalMachine.OpenSubKey(HagsKey);
        if (key?.GetValue(HagsValue) is not int raw) return null;
        return raw == 2;
    }

    private static TuningResult WriteHags(bool enable)
    {
        using var key = Registry.LocalMachine.OpenSubKey(HagsKey, writable: true);
        if (key is null) return new TuningResult(false, "The graphics driver key is not present on this system.");

        key.SetValue(HagsValue, enable ? 2 : 1, RegistryValueKind.DWord);

        if (ReadHags() != enable)
            return new TuningResult(false, "The value did not stick; it may be managed by policy.");

        return new TuningResult(true,
            $"Hardware-accelerated GPU scheduling {(enable ? "enabled" : "disabled")}. Takes effect after a restart.");
    }

    // ---------- per-user DWORDs ----------

    private static bool? ReadDword(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        if (key?.GetValue(name) is not int raw) return null;
        return raw != 0;
    }

    private static TuningResult WriteUserDword(string path, string name, bool enable, string label)
    {
        // CreateSubKey rather than OpenSubKey: these keys legitimately do not exist until
        // something writes them, and treating that as a failure would make the toggle look
        // broken on a clean install.
        using var key = Registry.CurrentUser.CreateSubKey(path);
        if (key is null) return new TuningResult(false, $"Could not open the {label} key.");

        key.SetValue(name, enable ? 1 : 0, RegistryValueKind.DWord);

        if (ReadDword(path, name) != enable)
            return new TuningResult(false, $"{label} did not change; it may be managed by policy.");

        return new TuningResult(true, $"{label} {(enable ? "enabled" : "disabled")}.");
    }
}
