// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using OmniHub.Core.Hardware;

namespace OmniHub.App.Wpf.Views;

public partial class GpuView : UserControl
{
    private readonly HardwareContext _ctx;
    private readonly AppSettings _settings;

    /// <summary>
    /// Two seconds, and only while this tab is on screen.
    ///
    /// GpuTelemetry caches for three, so most ticks are a field read rather than a query, and the
    /// timer stops entirely when the tab is not visible -- the card should not be woken to draw a
    /// readout nobody is looking at.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _telemetryTimer =
        new() { Interval = TimeSpan.FromSeconds(2) };

    public GpuView(HardwareContext ctx, AppSettings settings, MetricSource metrics)
    {
        InitializeComponent();
        _ctx = ctx;
        _settings = settings;
        ModeCombo.ItemsSource = new[] { GpuMode.Hybrid, GpuMode.Discrete, GpuMode.Optimus };

        // The card's readings, from the one shared source every table reads, and drawn from the same.
        SensorsHost.Content = new Controls.SensorTable(metrics, new[] { "gpu", "gpuclk", "gpuload", "gpuw" });
        _metrics = metrics;
        Loaded += (_, _) => { metrics.Updated -= ShowGauges; metrics.Updated += ShowGauges; ShowGauges(); };
        Unloaded += (_, _) => metrics.Updated -= ShowGauges;
        SizeChanged += (_, e) => ColumnReflow.Apply(e.NewSize.Width, below: 780, Gutter, SideColumn, sideWidth: 250, Side);

        // Paired on Loaded/Unloaded, as DashboardView and FansView are, and for the same reason:
        // navigating away detaches the control and a constructor-time subscription would be
        // cancelled the first time the user left the page and never restored.
        Loaded += (_, _) => { RefreshTelemetry(); _telemetryTimer.Start(); };
        Unloaded += (_, _) => _telemetryTimer.Stop();

        _telemetryTimer.Tick += (_, _) => RefreshTelemetry();

        // Gated on what the firmware says, rather than on the assumption that every HP board has
        // a MUX. These three options were offered unconditionally on every machine, including
        // ones with no switchable graphics at all -- and this is the most destructive control in
        // the application, so offering it where it cannot work matters more here than elsewhere.
        //
        // Only a stated denial disables it. A board that does not answer keeps the control; see
        // HpSystemData.Denies for why unknown must not switch anything off.
        if (!ctx.GpuModeSwitchAllowed)
        {
            ModeCombo.IsEnabled = false;
            ChangeModeBtn.IsEnabled = false;
            ModeUnsupportedNote.Visibility = Visibility.Visible;
            ModeUnsupportedNote.Text =
                "This machine's firmware reports no switchable graphics, so the mode cannot be "
                + "changed here. The power preset above is unaffected.";
        }

        // Applied here, in the constructor, rather than from the Tuning tab's startup block.
        // MainWindow builds this view during startup, so the unlock lands without waiting for
        // anyone to open a tab -- and the ForceMaxPower latch means it no longer matters
        // whether a preset write happens before or after it.
        MaxPowerCheck.IsChecked = _settings.GpuMaxPower;

        // Not while unplugged.
        //
        // The TGP unlock is a mains feature. Applying it from here meant that launching OmniHub
        // on battery wrote the unlock during startup, before the poll loop had taken its first
        // look at which rail the machine is on -- so the app spent its opening seconds holding up
        // a card it exists to let sleep, and only ReassertGpuPower noticing the rail afterwards
        // undid it. Skipping it here costs nothing: the next tick after the charger goes back in
        // sees the rail change and applies it.
        if (_settings.GpuMaxPower &&
            OmniHub.Core.Optimize.PowerSourceWatcher.Read() != OmniHub.Core.Optimize.PowerSource.Battery)
            ApplyMaxPower();

        MaxPowerCheck.Checked += MaxPowerChanged;
        MaxPowerCheck.Unchecked += MaxPowerChanged;

        RefreshMode();
    }

    private void MaxPowerChanged(object sender, RoutedEventArgs e)
    {
        _settings.GpuMaxPower = MaxPowerCheck.IsChecked == true;
        _settings.Save();
        ApplyMaxPower();
    }

    /// <summary>
    /// Pushes the GPU power ceiling to HP's BIOS and latches it against the preset buttons.
    ///
    /// Moved here from the Tuning tab, where it sat under "On startup" -- it is a graphics
    /// setting, and the presets it overrides are a few inches further down this same page.
    /// </summary>
    private void ApplyMaxPower()
    {
        bool on = _settings.GpuMaxPower;

        // Set BEFORE the write, and set to `on` rather than simply raised, so switching the
        // option off can still write the stock values through. While it is on, every other
        // writer of this register -- the presets below, the dashboard's mode buttons, the
        // Optimize tab's performance profiles -- is clamped to full power.
        _ctx.Gpu.ForceMaxPower = on;

        // The preset selector below cannot do anything while the latch is on, so it is
        // disabled rather than left looking live and silently ignored.
        PresetCard.IsEnabled = !on;

        // The BIOS work runs OFF the UI thread.
        //
        // This is a write followed by a read, and BIOS round trips are the slowest thing this
        // app does. It was called straight from the constructor, so both of them blocked the
        // window from appearing at launch, and again from the checkbox handler, so ticking the
        // box froze the UI mid-click. Neither needed to be synchronous: nothing on screen
        // depends on the result until the result exists.
        Task.Run(() =>
        {
            try
            {
                _ctx.Gpu.SetPower(new GpuPowerData(
                    on ? GpuCustomTgp.On : GpuCustomTgp.Off,
                    on ? GpuPpab.On : GpuPpab.Off,
                    GpuDState.D1,
                    0));

                // Read back rather than trust the write, as everywhere else on this page.
                var actual = _ctx.Gpu.GetPower();
                bool applied = (actual.CustomTgp == GpuCustomTgp.On) == on;
                return (Text: applied
                    ? $"GPU power {(on ? "unlocked" : "restored to stock")} (Custom TGP {actual.CustomTgp}, Boost {actual.Ppab})."
                    : $"Change did not take: BIOS still reports {actual}.", Ok: applied);
            }
            catch (Exception ex)
            {
                return (Text: $"GPU power change refused: {ex.Message}", Ok: false);
            }
        }).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.IsFaulted) return;
            MaxPowerResult.Text = t.Result.Text;
        }), TaskScheduler.Default);
    }

    // Set while the checked pill is being synced from a hardware read, so restoring the UI to
    // match the GPU never re-issues the command that was just read back.
    private bool _suppressPresetEvent;

    private void EcoBtn_Click(object sender, RoutedEventArgs e) => ApplyPreset(GpuPowerLevel.Eco);
    private void BalancedBtn_Click(object sender, RoutedEventArgs e) => ApplyPreset(GpuPowerLevel.Balanced);
    private void PerformanceBtn_Click(object sender, RoutedEventArgs e) => ApplyPreset(GpuPowerLevel.Performance);

    private void ApplyPreset(GpuPowerLevel level)
    {
        if (_suppressPresetEvent) return;
        PresetResult.Text = "Applying...";
        PresetResult.Foreground = (Brush)FindResource("TextMutedBrush");

        Task.Run(() =>
        {
            try
            {
                _ctx.Gpu.SetPowerPreset(level);
                // Read back rather than trust the write. The BIOS can accept a command and
                // apply something else, and the pill must end up showing the GPU's state, not
                // the button that was pressed.
                return (Ok: true, Applied: ReadPreset(), Error: "");
            }
            catch (Exception ex)
            {
                return (Ok: false, Applied: (GpuPowerLevel?)null, Error: ex.Message);
            }
        }).ContinueWith(t =>
        {
            var (ok, applied, error) = t.Result;
            Dispatcher.Invoke(() =>
            {
                if (!ok)
                {
                    PresetResult.Text = error;
                }
                else if (applied is GpuPowerLevel actual)
                {
                    PresetResult.Text = actual == level
                        ? $"{actual} applied."
                        : $"Requested {level}, but the GPU reports {actual}.";
                }
                else
                {
                    PresetResult.Text = $"{level} sent; the GPU did not report its state back.";
                }

                SyncPresetPills(applied);
                RefreshMode();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Derives the active preset from the flags the BIOS reports. SetPowerPreset builds its
    /// payload from these same two flags (see GpuPowerData's level constructor), so the
    /// mapping is exact rather than inferred.
    /// </summary>
    private GpuPowerLevel? ReadPreset()
    {
        try
        {
            var power = _ctx.Gpu.GetPower();
            if (power.CustomTgp == GpuCustomTgp.Off) return GpuPowerLevel.Eco;
            return power.Ppab == GpuPpab.On ? GpuPowerLevel.Performance : GpuPowerLevel.Balanced;
        }
        catch { return null; }
    }

    private void SyncPresetPills(GpuPowerLevel? level)
    {
        _suppressPresetEvent = true;
        EcoBtn.IsChecked = level == GpuPowerLevel.Eco;
        BalancedBtn.IsChecked = level == GpuPowerLevel.Balanced;
        PerformanceBtn.IsChecked = level == GpuPowerLevel.Performance;
        _suppressPresetEvent = false;
    }

    // GetMode() is a synchronous BIOS call. Called both from the constructor (blocking
    // Dashboard-style startup on real hardware I/O -- missed in the original freeze-fix
    // pass) and, previously, via Dispatcher.Invoke from ChangeModeBtn_Click, which
    // forced this same synchronous call right back onto the UI thread after a mode
    // change. Making it internally async fixes both call sites at once; callers can
    // invoke it directly from either the UI thread or a background thread now.
    private void RefreshMode()
    {
        Task.Run(() =>
        {
            string modeText = "Unavailable";
            GpuMode? mode = null;
            try
            {
                mode = _ctx.Gpu.GetMode();
                modeText = mode.Value.ToString();
            }
            catch { }

            // Read separately from the mode: GetPower can fail while GetMode succeeds, and one
            // unavailable reading should not blank out the other.
            string powerText = "--";
            string powerFoot = "NOT REPORTED";
            GpuPowerLevel? preset = null;
            try
            {
                var power = _ctx.Gpu.GetPower();
                powerText = power.CustomTgp == GpuCustomTgp.On ? "Custom TGP" : "Base TGP";
                powerFoot = $"TGP {power.CustomTgp} / BOOST {power.Ppab}".ToUpperInvariant();
                preset = power.CustomTgp == GpuCustomTgp.Off ? GpuPowerLevel.Eco
                    : power.Ppab == GpuPpab.On ? GpuPowerLevel.Performance
                    : GpuPowerLevel.Balanced;
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                // If the flags could not be read, every pill stays unchecked rather than one
                // being guessed at. An unselected group is honest; a wrong selection is not.
                SyncPresetPills(preset);

                ModeText.Text = modeText;
                ModeFoot.Text = mode.HasValue
                    ? (mode.Value == GpuMode.Discrete ? "DISCRETE DRIVES THE PANEL" : "IGPU DRIVES THE PANEL")
                    : "COULD NOT READ THE BIOS";

                PowerText.Text = powerText;
                PowerFoot.Text = powerFoot;

                if (mode.HasValue) ModeCombo.SelectedItem = mode.Value;
            });
        });
    }

    private void ChangeModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ModeCombo.SelectedItem is not GpuMode selected) return;
        var confirm = System.Windows.MessageBox.Show(
            $"Switch to {selected}? This requires a reboot to take effect and carries the display-output risk described above.",
            "Confirm graphics mode change", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Task.Run(() =>
        {
            try { _ctx.Gpu.SetMode(selected); }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show(ex.Message, "GPU command failed", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            RefreshMode();
        });
    }

    // Every action passed here is a synchronous BIOS call -- run off the UI
    // thread so a button click doesn't freeze the window while it completes.
    private void SafeCall(Action a)
    {
        Task.Run(() =>
        {
            try { a(); }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show(ex.Message, "GPU command failed", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        });
    }

    /// <summary>
    /// Draws what the card is doing, and says where the numbers came from.
    ///
    /// The source is on screen because the three routes do not agree: NVML and nvidia-smi read the
    /// driver, the Windows counters read the compositor's view, and a project whose first rule is
    /// that a reading names its source should not make an exception for the one tab about the
    /// hardware in question.
    ///
    /// Every field is independently nullable and renders "--" on its own. The vendor-neutral path
    /// reports utilisation and nothing else, so a card showing three dashes and a percentage is
    /// the expected output there rather than a fault.
    /// </summary>
    private void RefreshTelemetry()
    {
        var gpu = GpuTelemetry.Read();

        if (gpu is null)
        {
            GpuNameText.Text = "No discrete GPU reading";
            GpuSourceText.Text = "";

            // Distinguishes the two reasons for a blank, because they call for different actions:
            // one is a policy this application applies deliberately, the other is a card or driver
            // that did not answer.
            GpuLiveFoot.Text = OmniHub.Core.Optimize.PowerSourceWatcher.Read() == OmniHub.Core.Optimize.PowerSource.Battery
                ? "ON BATTERY - THE CARD IS NOT WOKEN FOR A READOUT"
                : "NO READING - NO DISCRETE GPU, OR THE DRIVER DID NOT ANSWER";
            return;
        }

        GpuNameText.Text = gpu.Name;
        GpuSourceText.Text = $"read via {SourceName(gpu.Source)}";

        GpuLiveFoot.Text = gpu.Source == GpuSource.WindowsCounters
            ? "UTILISATION ONLY - THE WINDOWS COUNTERS DO NOT REPORT TEMPERATURE, POWER OR CLOCK"
            : "";
    }

    private readonly MetricSource _metrics;

    /// <summary>The card drawn: temperature as a ring, load and power as bars against their real limits.</summary>
    private void ShowGauges()
    {
        GpuRing.ShowTemperature(_metrics, "gpu");

        double? load = _metrics.Value("gpuload"), watts = _metrics.Value("gpuw"), limit = Nvml.KnownPowerCeilingWatts;
        GpuLoadMeter.Show(load is { } l ? FormattableString.Invariant($"{l:0}%") : "--",
                          OmniHub.Core.Telemetry.Gauge.Fraction(load, 100));
        GpuPowerMeter.Show(watts is not { } w ? "--"
                           : limit is { } c ? FormattableString.Invariant($"{w:0.0} / {c:0} W")
                           : FormattableString.Invariant($"{w:0.0} W"),
                           OmniHub.Core.Telemetry.Gauge.Fraction(watts, limit));
    }

    private static string SourceName(GpuSource source) => source switch
    {
        GpuSource.Nvml => "NVML",
        GpuSource.NvidiaSmi => "nvidia-smi",
        GpuSource.WindowsCounters => "Windows counters",
        _ => source.ToString(),
    };
}
