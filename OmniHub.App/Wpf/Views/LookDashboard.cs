// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
using OmniHub.Core.Vendors;
using UserControl = System.Windows.Controls.UserControl;

using RadioButton = System.Windows.Controls.RadioButton;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// What the Frosted and Dot matrix Dashboards share (TECHNICAL-v5-ui.md, section 13): the readings on
/// the shared tick while the page is on screen, the firmware's throttling flag from the poll, the
/// presets through <see cref="PresetActions"/> and the fan modes through the window's one path to the
/// Fans page, the battery, and the entrance. Each look lays out its own tiles over this.
/// </summary>
public abstract class LookDashboard : UserControl
{
    protected readonly HardwareContext Ctx;
    protected readonly FanService Service;
    protected readonly AppSettings Settings;
    protected readonly MetricSource Metrics;
    private readonly Action<FanControlMode> _setFanMode;

    /// <summary>The tiles in reading order, for the entrance.</summary>
    protected readonly List<FrameworkElement> Tiles = new();

    /// <summary>The processor's short name, from the value Windows writes at boot; read once.</summary>
    protected readonly string CpuName = HardwareNames.ShortCpu(DashboardView.ReadProcessorName()) ?? "Processor";

    /// <summary>The firmware's own statement that it is holding the machine back, from the last reading.</summary>
    protected bool Throttling { get; private set; }

    /// <summary>The graphics card's name, and why it has no temperature when it has none.</summary>
    protected string GpuName { get; private set; } = "Discrete GPU";
    protected string? GpuState { get; private set; }

    /// <summary>The pack's wear, from the ACPI battery classes; read once, off the UI thread.</summary>
    protected BatteryHealth? Battery { get; private set; }

    protected LookDashboard(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics, Action<FanControlMode> setFanMode)
    {
        Ctx = ctx; Service = service; Settings = settings; Metrics = metrics;
        _setFanMode = setFanMode;

        // On Loaded rather than once: leaving the page detaches it, and a page that subscribed in its
        // constructor and unsubscribed on Unloaded came back frozen (DashboardView says how).
        Loaded += (_, _) =>
        {
            metrics.Updated -= Update; metrics.Updated += Update;
            ctx.OnReading -= OnReading; ctx.OnReading += OnReading;
            Update();
        };
        Unloaded += (_, _) => { metrics.Updated -= Update; ctx.OnReading -= OnReading; };

        Task.Run(BatteryInfoReader.Read).ContinueWith(t =>
            Dispatcher.BeginInvoke(() => { Battery = t.IsCompletedSuccessfully ? t.Result : null; Update(); }), TaskScheduler.Default);
    }

    // On the poll thread: the GPU's name and, when it gives no temperature, whether it is asleep --
    // read from the bus driver, which wakes nothing. BeginInvoke, never Invoke, so a busy UI thread
    // cannot park the poll (DashboardView.OnReading).
    private void OnReading(Reading r)
    {
        var gpu = GpuTelemetry.Read();
        string? state = null;
        if (gpu?.TempC is null)
        {
            var d = GpuPowerState.ReadDiscrete();
            state = d is DevicePowerState.D1 or DevicePowerState.D2 or DevicePowerState.D3
                ? GpuPowerState.Describe(d).ToLowerInvariant()
                : GpuTelemetry.IsAvailable ? "not reporting" : "no GPU reported";
        }
        string? name = HardwareNames.ShortGpu(gpu?.Name);
        Dispatcher.BeginInvoke(() =>
        {
            Throttling = r.Throttling == true;
            if (name is not null) GpuName = name;
            GpuState = state;
        });
    }

    /// <summary>Redraws every tile from the readings; on the shared tick, and after anything is chosen.</summary>
    protected abstract void Update();

    /// <summary>
    /// The page's entrance, called as it is shown: the tiles in reading order inside the motion budget,
    /// then whatever each look draws in. False when nothing moves, so the window's own stagger is skipped too.
    /// </summary>
    public bool Enter()
    {
        if (!Controls.Motion.On) return false;
        Controls.Motion.Stagger(Tiles.Cast<UIElement>().ToList());
        OnEnter(i => OmniHub.Core.Theming.MotionBudget.Delay(i, Tiles.Count));
        return true;
    }

    /// <summary>What a look animates besides the tiles, given each tile's entrance delay by index.</summary>
    protected virtual void OnEnter(Func<int, int> delayOf) { }

    // ---------------------------------------------------------------- choices

    /// <summary>
    /// The preset in force, as well as it can be known: the settings keep only the fan mode, so maximum
    /// fans read as Performance and anything else as Balanced -- the guess the console look makes.
    /// </summary>
    protected int ActivePreset => Settings.FanControlMode == FanControlMode.Max ? 2 : 1;

    protected void ApplyPreset(int index)
    {
        var p = PresetActions.All[index];
        PresetActions.Apply(Ctx, Service, Settings, p.Gpu, p.Fans).ContinueWith(_ => Dispatcher.BeginInvoke(Update), TaskScheduler.Default);
    }

    protected void SetFanMode(FanControlMode mode)
    {
        _setFanMode(mode);
        Update();
    }

    // ---------------------------------------------------------------- words

    protected static string Fig(double? value, string format, string unit = "") => DashboardView.Fig(value, format, unit);

    /// <summary>The machine's state in a few words, and the brush for its dot.</summary>
    protected (string Words, string BrushKey) State()
    {
        double? t = Metrics.Value("cpu");
        if (Throttling) return ("Firmware throttling", "DangerBrush");
        if (t is null) return ("No reading", "TextFaintBrush");
        return OmniHub.Core.Telemetry.Metrics.LevelOf("cpu", t) switch
        {
            MetricLevel.Hot => ("Running hot", "DangerBrush"),
            MetricLevel.Warn => ("Running warm", "WarnBrush"),
            _ => ("Running cool", "GoodBrush"),
        };
    }

    /// <summary>Who is steering the fans: the words the console's top bar uses, in sentence case.</summary>
    protected string FanWords()
    {
        bool steering = Settings.FanControlMode == FanControlMode.Auto && Service.IsRunning && Service.HasCommanded;
        return Settings.FanControlMode switch
        {
            FanControlMode.Auto when steering => $"On the curve · asks {Service.LastCommandedLevelPercent} %",
            FanControlMode.Auto => "Curve stopped",
            FanControlMode.BiosDefault => "The BIOS's own curve",
            FanControlMode.Max => "Held at maximum",
            _ => "Fans",
        };
    }

    /// <summary>Windows' own battery status: charge, whether it is charging, whether it is on the charger.</summary>
    protected static (double? Percent, bool Charging, bool? OnAc) Power()
    {
        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        bool battery = !power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                       && power.BatteryLifePercent <= 1f;   // 255 over 100 is Windows for "unknown"
        return (battery ? Math.Round(power.BatteryLifePercent * 100) : null,
                power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging),
                power.PowerLineStatus switch
                {
                    System.Windows.Forms.PowerLineStatus.Online => true,
                    System.Windows.Forms.PowerLineStatus.Offline => false,
                    _ => null,
                });
    }

    /// <summary>The pack's health against its design, or null where the pack does not say.</summary>
    protected string? Health() =>
        Battery is { DesignCapacityMWh: > 0, FullChargeCapacityMWh: > 0 } b
            ? FormattableString.Invariant($"health {b.FullChargeCapacityMWh * 100.0 / b.DesignCapacityMWh:0}%")
            : null;

    // ---------------------------------------------------------------- building

    /// <summary>A tile: the look's panel and border, its corners, placed in the grid and in reading order.</summary>
    protected Border Tile(Grid grid, UIElement child, int row, int column, int rowSpan = 1, int columnSpan = 1, Thickness? padding = null)
    {
        var tile = new Border { Child = child, BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(20, 18, 20, 18) };
        tile.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        tile.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        tile.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        Grid.SetRow(tile, At(row)); Grid.SetColumn(tile, At(column));
        Grid.SetRowSpan(tile, rowSpan * 2 - 1); Grid.SetColumnSpan(tile, columnSpan * 2 - 1);
        grid.Children.Add(tile);
        Tiles.Add(tile);
        return tile;
    }

    /// <summary>Words in the interface's face, in a theme brush.</summary>
    protected static TextBlock Words(string text = "", double size = 12.5, string brush = "TextMutedBrush", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A choice among several, in the look's pill: a round button when <paramref name="round"/> is set.</summary>
    protected RadioButton Pill(string text, string group, bool chosen, Action onChosen, double? round = null)
    {
        var b = new RadioButton { Content = text, GroupName = group + GetHashCode(), IsChecked = chosen, Style = (Style)FindResource("LookPillStyle") };
        if (round is { } size) { b.Width = b.Height = size; b.Padding = new Thickness(0); }
        b.SizeChanged += (_, e) => { if (b.Template?.FindName("Bd", b) is Border bd) bd.CornerRadius = new CornerRadius(e.NewSize.Height / 2); };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        b.Checked += (_, _) => { if (IsLoaded && !_syncing) onChosen(); };
        return b;
    }

    private bool _syncing;

    /// <summary>
    /// Shows a choice made elsewhere -- the tray, the Fans page -- without making it again: checking a
    /// button raises Checked, and a fan mode chosen twice is two writes to the BIOS.
    /// </summary>
    protected void Show(RadioButton[] choices, int chosen)
    {
        _syncing = true;
        try { for (int i = 0; i < choices.Length; i++) choices[i].IsChecked = i == chosen; }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Ends that are exact half-circles at whatever height the content makes. A corner radius larger
    /// than the box is not clamped by WPF but squashed into an ellipse, which turns a wide pill into an oval.
    /// </summary>
    protected static T Round<T>(T border) where T : Border
    {
        border.SizeChanged += (_, e) => border.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        return border;
    }

    /// <summary>A grid of star rows and columns with gap tracks between, for the tiles.</summary>
    protected static Grid Lattice(double[] columns, double[] rows, double gap, double[] minRows)
    {
        var g = new Grid();
        for (int i = 0; i < columns.Length; i++)
        {
            if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(columns[i], GridUnitType.Star) });
        }
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rows[i], GridUnitType.Star), MinHeight = minRows[i] });
        }
        return g;
    }

    /// <summary>The grid index of the <paramref name="i"/>-th star track, past the gap tracks between them.</summary>
    private static int At(int i) => i * 2;

    /// <summary>The page itself: the lattice, scrolling when the window is shorter than it needs.</summary>
    protected void Host(Grid lattice)
    {
        lattice.Margin = new Thickness(24, 18, 24, 24);
        var viewer = new ScrollViewer
        {
            Content = lattice,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        // A scroller measures its content at infinite height, where star rows fall to their minimum;
        // so the lattice is given the height there is, and scrolls only below what it needs.
        double least = lattice.RowDefinitions.Sum(r => r.Height.IsAbsolute ? r.Height.Value : r.MinHeight);
        viewer.SizeChanged += (_, e) => lattice.Height = Math.Max(e.NewSize.Height - lattice.Margin.Top - lattice.Margin.Bottom, least);
        Content = viewer;
    }
}
