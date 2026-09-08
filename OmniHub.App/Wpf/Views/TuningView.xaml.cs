using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;
// WinForms and WPF both define these, and this project references both. Aliased per-file
// rather than globally, because several other views already carry their own local aliases and
// a global one would collide with them (CS1537).
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The full tuning surface: every SMU knob this processor family exposes, one row each.
///
/// The rows are generated rather than written out in XAML. There are fourteen of them and
/// they differ only in label, range and unit, so hand-writing fourteen near-identical slider
/// blocks would be fourteen places for a range to drift out of step with the constant in
/// AmdTuning that actually bounds it. Here the bounds come from those constants directly.
/// </summary>
public partial class TuningView : UserControl, IDisposable
{
    /// <summary>Combo entry meaning "do not apply anything for this power source".</summary>
    private const string LeaveAlone = "Leave alone";

    private readonly HardwareContext _ctx;
    private readonly AppSettings _settings;
    private readonly AmdTuning? _tuning;
    private ComboBox? _acProfile, _dcProfile;
    private CheckBox? _autoSwitch;
    private PowerSourceWatcher? _watcher;
    private ProcessWatcher? _processWatcher;
    private ComboBox? _startupProfile;
    private CheckBox? _startupAdaptive;
    private AutoEco? _autoEco;
    private CheckBox? _adaptiveEnabled;
    private CheckBox? _ecoOnBattery, _ecoOnIdle;
    private Slider? _ecoIdleMinutes;
    private ComboBox? _ecoRefresh, _ecoProfile;

    /// <summary>Guards the startup controls while they are being populated from settings.</summary>
    private bool _suppressStartupEvents;
    private readonly Dictionary<string, Slider> _sliders = new();
    private readonly Dictionary<string, CheckBox> _enables = new();
    private AdaptiveTuning? _adaptive;
    private DispatcherTimer? _liveTimer;
    private TextBlock? _liveStapm, _liveFast, _liveSlow, _liveTemp;
    private TextBlock? _liveLimiting, _liveTdc, _liveEdc, _liveSocCurrent, _liveCoreTemp, _liveSocTemp;
    private TextBlock? _liveGpu;

    public TuningView(HardwareContext ctx, AppSettings settings)
    {
        InitializeComponent();
        _ctx = ctx;
        _settings = settings;

        if (ctx.Smu is null)
        {
            SetChip(false, "UNAVAILABLE");
            SmuTitle.Text = "Not available";
            SmuDetail.Text = ctx.SmuUnavailableReason ?? "The SMU could not be opened.";
            BuildCapabilityRows();
            return;
        }

        var tuning = new AmdTuning(ctx.Smu);
        SetChip(tuning.IsSupported, "READY");
        SmuTitle.Text = $"{ctx.Smu.CodeName}  ·  SMU {ctx.Smu.SmuVersionString}";

        if (!tuning.IsSupported)
        {
            SmuDetail.Text = tuning.UnsupportedReason!;
            BuildCapabilityRows();
            return;
        }

        _tuning = tuning;
        string driver = PawnIoAccess.RuntimeVersion() is { } v ? $"PawnIO {v.Major}.{v.Minor}.{v.Patch}" : "PawnIO";
        SmuDetail.Text = $"{driver}  ·  {System.IO.Path.GetFileName(ctx.Smu.ModulePath)}";

        BuildLiveRows();
        BuildPresets();
        BuildSettingRows();
        BuildAdaptive();
        _suppressStartupEvents = true;
        BuildAutoEcoRows();
        BuildStartupRows();
        _suppressStartupEvents = false;

        BuildPowerSourceRows();
        BuildGameRules();
        BuildApplyRow();
        BuildCapabilityRows();
        UpdateModeAvailability();
        StartLiveUpdates();
        ApplyStartupState();
    }

    // ---------------------------------------------------------------- construction

    private void SetChip(bool ok, string label)
    {
        SmuChipText.Text = ok ? label : "OFF";
        SmuChipText.Foreground = (Brush)FindResource(ok ? "AccentBrush" : "TextFaintBrush");
        SmuChip.BorderBrush = (Brush)FindResource(ok ? "AccentBrush" : "BorderBrush");
        SmuChip.Background = (Brush)FindResource(ok ? "AccentSoftBrush" : "PanelAltBrush");
    }

    private void BuildLiveRows()
    {
        _liveLimiting = AddReadout("Limited by");
        _liveStapm = AddReadout("Sustained (STAPM)");
        _liveFast = AddReadout("Boost (PPT fast)");
        _liveSlow = AddReadout("Slow (PPT slow)");
        _liveTdc = AddReadout("Core current (TDC)");
        _liveEdc = AddReadout("Core current (EDC)");
        _liveSocCurrent = AddReadout("SoC current");
        _liveCoreTemp = AddReadout("Core (SMU)");
        _liveSocTemp = AddReadout("SoC / graphics");
        _liveTemp = AddReadout("Die temperature");
        if (GpuTelemetry.IsAvailable) _liveGpu = AddReadout("GPU");

        TextBlock AddReadout(string label)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("MutedText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var value = new TextBlock
            {
                Text = "--",
                Style = (Style)FindResource("BodyText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(name);
            grid.Children.Add(value);
            LiveRows.Children.Add(grid);
            return value;
        }
    }

    /// <summary>
    /// Adaptive is offered as a profile in its own right, not just a mode.
    ///
    /// It is not an AmdTuningProfile because it is not a set of numbers to send once -- it is
    /// a controller that keeps running. But everywhere a profile can be chosen (on charge, on
    /// battery, per game, at startup, for eco) "hold a temperature" is a perfectly reasonable
    /// answer, and making the user set a mode separately from a profile meant those two could
    /// disagree about what the machine was supposed to be doing.
    /// </summary>
    private const string AdaptiveProfileName = "Adaptive";

    /// <summary>Built-in profiles plus whatever the user has saved.</summary>
    private IEnumerable<AmdTuningProfile> AllProfiles() =>
        AmdTuning.Profiles.Concat(_settings.CustomProfiles);

    /// <summary>Everything selectable in a profile picker, Adaptive included.</summary>
    private IEnumerable<string> ProfileNames() =>
        AllProfiles().Select(p => p.Name).Append(AdaptiveProfileName);

    /// <summary>
    /// The single place a profile gets applied by name. Every automatic trigger -- power
    /// source, per-game rule, startup, auto eco -- routes through here so they cannot disagree
    /// about what "Adaptive" means or drift apart in how a missing profile is reported.
    /// </summary>
    private Task ApplyNamed(string name)
    {
        if (string.Equals(name, AdaptiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            SetMode(TuningMode.Adaptive);
            return Task.CompletedTask;
        }

        var profile = AllProfiles().FirstOrDefault(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            ResultText.Text = $"Profile \"{name}\" no longer exists.";
            return Task.CompletedTask;
        }

        SetMode(TuningMode.Manual);
        _activeProfileName = profile.Name;
        HighlightActiveProfile();
        return RunProfile(profile);
    }

    /// <summary>
    /// A section's on/off switch: a checkbox above the controls it governs, which grey out
    /// while it is off.
    ///
    /// One helper rather than five hand-wired copies, because every section here has the same
    /// shape -- a feature you turn on, with settings that mean nothing until you do -- and
    /// hand-wiring is exactly how they ended up in four different idioms: a pill pair on
    /// Adaptive, a bare checkbox buried mid-list on power source, a combo with a "leave alone"
    /// entry on startup, and nothing at all on the rest.
    /// </summary>
    /// <param name="host">Panel the checkbox is added to, above the gated content.</param>
    /// <param name="gated">Content that is only usable while the box is ticked.</param>
    private CheckBox AddSectionSwitch(Panel host, string label, bool initial, UIElement gated, Action<bool> onChanged)
    {
        var box = new CheckBox
        {
            Style = (Style)FindResource("OmniCheckBoxStyle"),
            Content = label,
            IsChecked = initial,
            Margin = new Thickness(0, 0, 0, 12),
        };

        void Changed(object s, RoutedEventArgs e)
        {
            bool on = box.IsChecked == true;

            // The greying happens even while events are suppressed: it is a reflection of
            // state, not a reaction to the user, and skipping it during a programmatic change
            // is how a section ends up enabled with its switch unticked.
            gated.IsEnabled = on;
            if (_suppressStartupEvents) return;
            onChanged(on);
            _settings.Save();
        }

        box.Checked += Changed;
        box.Unchecked += Changed;
        gated.IsEnabled = initial;
        host.Children.Add(box);
        return box;
    }

    /// <summary>
    /// Switches mode without re-entering the checkbox handler, which would otherwise start or
    /// stop the controller a second time on its way through.
    /// </summary>
    private void SetMode(TuningMode mode)
    {
        bool wasSuppressed = _suppressStartupEvents;
        _suppressStartupEvents = true;
        try
        {
            if (_adaptiveEnabled is not null)
            {
                _adaptiveEnabled.IsChecked = mode == TuningMode.Adaptive;
                if (AdaptiveBody is not null) AdaptiveBody.IsEnabled = mode == TuningMode.Adaptive;
            }
        }
        finally { _suppressStartupEvents = wasSuppressed; }

        _settings.TuningMode = mode;

        if (mode == TuningMode.Adaptive)
        {
            _activeProfileName = AdaptiveProfileName;
            if (_adaptive is not { IsRunning: true }) StartAdaptive();
        }
        else if (_adaptive is { IsRunning: true })
        {
            _adaptive.Stop();
        }

        HighlightActiveProfile();
        UpdateModeAvailability();
    }

    /// <summary>Name of whatever was applied last, so the UI can show which one is live.</summary>
    private string? _activeProfileName;

    /// <summary>
    /// Marks the active preset button. UXTU shows which preset is in force and this did not,
    /// so a tab full of identical buttons gave no clue what the machine was actually running.
    /// </summary>
    private void HighlightActiveProfile()
    {
        foreach (var button in PresetRow.Children.OfType<Button>())
        {
            bool active = button.Tag is AmdTuningProfile p
                          && string.Equals(p.Name, _activeProfileName, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
            button.BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        string name = PresetNameBox.Text.Trim();
        if (name.Length == 0) { PresetStatus.Text = "Give the profile a name first."; return; }

        // Adaptive is reserved alongside the built-ins: a saved profile by that name would be
        // unreachable, since every picker resolves it to the controller instead.
        if (AmdTuning.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            || string.Equals(name, AdaptiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            PresetStatus.Text = $"\"{name}\" is a reserved profile name. Pick another.";
            return;
        }

        // A saved profile captures only the knobs whose enable box is ticked, exactly as Apply
        // would send them. Saving all fourteen regardless would turn "set the thermal target"
        // into "also pin every power limit to whatever the slider happened to show".
        var saved = CurrentProfile() with { Name = name, Description = "Saved profile" };
        _settings.CustomProfiles.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.CustomProfiles.Add(saved);
        _settings.Save();

        RebuildPresetRow();
        RefreshProfilePickers();
        PresetStatus.Text = $"Saved \"{name}\".";
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        string name = PresetNameBox.Text.Trim();
        int removed = _settings.CustomProfiles.RemoveAll(
            p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (removed == 0) { PresetStatus.Text = $"No saved profile called \"{name}\"."; return; }

        // Rules pointing at a profile that no longer exists would silently do nothing, so they
        // go too rather than becoming dead entries the user has to notice on their own.
        int orphaned = _settings.GameRules.RemoveAll(
            r => string.Equals(r.ProfileName, name, StringComparison.OrdinalIgnoreCase));

        _settings.Save();
        RebuildPresetRow();
        RefreshProfilePickers();
        RebuildGameRuleList();
        PresetStatus.Text = orphaned > 0
            ? $"Deleted \"{name}\" and {orphaned} rule(s) that used it."
            : $"Deleted \"{name}\".";
    }

    private void RebuildPresetRow()
    {
        PresetRow.Children.Clear();
        BuildPresets();
    }

    private void BuildPresets()
    {
        foreach (var profile in AllProfiles())
        {
            var button = new Button
            {
                Content = profile.Name,
                Style = (Style)FindResource("FlatButtonStyle"),
                Width = 104,
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = $"{profile.Description}\n{profile.StapmWatts}W sustained, {profile.FastWatts}W boost, {profile.TctlTempC}C",
                Tag = profile,
            };
            button.Click += Preset_Click;
            PresetRow.Children.Add(button);
        }
    }

    /// <summary>
    /// One row per knob. Each carries its own enable box, because "leave this alone" is a
    /// distinct intent from "set it to whatever the slider happens to show" -- a profile that
    /// silently pushed all fourteen values every time would overwrite settings never touched.
    /// </summary>
    private void BuildSettingRows()
    {
        AddRow(PowerRows, "stapm", "Sustained limit (STAPM)", AmdTuning.MinWatts, AmdTuning.MaxWatts, 45, "W",
            "The long-run average the processor settles to. The single biggest influence on sustained temperature.");
        AddRow(PowerRows, "fast", "Boost limit (PPT fast)", AmdTuning.MinWatts, AmdTuning.MaxWatts, 65, "W",
            "Short-burst ceiling, for the first seconds of a load.");
        AddRow(PowerRows, "slow", "Slow limit (PPT slow)", AmdTuning.MinWatts, AmdTuning.MaxWatts, 54, "W",
            "The medium window between burst and sustained.");
        AddRow(PowerRows, "stapmTime", "STAPM window", AmdTuning.MinSeconds, 120, 30, "s",
            "How long the sustained average is taken over. Longer means burstier behaviour.");
        AddRow(PowerRows, "slowTime", "Slow window", AmdTuning.MinSeconds, 120, 10, "s",
            "Averaging window for the slow limit.");

        AddRow(ThermalRows, "tctl", "Thermal limit (Tctl)", AmdTuning.MinTempC, AmdTuning.MaxTempC, 85, "C",
            "Die temperature the processor throttles itself to hold. Refused by this firmware.");
        AddRow(ThermalRows, "skin", "Skin temperature limit", AmdTuning.MinTempC, AmdTuning.MaxTempC, 45, "C",
            "How hot the chassis is allowed to get before the processor backs off.");
        AddRow(ThermalRows, "apuSkin", "APU skin temperature", AmdTuning.MinTempC, AmdTuning.MaxTempC, 45, "C",
            "The APU's own skin-temperature target.");
        AddRow(ThermalRows, "vrm", "VRM current (TDC)", AmdTuning.MinAmps, AmdTuning.MaxAmps, 70, "A",
            "Sustained current the voltage regulators may deliver. Refused by this firmware.");

        AddRow(GfxRows, "gfx", "GFX clock", AmdTuning.MinGfxMhz, AmdTuning.MaxGfxMhz, 2800, "MHz",
            "Integrated Radeon clock target.");
        AddRow(GfxRows, "co", "Curve Optimizer (all cores)", AmdTuning.MinCurveOptimizer, 0, 0, "",
            "Negative undervolts. Less voltage at the same clock is less heat. Move a few counts at a time.");
    }

    private void BuildAdaptive()
    {
        // Seeded from settings, not from constants: these are the controller's configuration
        // and resetting them to defaults on every launch would quietly discard whatever the
        // user tuned, which matters more now that adaptive can start itself at boot.
        AddRow(AdaptiveRows, "adaptiveTarget", "Target temperature", 60, 100, _settings.AdaptiveTargetTempC, "C",
            "The temperature adaptive mode steers towards.", enabled: true, showEnable: false);
        AddRow(AdaptiveRows, "adaptiveMin", "Minimum sustained", AmdTuning.MinWatts, AmdTuning.MaxWatts, _settings.AdaptiveMinWatts, "W",
            "Floor for the controller.", enabled: true, showEnable: false);
        AddRow(AdaptiveRows, "adaptiveMax", "Maximum sustained", AmdTuning.MinWatts, AmdTuning.MaxWatts, _settings.AdaptiveMaxWatts, "W",
            "Ceiling for the controller.", enabled: true, showEnable: false);

        foreach (string key in new[] { "adaptiveTarget", "adaptiveMin", "adaptiveMax" })
            _sliders[key].ValueChanged += (_, _) => SaveAdaptiveSettings();

        // The thermal limit persists on its own, because it is the knob that measurably works
        // and the one a preset is most likely to trample.
        if (_settings.ThermalLimitC is int saved) Set("tctl", saved);
        _sliders["tctl"].ValueChanged += (_, _) =>
        {
            if (_suppressStartupEvents) return;
            _settings.ThermalLimitC = (int)_sliders["tctl"].Value;
            _settings.Save();
        };

        // Mode selector. Manual and Adaptive are genuinely different intents -- one applies
        // the sliders once, the other holds a target continuously -- and leaving both live at
        // the same time meant an Apply and the controller could fight over the same limits.
        //
        // One checkbox instead of a Manual/Adaptive pill pair plus a Start/Stop button.
        //
        // Those three controls expressed one piece of state between them and could disagree
        // about it: the pill said Adaptive while the button still read "Start adaptive", and
        // whether the controller was actually running was a fourth thing again. Ticking the
        // box IS turning adaptive on, and unticking it is manual -- there is no separate
        // "Manual" control to leave stale, because manual is simply this being off.
        _adaptiveEnabled = AddSectionSwitch(
            AdaptiveSwitchRow,
            "Use adaptive tuning",
            _settings.TuningMode == TuningMode.Adaptive,
            AdaptiveBody,
            on =>
            {
                SetMode(on ? TuningMode.Adaptive : TuningMode.Manual);
                if (on) return;
                ResultText.Text = "Manual mode. The sliders apply only when you press Apply.";
                ResultText.Foreground = (Brush)FindResource("TextFaintBrush");
            });
    }

    /// <summary>
    /// Per-power-source profiles. "Leave alone" is the default on both, so enabling the
    /// feature does not silently start changing settings the moment a charger moves.
    /// </summary>
    private void BuildPowerSourceRows()
    {
        // The switch goes ABOVE the two pickers now rather than below them. It was the last
        // control in the card, so the two profile combos sat there looking live while the
        // feature they configure was switched off.
        _autoSwitch = AddSectionSwitch(
            PowerSourceSwitchRow,
            "Switch automatically when the charger changes",
            _settings.AutoSwitchProfiles,
            PowerSourceRows,
            on =>
            {
                _settings.AutoSwitchProfiles = on;
                if (on) _watcher?.Start(); else _watcher?.Stop();
                PowerSourceStatus.Text = on
                    ? $"Watching. Currently on {Describe(PowerSourceWatcher.Read())}."
                    : "Automatic switching is off.";
            });

        _acProfile = AddProfilePicker("On charge", _settings.AcProfileName);
        _dcProfile = AddProfilePicker("On battery", _settings.DcProfileName);

        _watcher = new PowerSourceWatcher();
        _watcher.OnChanged += source => Dispatcher.Invoke(() => ApplyForSource(source, automatic: true));
        if (_settings.AutoSwitchProfiles) _watcher.Start();

        PowerSourceStatus.Text = $"Currently on {Describe(PowerSourceWatcher.Read())}.";

        ComboBox AddProfilePicker(string label, string? selected)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("MutedText"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var combo = new ComboBox { Style = (Style)FindResource("OmniComboBoxStyle"), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 260, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200 };
            combo.Items.Add(LeaveAlone);
            foreach (var n in ProfileNames()) combo.Items.Add(n);
            combo.SelectedItem = combo.Items.Contains(selected) ? selected : LeaveAlone;
            combo.SelectionChanged += ProfilePicker_Changed;

            Grid.SetColumn(combo, 1);
            grid.Children.Add(name);
            grid.Children.Add(combo);
            PowerSourceRows.Children.Add(grid);
            return combo;
        }
    }

    private void ProfilePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStartupEvents) return;
        if (_acProfile is null || _dcProfile is null) return;
        _settings.AcProfileName = _acProfile.SelectedItem as string is { } ac && ac != LeaveAlone ? ac : null;
        _settings.DcProfileName = _dcProfile.SelectedItem as string is { } dc && dc != LeaveAlone ? dc : null;
        _settings.Save();
    }

    private void ApplyForSource(PowerSource source, bool automatic)
    {
        string? wanted = source == PowerSource.Mains ? _settings.AcProfileName : _settings.DcProfileName;
        if (wanted is null)
        {
            PowerSourceStatus.Text = $"Now on {Describe(source)}. No profile is set for it, so nothing changed.";
            return;
        }

        PowerSourceStatus.Text = $"Now on {Describe(source)} - applying {wanted}.";
        ApplyNamed(wanted);
    }

    private static string Describe(PowerSource source) => source switch
    {
        PowerSource.Mains => "mains power",
        PowerSource.Battery => "battery",
        _ => "an unknown power source",
    };

    /// <summary>
    /// Auto eco: the triggers, what it changes, and a live status line.
    ///
    /// The refresh-rate list comes from the display itself rather than a hardcoded 60/120/144,
    /// because a panel that does not offer the rate would just refuse the change and leave the
    /// user reading an error about a mode their hardware never had.
    /// </summary>
    private void BuildAutoEcoRows()
    {
        AddSectionSwitch(
            AutoEcoSwitchRow,
            "Enable auto eco",
            _settings.AutoEcoEnabled,
            AutoEcoRows,
            on =>
            {
                _settings.AutoEcoEnabled = on;
                UpdateAutoEcoWatcher();
            });

        _ecoOnBattery = AddEcoCheck("Engage on battery", _settings.AutoEcoOnBattery,
            v => _settings.AutoEcoOnBattery = v);
        _ecoOnIdle = AddEcoCheck("Engage when idle", _settings.AutoEcoOnIdle,
            v => _settings.AutoEcoOnIdle = v);

        var idleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        idleRow.Children.Add(new TextBlock
        {
            Text = "Idle after",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 108,
        });
        _ecoIdleMinutes = new Slider
        {
            Style = (Style)FindResource("OmniSliderStyle"),
            Minimum = 1, Maximum = 60, Value = _settings.AutoEcoIdleMinutes,
            Width = 160, IsSnapToTickEnabled = true, TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var minutesLabel = new TextBlock
        {
            Text = $"{_settings.AutoEcoIdleMinutes} min",
            Style = (Style)FindResource("BodyText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _ecoIdleMinutes.ValueChanged += (_, _) =>
        {
            minutesLabel.Text = $"{(int)_ecoIdleMinutes!.Value} min";
            if (_suppressStartupEvents) return;
            _settings.AutoEcoIdleMinutes = (int)_ecoIdleMinutes.Value;
            _settings.Save();
        };
        idleRow.Children.Add(_ecoIdleMinutes);
        idleRow.Children.Add(minutesLabel);
        AutoEcoRows.Children.Add(idleRow);

        var hzRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        hzRow.Children.Add(new TextBlock
        {
            Text = "Eco refresh",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 108,
        });
        _ecoRefresh = new ComboBox { Style = (Style)FindResource("OmniComboBoxStyle"), MinWidth = 120, VerticalAlignment = VerticalAlignment.Center };
        _ecoRefresh.Items.Add(LeaveAlone);
        foreach (int hz in DisplayControl.AvailableRefreshHz()) _ecoRefresh.Items.Add($"{hz} Hz");
        _ecoRefresh.SelectedItem = _settings.AutoEcoRefreshHz > 0
                                   && _ecoRefresh.Items.Contains($"{_settings.AutoEcoRefreshHz} Hz")
            ? $"{_settings.AutoEcoRefreshHz} Hz"
            : LeaveAlone;
        _ecoRefresh.SelectionChanged += (_, _) =>
        {
            if (_suppressStartupEvents) return;
            _settings.AutoEcoRefreshHz = _ecoRefresh!.SelectedItem is string s && s != LeaveAlone
                ? int.Parse(s.Replace(" Hz", ""))
                : 0;
            _settings.Save();
        };
        hzRow.Children.Add(_ecoRefresh);

        int? current = DisplayControl.CurrentRefreshHz();
        hzRow.Children.Add(new TextBlock
        {
            Text = current is int c ? $"(now {c} Hz)" : "",
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        });
        AutoEcoRows.Children.Add(hzRow);

        var profRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        profRow.Children.Add(new TextBlock
        {
            Text = "Eco profile",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 108,
        });
        _ecoProfile = new ComboBox { Style = (Style)FindResource("OmniComboBoxStyle"), MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };

        // Seeded here rather than left for RefreshProfilePickers. That method preserves the
        // current selection across a rebuild, so a combo that is still empty when it first
        // runs comes back selected on "Leave alone" and the saved eco profile is silently lost.
        _ecoProfile.Items.Add(LeaveAlone);
        foreach (var n in ProfileNames()) _ecoProfile.Items.Add(n);
        _ecoProfile.SelectedItem = _ecoProfile.Items.Contains(_settings.AutoEcoProfileName)
            ? _settings.AutoEcoProfileName
            : LeaveAlone;

        profRow.Children.Add(_ecoProfile);
        AutoEcoRows.Children.Add(profRow);

        _ecoProfile.SelectionChanged += (_, _) =>
        {
            if (_suppressStartupEvents) return;
            _settings.AutoEcoProfileName = _ecoProfile!.SelectedItem is string s && s != LeaveAlone ? s : null;
            _settings.Save();
        };

        _autoEco = new AutoEco(
            () => new AutoEcoSettings(
                _settings.AutoEcoOnBattery, _settings.AutoEcoOnIdle,
                _settings.AutoEcoIdleMinutes, _settings.AutoEcoRefreshHz, _settings.AutoEcoProfileName),
            ApplyProfileByName,
            RestoreFromEco);

        _autoEco.OnEcoChanged += (_, status) => Dispatcher.Invoke(() => AutoEcoStatus.Text = status);
        UpdateAutoEcoWatcher();

        CheckBox AddEcoCheck(string label, bool initial, Action<bool> set)
        {
            var box = new CheckBox { Style = (Style)FindResource("OmniCheckBoxStyle"), Content = label, IsChecked = initial, Margin = new Thickness(0, 4, 0, 0) };
            void Changed(object s, RoutedEventArgs e)
            {
                if (_suppressStartupEvents) return;
                set(box.IsChecked == true);
                _settings.Save();
                UpdateAutoEcoWatcher();
            }
            box.Checked += Changed;
            box.Unchecked += Changed;
            AutoEcoRows.Children.Add(box);
            return box;
        }
    }

    /// <summary>Runs the watcher only while a trigger is actually enabled.</summary>
    private void UpdateAutoEcoWatcher()
    {
        if (_autoEco is null) return;

        // The master switch gates the triggers rather than replacing them: a section switched
        // off never runs, and switching it back on restores whichever triggers were chosen
        // rather than making the user pick again.
        bool wanted = _settings.AutoEcoEnabled
                   && (_settings.AutoEcoOnBattery || _settings.AutoEcoOnIdle);

        if (wanted && !_autoEco.IsRunning) { _autoEco.Start(); AutoEcoStatus.Text = "Auto eco is watching."; }
        else if (!wanted && _autoEco.IsRunning) { _autoEco.Stop(); AutoEcoStatus.Text = "Auto eco is off."; }
    }

    private TuningResult ApplyProfileByName(string name)
    {
        if (_tuning is null) return new TuningResult(false, "No SMU access.");

        // Auto eco runs on a background thread, so Adaptive has to be handed back to the
        // dispatcher: starting the controller touches the sliders it reads its target from.
        if (string.Equals(name, AdaptiveProfileName, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.Invoke(() => SetMode(TuningMode.Adaptive));
            return new TuningResult(true, "Adaptive mode engaged.");
        }

        var profile = AllProfiles().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return new TuningResult(false, $"Profile \"{name}\" no longer exists.");

        var report = _tuning.Apply(profile);
        Dispatcher.Invoke(() => { _activeProfileName = profile.Name; HighlightActiveProfile(); });
        return new TuningResult(report.Applied, report.Summary);
    }

    /// <summary>
    /// What "not eco" means. Deliberately the power-source profile rather than a remembered
    /// snapshot: the machine may well have moved between mains and battery while eco was on,
    /// and restoring the profile from before that move would be restoring the wrong one.
    /// </summary>
    private void RestoreFromEco() =>
        Dispatcher.Invoke(() => ApplyForSource(PowerSourceWatcher.Read(), automatic: true));

    /// <summary>
    /// What to apply at launch. Built like the other pickers, then acted on once at the end
    /// of construction -- see ApplyStartupState.
    /// </summary>
    private void BuildStartupRows()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Apply profile",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 108,
        });

        _startupProfile = new ComboBox { Style = (Style)FindResource("OmniComboBoxStyle"), MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
        _startupProfile.Items.Add(LeaveAlone);
        foreach (var n in ProfileNames()) _startupProfile.Items.Add(n);
        _startupProfile.SelectedItem = _startupProfile.Items.Contains(_settings.StartupProfileName)
            ? _settings.StartupProfileName
            : LeaveAlone;
        _startupProfile.SelectionChanged += (_, _) =>
        {
            if (_suppressStartupEvents) return;
            _settings.StartupProfileName = _startupProfile.SelectedItem as string is { } s && s != LeaveAlone ? s : null;
            _settings.Save();
        };
        row.Children.Add(_startupProfile);
        StartupRows.Children.Add(row);

        // The GPU power unlock used to live here. It is a graphics setting and it now sits on
        // the Graphics tab, next to the presets it overrides, which is also where it gets
        // applied at launch.

        _startupAdaptive = new CheckBox
        {
            Style = (Style)FindResource("OmniCheckBoxStyle"),
            Content = "Start adaptive mode at launch",
            IsChecked = _settings.StartupAdaptive,
            Margin = new Thickness(0, 10, 0, 0),
        };
        void AdaptiveChanged(object s, RoutedEventArgs e)
        {
            if (_suppressStartupEvents) return;
            _settings.StartupAdaptive = _startupAdaptive!.IsChecked == true;
            _settings.Save();
        }
        _startupAdaptive.Checked += AdaptiveChanged;
        _startupAdaptive.Unchecked += AdaptiveChanged;
        StartupRows.Children.Add(_startupAdaptive);
    }

    /// <summary>
    /// Runs once, at the end of construction.
    ///
    /// Deferred to the dispatcher rather than run inline: this executes while MainWindow is
    /// still building its views, and applying a profile means a chain of SMU round trips.
    /// Doing that on the constructor's thread delays the window appearing at exactly the
    /// moment the user is watching for it at login.
    ///
    /// Order matters. The profile lands first, then adaptive starts, because adaptive steers
    /// the sustained limit continuously and would otherwise be overwritten by the profile a
    /// moment after it began.
    /// </summary>
    private void ApplyStartupState()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            // Captured BEFORE the profile is applied.
            //
            // ApplyNamed switches the mode to Manual for any non-adaptive profile, and it does
            // so by writing _settings.TuningMode. Reading that field afterwards therefore never
            // returned the saved mode -- it returned whatever the profile had just forced -- so
            // a saved Adaptive mode was silently discarded on every launch with a preset
            // configured. That is the "startup still uses manual" behaviour.
            bool goingAdaptive = _settings.TuningMode == TuningMode.Adaptive || _settings.StartupAdaptive;
            int? savedThermalLimit = _settings.ThermalLimitC;

            // The power source is a LIVE condition, so its profile outranks the plain startup
            // one: the machine is on battery NOW, and unplugging a moment later would apply
            // exactly this profile anyway.
            //
            // Until now the watcher was merely STARTED at launch and only fired on a CHANGE,
            // so booting on battery with "On battery: Eco" configured applied nothing at all
            // until the charger was physically moved. The setting was saved, shown, and
            // silently inert for the whole session.
            var source = PowerSourceWatcher.Read();
            string? sourceProfile = _settings.AutoSwitchProfiles
                ? (source == PowerSource.Mains ? _settings.AcProfileName : _settings.DcProfileName)
                : null;

            Task pending = Task.CompletedTask;

            if ((sourceProfile ?? _settings.StartupProfileName) is { } wanted)
            {
                StartupStatus.Text = sourceProfile is null
                    ? $"Applying {wanted} on startup."
                    : $"Applying {wanted} for {Describe(source)}.";
                pending = ApplyNamed(wanted);
            }

            // Chained onto the profile's completion, not merely written afterwards in source
            // order. RunProfile hands the work to a background task and returns immediately,
            // so setting the thermal limit inline meant the profile's own TctlTempC landed
            // second and won -- which is exactly how an explicit 90 C ended up back at the
            // preset's 85 C, looking like the setting had been ignored.
            pending.ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                // In Adaptive the target IS the thermal limit, so applying the standalone
                // ThermalLimitC as well meant two writers for one register and the winner was
                // decided by ordering -- an 88 C target came up as 90 C because the saved
                // limit was applied second. Adaptive owns it when adaptive is on.
                if (!goingAdaptive && savedThermalLimit is int limit && _tuning is not null)
                {
                    var r = _tuning.SetThermalLimitC(limit);
                    Set("tctl", limit);
                    StartupStatus.Text += r.Applied
                        ? $" Thermal limit held at {limit} C."
                        : $" Thermal limit {limit} C refused: {r.Detail}";
                }

                // The saved MODE has the final say, after any startup profile.
                //
                // ApplyNamed switches to Manual for anything that is not Adaptive, which is
                // right when a person picks a preset by hand but wrong here: a startup profile
                // of Balanced would drop the machine into Manual and overwrite a saved Adaptive
                // mode on every launch. Applying the profile and then restoring the mode keeps
                // both settings meaning what they say.
                if (goingAdaptive)
                {
                    if (_adaptive is not { IsRunning: true }) SetMode(TuningMode.Adaptive);
                    StartupStatus.Text += $" Adaptive holding {(int)_sliders["adaptiveTarget"].Value} C.";
                }
                else
                {
                    SetMode(TuningMode.Manual);
                }
            }), TaskScheduler.Default);
        });
    }

    /// <summary>
    /// Per-process rules. The watcher runs whenever any rule exists -- there is no separate
    /// on/off switch, because an empty rule list already means "do nothing" and a second
    /// control to express the same thing is one more state to get out of sync.
    /// </summary>
    private void BuildGameRules()
    {
        RefreshProcessList();
        RefreshProfilePickers();
        RebuildGameRuleList();

        AddSectionSwitch(
            GameSwitchRow,
            "Enable per-game profiles",
            _settings.GameRulesEnabled,
            GameBody,
            on =>
            {
                _settings.GameRulesEnabled = on;
                if (on && _settings.GameRules.Count > 0) _processWatcher?.Start();
                else _processWatcher?.Stop();
                GameRuleStatus.Text = on ? "" : "Per-game profiles are off; the rules below are kept.";
            });

        _processWatcher = new ProcessWatcher(
            () => _settings.GameRules.Select(r => r.ProcessName).ToArray());

        _processWatcher.OnChanged += name => Dispatcher.Invoke(() => OnWatchedProcessChanged(name));
        if (_settings.GameRulesEnabled && _settings.GameRules.Count > 0) _processWatcher.Start();
    }

    private void OnWatchedProcessChanged(string? processName)
    {
        if (processName is null)
        {
            // The game exited. Fall back to whatever the power source says, so the machine
            // does not silently stay on a gaming profile all evening.
            GameRuleStatus.Text = "Game closed - restoring the power-source profile.";
            ApplyForSource(PowerSourceWatcher.Read(), automatic: true);
            return;
        }

        var rule = _settings.GameRules.FirstOrDefault(
            r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            GameRuleStatus.Text = $"{processName} is running but has no rule.";
            return;
        }

        GameRuleStatus.Text = $"{processName} running - applying {rule.ProfileName}.";
        ApplyNamed(rule.ProfileName);
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcessList();

    private void RefreshProcessList()
    {
        string? typed = GameProcessPicker.Text;
        GameProcessPicker.Items.Clear();

        // Only processes with a visible window: the full list is hundreds of service names
        // nobody is going to scroll, and a game always has a window.
        foreach (var name in System.Diagnostics.Process.GetProcesses()
                     .Select(p => { try { return p.MainWindowHandle != IntPtr.Zero ? p.ProcessName : null; } catch { return null; } finally { p.Dispose(); } })
                     .Where(n => n is not null)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            GameProcessPicker.Items.Add(name!);

        GameProcessPicker.Text = typed;
    }

    private void RefreshProfilePickers()
    {
        string? selected = GameProfilePicker.SelectedItem as string;
        GameProfilePicker.Items.Clear();
        foreach (var n in ProfileNames()) GameProfilePicker.Items.Add(n);
        GameProfilePicker.SelectedItem = GameProfilePicker.Items.Contains(selected)
            ? selected
            : GameProfilePicker.Items.Count > 0 ? GameProfilePicker.Items[0] : null;

        // The power-source and startup pickers list the same profiles, so they need the new
        // entries too. Suppressed while rebuilding: clearing a combo raises SelectionChanged,
        // and those handlers write straight back to settings -- repopulating would otherwise
        // save a null over the user's choice on the way past.
        bool wasSuppressed = _suppressStartupEvents;
        _suppressStartupEvents = true;
        try
        {
            foreach (var combo in new[] { _acProfile, _dcProfile, _startupProfile, _ecoProfile })
            {
                if (combo is null) continue;
                string? keep = combo.SelectedItem as string;
                combo.Items.Clear();
                combo.Items.Add(LeaveAlone);
                foreach (var n in ProfileNames()) combo.Items.Add(n);
                combo.SelectedItem = combo.Items.Contains(keep) ? keep : LeaveAlone;
            }
        }
        finally { _suppressStartupEvents = wasSuppressed; }
    }

    private void AddGameRule_Click(object sender, RoutedEventArgs e)
    {
        string process = (GameProcessPicker.Text ?? string.Empty).Trim();
        if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) process = process[..^4];
        string? profile = GameProfilePicker.SelectedItem as string;

        if (process.Length == 0 || profile is null)
        {
            GameRuleStatus.Text = "Pick a process and a profile first.";
            return;
        }

        _settings.GameRules.RemoveAll(r => string.Equals(r.ProcessName, process, StringComparison.OrdinalIgnoreCase));
        _settings.GameRules.Add(new GameRule(process, profile));
        _settings.Save();

        RebuildGameRuleList();
        if (!_processWatcher!.IsRunning) _processWatcher.Start();
        GameRuleStatus.Text = $"{process} will use {profile}.";
    }

    private void RebuildGameRuleList()
    {
        GameRuleList.Items.Clear();
        foreach (var rule in _settings.GameRules)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = $"{rule.ProcessName}  ->  {rule.ProfileName}",
                Style = (Style)FindResource("BodyText"),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 280,
            });

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("FlatButtonStyle"),
                Tag = rule,
            };
            remove.Click += (s, _) =>
            {
                if (s is Button { Tag: GameRule r })
                {
                    _settings.GameRules.Remove(r);
                    _settings.Save();
                    RebuildGameRuleList();
                    if (_settings.GameRules.Count == 0) _processWatcher?.Stop();
                }
            };
            row.Children.Add(remove);
            GameRuleList.Items.Add(row);
        }
    }

    private void BuildApplyRow()
    {
        var apply = new Button { Content = "Apply", Style = (Style)FindResource("FlatButtonStyle"), Width = 120 };
        apply.Click += Apply_Click;

        var maxPerf = new Button
        {
            Content = "Max performance",
            Style = (Style)FindResource("FlatButtonStyle"),
            Width = 150,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "The SMU's own built-in maximum-performance profile.",
        };
        maxPerf.Click += (_, _) => RunSingle(() => _tuning!.ApplyMaxPerformance());

        var powerSave = new Button
        {
            Content = "Power saving",
            Style = (Style)FindResource("FlatButtonStyle"),
            Width = 140,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "The SMU's own built-in power-saving profile.",
        };
        powerSave.Click += (_, _) => RunSingle(() => _tuning!.ApplyPowerSaving());

        foreach (var b in new[] { apply, maxPerf, powerSave }) ApplyRow.Children.Add(b);
    }

    /// <summary>
    /// The measured capability matrix. Stated here rather than discovered at runtime because
    /// probing it means writing to every knob, and doing that on every visit to a tab would
    /// change machine state just to draw a table.
    /// </summary>
    private void BuildCapabilityRows()
    {
        (string Knob, string State, string Note)[] rows =
        {
            ("Thermal limit (Tctl)", "enforced", "Verified under load: a 75 C cap held the die at exactly 75.00 C, and raising it let the die climb. The one knob here that genuinely works."),
            ("Power limits (STAPM/fast/slow)", "accepted", "SMU returns OK and the PM table values change."),
            ("Skin / APU skin temp", "accepted", "SMU returns OK."),
            ("VRM current (TDC)", "accepted", "SMU returns OK."),
            ("Averaging windows", "accepted", "SMU returns OK."),
            ("Built-in SMU profiles", "accepted", "Max performance and power saving."),
            ("Curve Optimizer", "rejected", "SMU code 0xFF: a precondition is unmet, typically PBO disabled in firmware."),
            ("GFX clock", "rejected", "SMU code 0xFE: the SMU reports itself busy."),
            ("Enforcement of any of it", "ignored", "With a sustained limit reading 0.045 W the CPU still boosted to 4301 MHz. HP's firmware arbitrates power; the SMU's limits are advisory beneath it."),
        };

        foreach (var (knob, state, note) in rows) AddCapabilityRow(knob, state, note);
    }

    /// <summary>
    /// Re-runs the capability probe against this machine and replaces the baseline table with
    /// what the firmware actually answered just now.
    ///
    /// This is the difference from UXTU worth having. UXTU sends a command, sees the SMU
    /// acknowledge it, and reports success -- which on this platform is misleading, because
    /// the SMU returns OK for limits HP's firmware then arbitrates away. A hardcoded table of
    /// findings is only marginally better: it goes stale the moment the BIOS is updated, and
    /// it was never true of anyone else's machine to begin with.
    ///
    /// The probe is safe by construction -- see AmdTuning.ProbeCapabilities -- because every
    /// knob is re-sent the value it already holds. It still costs a round of SMU round trips,
    /// so it runs off the UI thread and only when asked.
    /// </summary>
    private void ProbeCaps_Click(object sender, RoutedEventArgs e)
    {
        if (_tuning is not { } tuning) return;

        ProbeCapsBtn.IsEnabled = false;
        CapabilityStatus.Text = "Asking the firmware...";
        CapabilityStatus.Foreground = (Brush)FindResource("TextMutedBrush");

        Task.Run(tuning.ProbeCapabilities).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            ProbeCapsBtn.IsEnabled = true;

            if (t.IsFaulted)
            {
                CapabilityStatus.Text = t.Exception?.GetBaseException().Message ?? "The probe failed.";
                return;
            }

            CapabilityRows.Children.Clear();
            foreach (var p in t.Result) AddCapabilityRow(p.Knob, p.State, p.Note);

            // Enforcement is a separate question from acceptance and cannot be answered by a
            // round of no-op writes -- it needs a sustained load. Kept on the end so a table
            // full of "accepted" is never mistaken for a table full of working knobs.
            AddCapabilityRow("Enforcement of any of it", "ignored",
                "Not probed here: acceptance and enforcement are different questions, and telling them apart needs a sustained load. Measured previously on this model, a sustained limit reading 0.045 W still let the CPU boost to 4301 MHz.");

            CapabilityStatus.Text = $"Measured just now: {t.Result.Count(r => r.State == "accepted")} accepted, "
                                  + $"{t.Result.Count(r => r.State is "refused" or "busy")} refused, "
                                  + $"{t.Result.Count(r => r.State == "not probed")} not probed. Nothing was changed.";
            CapabilityStatus.Foreground = (Brush)FindResource("TextMutedBrush");
        }), TaskScheduler.Default);
    }

    private void AddCapabilityRow(string knob, string state, string note)
    {
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock { Text = knob, Style = (Style)FindResource("BodyText") };
            var status = new TextBlock
            {
                Text = state.ToUpperInvariant(),
                Style = (Style)FindResource("MutedText"),
                Foreground = (Brush)FindResource(state switch
                {
                    // Green means the hardware demonstrably obeyed. "Accepted" is amber on
                    // purpose: the SMU returned success and nothing changed, which is the
                    // least useful of the three outcomes and should not look like a win.
                    "enforced" => "GoodBrush",
                    "accepted" => "WarnBrush",
                    "refused" => "DangerBrush",
                    _ => "TextFaintBrush",
                }),
            };
            var detail = new TextBlock
            {
                Text = note,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(status, 1);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(name);
            grid.Children.Add(status);
            grid.Children.Add(detail);
            CapabilityRows.Children.Add(grid);
        }
    }

    private void AddRow(Panel host, string key, string label, double min, double max, double value,
                        string unit, string tip, bool enabled = false, bool showEnable = true)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5), ToolTip = tip };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

        var enable = new CheckBox
        {
            Style = (Style)FindResource("OmniCheckBoxStyle"),
            IsChecked = enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = showEnable ? Visibility.Visible : Visibility.Hidden,
        };
        var name = new TextBlock { Text = label, Style = (Style)FindResource("MutedText"), VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Style = (Style)FindResource("OmniSliderStyle"),
            Minimum = min,
            Maximum = max,
            Value = value,
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var readout = new TextBlock
        {
            Text = $"{value:0} {unit}",
            Style = (Style)FindResource("BodyText"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Touching a slider is itself the intent to set that knob, so it ticks its own box.
        // Requiring both actions would mean silently dropped changes.
        slider.ValueChanged += (_, _) =>
        {
            readout.Text = $"{slider.Value:0} {unit}";
            if (showEnable) enable.IsChecked = true;
        };

        Grid.SetColumn(name, 1);
        Grid.SetColumn(slider, 2);
        Grid.SetColumn(readout, 3);
        grid.Children.Add(enable);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(readout);
        host.Children.Add(grid);

        _sliders[key] = slider;
        _enables[key] = enable;
    }

    // ---------------------------------------------------------------- behaviour

    private int? Value(string key) =>
        _enables.TryGetValue(key, out var box) && box.IsChecked == true && _sliders.TryGetValue(key, out var s)
            ? (int)Math.Round(s.Value)
            : null;

    private void Set(string key, int? value)
    {
        if (value is not int v) return;
        if (_sliders.TryGetValue(key, out var s)) s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
        if (_enables.TryGetValue(key, out var e)) e.IsChecked = true;
    }

    private AmdTuningProfile CurrentProfile() => new(
        "Custom", "The sliders as they stand",
        StapmWatts: Value("stapm"),
        FastWatts: Value("fast"),
        SlowWatts: Value("slow"),
        StapmTimeSeconds: Value("stapmTime"),
        SlowTimeSeconds: Value("slowTime"),
        TctlTempC: Value("tctl"),
        SkinTempC: Value("skin"),
        ApuSkinTempC: Value("apuSkin"),
        VrmCurrentAmps: Value("vrm"),
        CurveOptimizerAllCore: Value("co"),
        GfxClockMhz: Value("gfx"));

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AmdTuningProfile p }) return;

        Set("stapm", p.StapmWatts);
        Set("fast", p.FastWatts);
        Set("slow", p.SlowWatts);
        Set("stapmTime", p.StapmTimeSeconds);
        Set("slowTime", p.SlowTimeSeconds);
        Set("tctl", p.TctlTempC);
        Set("skin", p.SkinTempC);
        Set("apuSkin", p.ApuSkinTempC);
        Set("vrm", p.VrmCurrentAmps);
        Set("gfx", p.GfxClockMhz);

        // Curve Optimizer is deliberately not loaded from a preset. An undervolt stable on one
        // chip crashes the next, so it stays whatever the user chose.
        RunProfile(p with { CurveOptimizerAllCore = Value("co") });
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => RunProfile(CurrentProfile());

    /// <summary>
    /// Applies a profile in the background, returning a task that completes once the UI has
    /// been updated. Callers that must act AFTER the hardware has been written need that
    /// handle -- a fire-and-forget call returns while the SMU writes are still queued.
    /// </summary>
    private Task RunProfile(AmdTuningProfile profile)
    {
        if (_tuning is null) return Task.CompletedTask;
        var tuning = _tuning;

        ResultText.Text = $"Applying {profile.Name}...";
        ResultText.Foreground = (Brush)FindResource("TextFaintBrush");
        StepList.Items.Clear();

        return Task.Run(() => tuning.Apply(profile)).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            if (t.IsFaulted)
            {
                ResultText.Text = t.Exception?.GetBaseException().Message ?? "Tuning failed.";
                return;
            }

            var report = t.Result;
            ResultText.Text = report.Summary;

            // Three outcomes, not two: moved, demonstrably did not move, and nothing readable
            // to check against. Painting the third as success would be a claim not in evidence.
            ResultText.Foreground = (Brush)FindResource(report.Verified switch
            {
                true => "GoodBrush",
                false => "DangerBrush",
                null => "TextFaintBrush",
            });

            foreach (var step in report.Steps)
                StepList.Items.Add(new TextBlock
                {
                    Text = $"{(step.Sent ? "OK" : "--")}   {step.Name}: {step.Detail}",
                    Style = (Style)FindResource("MutedText"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                    Foreground = (Brush)FindResource(step.Sent ? "TextFaintBrush" : "DangerBrush"),
                });
        }));
    }

    private void RunSingle(Func<TuningResult> action)
    {
        if (_tuning is null) return;
        StepList.Items.Clear();
        Task.Run(action).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            var r = t.IsFaulted
                ? new TuningResult(false, t.Exception?.GetBaseException().Message ?? "Failed.")
                : t.Result;
            ResultText.Text = r.Detail;
        }));
    }

    /// <summary>
    /// Greys out everything that is not in charge, so it is obvious which controls are
    /// actually driving the processor rather than all of them looking equally live.
    ///
    /// Leaving adaptive stops the controller but deliberately does NOT undo the thermal limit
    /// it set. That limit is a real, enforced setting the user asked for; silently raising it
    /// again on a mode change would be undoing their choice behind their back. The slider
    /// still shows it, so it is visible and theirs to change.
    /// </summary>
    private void UpdateModeAvailability()
    {
        bool adaptive = _adaptiveEnabled?.IsChecked == true;

        // Every manual control goes dead while adaptive is on, not just the Apply button.
        // The controller owns the sustained limit and the thermal limit continuously, so an
        // Apply landing on top of it is two writers for one register -- and sliders that
        // looked live while a controller quietly overrode them is what made switching modes
        // feel unpredictable.
        ApplyRow.IsEnabled = !adaptive;
        PresetRow.IsEnabled = !adaptive;
        PowerRows.IsEnabled = !adaptive;
        ThermalRows.IsEnabled = !adaptive;
        GfxRows.IsEnabled = !adaptive;

        AdaptiveBody.IsEnabled = adaptive;
    }

    /// <summary>
    /// Pushes the GPU power state to HP's BIOS.
    ///
    /// Eco deliberately turns both off rather than leaving them alone: this is the discrete
    /// GPU's power ceiling, and a machine asked to save power that quietly kept Dynamic Boost
    /// running would be ignoring the request where it costs the most watts.
    /// </summary>
    private void SaveAdaptiveSettings()
    {
        if (_suppressStartupEvents) return;

        // Kept ordered: a floor above the ceiling would make the controller's clamp collapse
        // to a single value and it would stop responding to temperature entirely.
        int min = (int)_sliders["adaptiveMin"].Value;
        int max = (int)_sliders["adaptiveMax"].Value;

        int target = (int)_sliders["adaptiveTarget"].Value;
        _settings.AdaptiveTargetTempC = target;
        _settings.AdaptiveMinWatts = Math.Min(min, max);
        _settings.AdaptiveMaxWatts = Math.Max(min, max);

        // Re-apply live while adaptive is running. Without this the target only took effect on
        // the next start, which is precisely why moving the slider appeared to do nothing.
        if (_adaptive is { IsRunning: true } && _tuning is not null)
            QueueThermalLimit(target);

        _settings.Save();
    }

    // Debounce for the adaptive target slider.
    //
    // ValueChanged fires for every intermediate position of a drag, and this applied the thermal
    // limit inline on the UI thread. That is an SMU mailbox transaction, which spins on
    // Thread.Sleep(1) up to a thousand times, twice, waiting for the mailbox to answer -- so
    // dragging the slider issued one of those per pixel of travel and froze the window.
    //
    // Same shape as OptimizeView's brightness debounce, and for the same reason: only the value
    // the user settles on is written. The difference is that the write also leaves the UI thread,
    // because a single one of these can block for seconds by itself.
    private System.Windows.Threading.DispatcherTimer? _targetDebounce;
    private int _pendingTarget = -1;

    private void QueueThermalLimit(int target)
    {
        _pendingTarget = target;

        _targetDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _targetDebounce.Stop();
        _targetDebounce.Tick -= ApplyPendingTarget;
        _targetDebounce.Tick += ApplyPendingTarget;
        _targetDebounce.Start();
    }

    private void ApplyPendingTarget(object? sender, EventArgs e)
    {
        _targetDebounce?.Stop();
        if (_tuning is not { } tuning) return;

        int target = _pendingTarget;

        Task.Run(() => tuning.SetThermalLimitC(target)).ContinueWith(t =>
        {
            // A faulted call is reported as a refusal rather than swallowed: the whole point of
            // this tab is telling a limit the firmware accepted from one it did not.
            var r = t.IsFaulted
                ? new TuningResult(false, t.Exception?.GetBaseException().Message ?? "the call failed")
                : t.Result;

            _settings.ThermalLimitC = target;
            Set("tctl", target);
            ResultText.Text = r.Applied
                ? $"Holding {target} C via the thermal limit."
                : $"Thermal limit {target} C refused: {r.Detail}";
            _settings.Save();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// Starts the adaptive controller from the current slider values. Shared by the button and
    /// by startup, so the two cannot drift apart in what they configure.
    /// </summary>
    private void StartAdaptive()
    {
        if (_tuning is null) return;

        // The target drives the THERMAL LIMIT first, because that is the knob measured to be
        // enforced on this platform. The power-steering loop below chases the same target by
        // adjusting sustained watts, which this firmware ignores -- so on its own it would
        // leave "max temp 90" doing precisely nothing, which is how it looked broken.
        int target = (int)_sliders["adaptiveTarget"].Value;
        var capped = _tuning.SetThermalLimitC(target);
        Set("tctl", target);
        _settings.ThermalLimitC = target;
        _settings.Save();

        ResultText.Text = capped.Applied
            ? $"Holding {target} C via the thermal limit."
            : $"Thermal limit {target} C refused: {capped.Detail}";

        _adaptive = new AdaptiveTuning(_tuning, () => _ctx.CurrentTemperature().Celsius)
        {
            TargetTempC = (int)_sliders["adaptiveTarget"].Value,
            MinWatts = (int)_sliders["adaptiveMin"].Value,
            MaxWatts = (int)_sliders["adaptiveMax"].Value,

            // The discrete GPU shares this chassis' heatpipe and fans, so the CPU limit is not
            // the CPU's business alone. Read through GpuTelemetry's cache, which is refreshed in
            // the background, so the controller's tick never waits on a process launch.
            ReadGpu = () =>
            {
                var g = GpuTelemetry.Read();
                return new GpuDemand(g?.UtilisationPercent, g?.TempC, g?.PowerWatts);
            },
        };

        _adaptive.OnTick += (temp, watts) => Dispatcher.Invoke(() =>
        {
            if (_adaptive?.StoppedReason is { } reason)
            {
                ResultText.Text = reason;

                // The controller self-stopped, so the switch has to follow it. Otherwise the
                // box stays ticked describing something that is no longer running.
                if (_adaptiveEnabled is not null) SetMode(TuningMode.Manual);
                return;
            }
            ResultText.Text = $"Adaptive: {temp:0.0} C, holding {watts} W sustained.";
            ResultText.Foreground = (Brush)FindResource("TextFaintBrush");
        });

        _adaptive.Start();
    }

    /// <summary>
    /// Refreshes the live readouts. Two seconds, not faster: each tick refreshes and re-reads
    /// the SMU power table, and there is nothing here worth contending with the fan curve's
    /// own SMU access for.
    /// </summary>
    private void StartLiveUpdates()
    {
        // Only while this tab is actually on screen.
        //
        // Every view in this app is constructed once at startup and kept alive, so a timer
        // started in the constructor runs for the whole session regardless of which tab is
        // showing. Each tick here re-resolves and re-reads the SMU power table through the
        // kernel driver, which is not free, and doing it forever to update readouts nobody is
        // looking at is pure waste.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _liveTimer?.Start();
            else _liveTimer?.Stop();
        };

        // Four seconds, not two. This is a readout a person glances at, and it shares the
        // driver with the fan curve's own temperature reads.
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _liveTimer.Tick += (_, _) =>
        {
            var tuning = _tuning;
            if (tuning is null) return;

            Task.Run(() => (Power: tuning.ReadPower(), Temp: TryTemp(), Gpu: GpuTelemetry.Read())).ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                Dispatcher.Invoke(() =>
                {
                    var p = t.Result.Power;
                    const string NA = "unavailable";

                    _liveStapm!.Text = p is null ? NA : $"{p.StapmWatts:0.0} W  of  {p.StapmLimitWatts:0} W";
                    _liveFast!.Text = p is null ? NA : $"{p.FastWatts:0.0} W  of  {p.FastLimitWatts:0} W";
                    _liveSlow!.Text = p is null ? NA : $"{p.SlowWatts:0.0} W  of  {p.SlowLimitWatts:0} W";
                    _liveTdc!.Text = p is null ? NA : $"{p.TdcVddAmps:0.0} A  of  {p.TdcVddLimitAmps:0} A";
                    _liveEdc!.Text = p is null ? NA : $"{p.EdcVddAmps:0.0} A  of  {p.EdcVddLimitAmps:0} A";
                    _liveSocCurrent!.Text = p is null ? NA
                        : $"TDC {p.TdcSocAmps:0.0}/{p.TdcSocLimitAmps:0} A   EDC {p.EdcSocAmps:0.0}/{p.EdcSocLimitAmps:0} A";
                    _liveCoreTemp!.Text = p is null ? NA : $"{p.CoreTempC:0.0} C  of  {p.ThermalLimitC:0} C";
                    _liveSocTemp!.Text = p is null ? NA : $"{p.SocTempC:0.0} C  /  {p.GfxTempC:0.0} C";
                    _liveTemp!.Text = t.Result.Temp is double c ? $"{c:0.0} C" : NA;

                    if (_liveGpu is not null)
                    {
                        var g = t.Result.Gpu;
                        _liveGpu.Text = g is null ? NA
                            : $"{g.TempC:0} C   {g.PowerWatts:0.0} W   {g.ClockMhz} MHz   {g.UtilisationPercent}%";
                    }

                    // The headline: which ceiling the processor is actually pressed against.
                    // A wattage on its own says nothing about whether more power would help.
                    if (p is null) _liveLimiting!.Text = NA;
                    else
                    {
                        var (name, percent) = p.TightestLimit();
                        _liveLimiting!.Text = $"{name}  -  {percent:0}% of limit";
                        _liveLimiting.Foreground = (Brush)FindResource(
                            percent >= 95 ? "DangerBrush" : percent >= 80 ? "WarnBrush" : "TextFaintBrush");
                    }
                });
            });
        };

        // Deliberately NOT started here. The visibility handler above starts it when the tab
        // is first shown.
        if (IsVisible) _liveTimer.Start();
    }

    private double? TryTemp()
    {
        try { return _ctx.CurrentTemperature().Celsius; }
        catch { return null; }
    }

    /// <summary>
    /// Shuts down everything this view started.
    ///
    /// Auto eco comes first and is the reason this exists at all: it changes the display
    /// refresh rate, and its Stop() is what puts the panel back. Without a dispose reaching
    /// it, closing OmniHub while eco was engaged left the machine at 60 Hz with nothing on
    /// screen to explain it and no obvious way to undo it short of a display-settings hunt.
    ///
    /// Adaptive tuning is stopped too, so a controller is not left steering power limits after
    /// the window it belongs to has gone.
    /// </summary>
    public void Dispose()
    {
        try { _autoEco?.Dispose(); } catch { }
        try { _adaptive?.Dispose(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        try { _processWatcher?.Dispose(); } catch { }
        try { _liveTimer?.Stop(); } catch { }
    }
}

