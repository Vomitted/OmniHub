// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;

using RadioButton = System.Windows.Controls.RadioButton;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The Frosted look's Dashboard, after "Smart Home Dashboard -- Designed for Modern Living" by Beka
/// Kvaratskhelia (TECHNICAL-v5-ui.md, section 13): glass tiles over a warm, lit room, the machine as
/// the hero with its readings pinned beside it, soft pills for every choice. Every figure is this
/// laptop's own reading; a tile with no reading says so rather than showing a shape without one.
/// </summary>
public sealed class FrostedDashboardView : LookDashboard
{
    private const string Light = "Segoe UI Variable Display Light, Segoe UI Light, Segoe UI";

    private readonly TextBlock _screenTemp = Figure(54), _heroName = Words(size: 19, brush: "TextPrimaryBrush", weight: FontWeights.SemiBold),
        _heroState = Words(size: 12.5), _heroLimit = Words(size: 12.5);
    private readonly Ellipse _stateDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chipGpu = Words(size: 12.5, brush: "TextPrimaryBrush"), _chipFan = Words(size: 12.5, brush: "TextPrimaryBrush"),
        _chipPkg = Words(size: 12.5, brush: "TextPrimaryBrush");
    private readonly Border _lid = new(), _glow = new();

    private readonly Controls.FanGlyph _fan = new() { ColourKey = "MetricFanBrush", Width = 66, Height = 66 };
    private readonly TextBlock _rpm = Figure(44), _fanWords = Words(size: 12);
    private readonly RadioButton[] _fanModes;

    private readonly TextBlock _limitName = Words(size: 12.5, brush: "TextPrimaryBrush"), _limitPercent = Words(size: 12.5);
    private readonly Border _limitFill = Fill("AccentBrush");

    private readonly TextBlock _cpuTemp = Figure(46), _cpuLine = Words(size: 12), _gpuTemp = Figure(46), _gpuLine = Words(size: 12),
        _gpuTitle = Words(size: 12.5);

    private readonly Controls.RingGauge _power = new() { Diameter = 104 };
    private readonly TextBlock _powerFigure = Figure(38), _powerLine = Words(size: 12);

    private readonly TextBlock _memFigure = Figure(38), _memOf = Words(size: 12.5);
    private readonly Border _memFill = Fill("MetricMemBrush");

    private readonly Controls.BatteryGlyph _battery = new() { Width = 66, Height = 34 };
    private readonly TextBlock _batteryFigure = Figure(38), _batteryLine = Words(size: 12);

    private readonly List<(FrameworkElement Trace, int Tile)> _traces = new();

    public FrostedDashboardView(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics, Action<FanControlMode> setFanMode)
        : base(ctx, service, settings, metrics, setFanMode)
    {
        var grid = Lattice(new[] { 1.55, 1, 1 }, new[] { 1.7, 1, 1 }, 14, new double[] { 280, 156, 150 });

        Tile(grid, Hero(), 0, 0, padding: new Thickness(22, 18, 22, 18));
        Tile(grid, Fans(out _fanModes), 0, 1);
        Tile(grid, Presets(), 0, 2, rowSpan: 2);
        Tint(Tile(grid, Component("Processor", Words(CpuName, 12.5), _cpuTemp, _cpuLine, "cpu", "MetricCpu"), 1, 0), "MetricCpuColor");
        Tint(Tile(grid, Component("Graphics", _gpuTitle, _gpuTemp, _gpuLine, "gpu", "MetricGpu"), 1, 1), "MetricGpuColor");
        Tile(grid, PowerTile(), 2, 0);
        Tile(grid, MemoryTile(), 2, 1);
        Tile(grid, BatteryTile(), 2, 2);

        Host(grid);
    }

    // ---------------------------------------------------------------- the tiles

    private UIElement Hero()
    {
        // The machine and what it is doing on the left; its readings in a column of their own on the
        // right, so the two never share pixels however narrow the tile is.
        var g = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };

        var art = Laptop();
        art.HorizontalAlignment = HorizontalAlignment.Center;
        art.VerticalAlignment = VerticalAlignment.Center;
        art.Margin = new Thickness(0, 40, 12, 58);
        g.Children.Add(art);

        var live = Chip(new StackPanel { Orientation = Orientation.Horizontal, Children = { LiveDot(), Words("Live", 12.5, "TextPrimaryBrush") } });
        live.HorizontalAlignment = HorizontalAlignment.Left;
        live.VerticalAlignment = VerticalAlignment.Top;
        g.Children.Add(live);

        var foot = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
        _heroName.Text = HardwareNames.ShortModel(Ctx.Model.Manufacturer, Ctx.Model.Product) ?? "This machine";
        foot.Children.Add(_heroName);
        foot.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { _stateDot, _heroState } });
        g.Children.Add(foot);

        var chips = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) };
        foreach (var (label, value) in new[] { ("Graphics", _chipGpu), ("Fan", _chipFan), ("Processor power", _chipPkg) })
        {
            var chip = Chip(new StackPanel { Children = { Words(label, 11.5), value } });
            chip.Margin = new Thickness(0, 0, 0, 8);
            chip.MinWidth = 150;
            chips.Children.Add(chip);
        }
        Grid.SetColumn(chips, 1);
        g.Children.Add(chips);

        _heroLimit.HorizontalAlignment = HorizontalAlignment.Right;
        _heroLimit.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_heroLimit, 1);
        g.Children.Add(_heroLimit);
        return g;
    }

    /// <summary>
    /// A laptop, lid up: the reading on its screen in white over the screen's own near-black, which
    /// is drawn here and so is the same under every palette.
    /// </summary>
    private FrameworkElement Laptop()
    {
        var screen = new Grid();
        _glow.CornerRadius = new CornerRadius(7);
        _glow.Background = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.35), GradientOrigin = new Point(0.5, 0.35), RadiusX = 0.8, RadiusY = 0.9,
            GradientStops = { new GradientStop(Color.FromArgb(0x55, 0xFF, 0xD7, 0x9A), 0), new GradientStop(Color.FromArgb(0, 0xFF, 0xD7, 0x9A), 1) },
        };
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0D, 0x0C)) });
        screen.Children.Add(_glow);
        Controls.Motion.Loop(_glow, _glow, OpacityProperty, 1, 0.55, 4200, autoReverse: true);

        _screenTemp.Foreground = Brushes.White;
        _screenTemp.HorizontalAlignment = HorizontalAlignment.Center;
        var caption = new TextBlock { Text = "processor", Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xC4, 0xBC)), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };
        screen.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _screenTemp, caption } });

        _lid.Width = 250; _lid.Height = 160;
        _lid.CornerRadius = new CornerRadius(12);
        _lid.Padding = new Thickness(9);
        _lid.BorderThickness = new Thickness(1.5);
        _lid.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        _lid.Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x28, 0x25), Color.FromRgb(0x15, 0x14, 0x12), 90);
        _lid.Child = screen;
        _lid.RenderTransformOrigin = new Point(0.5, 1);
        _lid.RenderTransform = new ScaleTransform(1, 1);

        var deck = new Path
        {
            Data = Geometry.Parse("M 0,0 L 290,0 L 280,12 Q 278,14 272,14 L 18,14 Q 12,14 10,12 Z"),
            Fill = new LinearGradientBrush(Color.FromRgb(0x3A, 0x37, 0x33), Color.FromRgb(0x1C, 0x1A, 0x18), 90),
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var shadow = new Ellipse
        {
            Width = 300, Height = 22, Margin = new Thickness(0, -4, 0, 0),
            Fill = new RadialGradientBrush(Color.FromArgb(0x70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)),
        };

        return new Viewbox
        {
            MaxWidth = 330,
            Child = new StackPanel { Width = 300, Children = { new Border { Child = _lid, HorizontalAlignment = HorizontalAlignment.Center }, deck, shadow } },
        };
    }

    private UIElement Fans(out RadioButton[] modes)
    {
        var p = new DockPanel();
        _fanWords.Margin = new Thickness(0, 3, 0, 0);
        p.Children.Add(Docked(new StackPanel { Children = { Words("Fans", 16, "TextPrimaryBrush", FontWeights.SemiBold), _fanWords } }, Dock.Top));

        var choices = new UniformGrid { Columns = 3, Margin = new Thickness(0, 12, 0, 0) };
        modes = new[] { (FanControlMode.Auto, "Auto"), (FanControlMode.BiosDefault, "BIOS"), (FanControlMode.Max, "Max") }
            .Select(m => Pill(m.Item2, "fans", Settings.FanControlMode == m.Item1, () => SetFanMode(m.Item1))).ToArray();
        foreach (var m in modes) { m.Margin = new Thickness(0, 0, 6, 0); choices.Children.Add(m); }
        p.Children.Add(Docked(choices, Dock.Bottom));

        p.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Children = { _fan, new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _rpm, Words("rpm", 12) } } },
        });
        return p;
    }

    private UIElement Presets()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(new StackPanel { Children = { Words("Presets", 16, "TextPrimaryBrush", FontWeights.SemiBold), Words("What each one sends, in full", 12) } }, Dock.Top));

        var limit = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        limit.Children.Add(Words("What the processor answers to", 12));
        limit.Children.Add(new DockPanel { Margin = new Thickness(0, 4, 0, 8), Children = { Docked(_limitPercent, Dock.Right), _limitName } });
        limit.Children.Add(Track(_limitFill));
        p.Children.Add(Docked(limit, Dock.Bottom));

        var list = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < PresetActions.All.Length; i++)
        {
            var preset = PresetActions.All[i];
            int index = i;
            var b = new RadioButton
            {
                GroupName = "presets" + GetHashCode(), IsChecked = i == ActivePreset,
                Style = (Style)FindResource("LookChoiceStyle"), Margin = new Thickness(0, 0, 0, 10),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = preset.Name, FontSize = 14, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = preset.Note, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) },
                    },
                },
            };
            System.Windows.Automation.AutomationProperties.SetName(b, $"{preset.Name}: {preset.Note}");
            b.Checked += (_, _) => { if (IsLoaded) ApplyPreset(index); };
            list.Children.Add(b);
        }
        p.Children.Add(list);
        return p;
    }

    private UIElement Component(string title, TextBlock subtitle, TextBlock figure, TextBlock line, string key, string colour)
    {
        // The figure beside the name rather than under it, so a short tile keeps it; the last five
        // minutes fill what is left, against the reading's own hot point.
        var p = new DockPanel();
        figure.VerticalAlignment = VerticalAlignment.Top;
        figure.Margin = new Thickness(12, -8, 0, 0);
        p.Children.Add(Docked(new DockPanel
        {
            Children = { Docked(figure, Dock.Right), new StackPanel { Children = { Words(title, 14, "TextPrimaryBrush", FontWeights.SemiBold), subtitle } } },
        }, Dock.Top));

        line.Margin = new Thickness(0, 4, 0, 0);
        p.Children.Add(Docked(line, Dock.Top));

        var trace = new Controls.Trace(Metrics, () => OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find(key)!, null),
                                       (key, colour + "Brush")) { Margin = new Thickness(0, 10, 0, 0) };
        _traces.Add((trace, Tiles.Count));
        p.Children.Add(trace);
        return p;
    }

    private UIElement PowerTile() => new DockPanel
    {
        Children =
        {
            Docked(_power, Dock.Left),
            new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Words("Processor power", 12.5), _powerFigure, _powerLine } },
        },
    };

    private UIElement MemoryTile()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(new DockPanel { Children = { Docked(_memOf, Dock.Right), Words("Memory", 14, "TextPrimaryBrush", FontWeights.SemiBold) } }, Dock.Top));
        p.Children.Add(Docked(Track(_memFill), Dock.Bottom));
        _memFigure.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(_memFigure);
        return p;
    }

    private UIElement BatteryTile() => new StackPanel
    {
        Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
        Children = { _battery, new StackPanel { Margin = new Thickness(18, 0, 0, 0), Children = { _batteryFigure, _batteryLine } } },
    };

    // ---------------------------------------------------------------- the readings

    protected override void Update()
    {
        double? cpu = Metrics.Value("cpu"), gpu = Metrics.Value("gpu"), fan = Metrics.Value("fan"), pkg = Metrics.Value("pkg");
        var (state, dot) = State();

        _screenTemp.Text = Fig(cpu, "0", "°");
        _heroState.Text = state;
        _stateDot.SetResourceReference(Shape.FillProperty, dot);
        _chipGpu.Text = gpu is null && GpuState is { } asleep ? asleep : Fig(gpu, "0", " °C");
        _chipFan.Text = Fig(fan, "0", " rpm");
        _chipPkg.Text = Fig(pkg, "0.0", " W");
        _heroLimit.Text = Metrics.BindingLimitName is { } binding ? $"Held by {binding} · {Fig(Metrics.Value("limit"), "0", " %")}" : "";

        _fan.SetSpeed(fan);
        _rpm.Text = Fig(fan, "0");
        _fanWords.Text = FanWords();
        Show(_fanModes, (int)Settings.FanControlMode);

        _limitName.Text = Metrics.BindingLimitName ?? "No limit reported";
        _limitPercent.Text = Fig(Metrics.Value("limit"), "0", " %");
        FillTo(_limitFill, Gauge.Fraction(Metrics.Value("limit"), 100));

        _cpuTemp.Text = Fig(cpu, "0", "°");
        _cpuLine.Text = $"{Fig(Metrics.Value("cpuclk"), "0.00", " GHz")} · {Fig(Metrics.Value("cpuload"), "0", " %")} load";
        _gpuTitle.Text = GpuName;
        _gpuTemp.Text = Fig(gpu, "0", "°");
        _gpuLine.Text = gpu is null && GpuState is { } s ? s : $"{Fig(Metrics.Value("gpuclk"), "0", " MHz")} · {Fig(Metrics.Value("gpuload"), "0", " %")} load";

        // Against the SMU's sustained limit, the one top this reading has; none known, no ring.
        double? limit = Metrics.PackageLimitWatts;
        _power.Show(Gauge.Fraction(pkg, limit), pkg is { } w && limit is > 0 ? w / limit.Value * 100 : null, "0", "%",
                    (Brush)FindResource("AccentBrush"), (Brush)FindResource("TextPrimaryBrush"), null);
        _powerFigure.Text = Fig(pkg, "0.0", " W");
        _powerLine.Text = (limit is null ? "" : $"of {Fig(limit, "0", " W")} sustained · ") + $"graphics {Fig(Metrics.Value("gpuw"), "0.0", " W")}";

        double? mem = Metrics.Value("mem"), total = Metrics.MemoryTotalGB;
        _memFigure.Text = Fig(mem, "0.0", " GB");
        _memOf.Text = total is null ? "" : $"of {Fig(total, "0.0", " GB")}";
        FillTo(_memFill, Gauge.Fraction(mem, total));

        var (charge, charging, onAc) = Power();
        _battery.Show(charge, charging);
        _batteryFigure.Text = Fig(charge, "0", " %");
        _batteryLine.Text = string.Join(" · ", new[] { onAc switch { true => "On AC", false => "On battery", _ => null }, Health() }.Where(x => x is not null));
    }

    protected override void OnEnter(Func<int, int> delayOf)
    {
        // The lid opens as the hero arrives, and each curve draws in from the left with its tile.
        if (_lid.RenderTransform is ScaleTransform lift)
            lift.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(OmniHub.Core.Theming.MotionBudget.EnterMs + 160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
            });
        foreach (var (trace, tile) in _traces) Controls.Motion.Reveal(trace, delayOf(tile) + 80);
    }

    // ---------------------------------------------------------------- pieces

    private static TextBlock Figure(double size)
    {
        var t = new TextBlock { FontSize = size, FontFamily = new FontFamily(Light), FontWeight = FontWeights.Light, Text = OmniHub.Core.Telemetry.Metrics.Unavailable };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return t;
    }

    private static Border Chip(UIElement child)
    {
        var b = Round(new Border { Child = child, Padding = new Thickness(14, 6, 16, 6), BorderThickness = new Thickness(1) });
        b.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    private static Ellipse LiveDot()
    {
        var e = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        e.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Controls.Motion.Pulse(e);
        return e;
    }

    private static Border Fill(string brush)
    {
        var f = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(5), Width = 0 };
        f.SetResourceReference(Border.BackgroundProperty, brush);
        return f;
    }

    private static Border Track(Border fill)
    {
        var t = new Border { Height = 10, CornerRadius = new CornerRadius(5), Child = fill, ClipToBounds = true };
        t.SetResourceReference(Border.BackgroundProperty, "PanelHoverBrush");
        t.SizeChanged += (_, _) => FillTo(fill, fill.Tag as double?);
        return t;
    }

    // A bar's fill to a fraction of its track, eased; no fraction, no fill.
    private static void FillTo(Border fill, double? fraction)
    {
        fill.Tag = fraction;
        if (fill.Parent is not Border track) return;
        Controls.Motion.To(fill, WidthProperty, Math.Max(0, track.ActualWidth * (fraction ?? 0)), fill.IsVisible, 700);
    }

    private static T Docked<T>(T e, Dock dock) where T : UIElement { DockPanel.SetDock(e, dock); return e; }

    /// <summary>
    /// A component's colour as a glow from the tile's top corner, gone before the figure, so the words
    /// stand on the glass the contrast tests hold rather than on the tint.
    /// </summary>
    private void Tint(Border tile, string colourKey)
    {
        if (TryFindResource(colourKey) is not Color c) return;
        var content = tile.Child;
        tile.Child = null;
        var glow = new Border
        {
            Margin = new Thickness(-tile.Padding.Left, -tile.Padding.Top, -tile.Padding.Right, -tile.Padding.Bottom),
            Background = new LinearGradientBrush(Color.FromArgb(0x30, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), new Point(0, 0), new Point(0.55, 0.6)),
            IsHitTestVisible = false,
        };
        glow.SetResourceReference(Border.CornerRadiusProperty, "RadiusMd");
        tile.Child = new Grid { Children = { glow, content } };
    }
}
