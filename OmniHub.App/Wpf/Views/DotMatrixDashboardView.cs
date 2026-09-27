// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
using OmniHub.App.Wpf.Controls;
using Tone = OmniHub.App.Wpf.Controls.DotField.Tone;

using RadioButton = System.Windows.Controls.RadioButton;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The Dot matrix look's Dashboard, after "New Nothing OS 4.0 Concept" by Slava Kornilov for Geex Arts
/// (TECHNICAL-v5-ui.md, section 13): greys and one red, the readings drawn in dots, history as lit
/// columns, round buttons. Two tiles are light, as the reference's are, and set their words in fixed
/// near-blacks on a fixed near-white -- #111 and #555 on #F2F2F2, 17 : 1 and 6.7 : 1 -- whatever the
/// palette, since the palette's text is made for dark surfaces.
///
/// Every scale is one a reading already has: a temperature against its own hot point, a load against a
/// hundred, memory against what is installed, power against the SMU's sustained limit. No scale, no
/// dots -- the figure alone.
/// </summary>
public sealed class DotMatrixDashboardView : LookDashboard
{
    private static readonly Brush Ink = Frozen(0x11), InkSoft = Frozen(0x55), Paper = Frozen(0xF2), PaperTrack = Frozen(0xCF);

    private readonly DotText _temp = new() { Pitch = 16 }, _gpuTemp = new() { Pitch = 6 }, _range = new() { Pitch = 3.4 },
        _limit = new() { Pitch = 8 }, _rpm = new() { Pitch = 5 }, _power = new() { Pitch = 7 }, _gpu = new() { Pitch = 7 },
        _mem = new() { Pitch = 6 }, _charge = new() { Pitch = 8 }, _clock = new() { Pitch = 7 };
    private readonly TextBlock _hot = Words(size: 11.5), _state = Words(size: 13, weight: FontWeights.SemiBold), _limitName = Words(size: 12.5),
        _fanLine = Words(size: 11.5), _powerLine = Words(size: 12), _gpuLine = Words(size: 11.5), _memOf = Words(size: 11.5),
        _batteryLine = Words(size: 12), _cpuLine = Words(size: 11.5), _presetNote = Words(size: 11.5), _gpuTempLine = Words(size: 12);
    private readonly Ellipse _stateDot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    private readonly DotField _tempRow = new() { Columns = 30, Pitch = 10 }, _chart = new() { Columns = 56, Rows = 14, Pitch = 10 },
        _gpuRow = new() { Columns = 20, Pitch = 11 }, _memGrid = new() { Columns = 16, Rows = 6, Pitch = 14 }, _cpuRow = new() { Columns = 20, Pitch = 11 };
    private readonly FanGlyph _fan = new() { ColourKey = "TextPrimaryBrush", Width = 62, Height = 62 };
    private readonly Border _powerFill = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(27) };
    private readonly Border _chargeFill = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(7), Background = Ink };
    private readonly RadioButton[] _fanModes, _presets;

    public DotMatrixDashboardView(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics, Action<FanControlMode> setFanMode)
        : base(ctx, service, settings, metrics, setFanMode)
    {
        var grid = Lattice(new[] { 1.3, 1, 1, 1 }, new double[] { 1, 1, 1 }, 16, new double[] { 190, 190, 180 });

        Tile(grid, TemperatureTile(), 0, 0, rowSpan: 2);
        Tile(grid, ChartTile(), 0, 1, columnSpan: 2);
        Light(Tile(grid, StatusTile(), 0, 3));
        Tile(grid, FansTile(out _fanModes), 1, 1);
        Tile(grid, PowerTile(), 1, 2);
        Tile(grid, GraphicsTile(), 1, 3);
        Tile(grid, PresetTile(out _presets), 2, 0);
        Tile(grid, MemoryTile(), 2, 1);
        Light(Tile(grid, BatteryTile(), 2, 2));
        Tile(grid, ProcessorTile(), 2, 3);

        Host(grid);
    }

    // ---------------------------------------------------------------- the tiles

    private UIElement TemperatureTile()
    {
        var p = new DockPanel();
        var live = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Signal() };
        Motion.Pulse(live);
        p.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Children = { live, Caps("Die temperature · live") } }, Dock.Top));

        var foot = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        foot.Children.Add(Docked(_hot, Dock.Right));
        foot.Children.Add(Caps("0 °C"));
        p.Children.Add(Docked(foot, Dock.Bottom));
        p.Children.Add(Docked(Fitted(_tempRow, up: true), Dock.Bottom));

        _gpuTempLine.Margin = new Thickness(10, 0, 0, 0);
        _gpuTempLine.VerticalAlignment = VerticalAlignment.Bottom;
        _gpuTemp.SetResourceReference(DotText.FillProperty, "TextMutedBrush");
        p.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Fitted(_temp),
                new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { _gpuTemp, _gpuTempLine } },
            },
        });
        return p;
    }

    private UIElement ChartTile()
    {
        var p = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(Docked(_range, Dock.Right));
        head.Children.Add(Caps("Processor · five minutes"));
        p.Children.Add(Docked(head, Dock.Top));
        p.Children.Add(Fitted(_chart, up: true));
        return p;
    }

    private UIElement StatusTile()
    {
        var p = new DockPanel();
        _state.Foreground = Ink;
        p.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Children = { _stateDot, _state } }, Dock.Top));
        _limitName.Foreground = InkSoft;
        p.Children.Add(Docked(_limitName, Dock.Bottom));
        _limit.Fill = Ink;
        _limit.VerticalAlignment = VerticalAlignment.Bottom;
        _limit.Margin = new Thickness(0, 0, 0, 8);
        p.Children.Add(Fitted(_limit));
        return p;
    }

    private UIElement FansTile(out RadioButton[] modes)
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Caps("Fans"), Dock.Top));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        modes = new[] { (FanControlMode.Auto, "Auto"), (FanControlMode.BiosDefault, "BIOS"), (FanControlMode.Max, "Max") }
            .Select(m => Pill(m.Item2, "fans", Settings.FanControlMode == m.Item1, () => SetFanMode(m.Item1), round: 52)).ToArray();
        foreach (var m in modes) { m.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(m); }
        p.Children.Add(Docked(Fitted(buttons), Dock.Bottom));
        _fanLine.Margin = new Thickness(0, 6, 0, 0);
        _fanLine.TextWrapping = TextWrapping.Wrap;
        _rpm.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(new DockPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Docked(_fan, Dock.Left), new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { Fitted(_rpm), _fanLine } } },
        });
        return p;
    }

    private UIElement PowerTile()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Caps("Processor power"), Dock.Top));

        // The capsule: the package's watts against the SMU's sustained limit.
        var capsule = new Grid { Height = 54, ClipToBounds = true };
        var track = new Border { CornerRadius = new CornerRadius(27) };
        track.SetResourceReference(Border.BackgroundProperty, "PanelHoverBrush");
        _powerFill.SetResourceReference(Border.BackgroundProperty, "TextPrimaryBrush");
        capsule.Children.Add(track);
        capsule.Children.Add(_powerFill);
        capsule.SizeChanged += (_, _) => FillTo(_powerFill, capsule.ActualWidth, _powerFill.Tag as double?);
        _powerLine.Margin = new Thickness(4, 8, 0, 0);
        p.Children.Add(Docked(new StackPanel { Children = { capsule, _powerLine } }, Dock.Bottom));

        _power.VerticalAlignment = VerticalAlignment.Center;
        _power.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(Fitted(_power));
        return p;
    }

    private UIElement GraphicsTile()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Caps("Graphics"), Dock.Top));
        p.Children.Add(Docked(Fitted(_gpuRow, up: true), Dock.Bottom));
        _gpuLine.Margin = new Thickness(0, 0, 0, 10);
        p.Children.Add(Docked(_gpuLine, Dock.Bottom));
        _gpu.VerticalAlignment = VerticalAlignment.Center;
        _gpu.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(_gpu);
        return p;
    }

    private UIElement PresetTile(out RadioButton[] presets)
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Caps("Preset"), Dock.Top));
        _presetNote.TextWrapping = TextWrapping.Wrap;
        _presetNote.Margin = new Thickness(0, 12, 0, 0);
        p.Children.Add(Docked(_presetNote, Dock.Bottom));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        presets = PresetActions.All.Select((preset, i) => Pill(preset.Name, "presets", i == ActivePreset, () => ApplyPreset(i), round: 84)).ToArray();
        foreach (var b in presets) { b.Margin = new Thickness(0, 0, 10, 0); b.FontSize = 11.5; row.Children.Add(b); }
        var fitted = Fitted(row);
        fitted.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(fitted);
        return p;
    }

    private UIElement MemoryTile()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(new DockPanel { Children = { Docked(_memOf, Dock.Right), Caps("Memory") } }, Dock.Top));
        _mem.HorizontalAlignment = HorizontalAlignment.Left;
        _mem.Margin = new Thickness(0, 10, 0, 12);
        p.Children.Add(Docked(_mem, Dock.Top));
        p.Children.Add(Fitted(_memGrid, up: true));
        return p;
    }

    private UIElement BatteryTile()
    {
        var p = new DockPanel();
        _batteryLine.Foreground = InkSoft;
        p.Children.Add(Docked(_batteryLine, Dock.Top));
        var bar = new Border { Height = 14, CornerRadius = new CornerRadius(7), Background = PaperTrack, Child = _chargeFill, Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
        bar.SizeChanged += (_, _) => FillTo(_chargeFill, bar.ActualWidth, _chargeFill.Tag as double?);
        p.Children.Add(Docked(bar, Dock.Bottom));
        _charge.Fill = Ink;
        _charge.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(Fitted(_charge));
        return p;
    }

    private UIElement ProcessorTile()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Caps("Processor"), Dock.Top));
        p.Children.Add(Docked(Fitted(_cpuRow, up: true), Dock.Bottom));
        _cpuLine.Margin = new Thickness(0, 0, 0, 10);
        p.Children.Add(Docked(_cpuLine, Dock.Bottom));
        var ghz = Words("GHz", 14);
        ghz.Margin = new Thickness(8, 0, 0, 2);
        ghz.VerticalAlignment = VerticalAlignment.Bottom;
        var figure = Fitted(new StackPanel { Orientation = Orientation.Horizontal, Children = { _clock, ghz } });
        figure.VerticalAlignment = VerticalAlignment.Center;
        p.Children.Add(figure);
        return p;
    }

    // ---------------------------------------------------------------- the readings

    protected override void Update()
    {
        var cpuMetric = OmniHub.Core.Telemetry.Metrics.Find("cpu")!;
        double? cpu = Metrics.Value("cpu"), gpu = Metrics.Value("gpu"), hot = OmniHub.Core.Telemetry.Metrics.FullScale(cpuMetric, null);

        // The die, large, and its bar of dots against its hot point: white to the reading, the last lit dot red.
        _temp.Text = Dots(cpu, "0", "°");
        _gpuTemp.Text = Dots(gpu, "0", "°");
        _gpuTempLine.Text = gpu is null && GpuState is { } asleep ? $"graphics · {asleep}" : "graphics";
        _hot.Text = hot is null ? "" : $"hot at {hot:0} °C";
        int litTemp = Lit(cpu, hot, _tempRow.Columns);
        _tempRow.Cell = (c, _) => c < litTemp ? (c == litTemp - 1 ? Tone.Signal : Tone.Lit) : Tone.Off;
        _tempRow.Refresh();

        // Five minutes of the die, one column per slice, from zero to its hot point; the newest column red.
        var since = DateTime.UtcNow - Curves.Window;
        var inWindow = Metrics.Recent("cpu").Where(p => p.AtUtc >= since).ToList();
        var columns = new double?[_chart.Columns];
        foreach (var point in inWindow)
        {
            int c = Math.Clamp((int)((point.AtUtc - since).TotalSeconds / Curves.Window.TotalSeconds * _chart.Columns), 0, _chart.Columns - 1);
            columns[c] = Math.Max(columns[c] ?? double.MinValue, point.Value);
        }
        _range.Text = inWindow.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{inWindow.Min(p => p.Value):0}-{inWindow.Max(p => p.Value):0}°");
        int newest = Array.FindLastIndex(columns, v => v is not null);
        _chart.Cell = (c, r) =>
        {
            int lit = Lit(columns[c], hot, _chart.Rows);
            if (r >= lit) return Tone.Off;
            return c == newest ? Tone.Signal : r == lit - 1 ? Tone.Lit : Tone.Dim;
        };
        _chart.Refresh();

        // The light tile: how the machine is doing, and how close the binding limit is.
        var (state, dot) = State();
        _state.Text = state;
        _stateDot.Fill = dot == "GoodBrush" ? Ink : Signal();
        _limit.Text = Dots(Metrics.Value("limit"), "0", "%");
        _limitName.Text = Metrics.BindingLimitName is { } binding ? $"held by {binding}" : "no limit reported";

        double? fan = Metrics.Value("fan");
        _fan.SetSpeed(fan);
        _rpm.Text = Dots(fan, "0", "");
        _fanLine.Text = $"rpm · {FanWords()}";
        Show(_fanModes, (int)Settings.FanControlMode);

        double? pkg = Metrics.Value("pkg"), limit = Metrics.PackageLimitWatts;
        _power.Text = Dots(pkg, "0.0", "W");
        _powerLine.Text = (limit is null ? "" : $"of {Fig(limit, "0", " W")} sustained · ") + $"graphics {Fig(Metrics.Value("gpuw"), "0.0", " W")}";
        if (_powerFill.Parent is Grid capsule) FillTo(_powerFill, capsule.ActualWidth, Gauge.Fraction(pkg, limit));

        double? gpuLoad = Metrics.Value("gpuload");
        _gpu.Text = Dots(gpu, "0", "°");
        _gpuLine.Text = gpu is null && GpuState is { } s ? $"{GpuName} · {s}" : $"{GpuName} · {Fig(Metrics.Value("gpuclk"), "0", " MHz")} · {Fig(gpuLoad, "0", " %")} load";
        int litGpu = Lit(gpuLoad, 100, _gpuRow.Columns);
        _gpuRow.Cell = (c, _) => c < litGpu ? Tone.Lit : Tone.Off;
        _gpuRow.Refresh();

        _presetNote.Text = PresetActions.All[ActivePreset].Note;
        Show(_presets, ActivePreset);

        double? mem = Metrics.Value("mem"), total = Metrics.MemoryTotalGB;
        _mem.Text = Dots(mem, "0.0", "");
        _memOf.Text = total is null ? "GB" : $"of {Fig(total, "0.0", " GB")}";
        int litMem = Lit(mem, total, _memGrid.Columns * _memGrid.Rows);
        _memGrid.Cell = (c, r) => (_memGrid.Rows - 1 - r) * _memGrid.Columns + c < litMem ? Tone.Lit : Tone.Off;
        _memGrid.Refresh();

        var (charge, charging, onAc) = Power();
        _charge.Text = Dots(charge, "0", "%");
        _batteryLine.Text = string.Join(" · ", new[] { "Battery", onAc switch { true => "on AC", false => "on battery", _ => null }, charging ? "charging" : null, Health() }.Where(x => x is not null));
        if (_chargeFill.Parent is Border bar) FillTo(_chargeFill, bar.ActualWidth, charge / 100);

        double? cpuLoad = Metrics.Value("cpuload");
        _clock.Text = Dots(Metrics.Value("cpuclk"), "0.00", "");
        _cpuLine.Text = $"{CpuName} · {Fig(cpuLoad, "0", " %")} load";
        int litCpu = Lit(cpuLoad, 100, _cpuRow.Columns);
        _cpuRow.Cell = (c, _) => c < litCpu ? Tone.Signal : Tone.Off;
        _cpuRow.Refresh();
    }

    protected override void OnEnter(Func<int, int> delayOf)
    {
        // Each field lights column by column as its tile arrives.
        foreach (var (field, tile) in new[] { (_tempRow, 0), (_chart, 1), (_gpuRow, 5), (_memGrid, 7), (_cpuRow, 9) })
            field.Enter(delayOf(tile) + 60);
    }

    // ---------------------------------------------------------------- pieces

    /// <summary>A figure for the dot font: digits and its few marks, "-" standing for no reading.</summary>
    private static string Dots(double? value, string format, string unit) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) + unit : "-";

    /// <summary>How many of <paramref name="count"/> dots a reading lights against its full scale; none without one.</summary>
    private static int Lit(double? value, double? full, int count) =>
        Gauge.Fraction(value, full) is { } f ? (int)Math.Round(f * count) : 0;

    // A bar's fill to a fraction of its width, eased; no fraction, no fill.
    private static void FillTo(Border fill, double width, double? fraction)
    {
        fill.Tag = fraction;
        Motion.To(fill, WidthProperty, Math.Max(0, width * Math.Clamp(fraction ?? 0, 0, 1)), fill.IsVisible, 700);
    }

    /// <summary>The small label each tile opens with.</summary>
    private static TextBlock Caps(string text)
    {
        var t = Words(text, 11.5, "TextMutedBrush");
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    /// <summary>
    /// Drawn at a size and scaled to the room its tile has: a figure only ever down, a field of dots up
    /// as well, so a wide tile's chart fills it instead of sitting in a corner.
    /// </summary>
    private static Viewbox Fitted(FrameworkElement e, bool up = false) => new()
    {
        Child = e, Stretch = Stretch.Uniform, StretchDirection = up ? StretchDirection.Both : StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = e.VerticalAlignment,
    };

    private static T Docked<T>(T e, Dock dock) where T : UIElement { DockPanel.SetDock(e, dock); return e; }

    private static Brush Signal() => new SolidColorBrush((Color)ColorConverter.ConvertFromString(OmniHub.Core.Theming.LookSurfaces.Signal));

    private static Brush Frozen(byte grey)
    {
        var b = new SolidColorBrush(Color.FromRgb(grey, grey, grey));
        b.Freeze();
        return b;
    }

    /// <summary>A light tile: the reference's near-white, which the look draws the same under every palette.</summary>
    private static void Light(Border tile)
    {
        tile.Background = Paper;
        tile.BorderBrush = Paper;
    }
}
