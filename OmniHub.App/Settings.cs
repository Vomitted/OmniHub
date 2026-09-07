using System.IO;
using System.Text.Json;
using OmniHub.Core.Fan;
using OmniHub.Core.Optimize;

namespace OmniHub.App;

public enum FanControlMode { Auto, BiosDefault, Max }
public enum CloseBehavior { MinimizeToTray, Exit }

/// <summary>Which screen corner the telemetry overlay anchors to.</summary>
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>How the Tuning tab drives the processor.</summary>
public enum TuningMode
{
    /// <summary>The sliders are in charge; nothing changes unless Apply is pressed.</summary>
    Manual,

    /// <summary>A target temperature is held, and the controller adjusts to keep it.</summary>
    Adaptive,
}

/// <summary>
/// Apply a tuning profile whenever a given process is running.
///
/// Matched on process name without extension, case-insensitively, because that is what a
/// person can actually find in Task Manager -- a full path breaks the moment a launcher moves
/// the game, and a window title changes with the loading screen.
/// </summary>
public sealed record GameRule(string ProcessName, string ProfileName);

/// <summary>Small local settings file so curve edits and the chosen fan mode survive a restart.
/// Lives at %AppData%\OmniHub\settings.json -- plain JSON, no telemetry, nothing phoned home.</summary>
public sealed class AppSettings
{
    public FanControlMode FanControlMode { get; set; } = FanControlMode.Auto;
    public double FloorTempC { get; set; } = 55.0;
    public byte FloorLevelPercent { get; set; } = 15;
    public List<CurvePoint> CurvePoints { get; set; } = FanCurve.CreateDefault().Points.ToList();
    public bool StartMinimizedToTray { get; set; } = false;
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.MinimizeToTray;

    /// <summary>
    /// Seconds of predictive lead for the fan curve (see FanService.PredictiveLeadSeconds).
    /// Defaults to 0 so an existing install behaves exactly as before until the user opts in.
    /// </summary>
    public double PredictiveLeadSeconds { get; set; } = 0;

    /// <summary>Writes a rolling temperature/fan/throttle CSV under %AppData%\OmniHub\logs.
    /// Off by default: it is a diagnostic aid, not something to leave running permanently.</summary>
    public bool ThermalLogging { get; set; } = false;

    /// <summary>Active colour palette id, matching a ThemeManager.All entry (e.g. "OledBlack").
    /// An unknown value falls back to the default rather than failing to start.</summary>
    public string ThemeName { get; set; } = "OledBlack";

    /// <summary>Request the finest system timer resolution. Neither of these persists across a
    /// reboot on its own -- they are re-applied at startup, which is why they are stored.</summary>
    public bool HighResolutionTimer { get; set; } = false;

    /// <summary>Run DWM composition on the multimedia class scheduler.</summary>
    public bool DwmMmcss { get; set; } = false;

    /// <summary>Tuning profile applied when the charger is connected. Null means leave alone.</summary>
    public string? AcProfileName { get; set; }

    /// <summary>Tuning profile applied when running on battery. Null means leave alone.</summary>
    public string? DcProfileName { get; set; }

    /// <summary>Whether to apply those profiles automatically as the power source changes.</summary>
    public bool AutoSwitchProfiles { get; set; } = false;

    /// <summary>
    /// Whether OmniHub switches the Windows power scheme when the charger goes in or out.
    ///
    /// Off by default, and separate from <see cref="AutoSwitchProfiles"/>: that one moves
    /// firmware wattage caps, this one moves OS power policy. Sharing a trigger does not make
    /// them the same decision, and someone may well want one without the other.
    /// </summary>
    public bool AutoPowerPlan { get; set; } = false;

    /// <summary>Scheme applied on mains. Null until the plans have been created.</summary>
    public Guid? AcPlanId { get; set; }

    /// <summary>Scheme applied on battery. Null until the plans have been created.</summary>
    public Guid? DcPlanId { get; set; }

    /// <summary>Whether the always-on-top telemetry overlay is showing.</summary>
    public bool OverlayEnabled { get; set; } = false;

    /// <summary>Which screen corner the overlay sits in.</summary>
    public OverlayCorner OverlayCorner { get; set; } = OverlayCorner.TopRight;

    /// <summary>
    /// Overlay opacity, 0.2 to 1.0. Clamped on use rather than trusted: this is a
    /// hand-editable file, and an opacity of 0 is an overlay you cannot find to fix.
    /// </summary>
    public double OverlayOpacity { get; set; } = 0.88;

    /// <summary>
    /// Which metrics the overlay shows, in order. Keys are matched against
    /// OverlayWindow.AvailableMetrics; unknown keys are ignored rather than throwing, so a
    /// settings file written by a newer build still loads.
    /// </summary>
    public List<string> OverlayMetrics { get; set; } = new() { "cpu", "gpu", "fan", "pkg" };

    /// <summary>User-defined tuning profiles, saved from the Tuning tab's sliders.</summary>
    public List<AmdTuningProfile> CustomProfiles { get; set; } = new();

    /// <summary>Per-process profile rules, applied while a matching process is running.</summary>
    public List<GameRule> GameRules { get; set; } = new();

    /// <summary>Tuning profile applied once at launch. Null means apply nothing.</summary>
    public string? StartupProfileName { get; set; }

    /// <summary>Whether adaptive mode starts itself at launch.</summary>
    public bool StartupAdaptive { get; set; } = false;

    /// <summary>
    /// Enable HP's Custom TGP and PPAB (Dynamic Boost) for the discrete GPU, and re-apply at
    /// launch.
    ///
    /// Measured: both ship OFF, which caps an RTX 4060 at 60 W against a 75 W card ceiling and
    /// holds it around 2190 of 3105 MHz with nvidia-smi reporting sw_power_cap active. This is
    /// the one power control on this machine that HP's firmware actually honours, so it is the
    /// only real answer to GPU-side throttling while gaming.
    /// </summary>
    public bool GpuMaxPower { get; set; } = false;

    /// <summary>
    /// Thermal limit to enforce, applied at launch AFTER the startup profile so an explicit
    /// choice outlives whatever a preset happens to carry. Null leaves the profile's value.
    ///
    /// This one gets its own setting because it is the only SMU knob measured to actually take
    /// effect on this platform: capping it at 75C held the die at exactly 75.00C under load,
    /// and raising the cap let it climb. A preset silently overwriting it -- Quiet carries 78C
    /// -- looks exactly like the temperature sensor being broken.
    /// </summary>
    public int? ThermalLimitC { get; set; }

    /// <summary>Whether the Tuning tab is driving manually or holding a target.</summary>
    public TuningMode TuningMode { get; set; } = TuningMode.Manual;

    /// <summary>Die temperature adaptive mode steers towards.</summary>
    public int AdaptiveTargetTempC { get; set; } = 85;

    /// <summary>Lowest sustained limit adaptive mode may command, watts.</summary>
    public int AdaptiveMinWatts { get; set; } = 15;

    /// <summary>Highest sustained limit adaptive mode may command, watts.</summary>
    public int AdaptiveMaxWatts { get; set; } = 54;

    /// <summary>
    /// Master switch for auto eco. Its triggers below only mean anything while this is on.
    ///
    /// A real stored flag rather than "on if any trigger is set", so turning the section off
    /// and back on does not silently forget which triggers were chosen.
    /// </summary>
    public bool AutoEcoEnabled { get; set; } = false;

    /// <summary>Master switch for per-game profiles, for the same reason: the rule list
    /// survives switching the feature off.</summary>
    public bool GameRulesEnabled { get; set; } = true;

    /// <summary>Engage eco automatically while running on battery.</summary>
    public bool AutoEcoOnBattery { get; set; } = false;

    /// <summary>Engage eco automatically after a stretch with no keyboard or mouse input.</summary>
    public bool AutoEcoOnIdle { get; set; } = false;

    /// <summary>How many minutes of no input before idle eco engages.</summary>
    public int AutoEcoIdleMinutes { get; set; } = 5;

    /// <summary>Refresh rate eco drops the panel to. 0 leaves the display alone.</summary>
    public int AutoEcoRefreshHz { get; set; } = 60;

    /// <summary>Tuning profile eco applies. Null applies none.</summary>
    public string? AutoEcoProfileName { get; set; } = "Eco";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // Corrupt or unreadable settings file -- fall back to defaults rather than crash startup.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence -- a failed save shouldn't block using the app.
        }
    }

    public FanCurve BuildCurve()
    {
        var curve = new FanCurve(CurvePoints.Count >= 2 ? CurvePoints : FanCurve.CreateDefault().Points)
        {
            FloorTempC = FloorTempC,
            FloorLevelPercent = FloorLevelPercent,
        };
        return curve;
    }
}
