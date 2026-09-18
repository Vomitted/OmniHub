// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;
using OmniHub.Core.Telemetry;

namespace OmniHub.App.Wpf;

/// <summary>
/// A small always-on-top readout for while you are doing something else.
///
/// Click-through is the whole point and is done with window styles rather than by trying to
/// dodge the mouse: WS_EX_TRANSPARENT makes hit-testing pass straight through to whatever is
/// underneath, WS_EX_NOACTIVATE stops it stealing focus from a game, and WS_EX_TOOLWINDOW
/// keeps it out of Alt-Tab. Without these it would be a window that sits on top of your game
/// and swallows clicks, which is worse than no overlay at all.
///
/// KNOWN LIMIT, because it is the first thing anyone hits: this draws as a normal desktop
/// window, so it appears over borderless and windowed games but NOT over true fullscreen
/// exclusive ones. Those composite through the display driver and the only way in is to hook
/// the graphics API, which is what RTSS exists to do. Being honest about that is better than
/// shipping something that mysteriously vanishes in half your games.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly HardwareContext _ctx;
    private readonly AppSettings _settings;
    private AmdTuning? _tuning;
    private DispatcherTimer? _powerTimer;

    public OverlayWindow(HardwareContext ctx, AppSettings settings)
    {
        InitializeComponent();
        _ctx = ctx;
        _settings = settings;

        if (ctx.Smu is { } smu)
        {
            var tuning = new AmdTuning(smu);
            if (tuning.IsSupported) _tuning = tuning;
        }

        // No SMU means no package power to show. Hiding the row is better than a permanent
        // "--" that looks like a fault. With rows now chosen by the user, an unavailable
        // metric simply renders "--" like any other missing reading; the timer only starts
        // when there is something for it to read.
        ApplyAppearance();
        StartPowerUpdates();

        // Re-place on resize: SizeToContent means the size is not known until the window has
        // laid out, and a corner-anchored window placed before that lands in the wrong spot.
        SizeChanged += (_, _) => MoveToCorner();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);

        MoveToCorner();
    }

    /// <summary>Places the window in the configured screen corner, clear of the taskbar.</summary>
    public void MoveToCorner()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 14;

        Left = _settings.OverlayCorner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft
            ? area.Left + margin
            : area.Right - ActualWidth - margin;

        Top = _settings.OverlayCorner is OverlayCorner.TopLeft or OverlayCorner.TopRight
            ? area.Top + margin
            : area.Bottom - ActualHeight - margin;
    }

    /// <summary>
    /// Refreshes from a poll the app already performs, so the overlay costs nothing extra for
    /// temperature and fan speed.
    /// </summary>
    /// <summary>
    /// Every metric the overlay can show: stored key, and the label drawn beside it.
    ///
    /// The order here is the order the settings UI offers them in; the order the user picks is
    /// what the overlay draws, so a list is the right shape rather than a flags enum.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> AvailableMetrics { get; } =
        Metrics.All.Select(m => (m.Key, Label: m.Compact)).ToArray();

    // Derived rather than written out again. The keys existed in two places, and a reading added
    // to the catalogue in Core would simply not have appeared here -- silently, because a missing
    // key is indistinguishable from one nobody chose.
    //
    // The VALUES are still formatted below rather than by Metrics.Text, and that is deliberate.
    // This card is about a hundred and thirty pixels wide over somebody's game, so it writes "2400"
    // where a panel writes "2400 RPM" and "14.2G" where a panel writes "14.2GB". The cpu row also
    // changes precision with the sensor that answered -- a tenth from the die, whole degrees from
    // the ACPI zone, which is as much resolution as that zone has. A shared formatter cannot
    // express "depends which sensor replied", and flattening it would show a tenth of a degree the
    // reading does not contain.

    private readonly Dictionary<string, TextBlock> _valueCells = new();
    private readonly Dictionary<string, System.Windows.Shapes.Path> _sparkCells = new();

    /// <summary>
    /// One rolling window per metric, kept whether or not sparklines are switched on.
    ///
    /// Kept regardless because the alternative is a blank strip for the first three minutes after
    /// the user turns them on, which reads as broken.
    /// </summary>
    private readonly Dictionary<string, Sparkline> _history = new();

    // Its own sampler: CPU load is the delta since this reader's previous call, and the dashboard
    // keeps a second one on a faster timer.
    private readonly SystemPerfReader _perf = new();

    /// <summary>
    /// Rebuilds the rows and re-applies opacity. Called at construction and whenever the
    /// settings change, so a choice shows immediately rather than at next launch -- a preview
    /// you have to restart to see is not a preview.
    /// </summary>
    public void ApplyAppearance()
    {
        // Clamped, not trusted: settings.json is hand-editable, and an opacity of 0 leaves an
        // overlay that is still there, still click-through, and impossible to find.
        Card.Opacity = Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);

        // Same reasoning as opacity. The lower bound is where the figures stop being readable at
        // arm's length on this panel; the upper is where the card starts covering a useful part
        // of the screen it is drawn over.
        double scale = Math.Clamp(_settings.OverlayScale, 0.7, 2.0);

        MetricRows.Children.Clear();
        _valueCells.Clear();
        _sparkCells.Clear();

        SourceLabel.FontSize = 10 * scale;
        StateText.FontSize = 10.5 * scale;

        foreach (var key in _settings.OverlayMetrics)
        {
            var metric = AvailableMetrics.FirstOrDefault(m => m.Key == key);
            if (metric.Key is null) continue;   // unknown key from a newer build: skip, never throw

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 3 * scale) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58 * scale) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = metric.Label,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11 * scale,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var value = new TextBlock
            {
                Style = (Style)FindResource("BigNumberText"),
                FontSize = 17 * scale,
                Text = "--",
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            _valueCells[key] = value;
            _history.TryAdd(key, new Sparkline());

            if (_settings.OverlaySparklines)
            {
                var spark = new System.Windows.Shapes.Path
                {
                    Stroke = (Brush)FindResource("AccentBrush"),
                    StrokeThickness = 1.2,
                    Width = SparkWidth * scale,
                    Height = SparkHeight * scale,
                    Margin = new Thickness(8 * scale, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    SnapsToDevicePixels = true,
                };
                Grid.SetColumn(spark, 2);
                grid.Children.Add(spark);
                _sparkCells[key] = spark;
            }

            MetricRows.Children.Add(grid);
        }

        // The rows just changed shape, so whatever is already in the windows should be drawn at
        // the new size rather than waiting for the next tick to look right.
        foreach (var key in _sparkCells.Keys) DrawSpark(key);
    }

    private const double SparkWidth = 46, SparkHeight = 14;

    /// <summary>
    /// Sets one row: the number that is drawn, and the value behind it for the rolling window.
    ///
    /// The two are separate arguments because the text is formatted for reading -- "82.4 degrees",
    /// "EDC 94%" -- and the window needs the quantity. Parsing the label back out would be the
    /// kind of shortcut that works until a unit changes.
    /// </summary>
    private void Show(string key, string text, double? value)
    {
        if (_valueCells.TryGetValue(key, out var cell))
        {
            cell.Text = text;

            // The same thresholds the panels use, from the same catalogue. This is the surface
            // where it matters most: it is the one visible while the machine is actually under
            // load, and a die crossing eighty degrees is worth noticing without reading the digits.
            //
            // The figure only. Nothing else in this card takes a colour -- the labels and the
            // state line stay what they are.
            cell.Foreground = Metrics.LevelOf(key, value) switch
            {
                MetricLevel.Hot => (Brush)FindResource("DangerBrush"),
                MetricLevel.Warn => (Brush)FindResource("WarnBrush"),
                _ => (Brush)FindResource("TextPrimaryBrush"),
            };
        }

        if (!_history.TryGetValue(key, out var history)) return;

        history.Push(value);
        DrawSpark(key);
    }

    private void DrawSpark(string key)
    {
        if (!_sparkCells.TryGetValue(key, out var path) || !_history.TryGetValue(key, out var history)) return;

        var geometry = new PathGeometry();

        foreach (var segment in history.Segments(path.Width, path.Height))
        {
            var figure = new PathFigure { StartPoint = new Point(segment[0].X, segment[0].Y), IsFilled = false };
            for (int i = 1; i < segment.Count; i++)
                figure.Segments.Add(new LineSegment(new Point(segment[i].X, segment[i].Y), isStroked: true));

            geometry.Figures.Add(figure);
        }

        geometry.Freeze();
        path.Data = geometry;
    }

    public void Update(Reading r, FanService service)
    {
        double tempC = double.IsNaN(r.PreciseTemperatureC) ? r.TemperatureC : r.PreciseTemperatureC;
        bool fromDie = r.TemperatureSource == TemperatureSource.SmuDieTctl;

        // Filtered, matching the dashboard. Two readouts of the same sensor disagreeing by ten
        // degrees because one caught a die excursion and the other did not is worse than either
        // number on its own.
        // The context's CpuTrend, for the same two reasons the dashboard uses it: FanService's
        // Trend is the control temperature (max of CPU and GPU) while this readout is labelled
        // DIE, and FanService stops outside Auto fan mode whereas the poll loop never does.
        double displayC = _ctx.CpuTrend.HasEnoughData ? _ctx.CpuTrend.FilteredTempC : tempC;
        // A saturated zone reading is not a temperature -- same rule as the dashboard. An
        // uninitialised ACPI zone reporting ~86 C on a cold machine is not a measurement, and
        // appending a "+" to it does not make it one.
        bool ceiling = !fromDie && ThermalReader.IsAtSensorCeiling(tempC);

        Show("cpu",
             ceiling ? "--" : fromDie ? $"{displayC:0.0}°" : $"{Math.Round(displayC):0}°",
             ceiling ? null : displayC);
        SourceLabel.Text = fromDie ? "OMNIHUB · DIE" : "OMNIHUB · ACPI";

        Show("fan", _ctx.FanBackend.Calibration.RpmText(r.FanLevel1), r.FanLevel1 is { } f1 ? _ctx.FanBackend.Calibration.RawToRpm(f1) : null);
        Show("fan2", _ctx.FanBackend.Calibration.RpmText(r.FanLevel2), r.FanLevel2 is { } f2 ? _ctx.FanBackend.Calibration.RawToRpm(f2) : null);

        // Package power, the GPU and the system figures are not read here. They arrive on the slow
        // timer and write their own rows, because a cache miss in GpuTelemetry spawns nvidia-smi
        // and 53 ms of process startup on the UI thread is a visible stutter over a game.

        StateText.Text = r.Throttling == true ? "THROTTLING"
            : r.MaxFanActive ? "MAX FAN"
            : service.IsRunning ? $"CURVE {service.LastCommandedLevelPercent}%"
            : "BIOS AUTO";
    }

    /// <summary>
    /// Package power on its own slow timer.
    ///
    /// Separate from the reading tick because it is the only value here that costs a driver
    /// round trip of its own -- the PM table has to be refreshed and re-read -- and five
    /// seconds is plenty for a number that is an average over a window anyway.
    /// </summary>
    private void StartPowerUpdates()
    {
        _powerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _powerTimer.Tick += (_, _) =>
        {
            // Both the SMU read and the GPU query run off the UI thread. The GPU one is the
            // reason this matters: a cache miss spawns nvidia-smi, measured at 53 ms, which
            // would be a visible stutter in a window drawn over a game.
            var tuning = _tuning;

            Task.Run(() =>
            {
                PowerSnapshot? snapshot = null;
                if (tuning is not null)
                {
                    try { snapshot = tuning.ReadPower(); } catch { }
                }

                // Three cheap syscalls -- kernel tick counters, the per-processor clocks and
                // GlobalMemoryStatusEx. Off the UI thread anyway, since it shares a task with
                // the two that genuinely cost something.
                SystemPerf? perf = null;
                try { perf = _perf.Read(); } catch { }

                return (snapshot, gpu: GpuTelemetry.Read(), perf);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) return;
                var (snapshot, gpu, perf) = t.Result;

                // Null is an ordinary answer throughout -- no NVIDIA GPU, no SMU, a failed query.
                // Each renders "--" rather than holding the last value, so a dead reading cannot
                // sit on screen looking live.
                var limit = snapshot?.TightestLimit();

                Dispatcher.BeginInvoke(() =>
                {
                    Show("pkg", snapshot is { } p ? $"{p.StapmWatts:0.0}W" : "--", snapshot?.StapmWatts);

                    Show("limit",
                         limit is { } l ? $"{l.Name} {l.Percent:0}%" : "--",
                         limit?.Percent);

                    Show("gpu",     gpu?.TempC is double gt ? $"{Math.Round(gt):0}°" : "--", gpu?.TempC);
                    Show("gpuw",    gpu?.PowerWatts is double gw ? $"{gw:0.0}W" : "--", gpu?.PowerWatts);
                    Show("gpuclk",  gpu?.ClockMhz is int gc ? $"{gc}" : "--", gpu?.ClockMhz);
                    Show("gpuload", gpu?.UtilisationPercent is int gu ? $"{gu}%" : "--", gpu?.UtilisationPercent);

                    Show("cpuload", perf?.CpuLoadPercent is double cl ? $"{Math.Round(cl):0}%" : "--", perf?.CpuLoadPercent);
                    Show("cpuclk",  perf?.CpuClockGHz is double cc ? $"{cc:0.00}" : "--", perf?.CpuClockGHz);
                    Show("mem",     perf is { } m ? $"{m.MemoryUsedGB:0.0}G" : "--", perf?.MemoryUsedGB);
                });
            }, TaskScheduler.Default);
        };

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) _powerTimer.Start();
            else _powerTimer.Stop();
        };
    }
}
