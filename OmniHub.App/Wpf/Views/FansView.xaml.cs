using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.App.Wpf.Views;

public partial class FansView : UserControl
{
    private readonly HardwareContext _ctx;
    private readonly FanService _service;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<CurvePointRow> _rows;
    private bool _suppressModeEvent;

    public event Action? ModeChanged;

    public FansView(HardwareContext ctx, FanService service, AppSettings settings)
    {
        InitializeComponent();
        _ctx = ctx; _service = service; _settings = settings;

        // Titles and accents are declared in XAML now. The three StatTiles were replaced by
        // plain cards so a long value like "BIOS Default" sizes to its content instead of
        // being clipped by a fixed 176px tile.

        _rows = new ObservableCollection<CurvePointRow>(
            settings.CurvePoints.Select(p => new CurvePointRow { TempC = p.TempC, LevelPercent = p.LevelPercent }));
        CurveGrid.ItemsSource = _rows;

        FloorTempBox.Text = settings.FloorTempC.ToString("0");
        FloorLevelBox.Text = settings.FloorLevelPercent.ToString();

        RefreshChartFromSettings();
        SetActiveModeButton(settings.FanControlMode);
        UpdateStatusTile(settings.FanControlMode);

        // Says what the percentage scale actually means on this chassis. Without it, "12%"
        // reads as "nearly off" when it is in fact 1500 RPM and clearly audible.
        CurveScaleNote.Text =
            $"Level is a percentage of this chassis's measured fan band, {FanService.MinRpm}-{FanService.MaxRpm} RPM. "
            + $"0% stops the fans and hands them back to the BIOS curve; 1% is already {FanService.MinRpm} RPM; "
            + $"100% is {FanService.MaxRpm} RPM on both fans. Raw is the byte sent to the controller, which is RPM/100 "
            + "and is what the manual calibration below steps through.";

        // Paired on Loaded/Unloaded, not subscribed once in the constructor -- see the same
        // change in DashboardView. Navigating to another tab detaches this control and raises
        // Unloaded, so a constructor-time subscription was cancelled the first time the user
        // left the page and never restored, freezing the live curve chart and the RPM readout.
        Loaded += (_, _) =>
        {
            ctx.OnReading -= OnHardwareReading;
            ctx.OnReading += OnHardwareReading;
            service.OnTick -= OnServiceTick;
            service.OnTick += OnServiceTick;
        };
        Unloaded += (_, _) =>
        {
            ctx.OnReading -= OnHardwareReading;
            service.OnTick -= OnServiceTick;
        };
    }

    public void ApplySavedMode() => ApplyMode(_settings.FanControlMode);
    public void ApplyModeFromTray(FanControlMode mode) { ApplyMode(mode); SetActiveModeButton(mode); }

    private void ModeButtonChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;
        if (sender is RadioButton rb && rb.Tag is string tag && Enum.TryParse<FanControlMode>(tag, out var mode))
            ApplyMode(mode);
    }

    private void SetActiveModeButton(FanControlMode mode)
    {
        _suppressModeEvent = true;
        AutoModeBtn.IsChecked = mode == FanControlMode.Auto;
        BiosModeBtn.IsChecked = mode == FanControlMode.BiosDefault;
        MaxModeBtn.IsChecked = mode == FanControlMode.Max;
        _suppressModeEvent = false;
    }

    // Every branch here is a synchronous BIOS/WMI call (SetMaxFan, Start/Stop's own
    // hardware calls, SetFanMode). Run the whole thing off the UI thread -- called
    // directly from a RadioButton click or the tray menu, this was blocking the UI
    // for the full chain of hardware calls before, which read as "freezing."
    private void ApplyMode(FanControlMode mode)
    {
        // The one choke point every fan-mode change routes through -- the mode buttons, the
        // tray menu, and the saved-mode restore at startup -- so the check belongs here rather
        // than being repeated at each caller.
        //
        // On a machine without the vendor control interface there is nothing to drive. Saying
        // so once is far better than starting a curve loop that retries a BIOS call it can
        // never complete, every two seconds, for the life of the process.
        if (!_ctx.VendorSupported)
        {
            ModeValue.Text = "Unavailable";
            ModeFoot.Text = "NO VENDOR INTERFACE";
            return;
        }

        Task.Run(() =>
        {
            try
            {
                switch (mode)
                {
                    case FanControlMode.Auto:
                        SafeCall(() => _ctx.System.SetMaxFan(false));
                        _service.Start();
                        break;
                    case FanControlMode.BiosDefault:
                        if (_service.IsRunning) _service.Stop();
                        SafeCall(() => _ctx.System.SetMaxFan(false));
                        SafeCall(() => _ctx.Fan.SetFanMode(FanMode.Default));
                        break;
                    case FanControlMode.Max:
                        if (_service.IsRunning) _service.Stop();
                        SafeCall(() => _ctx.System.SetMaxFan(true));
                        break;
                }

                _settings.FanControlMode = mode;
                _settings.Save();
                Dispatcher.Invoke(() =>
                {
                    SetActiveModeButton(mode);
                    UpdateStatusTile(mode);
                    ModeChanged?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    System.Windows.MessageBox.Show(ex.Message, "Fan mode change failed", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        });
    }

    private void UpdateStatusTile(FanControlMode mode)
    {
        ModeValue.Text = mode switch
        {
            FanControlMode.Auto => "Curve",
            FanControlMode.BiosDefault => "BIOS Auto",
            FanControlMode.Max => "Max Fan",
            _ => "--",
        };
        ModeFoot.Text = mode switch
        {
            FanControlMode.Auto => "FLOOR-PROTECTED, RE-APPLIED EVERY TICK",
            FanControlMode.BiosDefault => "MAY IDLE AT 0% WHILE HOT",
            FanControlMode.Max => "PINNED TO MAXIMUM",
            _ => "",
        };

        // BIOS mode is the one that carries the known defect, so its footer is the one place
        // this card is allowed to use the warning colour.
        ModeFoot.Foreground = (Brush)FindResource(
            mode == FanControlMode.BiosDefault ? "WarnBrush" : "TextFaintBrush");
    }

    private void ApplyFloorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(FloorTempBox.Text, out var floorTemp) || !byte.TryParse(FloorLevelBox.Text, out var floorLevel))
        {
            System.Windows.MessageBox.Show("Enter valid numbers for the floor.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.FloorTempC = floorTemp;
        _settings.FloorLevelPercent = floorLevel;
        _settings.Save();
        _service.Curve.FloorTempC = floorTemp;
        _service.Curve.FloorLevelPercent = floorLevel;
        RefreshChartFromSettings();
    }

    private void ApplyCurveBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var points = _rows.Where(r => r.TempC >= 0)
                .Select(r => new CurvePoint(r.TempC, (byte)Math.Clamp((int)r.LevelPercent, 0, 100)))
                .ToList();
            if (points.Count < 2)
            {
                System.Windows.MessageBox.Show("Need at least 2 curve points.", "Curve not applied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _settings.CurvePoints = points;
            _settings.Save();
            _service.Curve.SetPoints(points);
            RefreshChartFromSettings();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not apply curve", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetCurveBtn_Click(object sender, RoutedEventArgs e)
    {
        var defaults = FanCurve.CreateDefault().Points;
        _rows.Clear();
        foreach (var p in defaults) _rows.Add(new CurvePointRow { TempC = p.TempC, LevelPercent = p.LevelPercent });
        _settings.CurvePoints = defaults.ToList();
        _settings.FloorTempC = 55.0;
        _settings.FloorLevelPercent = 15;
        FloorTempBox.Text = "55";
        FloorLevelBox.Text = "15";
        _settings.Save();
        _service.Curve.SetPoints(defaults);
        _service.Curve.FloorTempC = 55.0;
        _service.Curve.FloorLevelPercent = 15;
        RefreshChartFromSettings();
    }

    private void RefreshChartFromSettings()
    {
        Chart.Points = _settings.CurvePoints;
        Chart.FloorTempC = _settings.FloorTempC;
        Chart.FloorLevelPercent = _settings.FloorLevelPercent;
        Chart.RefreshData();
    }

    private void OnHardwareReading(Reading r)
    {
        // BeginInvoke: see DashboardView.OnReading. A synchronous Invoke from the poll thread
        // stalls the temperature poll for as long as the UI thread is busy, which stops the
        // fan curve.
        Dispatcher.BeginInvoke(() =>
        {
            SetTemperature(r.TemperatureC, r.Throttling == ThrottlingState.On);

            if (_settings.FanControlMode != FanControlMode.Auto)
            {
                // Raw is an RPM/100 target on a ~20-55 usable scale, not a 0-255 PWM duty
                // cycle -- see FanService.RawToPercent. The old raw/255*100 here was the
                // debunked PWM assumption and under-reported this tile by roughly half.
                int levelPercent = FanService.RawToPercent(r.FanLevel1);
                LevelValue.Text = levelPercent.ToString();
                // Both fans: this is a tachometer reading, not an echo of the commanded level,
                // so the two can differ from each other and from the curve's target while the
                // fans are still spinning up (measured: about six seconds for a full step).
                LevelFoot.Text = $"RAW {r.FanLevel1}/{r.FanLevel2} - " +
                                 $"{FanService.RawToRpm(r.FanLevel1)}/{FanService.RawToRpm(r.FanLevel2)} RPM";
                Chart.SetLive(r.TemperatureC, (byte)Math.Clamp(levelPercent, 0, 100));
            }
            else
            {
                // Auto mode. The big number is the curve's command, written by OnServiceTick;
                // this line is what the tachometer actually reads, plus the target it is
                // heading for. The two disagreeing is the fan spinning up -- measured at about
                // six seconds for a full step -- not a fault, which is exactly why both are
                // shown rather than just the one that happens to look tidier.
                string measured = $"{FanService.RawToRpm(r.FanLevel1)}/{FanService.RawToRpm(r.FanLevel2)} RPM";
                if (_service.HasCommanded)
                {
                    byte targetRaw = FanService.PercentToRawLevel(_service.LastCommandedLevelPercent);
                    LevelFoot.Text = $"NOW {measured} - CURVE WANTS {FanService.RawToRpm(targetRaw)} RPM (RAW {targetRaw})";
                }
                else
                {
                    LevelFoot.Text = $"NOW {measured} - CURVE HAS NOT TICKED YET";
                }

                // No Chart.SetLive here: in Auto the curve's own tick drives the chart with
                // the temperature it actually evaluated, which is the control temperature
                // (max of CPU and GPU) rather than the CPU reading in this Reading.
            }
        });
    }

    private void OnServiceTick(double tempC, byte levelPercent)
    {
        // BeginInvoke: this one is raised from inside the fan curve's own loop, so a blocked
        // UI thread would delay the next fan evaluation, not just the next repaint.
        Dispatcher.BeginInvoke(() =>
        {
            if (_settings.FanControlMode != FanControlMode.Auto) return;
            SetTemperature((int)Math.Round(tempC), throttling: false);
            LevelValue.Text = levelPercent.ToString();
            Chart.SetLive(tempC, levelPercent);

            // "COMMANDED BY CURVE" was all this said, which told you nothing you could act on.
            // The reading handler fills in measured versus target RPM on its own tick.
        });
    }

    // Same thresholds as the Dashboard's thermal card, so the two screens can never disagree
    // about what counts as hot.
    private void SetTemperature(int tempC, bool throttling)
    {
        TempValue.Text = tempC.ToString();
        TempFoot.Text = throttling ? "THROTTLING NOW" : "NOMINAL";

        // No amber tier -- see ThermalBrushFor in DashboardView. A warning colour that is on
        // through the machine's whole normal range stops being a warning.
        var brush = (Brush)FindResource(
            throttling || tempC >= 80 ? "DangerBrush"
            : "TextPrimaryBrush");

        TempValue.Foreground = brush;
        TempUnit.Foreground = brush;
        TempFoot.Foreground = throttling ? brush : (Brush)FindResource("TextFaintBrush");
    }

    // Same candidate levels and rationale as Program.cs's CLI -Calibrate mode: this
    // hardware family's usable raw range (20-55) is a well-sourced but *borrowed*
    // community bound, not something measured on this specific unit -- listening
    // through these candidates is how a user finds their own unit's real ceiling.
    private static readonly byte[] CalibrateCandidates = { 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 };
    private int _calibrateIndex;
    private bool _calibrating;

    private void StartCalibrate_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "This takes direct manual control of the fan and steps it through speed levels so you can listen. " +
            "Your current fan mode is paused until you click Stop & Restore.",
            "Start calibration", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.Yes) return;

        _calibrating = true;
        _calibrateIndex = 0;
        CalibrateIdlePanel.Visibility = Visibility.Collapsed;
        CalibrateActivePanel.Visibility = Visibility.Visible;

        Task.Run(() =>
        {
            if (_service.IsRunning) _service.Stop();
            SafeCall(() => _ctx.Fan.SetFanMode(FanMode.Performance));
            ApplyCalibrateLevel();
        });
    }

    private void ApplyCalibrateLevel()
    {
        byte raw = CalibrateCandidates[_calibrateIndex];
        SafeCall(() => _ctx.Fan.SetFanLevel(raw, raw));
        Dispatcher.Invoke(() => CalibrateStatusText.Text = $"Raw {raw} — listen now");
    }

    private void NextCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!_calibrating) return;
        _calibrateIndex++;
        if (_calibrateIndex >= CalibrateCandidates.Length)
        {
            StopCalibrate_Click(sender, e);
            return;
        }
        Task.Run(ApplyCalibrateLevel);
    }

    private void StopCalibrate_Click(object sender, RoutedEventArgs e)
    {
        _calibrating = false;
        CalibrateActivePanel.Visibility = Visibility.Collapsed;
        CalibrateIdlePanel.Visibility = Visibility.Visible;

        Task.Run(() =>
        {
            SafeCall(() => _ctx.Fan.RestoreAutomaticControl());
            ApplyMode(_settings.FanControlMode);
        });
    }

    private void SafeCall(Action a) { try { a(); } catch { } }

    /// <summary>
    /// Implements INotifyPropertyChanged only so the derived RPM columns update as soon as a
    /// level is edited. Without it the grid shows a new percentage beside the RPM of the old
    /// one until something else forces a refresh, which is worse than not showing RPM at all.
    /// </summary>
    private sealed class CurvePointRow : System.ComponentModel.INotifyPropertyChanged
    {
        private double _tempC;
        private byte _levelPercent;

        public double TempC
        {
            get => _tempC;
            set { _tempC = value; Raise(nameof(TempC)); }
        }

        public byte LevelPercent
        {
            get => _levelPercent;
            set
            {
                _levelPercent = value;
                Raise(nameof(LevelPercent));
                Raise(nameof(TargetRpm));
                Raise(nameof(RawLevel));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        /// <summary>
        /// What this row's percentage actually commands, in RPM.
        ///
        /// Derived rather than stored, and shown because the percentage alone is close to
        /// meaningless on this hardware: the usable band is raw 10-56, so 0% is a real stop,
        /// 1% is already 1000 RPM, and the whole scale is squeezed into 4600 RPM of range.
        /// "52%" tells you nothing about how loud the machine will be. "3400 RPM" does.
        /// </summary>
        public string TargetRpm => LevelPercent == 0
            ? "off"
            : FanService.RawToRpm(FanService.PercentToRawLevel(LevelPercent)).ToString();

        /// <summary>The byte actually sent to the EC, which is RPM/100. Shown because it is
        /// what the manual calibration tool below steps through.</summary>
        public byte RawLevel => FanService.PercentToRawLevel(LevelPercent);
    }
}
