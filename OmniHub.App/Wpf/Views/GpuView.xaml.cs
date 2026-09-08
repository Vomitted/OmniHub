using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using OmniHub.Core.Hardware;

namespace OmniHub.App.Wpf.Views;

public partial class GpuView : UserControl
{
    private readonly HardwareContext _ctx;
    private readonly AppSettings _settings;

    public GpuView(HardwareContext ctx, AppSettings settings)
    {
        InitializeComponent();
        _ctx = ctx;
        _settings = settings;
        ModeCombo.ItemsSource = new[] { GpuMode.Hybrid, GpuMode.Discrete, GpuMode.Optimus };

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
        if (_settings.GpuMaxPower) ApplyMaxPower();

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
}
