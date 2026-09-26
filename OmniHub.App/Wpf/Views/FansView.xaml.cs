// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;

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
            settings.CurvePoints.Select(p => new CurvePointRow { TempC = p.TempC, LevelPercent = p.LevelPercent, Calibration = _ctx.FanBackend.Calibration }));
        CurveGrid.ItemsSource = _rows;

        FloorTempBox.Text = settings.FloorTempC.ToString("0");
        FloorLevelBox.Text = settings.FloorLevelPercent.ToString();

        RefreshChartFromSettings();
        SetActiveModeButton(settings.FanControlMode);
        UpdateStatusTile(settings.FanControlMode);

        // Says what the percentage scale actually means on this chassis. Without it, "12%"
        // reads as "nearly off" when it is in fact 1500 RPM and clearly audible.
        CurveScaleNote.Text =
            $"Level is a percentage of this chassis's measured fan band, {_ctx.FanBackend.Calibration.MinRpm}-{_ctx.FanBackend.Calibration.MaxRpm} RPM. "
            + $"0% stops the fans and hands them back to the BIOS curve; 1% is already {_ctx.FanBackend.Calibration.MinRpm} RPM; "
            + $"100% is {_ctx.FanBackend.Calibration.MaxRpm} RPM on both fans. Raw is the byte sent to the controller, which is RPM/100 "
            + "and is what the manual calibration below steps through.";

        InitialiseBandEditor();
        BuildLoopTable();

        // The rail selector, and the curve for whichever rail is being edited.
        _suppressModeEvent = true;
        SeparateCurveToggle.IsChecked = settings.SeparateBatteryCurve;
        SeparateFan2Toggle.IsChecked = settings.SeparateFan2Curve;
        _suppressModeEvent = false;
        BuildRailPills();

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

            // The service kept recording while this page was away, so the table is whole on arrival.
            RefreshLoop();
        };

        SizeChanged += (_, e) => Reflow(e.NewSize.Width);
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
            FanControlMode.BiosDefault => "BIOS",
            FanControlMode.Max => "Maximum",
            _ => "--",
        };
        ModeFoot.Text = mode switch
        {
            FanControlMode.Auto => "FLOOR-PROTECTED, RE-APPLIED EVERY TICK",
            FanControlMode.BiosDefault => "THE FIRMWARE'S OWN TABLE",
            FanControlMode.Max => "PINNED TO MAXIMUM",
            _ => "",
        };

        // The defect BIOS mode carries is explained in its own note above the curve, marked by a
        // coloured rule. The footer used to say it in the warning colour instead, and this
        // application does not colour words.
        BiosNote.Visibility = mode == FanControlMode.BiosDefault ? Visibility.Visible : Visibility.Collapsed;
        RefreshLoopMeta();
    }

    // ---------------------------------------------------------------- the loop, as a table

    /// <summary>How many of the recorded ticks the table shows: half a minute at the loop's pace.</summary>
    private const int LoopRows = 12;

    private readonly List<(Border Surface, TextBlock Time, TextBlock Measured, TextBlock ActedOn, TextBlock Level, TextBlock Note)> _loopRows = new();

    /// <summary>
    /// Builds the table once; afterwards only its text changes. Twelve rows of five cells, every
    /// tick, is cheap to update and would not be cheap to rebuild.
    /// </summary>
    private void BuildLoopTable()
    {
        LoopTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        foreach (double width in new[] { 84.0, 84, 64 })
            LoopTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        LoopTable.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        LoopTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] titles = { "TIME", "MEASURED", "ACTED ON", "LEVEL", "NOTE" };
        for (int column = 0; column < titles.Length; column++)
        {
            bool figure = column is >= 1 and <= 3;
            var header = new TextBlock
            {
                Text = titles[column],
                Style = (Style)FindResource("DataLabelText"),
                Foreground = (Brush)FindResource("TextFaintBrush"),
                HorizontalAlignment = figure ? System.Windows.HorizontalAlignment.Right : System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(column == 0 ? 2 : 0, 0, figure ? 14 : 0, 6),
            };
            Place(LoopTable, header, 0, column);
        }
        var rule = new Border { Height = 1, Background = (Brush)FindResource("BorderBrush"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumnSpan(rule, titles.Length);
        Place(LoopTable, rule, 0, 0);

        for (int i = 0; i < LoopRows; i++)
        {
            LoopTable.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int row = i + 1;

            // The row's height comes from the density setting, like every table's.
            var surface = new Border();
            surface.SetResourceReference(HeightProperty, "TableRowHeight");
            Grid.SetColumnSpan(surface, titles.Length);
            Place(LoopTable, surface, row, 0);

            TextBlock Cell(string style, int column, bool figure)
            {
                var text = new TextBlock
                {
                    Style = (Style)FindResource(style),
                    Margin = new Thickness(column == 0 ? 2 : 0, 0, figure ? 14 : 0, 0),
                };
                Place(LoopTable, text, row, column);
                return text;
            }

            _loopRows.Add((surface, Cell("CellFigureMuted", 0, false), Cell("CellFigure", 1, true), Cell("CellFigure", 2, true),
                           Cell("CellFigure", 3, true), Cell("CellNote", 4, false)));
            _loopRows[^1].Time.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }

        RefreshLoopMeta();
    }

    /// <summary>Fills the table from the service's record, newest first.</summary>
    private void RefreshLoop()
    {
        var recent = _service.Recent.Newest();

        for (int i = 0; i < _loopRows.Count; i++)
        {
            var (surface, time, measured, actedOn, level, note) = _loopRows[i];

            // A row with no tick behind it collapses rather than standing empty: before the curve
            // has run, twelve blank rows were a third of a screen of nothing.
            surface.Visibility = i < recent.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i >= recent.Count)
            {
                time.Text = measured.Text = actedOn.Text = level.Text = note.Text = "";
                continue;
            }

            var (at, tick) = recent[i];
            time.Text = at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            measured.Text = Degrees(tick.MeasuredC);
            actedOn.Text = Degrees(tick.EffectiveC);
            level.Text = tick.HasCommanded ? $"{tick.CommandedPercent}%" : "--";

            // The rows where the loop did something other than follow the curve are the ones
            // worth reading, so their note is set in the primary text colour and the rest stay
            // quiet. Brightness, not hue: nothing here is a warning colour.
            note.Text = tick.Note();
            note.Foreground = (Brush)FindResource(note.Text.Length > 0 ? "TextPrimaryBrush" : "TextFaintBrush");
        }

        RefreshLoopMeta();
    }

    private static string Degrees(double celsius) =>
        celsius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°";

    private void RefreshLoopMeta()
    {
        if (LoopMeta is null) return;   // UpdateStatusTile runs before the table is built

        LoopMeta.Text = _settings.FanControlMode == FanControlMode.Auto
            ? $"newest first · {Math.Min(_service.Recent.Newest().Count, LoopRows)} of the last {_service.Recent.Capacity} ticks"
            : "the curve is not running · its last ticks before it stopped";
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>
    /// Two columns where there is room, one where there is not: below the width the curve needs
    /// beside its side panes, the panes go underneath rather than squeezing the chart.
    /// </summary>
    private void Reflow(double width)
    {
        ColumnReflow.Apply(width, below: 780, TopGutter, TopSide, sideWidth: 250, NowPane);
        ColumnReflow.Apply(width, below: 780, EditGutter, EditSide, sideWidth: 300, EditSidePanes);
    }

    /// <summary>
    /// Which rail's curve the editor below is writing to.
    ///
    /// Explicit rather than "whichever rail you are on", so the battery curve can be tuned while
    /// plugged in -- which is when anybody actually sits down to tune it.
    /// </summary>
    private bool _editingBattery;

    /// <summary>Which fan's curve the editor is writing to. See <see cref="_editingBattery"/>.</summary>
    private bool _editingFan2;

    /// <summary>
    /// The stored curve the editor is pointed at: 0 mains fan 1, 1 mains fan 2, 2 battery fan 1,
    /// 3 battery fan 2.
    ///
    /// One index rather than a pair of flags threaded through every read and write, because there
    /// are twelve settings properties behind these four cells and a missed branch in one of them
    /// would silently edit the wrong curve.
    /// </summary>
    private int EditedCell => (_editingBattery ? 2 : 0) + (_editingFan2 ? 1 : 0);

    private (List<CurvePoint> Points, double FloorTempC, byte FloorLevelPercent) EditedRail() => EditedCell switch
    {
        1 => (_settings.Fan2CurvePoints, _settings.Fan2FloorTempC, _settings.Fan2FloorLevelPercent),
        2 => (_settings.BatteryCurvePoints, _settings.BatteryFloorTempC, _settings.BatteryFloorLevelPercent),
        3 => (_settings.BatteryFan2CurvePoints, _settings.BatteryFan2FloorTempC, _settings.BatteryFan2FloorLevelPercent),
        _ => (_settings.CurvePoints, _settings.FloorTempC, _settings.FloorLevelPercent),
    };

    private void WriteEditedPoints(List<CurvePoint> points)
    {
        switch (EditedCell)
        {
            case 1: _settings.Fan2CurvePoints = points; break;
            case 2: _settings.BatteryCurvePoints = points; break;
            case 3: _settings.BatteryFan2CurvePoints = points; break;
            default: _settings.CurvePoints = points; break;
        }
    }

    private void WriteEditedFloor(double tempC, byte levelPercent)
    {
        switch (EditedCell)
        {
            case 1: _settings.Fan2FloorTempC = tempC; _settings.Fan2FloorLevelPercent = levelPercent; break;
            case 2: _settings.BatteryFloorTempC = tempC; _settings.BatteryFloorLevelPercent = levelPercent; break;
            case 3: _settings.BatteryFan2FloorTempC = tempC; _settings.BatteryFan2FloorLevelPercent = levelPercent; break;
            default: _settings.FloorTempC = tempC; _settings.FloorLevelPercent = levelPercent; break;
        }
    }

    /// <summary>
    /// Builds the rail selector and says which curve is in force.
    ///
    /// The pills are disabled while the feature is off, because with one curve there is nothing
    /// to choose between and a live selector would imply otherwise.
    /// </summary>
    private void BuildRailPills()
    {
        BuildPillRow(EditingRailPills, "CurveRail", "MAINS", "BATTERY",
                     enabled: _settings.SeparateBatteryCurve, selected: _editingBattery,
                     onPick: second => _editingBattery = second);

        BuildPillRow(EditingFanPills, "CurveFan", "FAN 1", "FAN 2",
                     enabled: _settings.SeparateFan2Curve, selected: _editingFan2,
                     onPick: second => _editingFan2 = second);

        RefreshRailNote();
    }

    /// <summary>
    /// One two-way pill row.
    ///
    /// The pills are disabled while their feature is off, because with one curve there is nothing
    /// to choose between and a live selector would imply otherwise.
    /// </summary>
    private void BuildPillRow(WrapPanel host, string group, string firstLabel, string secondLabel,
                              bool enabled, bool selected, Action<bool> onPick)
    {
        host.Children.Clear();

        foreach (var (label, isSecond) in new[] { (firstLabel, false), (secondLabel, true) })
        {
            bool captured = isSecond;

            var pill = new RadioButton
            {
                Content = label,
                GroupName = group,
                Style = (Style)FindResource("PillRadioStyle"),
                Height = 28,
                MinWidth = 82,
                Margin = new Thickness(0, 0, 3, 0),
                IsChecked = isSecond == selected,
                IsEnabled = enabled,
            };

            pill.Checked += (_, _) => { onPick(captured); LoadCurveForEditedRail(); };
            host.Children.Add(pill);
        }
    }

    private void RefreshRailNote()
    {
        bool onBattery = PowerSourceWatcher.Read() == PowerSource.Battery;

        if (!_settings.SeparateBatteryCurve && !_settings.SeparateFan2Curve)
        {
            RailNote.Text = "One curve, applied to both fans on both power rails.";
            CurveMeta.Text = "one curve · both fans · both rails";
            return;
        }

        string rail = _editingBattery ? "battery" : "mains";
        string fan = _editingFan2 ? "2" : "1";

        string editing = (_settings.SeparateBatteryCurve, _settings.SeparateFan2Curve) switch
        {
            (true, true) => $"the {rail} curve for fan {fan}",
            (true, false) => $"the {rail} curve",
            _ => $"the curve for fan {fan}",
        };

        string inForce = _settings.SeparateBatteryCurve
            ? $" This machine is on {(onBattery ? "battery" : "mains")} right now, so the "
              + $"{(onBattery ? "battery" : "mains")} curve is the one in force."
            : "";

        RailNote.Text = $"Editing {editing}.{inForce}";

        // The chart draws the curve being edited, which is not always the one in force -- tuning
        // the battery curve while plugged in is the normal case -- so its header says which.
        bool editingInForce = !_settings.SeparateBatteryCurve || _editingBattery == onBattery;
        CurveMeta.Text = $"{editing[4..]}{(editingInForce ? " · in force" : " · not in force now")}";
    }

    /// <summary>Loads whichever rail's stored curve into the editor.</summary>
    private void LoadCurveForEditedRail()
    {
        var (points, floorTempC, floorLevel) = EditedRail();

        _rows.Clear();
        foreach (var p in points)
            _rows.Add(new CurvePointRow { TempC = p.TempC, LevelPercent = p.LevelPercent, Calibration = _ctx.FanBackend.Calibration });

        FloorTempBox.Text = floorTempC.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        FloorLevelBox.Text = floorLevel.ToString();

        RefreshRailNote();
        RefreshChartFromSettings();
    }

    private void SeparateCurveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;

        _settings.SeparateBatteryCurve = SeparateCurveToggle.IsChecked == true;
        _settings.Save();

        // Turning it off returns the editor to the one curve there now is.
        if (!_settings.SeparateBatteryCurve) _editingBattery = false;

        BuildRailPills();
        LoadCurveForEditedRail();

        // MainWindow owns the switching, and re-reads what is in force for the current rail.
        (System.Windows.Application.Current.MainWindow as MainWindow)?.RefreshCurveFromSettings();
    }

    /// <summary>How far apart the two fans are commanded during the check, in raw units.</summary>
    private const byte SplitProbeHigh = 45, SplitProbeLow = 25;

    /// <summary>Twenty samples two seconds apart -- forty seconds of fans held apart.</summary>
    private const int SplitProbeSamples = 20;

    /// <summary>
    /// Asks the board whether it will drive its two fans at two different speeds.
    ///
    /// Worth running rather than assuming, because a second curve the firmware quietly collapses
    /// into one is a control that looks like it works and does nothing. The evidence short of an
    /// experiment points at independence and does not settle it -- see FanSplitProbe.
    ///
    /// Both levels are inside this chassis's measured band and both are above its floor, so the
    /// experiment only ever moves more air than idle. If this application dies partway through,
    /// the fans are left at 4500 and 2500 RPM rather than stopped, which is the safe direction to
    /// fail in.
    /// </summary>
    private async void SplitProbeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_ctx.VendorSupported)
        {
            SplitProbeNote.Text = "No vendor interface on this machine, so there is nothing to ask.";
            return;
        }

        var go = System.Windows.MessageBox.Show(
            $"The two fans will be held about {_ctx.FanBackend.Calibration.RawToRpm(SplitProbeHigh)} and "
            + $"{_ctx.FanBackend.Calibration.RawToRpm(SplitProbeLow)} RPM for {SplitProbeSamples * 2} seconds, then the "
            + "curve takes over again.\n\nIt is audible and uneven while it runs. Nothing is written "
            + "that the curve does not overwrite immediately afterwards.",
            "Check the fans move separately", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (go != MessageBoxResult.OK) return;

        SplitProbeBtn.IsEnabled = false;
        SplitProbeNote.Text = "Running. Holding the fans apart and reading them back...";

        bool wasRunning = _service.IsRunning;

        try
        {
            var samples = await Task.Run(() =>
            {
                // The curve would overwrite the split on its next tick, so it stands down for the
                // duration. Max fan would too, and it wins over any level write.
                if (wasRunning) _service.Stop();
                SafeCall(() => _ctx.System.SetMaxFan(false));

                var taken = new List<(byte? Fan1, byte? Fan2)>();

                for (int i = 0; i < SplitProbeSamples; i++)
                {
                    // Re-asserted every sample for the same reason the curve loop re-asserts it:
                    // SetFanLevel is ignored unless manual mode is currently held, and the EC can
                    // reclaim it at any point.
                    SafeCall(() => _ctx.Fan.SetFanMode(FanMode.Performance));
                    SafeCall(() => _ctx.Fan.SetFanLevel(SplitProbeHigh, SplitProbeLow));

                    System.Threading.Thread.Sleep(2000);
                    taken.Add(_ctx.Fan.ReadLevels());
                }

                return taken;
            });

            var verdict = FanSplitProbe.Judge(samples, SplitProbeHigh, SplitProbeLow);
            SplitProbeNote.Text = FanSplitProbe.Describe(verdict, SplitProbeHigh - SplitProbeLow);
        }
        catch (Exception ex)
        {
            SplitProbeNote.Text = $"The check could not finish: {ex.Message}";
        }
        finally
        {
            // Hand the fans back whatever happened above, including a thrown BIOS call. Leaving
            // them held apart because an exception escaped would be the worst outcome here.
            if (wasRunning) _service.Start();
            else ApplyMode(_settings.FanControlMode);

            SplitProbeBtn.IsEnabled = true;
        }
    }

    private void SeparateFan2Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;

        _settings.SeparateFan2Curve = SeparateFan2Toggle.IsChecked == true;
        _settings.Save();

        // Turning it off returns the editor to the one fan there now is.
        if (!_settings.SeparateFan2Curve) _editingFan2 = false;

        BuildRailPills();
        LoadCurveForEditedRail();

        (System.Windows.Application.Current.MainWindow as MainWindow)?.RefreshCurveFromSettings();
    }

    private void ApplyFloorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(FloorTempBox.Text, out var floorTemp) || !byte.TryParse(FloorLevelBox.Text, out var floorLevel))
        {
            System.Windows.MessageBox.Show("Enter valid numbers for the floor.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        WriteEditedFloor(floorTemp, floorLevel);
        _settings.Save();

        // Only what is in force reaches the running curve. Writing the battery floor into a
        // running mains curve would apply a setting the user has not asked for yet.
        (System.Windows.Application.Current.MainWindow as MainWindow)?.RefreshCurveFromSettings();
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
            WriteEditedPoints(points);
            _settings.Save();

            // Only the rail in force reaches the running curve; MainWindow decides which that is.
            (System.Windows.Application.Current.MainWindow as MainWindow)?.RefreshCurveFromSettings();
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
        foreach (var p in defaults) _rows.Add(new CurvePointRow { TempC = p.TempC, LevelPercent = p.LevelPercent, Calibration = _ctx.FanBackend.Calibration });
        WriteEditedPoints(defaults.ToList());
        WriteEditedFloor(55.0, 15);

        FloorTempBox.Text = "55";
        FloorLevelBox.Text = "15";
        _settings.Save();

        (System.Windows.Application.Current.MainWindow as MainWindow)?.RefreshCurveFromSettings();
        RefreshChartFromSettings();
    }

    // Draws the rail being edited, not the rail in force. Editing the battery curve while plugged
    // in is the normal case, and a chart that kept showing the mains curve would be showing the
    // wrong thing at exactly the moment the numbers below it changed.
    private void RefreshChartFromSettings()
    {
        var (points, floorTempC, floorLevel) = EditedRail();

        Chart.Points = points;
        Chart.FloorTempC = floorTempC;
        Chart.FloorLevelPercent = floorLevel;
        Chart.RefreshData();
    }

    /// <summary>
    /// Says so when the board has declined to report a fan speed.
    ///
    /// The readings themselves already render as "--", which is honest but silent about why. This
    /// is the difference between a blank that looks like a bug in this application and a blank
    /// that is the firmware not answering.
    /// </summary>
    private void RefreshSensorNote()
    {
        int declined = _ctx.Fan.NoReadingCount;
        if (declined == 0) return;

        SensorNote.Visibility = Visibility.Visible;
        SensorNote.Text = $"THE BOARD DECLINED A FAN READING {declined}× THIS SESSION - SHOWN AS -- RATHER THAN AS A SPEED";
    }

    private void OnHardwareReading(Reading r)
    {
        // BeginInvoke: see DashboardView.OnReading. A synchronous Invoke from the poll thread
        // stalls the temperature poll for as long as the UI thread is busy, which stops the
        // fan curve.
        Dispatcher.BeginInvoke(() =>
        {
            // In Auto the curve's own tick sets the temperature it acted on; here only outside it.
            if (_settings.FanControlMode != FanControlMode.Auto)
                SetTemperature(r.TemperatureC, r.Throttling == true);
            RefreshSensorNote();

            if (_settings.FanControlMode != FanControlMode.Auto)
            {
                // Raw is an RPM/100 target on a ~20-55 usable scale, not a 0-255 PWM duty
                // cycle -- see FanCalibration.RawToPercent. The old raw/255*100 here was the
                // debunked PWM assumption and under-reported this tile by roughly half.
                int? levelPercent = r.FanLevel1 is { } raw1 ? _ctx.FanBackend.Calibration.RawToPercent(raw1) : null;
                LevelValue.Text = levelPercent?.ToString() ?? "--";

                // Both fans: this is a tachometer reading, not an echo of the commanded level,
                // so the two can differ from each other and from the curve's target while the
                // fans are still spinning up (measured: about six seconds for a full step).
                //
                // And a fan the board did not report on shows as "--" rather than as zero. The
                // difference matters most on exactly this screen: zero here reads as a stopped
                // fan, which is the fault the curve exists to prevent.
                LevelFoot.Text = $"RAW {FanService.RawText(r.FanLevel1)}/{FanService.RawText(r.FanLevel2)} - " +
                                 $"{_ctx.FanBackend.Calibration.RpmText(r.FanLevel1)}/{_ctx.FanBackend.Calibration.RpmText(r.FanLevel2)} RPM";

                if (levelPercent is { } plotted)
                    Chart.SetLive(r.TemperatureC, (byte)Math.Clamp(plotted, 0, 100));
            }
            else
            {
                // Auto mode. The big number is the curve's command, written by OnServiceTick;
                // this line is what the tachometer actually reads, plus the target it is
                // heading for. The two disagreeing is the fan spinning up -- measured at about
                // six seconds for a full step -- not a fault, which is exactly why both are
                // shown rather than just the one that happens to look tidier.
                string measured = $"{_ctx.FanBackend.Calibration.RpmText(r.FanLevel1)}/{_ctx.FanBackend.Calibration.RpmText(r.FanLevel2)} RPM";
                if (_service.HasCommanded)
                {
                    byte targetRaw = _ctx.FanBackend.Calibration.PercentToRawFan1(_service.LastCommandedLevelPercent);
                    LevelFoot.Text = $"NOW {measured} - CURVE WANTS {_ctx.FanBackend.Calibration.RawToRpm(targetRaw)} RPM (RAW {targetRaw})";
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

            var tick = _service.LastTick;
            SetTemperature((int)Math.Round(tempC), throttling: false,
                           foot: tick.FilteredC is { } filtered && Math.Abs(filtered - tick.MeasuredC) >= 0.05
                               ? $"ACTED ON {filtered.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} C, THE MEDIAN OF FIVE"
                               : null);
            LevelValue.Text = levelPercent.ToString();
            Chart.SetLive(tempC, levelPercent);

            // Everything the tick decided, in a sentence, over the table of the ticks before it.
            TickNote.Text = tick.Describe();
            RefreshLoop();

            // "COMMANDED BY CURVE" was all this said, which told you nothing you could act on.
            // The reading handler fills in measured versus target RPM on its own tick.
        });
    }

    // Hot at 80, the threshold the sensor table and the overlay use, so no two screens disagree
    // about what counts as hot. No amber tier: a warning colour that is on through the machine's
    // whole normal range stops being a warning.
    //
    // The colour is on the figure only. "THROTTLING NOW" used to turn red with it, and this
    // application does not colour words; the figure going red beside it says the same thing.
    private void SetTemperature(int tempC, bool throttling, string? foot = null)
    {
        TempValue.Text = tempC.ToString();
        TempFoot.Text = throttling ? "THROTTLING NOW" : foot ?? "NOMINAL";

        var brush = (Brush)FindResource(throttling || tempC >= 80 ? "DangerBrush" : "TextPrimaryBrush");
        TempValue.Foreground = brush;
        TempUnit.Foreground = brush;
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

    /// <summary>
    /// Fills the band boxes from whatever calibration is actually in force, and says where a
    /// saved profile would go.
    ///
    /// Seeded from the live values rather than from FanCalibration.Default, so somebody who
    /// already has a profile sees their own numbers and can adjust one of them instead of
    /// retyping a band they measured months ago.
    /// </summary>
    private void InitialiseBandEditor()
    {
        var band = _ctx.FanBackend.Calibration;
        BandMinBox.Text = band.MinRawLevel.ToString();
        BandMax1Box.Text = band.MaxRawLevelFan1.ToString();
        BandMax2Box.Text = band.MaxRawLevelFan2.ToString();

        string? path = FanProfiles.PathFor(_ctx.Model);

        ProfileIntro.Text = path is null
            ? "This machine does not report a baseboard product, so there is no name to file a profile under. "
            + "The band above is still what the curve is using."
            : "Raw levels, not percentages. The curve maps 0-100% onto this band, so these three numbers decide "
            + "what every point on your curve actually commands. Saving writes a profile for this board that is "
            + "loaded at every launch, which is what the -Probe workflow and the per-model profile mechanism have "
            + "always pointed at -- until now nothing could write one.";

        SaveBandBtn.IsEnabled = path is not null;
        ProfileStatus.Text = path is null ? "" : $"Profile path: {path}";
    }

    private void SaveBandBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!byte.TryParse(BandMinBox.Text?.Trim(), out byte min)
            || !byte.TryParse(BandMax1Box.Text?.Trim(), out byte max1)
            || !byte.TryParse(BandMax2Box.Text?.Trim(), out byte max2))
        {
            ProfileStatus.Text = "All three values need to be whole numbers between 0 and 255.";
            return;
        }

        var (saved, detail) = FanProfiles.Save(_ctx.Model, new FanCalibration(min, max1, max2));
        ProfileStatus.Text = detail;

        // Deliberately NOT applied to the running FanService. The band is read once at startup
        // and handed to a static that the curve, the UI and the RPM readouts all share; swapping
        // it underneath a running curve would change what every displayed percentage means
        // halfway through a session. Saying "next launch" is the honest version.
        if (saved) _settings.Save();
    }

    /// <summary>
    /// Makes the application passive and reports what is still in force.
    ///
    /// Off the UI thread because the first thing it does is stop the fan loop, which issues BIOS
    /// calls that queue behind whatever the poll thread is already doing -- the same reason every
    /// other hardware path in this view is pushed off the dispatcher.
    ///
    /// The mode buttons are left showing whatever the user last chose rather than being forced to
    /// BIOS Default. The point of this control is to make OmniHub stop acting, not to rewrite the
    /// user's preference: clicking Auto again puts them exactly where they were.
    /// </summary>
    private void StockBtn_Click(object sender, RoutedEventArgs e)
    {
        StockBtn.IsEnabled = false;
        StockSummary.Text = "Handing everything back...";
        StockRows.Children.Clear();

        Task.Run(() => ReturnToStock.Run(
                    _service,
                    _ctx.VendorSupported ? _ctx.GpuBackend : null,
                    tuningWasApplied: _settings.TuningMode != TuningMode.Manual || _settings.StartupProfileName is not null,
                    restorePlan: _settings.AcPlanId,
                    journal: OmniHub.Core.Diagnostics.RestoreJournal.Shared))
            .ContinueWith(t => Dispatcher.Invoke(() =>
            {
                StockBtn.IsEnabled = true;

                if (t.IsFaulted || t.Result is not { } steps)
                {
                    StockSummary.Text = $"Could not complete ({t.Exception?.GetBaseException().Message}).";
                    return;
                }

                StockSummary.Text = ReturnToStock.Summarise(steps);

                foreach (var step in steps) StockRows.Children.Add(BuildStockRow(step));

                // The mode buttons no longer reflect a running curve.
                SetActiveModeButton(FanControlMode.BiosDefault);
                UpdateStatusTile(FanControlMode.BiosDefault);
            }));
    }

    /// <summary>
    /// One row per step: a state dot, the name, and what happened.
    ///
    /// The dot carries the colour, not the words. A sentence saying a limit is still in force is
    /// a statement of fact, and tinting it red would make it read as an alarm about something
    /// that is working exactly as documented.
    /// </summary>
    private UIElement BuildStockRow(ReturnToStock.Step step)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Fill = (Brush)FindResource(step.State switch
            {
                ReturnToStock.StockState.Restored => "GoodBrush",
                ReturnToStock.StockState.NotChanged => "TextFaintBrush",
                ReturnToStock.StockState.NeedsReboot => "WarnBrush",
                _ => "DangerBrush",
            }),
        };
        Grid.SetColumn(dot, 0);

        var name = new TextBlock
        {
            Text = step.Name,
            Style = (Style)FindResource("TileFoot"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);

        var detail = new TextBlock
        {
            Text = step.Detail,
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(detail, 2);

        row.Children.Add(dot);
        row.Children.Add(name);
        row.Children.Add(detail);
        return row;
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
            : Calibration.RawToRpm(Calibration.PercentToRawFan1(LevelPercent)).ToString();

        /// <summary>The byte actually sent to the EC, which is RPM/100. Shown because it is
        /// what the manual calibration tool below steps through.</summary>
        public byte RawLevel => Calibration.PercentToRawFan1(LevelPercent);

        /// <summary>
        /// The band this row's percentage is a percentage of.
        ///
        /// Required rather than defaulted on purpose. A row that quietly assumed the shipped band
        /// would show the right RPM on this chassis and a confidently wrong one on any machine
        /// with a profile of its own -- and being confidently wrong about a fan speed is the
        /// failure this application exists to avoid.
        /// </summary>
        public required FanCalibration Calibration { get; init; }
    }
}
