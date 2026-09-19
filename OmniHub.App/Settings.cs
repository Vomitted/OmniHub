// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.Json;
using OmniHub.Core.Fan;
using OmniHub.Core.Optimize;

namespace OmniHub.App;

public enum FanControlMode { Auto, BiosDefault, Max }
public enum CloseBehavior { MinimizeToTray, Exit }

/// <summary>
/// Which shape the application takes.
///
/// Not a theme. A theme changes how the same content is drawn; these change WHAT is on screen
/// and how much of it, which is the axis the user was actually asking about when a palette pass
/// and then a type pass both landed as "it still looks the same".
///
/// Workspaces is what every build before this one was: a sidebar of user-arranged screens, each
/// showing a handful of readings at a comfortable size. It stays the default and stays exactly
/// as it was, because somebody who likes it should not have their application redesigned out
/// from under them by an update.
/// </summary>
public enum InterfaceMode
{
    /// <summary>The sidebar of arrangeable workspaces. The original, and the default.</summary>
    Workspaces,

    /// <summary>
    /// One screen, everything on it. No paging: every reading this machine actually exposes,
    /// each on its own row with its source named and its recent history beside it.
    /// </summary>
    Instrument,
}

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

    /// <summary>
    /// Watch the connection continuously rather than only when the Network screen is open.
    ///
    /// On by default, which the rest of this file's defaults do not lightly do. The justification
    /// is cost and usefulness together: one ICMP echo every five seconds is a few bytes, far less
    /// than any single page load, and the faults it exists to catch are intermittent -- a
    /// connection that misbehaves for ten seconds every few minutes reads perfectly clean to
    /// anyone who presses a button, and ruins a match anyway.
    /// </summary>
    public bool NetworkMonitorEnabled { get; set; } = true;

    /// <summary>
    /// Host the monitor samples. A nearby resolver by default, because the point of the running
    /// figure is the SHAPE of the connection -- variance and loss -- rather than the distance to
    /// any particular server. Set it to a game server to watch that path instead.
    /// </summary>
    public string NetworkMonitorTarget { get; set; } = "1.1.1.1";

    /// <summary>Seconds between probes. Five is the default; below two this stops being a
    /// measurement and starts being traffic.</summary>
    public int NetworkMonitorIntervalSeconds { get; set; } = 5;

    /// <summary>Writes a rolling latency/jitter/loss CSV beside the thermal one. Off by default,
    /// for the same reason: a diagnostic aid rather than something to leave running forever.</summary>
    public bool NetworkLogging { get; set; } = false;

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
    /// How much room the interface gives each thing on it.
    ///
    /// A multiplier over the palette rather than a fourth theme: the eight palettes already
    /// disagree about padding and figure size on purpose, and density moves that difference
    /// rather than flattening it. Normal is the palette untouched.
    /// </summary>
    public OmniHub.Core.Optimize.UiDensity Density { get; set; } = OmniHub.Core.Optimize.UiDensity.Normal;

    /// <summary>
    /// Which interface this user wants. See <see cref="InterfaceMode"/>.
    ///
    /// Defaults to Workspaces so that an update never changes the shape of somebody's
    /// application without them asking for it.
    /// </summary>
    public InterfaceMode Interface { get; set; } = InterfaceMode.Workspaces;

    /// <summary>
    /// The palette built in the application, as the four colours it is described by.
    ///
    /// Stored rather than the thirty it produces, so a later build deriving a border differently
    /// re-derives it rather than keeping whatever this build worked out. It is the choices that are
    /// the user's; the rest is arithmetic.
    /// </summary>
    public string CustomPaletteBackground { get; set; } = "#0A0D12";
    public string CustomPalettePanel { get; set; } = "#11151B";
    public string CustomPaletteAccent { get; set; } = "#4F9CF5";
    public string CustomPaletteText { get; set; } = "#E8ECF2";
    public int CustomPaletteRadius { get; set; } = 10;

    /// <summary>The four choices as the model understands them, or null if any will not parse.</summary>
    public OmniHub.Core.Theming.CustomPalette? BuildCustomPalette()
    {
        var background = OmniHub.Core.Theming.Rgb.Parse(CustomPaletteBackground);
        var panel = OmniHub.Core.Theming.Rgb.Parse(CustomPalettePanel);
        var accent = OmniHub.Core.Theming.Rgb.Parse(CustomPaletteAccent);
        var text = OmniHub.Core.Theming.Rgb.Parse(CustomPaletteText);

        if (background is null || panel is null || accent is null || text is null) return null;

        return new OmniHub.Core.Theming.CustomPalette(
            "Custom", background.Value, panel.Value, accent.Value, text.Value, CustomPaletteRadius);
    }

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

    /// <summary>
    /// Figure size in the overlay, 0.7 to 2.0. Clamped on use, like the opacity above.
    ///
    /// One control rather than a font picker: the overlay has exactly two type sizes and they
    /// have to stay in proportion to each other and to the row height, so the useful choice is
    /// how big the card is, not which parts of it are.
    /// </summary>
    public double OverlayScale { get; set; } = 1.0;

    /// <summary>
    /// Whether each row carries a small trend line beside its number.
    ///
    /// Off by default, because it widens the card, and the card is drawn over whatever the user
    /// is actually doing.
    /// </summary>
    public bool OverlaySparklines { get; set; }

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
    /// Highest sustained limit adaptive mode may command on battery, watts.
    ///
    /// Far below the mains ceiling on purpose. Unplugged, the cost of a high ceiling is not heat
    /// but charge: the processor bursts to whatever it is allowed every time a tab opens, and it
    /// is the bursts rather than the idle draw that empty the battery. Eighteen watts still
    /// leaves enough headroom to finish short work quickly and return to idle, which is cheaper
    /// than running slowly for longer.
    /// </summary>
    public int AdaptiveMaxWattsBattery { get; set; } = 18;

    /// <summary>
    /// Die temperature adaptive mode steers towards on battery.
    ///
    /// Lower than the mains target, because at a battery-sized power ceiling the die will not
    /// approach the mains figure anyway. Leaving it at 85 would make the temperature term inert
    /// and hand every decision to the demand term, which is not what "adaptive" should mean on
    /// either rail.
    /// </summary>
    public int AdaptiveTargetTempCBattery { get; set; } = 70;

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

    /// <summary>
    /// Writes the settings, atomically.
    ///
    /// This used to be a bare File.WriteAllText, which truncates the file and then writes into
    /// it. Forty-two call sites across seven views mean it runs constantly, and this machine
    /// records unexpected shutdowns often enough that landing inside that window is not
    /// hypothetical. The result of losing that race is a truncated file, a JsonException on the
    /// next launch, and Load() quietly returning defaults -- so the fan curve, the tuning
    /// profiles, the game rules, the overlay layout, the theme and the power-plan bindings all
    /// disappear at once, with no error and nothing to say what happened. Two hand-made .bak
    /// files sitting beside settings.json say this has already been worked around by hand.
    ///
    /// AtomicFile carries the mechanism and the reasoning; what matters here is that the loss
    /// is total and silent. Load() catches the parse failure and returns a fresh AppSettings,
    /// so the symptom is not an error but a machine that has forgotten everything about itself.
    /// </summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            OmniHub.Core.AtomicFile.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence -- a failed save shouldn't block using the app.
        }
    }

    /// <summary>
    /// Whether the battery curve below is used at all.
    ///
    /// Off by default, and that is the whole non-regression story: with it off there is one curve
    /// and the application behaves exactly as it did. Nothing about a machine changes because a
    /// second set of numbers exists in a settings file.
    /// </summary>
    public bool SeparateBatteryCurve { get; set; }

    /// <summary>
    /// The curve used on battery, when the setting above is on.
    ///
    /// A copy of the default rather than of the mains curve, because the mains curve is whatever
    /// the user has since made it, and seeding from it would silently freeze a snapshot of it at
    /// the moment this feature was first switched on.
    /// </summary>
    public List<CurvePoint> BatteryCurvePoints { get; set; } = FanCurve.CreateDefault().Points.ToList();

    public double BatteryFloorTempC { get; set; } = 55.0;

    public byte BatteryFloorLevelPercent { get; set; } = 15;

    /// <summary>
    /// Whether the second fan runs its own curve rather than following the first.
    ///
    /// Off by default, and off is exactly the behaviour this application has always had: one
    /// curve, evaluated once, sent to both fans.
    ///
    /// Worth having because the firmware itself runs the two fans at different speeds -- across
    /// 192,791 logged readings they sit exactly two apart in 98.4 per cent of the steady samples
    /// where the BIOS was driving. Whether it lets this application do the same is a separate
    /// question, and the Fans tab has a measurement that answers it rather than a claim.
    /// </summary>
    public bool SeparateFan2Curve { get; set; }

    public List<CurvePoint> Fan2CurvePoints { get; set; } = FanCurve.CreateDefault().Points.ToList();
    public double Fan2FloorTempC { get; set; } = 55.0;
    public byte Fan2FloorLevelPercent { get; set; } = 15;

    public List<CurvePoint> BatteryFan2CurvePoints { get; set; } = FanCurve.CreateDefault().Points.ToList();
    public double BatteryFan2FloorTempC { get; set; } = 55.0;
    public byte BatteryFan2FloorLevelPercent { get; set; } = 15;

    /// <summary>All four stored curves, in the shape the selection logic in Core expects.</summary>
    public CurveSet BuildCurveSet() => new(
        MainsFan1:   new CurveRail(CurvePoints, FloorTempC, FloorLevelPercent),
        MainsFan2:   new CurveRail(Fan2CurvePoints, Fan2FloorTempC, Fan2FloorLevelPercent),
        BatteryFan1: new CurveRail(BatteryCurvePoints, BatteryFloorTempC, BatteryFloorLevelPercent),
        BatteryFan2: new CurveRail(BatteryFan2CurvePoints, BatteryFan2FloorTempC, BatteryFan2FloorLevelPercent));

    /// <summary>
    /// The curve for a rail.
    ///
    /// The two rails are genuinely different problems. On mains the question is how much noise is
    /// worth how much headroom; on battery it is that every fan watt is a watt not going into
    /// runtime, and the machine is usually doing less work anyway. One curve has to compromise
    /// between those, and the compromise is worse than either answer.
    /// </summary>
    public FanCurve BuildCurve(bool onBattery = false, bool fan2 = false) =>
        CurveRails.Build(BuildCurveSet(), onBattery, SeparateBatteryCurve, fan2, SeparateFan2Curve);
}
