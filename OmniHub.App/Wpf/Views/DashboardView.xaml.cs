// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
using OmniHub.Core.Vendors;
using UserControl = System.Windows.Controls.UserControl;
namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The machine at a glance, built to the reference the user supplied (technical/TECHNICAL-v5-ui.md,
/// section 9): the presets, a card per component down each side of a dial, the history on one
/// clock, what is limiting the processor, and every reading as a tile.
///
/// Everything here reads the one shared <see cref="MetricSource"/>. What this page no longer does
/// is as deliberate as what it does: it carried six chips that duplicated controls living on the
/// Fans and System pages -- and one of them changed the saved fan mode without the Fans page's own
/// selector finding out.
/// </summary>
public partial class DashboardView : UserControl
{
    private readonly HardwareContext _ctx;
    private readonly FanService _service;
    private readonly AppSettings _settings;
    private readonly MetricSource _metrics;
    private bool _suppressPresetEvent;

    // The cards' bars, made once and filled on every reading.
    private readonly Controls.Meter _cpuLoad, _memUsed, _gpuLoad, _curve, _fan2;
    private readonly Controls.FanGlyph _fanIcon = new() { ColourKey = "MetricFanBrush" };

    // What the poll last said that the shared readings do not carry: whether the firmware reports
    // throttling.
    private bool _throttling;

    // Graphics is stated in two halves that change at different rates: the BIOS mode and power
    // flags, which move only when something writes them, and whether the card is awake at all.
    private string _gpuMode = "--";
    private string? _gpuState;

    /// <summary>
    /// The tiles, in the order a person reads the machine: the processor, the graphics card, what
    /// the fans are doing about both, and the rest of the system -- this application's own cost
    /// last, because a tool for finding what drains a laptop should say what it draws itself.
    /// </summary>
    private static readonly string[] SensorKeys =
    {
        "cpu", "cpuclk", "cpuload", "pkg", "limit",
        "gpu", "gpuclk", "gpuload", "gpuw",
        "fan", "fan2",
        "mem", "selfcpu", "selfmem",
    };

    private readonly Controls.ChartStack _history;

    public DashboardView(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics)
    {
        InitializeComponent();
        _ctx = ctx; _service = service; _settings = settings; _metrics = metrics;

        MachineLine.Text = $"{ctx.Model.Manufacturer} {ctx.Model.Product}".Trim();

        // The console's cards: the icon and bars each carries, named here so they can be filled by
        // name below. The processor's own name comes from the value Windows writes at boot, read
        // once -- no WMI, and nothing on the poll.
        CpuCard.IconData = Controls.ComponentCard.Icons.Processor;
        GpuCard.IconData = Controls.ComponentCard.Icons.Graphics;
        MemCard.IconData = Controls.ComponentCard.Icons.Memory;
        CoolCard.SetIcon(_fanIcon);
        CpuCard.Title = HardwareNames.ShortCpu(ReadProcessorName()) ?? "Processor";
        GpuCard.Title = "Discrete GPU";
        _cpuLoad = CpuCard.AddBar("LOAD");
        _memUsed = MemCard.AddBar("IN USE");
        _gpuLoad = GpuCard.AddBar("LOAD");
        _curve = CoolCard.AddBar("CURVE ASKS");
        _fan2 = CoolCard.AddBar("FAN 2");

        SensorsHost.Content = new Controls.SensorTiles(metrics, SensorKeys);
        ShowSince();

        // Four strips, the processor and the graphics card in their own colours in every one, so a
        // colour means one component all the way down. Each strip shares a scale across its lines:
        // temperatures and power from the data, the fans against their measured maximum, load
        // against a hundred.
        double? maxRpm = ctx.FanBackend.Calibration.MaxRpm;
        _history = new Controls.ChartStack(metrics);
        _history.AddStrip("TEMPERATURE", ("cpu", "CPU", "MetricCpuBrush", null, null), ("gpu", "GPU", "MetricGpuBrush", null, null));
        _history.AddStrip("FANS", ("fan", "Fan 1", "MetricFanBrush", 0, maxRpm), ("fan2", "Fan 2", "TextMutedBrush", 0, maxRpm));
        _history.AddStrip("POWER", ("pkg", "CPU", "MetricCpuBrush", 0, null), ("gpuw", "GPU", "MetricGpuBrush", 0, null));
        _history.AddStrip("LOAD", ("cpuload", "CPU", "MetricCpuBrush", 0, 100), ("gpuload", "GPU", "MetricGpuBrush", 0, 100));
        _history.SetWindow(TimeSpan.FromMinutes(5));
        _history.HoverChanged += at => HistoryMeta.Text = at is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "live";
        HistoryHost.Content = _history;

        LoadBattery();
        StartPowerDrawTimer();

        // Warmed off-thread. The first ReadDiscrete resolves the device path with a WMI
        // query, and the call site is inside a dispatcher callback, so leaving it cold
        // would put that one query on the UI thread the first time the GPU reads as asleep.
        Task.Run(() => GpuPowerState.ReadDiscrete());

        // Best-effort guess at which preset is "active" -- settings only stores the
        // fan mode, not which GPU level was paired with it, so Auto defaults to
        // Balanced rather than trying to distinguish Silent from Balanced.
        _suppressPresetEvent = true;
        if (_settings.FanControlMode == FanControlMode.Max) PerformanceBtn.IsChecked = true;
        else BalancedBtn.IsChecked = true;
        _suppressPresetEvent = false;
        ShowProfileNote();

        // Subscribed on Loaded rather than once here.
        //
        // Switching tabs assigns MainWindow's ViewHost.Content, which detaches this control and
        // raises Unloaded -- so a constructor-time subscription paired with an Unloaded
        // unsubscribe detached PERMANENTLY the first time the user left the Dashboard. Coming
        // back re-attached the control and re-subscribed nothing, leaving the page frozen on the
        // last values it happened to see: a plausible temperature, no longer connected to the
        // hardware, with nothing on screen saying so.
        //
        // -= before += because Loaded fires again on every re-attach and a multicast delegate
        // will hold the same handler twice without complaining.
        Loaded += (_, _) =>
        {
            ctx.OnReading -= OnReading;
            ctx.OnReading += OnReading;

            // The widgets follow the shared tick, which only runs while the window is on screen.
            _metrics.Updated -= UpdateWidgets;
            _metrics.Updated += UpdateWidgets;
            UpdateWidgets();

            // Re-read what the poll does not drive, so returning to the tab shows current state
            // rather than state from launch.
            RefreshGpuMode();
            ShowSince();

            // The readiness card explains which capabilities are missing and why. Built here
            // rather than in the constructor because the SMU is retried over the first seconds of
            // a session: asked once at startup it would report tuning unavailable on every launch
            // that lost the race with PawnIO's service, and never correct itself.
            BuildReadiness();
        };
        Unloaded += (_, _) =>
        {
            ctx.OnReading -= OnReading;
            _metrics.Updated -= UpdateWidgets;
        };

        SizeChanged += (_, e) => Reflow(e.NewSize.Width);
    }

    // ---------------------------------------------------------------- layout

    private void HistoryWindow_Checked(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent for the default, before the stack exists.
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag } && int.TryParse(tag, out int minutes))
            _history?.SetWindow(TimeSpan.FromMinutes(minutes));
    }

    /// <summary>
    /// The console as the reference lays it out -- cards down each side of the dial -- where there
    /// is room for three columns, and otherwise the dial across the top with the cards two by two.
    ///
    /// The dial's field is 380 px and a card needs about 210 to keep its figure and its stats on
    /// their lines, so three columns want about 900 with the gaps; below that the dial would be
    /// clipped at the sides or the cards squeezed until their names cut off.
    /// </summary>
    private void Reflow(double width)
    {
        bool stacked = width < 960;

        CentreColumn.Width = stacked ? new GridLength(0) : new GridLength(1.3, GridUnitType.Star);
        RightGap.Width = stacked ? new GridLength(0) : new GridLength(14);
        StackedGap.Height = new GridLength(stacked ? 14 : 0);

        // The right-hand cards stay in the last column either way: stacked, the centre column and
        // its gap close to nothing and the last column moves in beside the first.
        Place(Centre, row: 0, column: stacked ? 0 : 2, rowSpan: stacked ? 1 : 3, columnSpan: stacked ? 5 : 1);
        Place(CpuCard, row: stacked ? 2 : 0, column: 0);
        Place(GpuCard, row: stacked ? 2 : 0, column: 4);
        Place(MemCard, row: stacked ? 4 : 2, column: 0);
        Place(CoolCard, row: stacked ? 4 : 2, column: 4);

        static void Place(UIElement element, int row, int column, int rowSpan = 1, int columnSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            Grid.SetRowSpan(element, rowSpan);
            Grid.SetColumnSpan(element, columnSpan);
        }
    }

    // ---------------------------------------------------------------- the widgets

    /// <summary>
    /// Redraws the dial and the four cards from the shared readings.
    ///
    /// Every dial and bar is drawn against the limit its hardware actually holds it to, read from
    /// that hardware: the thermal thresholds the readings define, 100% for a load, the fan band's
    /// measured maximum, the SMU's sustained limit, NVML's enforced power limit, the memory
    /// installed. Where no such limit is known the figure stands alone and no bar is drawn.
    /// </summary>
    private void UpdateWidgets()
    {
        double? maxRpm = _ctx.FanBackend.Calibration.MaxRpm;
        double? fan1 = _metrics.Value("fan"), fan2 = _metrics.Value("fan2");
        double? pkg = _metrics.Value("pkg"), pkgLimit = _metrics.PackageLimitWatts;

        // The dial: the die temperature against its hot point, its warning stretch tinted, and what
        // the fans and the package are doing about it underneath.
        var cpuMetric = Metrics.Find("cpu")!;
        double? temp = _metrics.Value("cpu");
        var level = Metrics.LevelOf("cpu", temp);
        double? full = Metrics.FullScale(cpuMetric, null);
        Dial.Show(Gauge.Fraction(temp, full), temp, cpuMetric.Format, "°C",
                  Brush(level switch { MetricLevel.Hot => "DangerBrush", MetricLevel.Warn => "WarnBrush", _ => "AccentGradientBrush" }),
                  Brush(temp is null ? "TextFaintBrush" : level switch { MetricLevel.Hot => "DangerBrush", MetricLevel.Warn => "WarnBrush", _ => "TextPrimaryBrush" }),
                  Gauge.Fraction(cpuMetric.WarnAt, full));
        Dial.ShowStatus(
            _throttling ? "FIRMWARE THROTTLING" : temp is null ? "NO READING" : level switch { MetricLevel.Hot => "HOT", MetricLevel.Warn => "WARM", _ => "NOMINAL" },
            Brush(_throttling || level == MetricLevel.Hot ? "DangerBrush" : level == MetricLevel.Warn ? "WarnBrush" : temp is null ? "TextFaintBrush" : "GoodBrush"));
        Dial.ShowChips("FAN", Fig(fan1, "0", " rpm"), "PACKAGE", Fig(pkg, "0.0", " W"));

        // The processor: its clock large, its load, and its power against the sustained limit.
        double? cpuLoad = _metrics.Value("cpuload");
        CpuCard.ShowFigure(_metrics.Value("cpuclk"), "0.00", "GHz");
        CpuCard.ShowBar(_cpuLoad, Fig(cpuLoad, "0", " %"), Gauge.Fraction(cpuLoad, 100));
        CpuCard.ShowStats("PACKAGE", Fig(pkg, "0.0", " W"), "SUSTAINED LIMIT", Fig(pkgLimit, "0", " W"));

        // The graphics card: its clock large, its load, its power against the driver's limit.
        double? gpuLoad = _metrics.Value("gpuload"), gpuW = _metrics.Value("gpuw");
        GpuCard.ShowFigure(_metrics.Value("gpuclk"), "0", "MHz");
        GpuCard.ShowBar(_gpuLoad, Fig(gpuLoad, "0", " %"), Gauge.Fraction(gpuLoad, 100));
        GpuCard.ShowStats("POWER", Of(gpuW, Nvml.KnownPowerCeilingWatts, "0.0", "W"), "TEMPERATURE", Fig(_metrics.Value("gpu"), "0", " °C"));

        // Memory in use against installed, with the battery beside it.
        double? mem = _metrics.Value("mem"), memTotal = _metrics.MemoryTotalGB;
        MemCard.ShowFigure(mem, "0.0", "GB");
        MemCard.ShowBar(_memUsed,
                        memTotal is { } t && mem is { } m && t > 0 ? FormattableString.Invariant($"{m / t * 100:0} % of {t:0.0} GB") : Metrics.Unavailable,
                        Gauge.Fraction(mem, memTotal));

        // Windows' own battery status: no WMI and no driver, so nothing here can wait.
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool battery = !power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                       && power.BatteryLifePercent <= 1f;   // 255 over 100 is Windows for "unknown"
        double? charge = battery ? Math.Round(power.BatteryLifePercent * 100) : null;
        _chargeText = Fig(charge, "0", " %")
            + (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging) ? " charging" : "");
        MemCard.ShowStats("BATTERY", _chargeText, "POWER", _drawText);

        // The fans: the first one's speed large and turning, what the curve is asking for, the second fan.
        _fanIcon.SetSpeed(fan1);
        CoolCard.ShowFigure(fan1, "0", "rpm");
        bool commanding = _settings.FanControlMode == FanControlMode.Auto && _service.IsRunning && _service.HasCommanded;
        CoolCard.ShowBar(_curve, commanding ? $"{_service.LastCommandedLevelPercent} %" : "not commanding",
                         commanding ? _service.LastCommandedLevelPercent / 100.0 : null);
        CoolCard.ShowBar(_fan2, Fig(fan2, "0", " rpm"), Gauge.Fraction(fan2, maxRpm));
        CoolCard.ShowStats("MODE", _settings.FanControlMode switch
                           {
                               FanControlMode.Auto => _service.IsRunning ? "curve" : "curve stopped",
                               FanControlMode.BiosDefault => "BIOS",
                               FanControlMode.Max => "maximum",
                               _ => Metrics.Unavailable,
                           },
                           "THROTTLING", _throttling ? "reported" : "none");
    }

    // The battery's two lines, kept between the readings that fill them at different rates: the
    // charge on every tick, the draw from its own five-second WMI read.
    private string _chargeText = Metrics.Unavailable;
    private string _drawText = Metrics.Unavailable;

    private Brush Brush(string key) => (Brush)FindResource(key);

    /// <summary>
    /// The processor's name as Windows records it at boot. The registry rather than WMI, because it
    /// is one value read once and WMI is a service round trip; null when it cannot be read.
    /// </summary>
    private static string? ReadProcessorName()
    {
        try
        {
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
        }
        catch { return null; }
    }

    private static string Fig(double? value, string format, string unit) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit : Metrics.Unavailable;

    /// <summary>"19.3 / 25 W" against a known limit, "19.3 W" without one.</summary>
    private static string Of(double? value, double? limit, string format, string unit) =>
        value is not { } v ? Metrics.Unavailable
        : limit is { } l && l > 0
            ? $"{v.ToString(format, CultureInfo.InvariantCulture)} / {l.ToString("0", CultureInfo.InvariantCulture)} {unit}"
            : $"{v.ToString(format, CultureInfo.InvariantCulture)} {unit}";

    // ---------------------------------------------------------------- session figures

    private void ShowSince() => SensorsSince.Text = $"since {_metrics.StatsSince:HH:mm}";

    private void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        _metrics.ResetStats();
        ShowSince();
    }

    // ---------------------------------------------------------------- power and battery

    private System.Windows.Threading.DispatcherTimer? _drawTimer;
    private int _drawInFlight;

    /// <summary>
    /// Live battery draw: what the machine is actually pulling from the pack, and how long that
    /// leaves.
    ///
    /// Its own timer rather than the hardware poll, and the read is pushed to the thread
    /// pool, because ReadDraw is a WMI query against root\wmi. Every OnReading subscriber
    /// runs synchronously inside the poll loop's re-entrancy interlock, so a WMI round trip
    /// there sits directly on the critical path of temperature polling and the fan curve.
    /// That mistake has been made twice in this file already, once with nvidia-smi and once
    /// with an SMU read, and both times it presented as the UI stuttering.
    ///
    /// Five seconds because a discharge figure that updates faster than that is noise: the
    /// ACPI rate is itself an average over the firmware's own sampling window.
    /// </summary>
    private void StartPowerDrawTimer()
    {
        _drawTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _drawTimer.Tick += (_, _) => { RefreshPowerDraw(); RefreshLimits(); };

        // Runs only while this tab is on screen. Views are constructed once and kept, and this
        // one is not IDisposable, so a timer started in the constructor would query WMI every five
        // seconds for the life of the process -- including the whole time the window is hidden in
        // the tray, which is how this application normally sits.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { _drawTimer.Start(); RefreshPowerDraw(); RefreshLimits(); }
            else _drawTimer.Stop();
        };

        if (IsVisible) { _drawTimer.Start(); RefreshPowerDraw(); RefreshLimits(); }
    }

    private int _limitsInFlight;

    /// <summary>
    /// Refreshes the binding-limit strip.
    ///
    /// Shares the five-second timer with the battery draw rather than adding a second one,
    /// because the PM table behind it is cached for five seconds in Core anyway -- the refresh
    /// holds the global Access_PCI mutex and shows up as DPC latency and audio dropouts, which
    /// is exactly why that cache exists. Reading faster than the cache would buy nothing and
    /// risk the thing the cache was added to prevent.
    ///
    /// Off the UI thread and single-flighted, for the same reason the draw read is.
    /// </summary>
    private void RefreshLimits()
    {
        if (Interlocked.Exchange(ref _limitsInFlight, 1) == 1) return;

        Task.Run(() =>
        {
            try { return _ctx.Smu?.ReadPowerSnapshot(); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _limitsInFlight, 0);
            Dispatcher.BeginInvoke(() =>
            {
                Limits.Show(
                    t.IsCompletedSuccessfully ? t.Result : null,
                    _ctx.SmuUnavailableReason);

                // The band underneath, from the history the read above has just contributed to.
                // No second hardware access: the SMU records into it on every real read, so this
                // is a walk over an in-memory ring.
                if (_ctx.Smu is { } smu) Limits.ShowHistory(smu.Limits, LimitWindow);
            });
        });
    }

    /// <summary>
    /// How far back the binding-limit band looks.
    ///
    /// An hour, because the question it answers is about a session rather than a moment -- "what
    /// held this back while I was playing" -- and because the ring holds two, so an hour is a
    /// window the history can always fill rather than a claim it cannot back.
    /// </summary>
    private static readonly TimeSpan LimitWindow = TimeSpan.FromHours(1);

    private void RefreshPowerDraw()
    {
        // Single-flight: a slow WMI provider must not stack reads behind itself.
        if (Interlocked.Exchange(ref _drawInFlight, 1) == 1) return;

        Task.Run(() =>
        {
            OmniHub.Core.Optimize.BatteryDraw? draw = null;
            try { draw = OmniHub.Core.Optimize.BatterySaver.ReadDraw(); }
            catch { }
            finally { Interlocked.Exchange(ref _drawInFlight, 0); }

            Dispatcher.BeginInvoke(() => ShowPowerDraw(draw));
        });
    }

    private void ShowPowerDraw(OmniHub.Core.Optimize.BatteryDraw? draw)
    {
        _drawText = DrawText(draw);
        MemCard.ShowStats("BATTERY", _chargeText, "POWER", _drawText);
    }

    /// <summary>What the pack is doing, short enough for a card's foot.</summary>
    private static string DrawText(OmniHub.Core.Optimize.BatteryDraw? draw)
    {
        // No battery, or the provider refused. Not zero watts.
        if (draw is null) return "unavailable";

        // Charging draws real power too, and it is worth seeing, but the pack is not discharging
        // so there is no runtime to report.
        if (draw.OnAc)
            return draw.Charging && draw.ChargeMilliwatts > 0
                ? FormattableString.Invariant($"AC, +{draw.ChargeMilliwatts / 1000.0:0.0} W")
                : "AC";

        // On battery but the rate came back zero: the firmware not having sampled yet, not the
        // machine drawing nothing.
        if (draw.DischargeMilliwatts <= 0) return "battery, measuring";

        string watts = FormattableString.Invariant($"{draw.DischargeMilliwatts / 1000.0:0.0} W");
        return OmniHub.Core.Optimize.BatterySaver.EstimateRuntime(draw) is { } t
            ? FormattableString.Invariant($"{watts}, {(int)t.TotalHours} h {t.Minutes:00} left")
            : watts;
    }

    /// <summary>
    /// Charge, health and cycles. Static enough that once per launch is enough, and
    /// BatteryInfoReader runs several WMI queries, so it stays off the UI thread.
    /// </summary>
    private void LoadBattery()
    {
        Task.Run(() => BatteryInfoReader.Read()).ContinueWith(t =>
        {
            var b = t.IsCompletedSuccessfully ? t.Result : null;
            Dispatcher.BeginInvoke(() =>
            {
                if (b is null)
                {
                    MemCard.ToolTip = "Battery details unavailable";
                    return;
                }

                // Health only when both capacities were actually reported. A wear figure derived
                // from a zero design capacity would be invented, not measured.
                string health = b.DesignCapacityMWh > 0 && b.FullChargeCapacityMWh > 0
                    ? FormattableString.Invariant($"health {b.FullChargeCapacityMWh * 100.0 / b.DesignCapacityMWh:0.#}%")
                    : "health not reported";
                string cycles = b.CycleCount > 0 ? $" · {b.CycleCount} cycles" : "";

                // In the card's tooltip: the card is memory and the battery beside it, and the pack's
                // wear is a slow fact that does not need a place on the screen every second.
                MemCard.ToolTip = $"Battery {b.ChargePercent}% · {health}{cycles}";
            });
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- profile

    private void SilentBtn_Checked(object sender, RoutedEventArgs e) { ShowProfileNote(); if (!_suppressPresetEvent) ApplyPreset(GpuPowerLevel.Eco, FanControlMode.Auto); }
    private void BalancedBtn_Checked(object sender, RoutedEventArgs e) { ShowProfileNote(); if (!_suppressPresetEvent) ApplyPreset(GpuPowerLevel.Balanced, FanControlMode.Auto); }
    private void PerformanceBtn_Checked(object sender, RoutedEventArgs e) { ShowProfileNote(); if (!_suppressPresetEvent) ApplyPreset(GpuPowerLevel.Performance, FanControlMode.Max); }

    /// <summary>
    /// What the selected profile sends, in full.
    ///
    /// The three buttons were a mood -- quiet, middle, loud -- and nothing said that Performance
    /// also pins both fans at maximum, or that Eco turns the custom TGP off. These are the exact
    /// writes ApplyPreset makes, per GpuPowerData.ForLevel.
    /// </summary>
    private void ShowProfileNote()
    {
        if (ProfileNote is null) return;   // Checked fires during InitializeComponent

        ProfileNote.Text = PerformanceBtn.IsChecked == true
            ? "Custom TGP on, Dynamic Boost on, fans held at maximum."
            : SilentBtn.IsChecked == true
                ? "Custom TGP off, Dynamic Boost off, fans on the curve."
                : "Custom TGP on, Dynamic Boost off, fans on the curve.";
    }

    // Every call here (SetPowerPreset, SetMaxFan, Stop's RestoreAutomaticControl, and
    // RefreshGpuMode's own reads) is a synchronous BIOS/WMI call. Run the whole thing
    // off the UI thread -- called directly from a button click, this was blocking the
    // UI for the full chain of hardware calls before, which read as "freezing."
    private void ApplyPreset(GpuPowerLevel gpuLevel, FanControlMode fanMode)
    {
        Task.Run(() =>
        {
            try { _ctx.Gpu.SetPowerPreset(gpuLevel); } catch { }
            try
            {
                if (fanMode == FanControlMode.Auto) { _ctx.System.SetMaxFan(false); _service.Start(); }
                else if (fanMode == FanControlMode.Max) { if (_service.IsRunning) _service.Stop(); _ctx.System.SetMaxFan(true); }
                _settings.FanControlMode = fanMode;
                _settings.Save();
            }
            catch { }

            string mode = ReadGpuMode();
            Dispatcher.BeginInvoke(() => { _gpuMode = mode; ShowGpu(); });
        });
    }

    // ---------------------------------------------------------------- graphics

    // GetMode()/GetPower() are synchronous BIOS/WMI calls, so never on the UI thread.
    private void RefreshGpuMode()
    {
        Task.Run(ReadGpuMode).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _gpuMode = t.IsCompletedSuccessfully ? t.Result : "--";
            ShowGpu();
        }), TaskScheduler.Default);
    }

    /// <summary>The BIOS's own statement of the graphics mode and power flags, and who answered for the readings.</summary>
    private string ReadGpuMode()
    {
        var parts = new List<string>();
        try
        {
            parts.Add(_ctx.Gpu.GetMode().ToString());
            var power = _ctx.Gpu.GetPower();
            parts.Add($"custom TGP {power.CustomTgp.ToString().ToLowerInvariant()}");
            parts.Add($"Dynamic Boost {power.Ppab.ToString().ToLowerInvariant()}");
        }
        catch { }

        // Name the provider. The sources genuinely differ -- nvidia-smi gives temperature, power
        // and clock, the Windows counters utilisation and nothing else -- and without this a
        // blank temperature reads as a broken sensor rather than as a source that has none.
        if (GpuTelemetry.Read() is { } gpu) parts.Add($"read via {SourceName(gpu.Source)}");

        return parts.Count == 0 ? "--" : string.Join(" · ", parts);
    }

    /// <summary>
    /// Whether the card is awake beside its category, where it changes what every figure on the card
    /// means; the BIOS mode, the power flags and the reading's route in the card's tooltip.
    /// </summary>
    private void ShowGpu()
    {
        GpuCard.Category = _gpuState is { } state ? $"Graphics · {state}" : "Graphics";
        GpuCard.ToolTip = _gpuMode;
    }

    private static string SourceName(GpuSource source) => source switch
    {
        GpuSource.Nvml => "NVML",
        GpuSource.NvidiaSmi => "nvidia-smi",
        GpuSource.WindowsCounters => "Windows counters",
        var other => other.ToString(),
    };

    // ---------------------------------------------------------------- readiness

    /// <summary>
    /// Says what this machine is and which of OmniHub's capabilities it actually has.
    ///
    /// Every fact here was already being detected -- the model from ModelProfile, the vendor
    /// interface from BiosInterop, the SMU from RyzenSmu, the GPU from GpuTelemetry -- but the
    /// only place any of it surfaced was one line buried in the Tuning tab. Someone whose
    /// machine was missing a piece had to go looking for the explanation of why half the app
    /// did nothing.
    ///
    /// Deliberately NOT a downloader. The only component that could ever be fetched is PawnIO,
    /// which is a signed kernel driver: fetching and executing ring-0 code on first launch,
    /// without the user reading what it is, is a supply-chain problem wearing a convenience
    /// costume. Its own installer registers the service and handles signing properly, so the
    /// honest move is to name what is missing and open the page.
    /// </summary>
    private void BuildReadiness()
    {
        // The deciding moved to OmniHub.Core.Vendors.MachineSupport, which is a pure function of
        // stated facts and therefore testable -- this reasoning used to live here, in a view, on
        // a project whose tests deliberately cannot reference the application at all.
        var facts = _ctx.Facts();
        var limited = MachineSupport.Shortfalls(facts);

        // Rebuilt from scratch, because this now runs on every return to the tab rather than
        // once: appending without clearing would stack a fresh copy of every row each visit,
        // and a capability that has since become available has to be able to disappear.
        ReadinessRows.Children.Clear();

        if (limited.Count == 0)
        {
            ReadinessCard.Visibility = Visibility.Collapsed;
            return;
        }

        ReadinessCard.Visibility = Visibility.Visible;
        ReadinessHeadline.Text = MachineSupport.Summarise(facts);

        foreach (var capability in limited)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };

            // The marker is plain text and stays the colour everything else is. A capability that
            // reads but cannot be commanded is information, not an alarm, and this application
            // does not colour prose.
            panel.Children.Add(new TextBlock
            {
                Text = capability.State == SupportState.ReadOnly
                    ? capability.Name + "  —  READ ONLY"
                    : capability.Name,
                Style = (Style)FindResource("BodyText"),
                FontSize = 12,
            });
            panel.Children.Add(new TextBlock
            {
                Text = capability.Detail,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 740,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            });
            ReadinessRows.Children.Add(panel);
        }

        // The one missing piece a user can actually install, so the one that gets a button.
        // Set both ways: the SMU can open on a retry after this card first appeared, and the
        // offer to install a driver that is already loaded would be nonsense.
        GetPawnIoBtn.Visibility = _ctx.Smu is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GetPawnIo_Click(object sender, RoutedEventArgs e)
    {
        GetPawnIoBtn.IsEnabled = false;
        ReadinessHeadline.Text = "Installing the PawnIO driver through winget. Windows will ask you to approve it.";

        Task.Run(InstallPawnIo).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            GetPawnIoBtn.IsEnabled = true;
            ReadinessHeadline.Text = t.IsFaulted
                ? $"Install failed: {t.Exception?.GetBaseException().Message}. The driver is at https://pawnio.eu"
                : t.Result;
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Installs PawnIO through winget rather than through a downloader written here.
    ///
    /// This is the whole reason it can be one click safely. A hand-rolled version would have
    /// to fetch a binary over the network and execute it in kernel space, which means owning
    /// URL trust, hash verification and a publisher check for exactly one file -- a trust
    /// store maintained by this app, for one dependency, forever. winget already does all of
    /// it: the package manifest pins a SHA256, Windows verifies the download against it, and
    /// the installer raises its own elevation prompt that the user sees and approves.
    ///
    /// The safety here is therefore not "we were careful", it is that the verification belongs
    /// to Microsoft's package manager rather than to code written in an afternoon.
    ///
    /// No restart afterwards: HardwareContext.RetrySmuOpen re-attempts every ten seconds while
    /// the SMU is closed, so tuning comes online on its own once the driver registers.
    /// </summary>
    private static string InstallPawnIo()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("winget.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Exact id match, and both agreement flags: output is redirected, so an interactive
        // "do you accept the source agreement?" would block forever with nobody able to answer.
        foreach (var arg in new[]
                 {
                     "install", "--id", "namazso.PawnIO", "--exact",
                     "--accept-source-agreements", "--accept-package-agreements",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return "winget could not be started. Install the driver from https://pawnio.eu";

            // stderr drained concurrently -- see GpuTelemetry.Query for why. winget is by far
            // the most verbose thing this app launches, so it is the likeliest to fill a pipe.
            _ = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(180_000))
                return "The install is taking unusually long; check on it in a terminal. Driver: https://pawnio.eu";

            if (proc.ExitCode == 0)
                return "PawnIO installed. Processor tuning comes online within about ten seconds -- no restart needed.";

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string last = lines.Length > 0 ? lines[^1].Trim() : "";
            return $"winget exited with code {proc.ExitCode}. {last} Install manually from https://pawnio.eu";
        }
        catch (Exception ex)
        {
            // Most likely winget itself is absent -- it ships with App Installer, which some
            // trimmed or older Windows builds do not have.
            return $"winget is unavailable here ({ex.GetType().Name}). Install the driver from https://pawnio.eu";
        }
    }

    // ---------------------------------------------------------------- the poll

    private void OnReading(Reading r)
    {
        // Read on the poll thread, before marshalling. A cache miss spawns nvidia-smi, which
        // costs about 56 ms -- fine here, a visible hitch on the UI thread. The power state comes
        // from the PCI bus driver rather than from the card, so asking it wakes nothing: it is the
        // one measurement of a sleeping GPU that can be taken without destroying it.
        var gpu = GpuTelemetry.Read();
        string? gpuState = null;
        if (gpu?.TempC is null)
        {
            // "Asleep" and "unavailable" are different facts: on battery the card is deliberately
            // not queried, because asking wakes it, and that is the feature working.
            var d = GpuPowerState.ReadDiscrete();
            gpuState = d is DevicePowerState.D1 or DevicePowerState.D2 or DevicePowerState.D3
                ? GpuPowerState.Describe(d).ToLowerInvariant()
                : GpuTelemetry.IsAvailable ? "not reporting" : "no GPU reported";
        }

        // BeginInvoke, not Invoke.
        //
        // This runs on the poll thread, which holds the poll loop's re-entrancy interlock for
        // the whole call. A synchronous Invoke therefore parks the entire temperature poll for
        // as long as the UI thread is busy -- and the UI thread can be busy for an unbounded
        // time, because several error paths in this app open a modal MessageBox. While it is
        // parked no readings are produced, CurrentTemperature starts throwing "stale", and the
        // fan curve stops commanding: a UI hiccup taking the cooling down with it.
        string? gpuName = HardwareNames.ShortGpu(gpu?.Name);

        Dispatcher.BeginInvoke(() =>
        {
            // The firmware's own statement, shown on the dial's status rather than inferred from
            // the temperature, because it is the one reading that says the machine is already
            // doing something about the heat.
            _throttling = r.Throttling == true;

            if (gpuName is not null) GpuCard.Title = gpuName;
            _gpuState = gpuState;
            ShowGpu();
        });
    }
}
