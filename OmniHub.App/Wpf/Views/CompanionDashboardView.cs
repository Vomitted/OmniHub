// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
using RadioButton = System.Windows.Controls.RadioButton;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The Companion look's Dashboard, after "Tesla Concept" by yuhang (TECHNICAL-v5-ui.md, section 15): the
/// laptop as the hero with its charge and three round preset buttons, as the reference shows its car with
/// its range and summon, unlock and climate; cooling, graphics and power as clean black cards; and the
/// last five minutes drawn like a route on a dark map, where the reference has its location.
/// </summary>
public sealed class CompanionDashboardView : LookDashboard
{
    // iOS's green for the charge: a graphic, with black words on it at over 10 : 1.
    private static readonly Brush ChargeGreen = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));

    private readonly Controls.LaptopArt _laptop = new(Controls.LaptopArt.Finish.Silver, 46);
    private readonly TextBlock _state = Words(size: 12.5), _charge = Big(), _health = Words(size: 12.5),
        _chargeWords = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
        _presetNote = Words(size: 12.5),
        _die = Big(), _fans = Words(size: 12.5), _ask = Words(size: 12.5),
        _range = Words(size: 12.5),
        _gpuName = Caps(""), _gpu = Big(), _gpuNote = Words(size: 12.5), _gpuLoad = Words(size: 12.5),
        _pkg = Big(), _pkgNote = Words(size: 12.5), _limit = Words(size: 12.5);
    private readonly Border _chargeFill = new() { Background = ChargeGreen, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(7) };
    private readonly (Border Track, Action<double?> Set) _gpuBar = Meter("AccentBrush"), _limitBar = Meter("WarnBrush");
    private readonly RouteMap _route = new();
    private readonly Controls.Trace _dieTrace, _gpuTrace, _pkgTrace;
    private readonly RadioButton[] _presets, _modes;
    private double? _chargeFraction;

    public CompanionDashboardView(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics, Action<FanControlMode> setFanMode)
        : base(ctx, service, settings, metrics, setFanMode)
    {
        // Each card's last five minutes against the top its reading has: the die and the card against
        // their hot points, the package against the SMU's sustained limit.
        double? HotOf(string key) => OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find(key)!, null);
        _dieTrace = new Controls.Trace(metrics, () => HotOf("cpu"), ("cpu", "AccentBrush")) { Margin = new Thickness(0, 12, 0, 8) };
        _gpuTrace = new Controls.Trace(metrics, () => HotOf("gpu"), ("gpu", "AccentBrush")) { Margin = new Thickness(0, 12, 0, 8) };
        _pkgTrace = new Controls.Trace(metrics, () => metrics.PackageLimitWatts, ("pkg", "WarnBrush")) { Margin = new Thickness(0, 12, 0, 8) };
        var grid = Lattice(new[] { 1.1, 1, 1 }, new double[] { 1, 1 }, 18, new double[] { 300, 280 });
        Tile(grid, Hero(out _presets), 0, 0, rowSpan: 2, padding: new Thickness(24, 22, 24, 22));
        Tile(grid, Cooling(out _modes), 0, 1);
        Tile(grid, Route(), 0, 2, padding: new Thickness(0));
        Tile(grid, Graphics(), 1, 1);
        Tile(grid, PowerCard(), 1, 2);
        Host(grid);
    }

    // ---------------------------------------------------------------- the tiles

    private UIElement Hero(out RadioButton[] presets)
    {
        var p = new DockPanel();
        var name = Words(HardwareNames.ShortModel(Ctx.Model.Manufacturer, Ctx.Model.Product) ?? "This machine", 20, "TextPrimaryBrush", FontWeights.SemiBold);
        p.Children.Add(Docked(new StackPanel { Children = { name, _state } }, Dock.Top));

        // Three round actions with their names under them, as the reference's summon, unlock and climate.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        var icons = new[]
        {
            "M5 19 C 5 10, 11 4, 20 4 C 20 13, 14 19, 5 19 Z M5 19 L13 11",
            "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M12 3 V21",
            "M13 2 L4 14 H11 L10 22 L19 10 H12 Z",
        };
        presets = new RadioButton[PresetActions.All.Length];
        for (int i = 0; i < PresetActions.All.Length; i++)
        {
            int index = i;
            var b = Pill(Glyph(icons[i]), "presets", i == ActivePreset, () => ApplyPreset(index), round: 62);
            System.Windows.Automation.AutomationProperties.SetName(b, PresetActions.All[i].Name);
            presets[i] = b;
            var caption = Caps(PresetActions.All[i].Name.ToUpperInvariant());
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            caption.Margin = new Thickness(0, 9, 0, 0);
            actions.Children.Add(new StackPanel { Margin = new Thickness(20, 0, 20, 0), Children = { b, caption } });
        }
        _presetNote.HorizontalAlignment = HorizontalAlignment.Center;
        _presetNote.TextAlignment = TextAlignment.Center;
        _presetNote.TextWrapping = TextWrapping.Wrap;
        _presetNote.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(Docked(new StackPanel { Children = { actions, _presetNote } }, Dock.Bottom));

        // The charge, as the reference's range: the figure, and a bar with the state written on it.
        var bar = new Grid { Height = 26, ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0) };
        var track = new Border { CornerRadius = new CornerRadius(7) };
        track.SetResourceReference(Border.BackgroundProperty, "PanelHoverBrush");
        bar.Children.Add(track);
        bar.Children.Add(_chargeFill);
        bar.Children.Add(_chargeWords);
        bar.SizeChanged += (_, _) => FillCharge();
        _health.Margin = new Thickness(12, 0, 0, 6);
        _health.VerticalAlignment = VerticalAlignment.Bottom;
        var figure = new StackPanel { Orientation = Orientation.Horizontal, Children = { _charge, _health } };
        p.Children.Add(Docked(new StackPanel { Children = { figure, bar } }, Dock.Bottom));

        _laptop.Margin = new Thickness(0, 10, 0, 10);
        p.Children.Add(_laptop);
        return p;
    }

    private UIElement Cooling(out RadioButton[] modes)
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Head("Cooling", Caps("DIE · HOT AT 90 °C")), Dock.Top));
        var steps = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 14, -8, 0) };
        modes = new[] { (FanControlMode.Auto, "Auto"), (FanControlMode.BiosDefault, "BIOS"), (FanControlMode.Max, "Max") }
            .Select(m => Pill(m.Item2, "modes", Settings.FanControlMode == m.Item1, () => SetFanMode(m.Item1))).ToArray();
        foreach (var m in modes) { m.Height = 40; m.Margin = new Thickness(0, 0, 8, 0); steps.Children.Add(m); }
        p.Children.Add(Docked(steps, Dock.Bottom));
        _ask.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(Docked(_ask, Dock.Bottom));
        _fans.Margin = new Thickness(12, 0, 0, 8);
        _fans.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Children = { _die, _fans } }, Dock.Bottom));
        p.Children.Add(_dieTrace);
        return p;
    }

    private UIElement Route()
    {
        var g = new Grid();
        g.Children.Add(_route);
        g.Children.Add(new StackPanel { Margin = new Thickness(22, 20, 22, 0), Children = { Words("Last 5 minutes", 17, "TextPrimaryBrush", FontWeights.SemiBold), _range } });
        return g;
    }

    private UIElement Graphics()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Head("Graphics", _gpuName), Dock.Top));
        _gpuLoad.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(Docked(_gpuLoad, Dock.Bottom));
        _gpuBar.Track.Margin = new Thickness(0, 16, 0, 0);
        p.Children.Add(Docked(_gpuBar.Track, Dock.Bottom));
        _gpuNote.Margin = new Thickness(12, 0, 0, 8);
        _gpuNote.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Children = { _gpu, _gpuNote } }, Dock.Bottom));
        p.Children.Add(_gpuTrace);
        return p;
    }

    private UIElement PowerCard()
    {
        var p = new DockPanel();
        p.Children.Add(Docked(Head("Power", Caps("PACKAGE")), Dock.Top));
        _limit.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(Docked(_limit, Dock.Bottom));
        _limitBar.Track.Margin = new Thickness(0, 16, 0, 0);
        p.Children.Add(Docked(_limitBar.Track, Dock.Bottom));
        _pkgNote.Margin = new Thickness(12, 0, 0, 8);
        _pkgNote.VerticalAlignment = VerticalAlignment.Bottom;
        p.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Children = { _pkg, _pkgNote } }, Dock.Bottom));
        p.Children.Add(_pkgTrace);
        return p;
    }

    // ---------------------------------------------------------------- the readings

    protected override void Update()
    {
        var (charge, charging, onAc) = Power();
        _state.Text = string.Join(" · ", new[] { onAc switch { true => "On AC", false => "On battery", _ => null }, charging ? "charging" : null, FanWords() }.Where(x => x is not null));
        _charge.Text = Fig(charge, "0", "%");
        _health.Text = Health() is { } h ? $"battery · {h}" : "battery";
        _chargeWords.Text = onAc == true ? (charging ? "⚡ charging" : "⚡ plugged in") : "on battery";
        _chargeFraction = charge / 100;
        FillCharge();
        Show(_presets, ActivePreset);
        _presetNote.Text = PresetActions.All[ActivePreset].Note;

        double? cpu = Metrics.Value("cpu"), hot = OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find("cpu")!, null);
        _die.Text = Fig(cpu, "0.0", "°C");
        _laptop.Reading.Text = Fig(cpu, "0", "°");
        _fans.Text = $"fans {Fig(Metrics.Value("fan"), "0", " rpm")}";
        _ask.Text = FanWords();
        Show(_modes, (int)Settings.FanControlMode);

        var recent = Metrics.Recent("cpu");
        _range.Text = recent.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"the die from {recent.Min(p => p.Value):0} to {recent.Max(p => p.Value):0} °C") : "no readings yet";
        _route.Show(recent, hot);

        double? gpu = Metrics.Value("gpu"), gpuLoad = Metrics.Value("gpuload");
        _gpuName.Text = GpuName.ToUpperInvariant();
        _gpu.Text = gpu is null && GpuState is not null ? "Asleep" : Fig(gpu, "0", "°C");
        _gpuNote.Text = $"{Fig(Metrics.Value("gpuw"), "0.0", " W")} · {Fig(Metrics.Value("gpuclk"), "0", " MHz")}";
        _gpuBar.Set(Gauge.Fraction(gpuLoad, 100));
        _gpuLoad.Text = $"{Fig(gpuLoad, "0", " %")} load";

        _pkg.Text = Fig(Metrics.Value("pkg"), "0.0", " W");
        _pkgNote.Text = $"{Fig(Metrics.Value("cpuclk"), "0.00", " GHz")} · {Fig(Metrics.Value("cpuload"), "0", " %")} load";
        _limitBar.Set(Gauge.Fraction(Metrics.Value("limit"), 100));
        _limit.Text = Metrics.BindingLimitName is { } binding ? $"Held by {binding.ToLowerInvariant()} · {Fig(Metrics.Value("limit"), "0", " %")} of it" : "No limit reported";
    }

    protected override void OnEnter(Func<int, int> delayOf)
    {
        _laptop.Open();
        Controls.Motion.Reveal(_route, delayOf(2) + 80, 600);
        foreach (var (trace, tile) in new[] { (_dieTrace, 1), (_gpuTrace, 3), (_pkgTrace, 4) }) Controls.Motion.Reveal(trace, delayOf(tile) + 80);
    }

    private void FillCharge()
    {
        if (_chargeFill.Parent is Grid bar)
            Controls.Motion.To(_chargeFill, WidthProperty, bar.ActualWidth * Math.Clamp(_chargeFraction ?? 0, 0, 1), _chargeFill.IsVisible, 700);
    }

    // ---------------------------------------------------------------- pieces

    private static UIElement Head(string title, TextBlock right)
    {
        right.VerticalAlignment = VerticalAlignment.Center;
        return new DockPanel { Children = { Docked(right, Dock.Right), Words(title, 17, "TextPrimaryBrush", FontWeights.SemiBold) } };
    }

    private static TextBlock Caps(string text) => Words(text, 11, "TextMutedBrush", FontWeights.SemiBold);

    private static TextBlock Big()
    {
        var t = new TextBlock { FontSize = 44, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return t;
    }

    private static T Docked<T>(T e, Dock dock) where T : UIElement { DockPanel.SetDock(e, dock); return e; }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// Five minutes of the die drawn like a road on a dark map: a grid of streets, the route from zero to the
    /// hot point across the window's width, the newest reading a dot at its end. One drawing, redrawn on the tick.
    /// </summary>
    private sealed class RouteMap : FrameworkElement
    {
        private IReadOnlyList<TimePoint> _points = Array.Empty<TimePoint>();
        private double? _top;

        public RouteMap() => IsHitTestVisible = false;

        public void Show(IReadOnlyList<TimePoint> points, double? top)
        {
            _points = points;
            _top = top;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 40 || h < 40) return;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 22, 22));

            var street = new Pen((Brush)FindResource("PanelAltBrush"), 1);
            for (double x = 17; x < w; x += 34) dc.DrawLine(street, new Point(x, 0), new Point(x, h));
            for (double y = 17; y < h; y += 34) dc.DrawLine(street, new Point(0, y), new Point(w, y));

            if (_top is > 0 && _points.Count > 1)
            {
                var since = DateTime.UtcNow - Controls.Curves.Window;
                double left = 22, right = w - 22, bottom = h - 22, topY = 78;
                double X(DateTime t) => left + (t - since).TotalSeconds / Controls.Curves.Window.TotalSeconds * (right - left);
                double Y(double v) => bottom - Math.Clamp(v / _top.Value, 0, 1) * (bottom - topY);

                var g = new StreamGeometry();
                Point last = default;
                bool any = false;
                using (var ctx = g.Open())
                {
                    foreach (var p in _points)
                    {
                        if (p.AtUtc < since) continue;
                        last = new Point(X(p.AtUtc), Y(p.Value));
                        if (!any) { ctx.BeginFigure(last, false, false); any = true; }
                        else ctx.LineTo(last, true, true);
                    }
                }
                g.Freeze();
                var accent = (Brush)FindResource("AccentBrush");
                if (any)
                {
                    dc.DrawGeometry(null, new Pen(accent, 4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
                    dc.DrawEllipse(accent, new Pen((Brush)FindResource("TextPrimaryBrush"), 3), last, 8, 8);
                }
            }
            dc.Pop();
        }
    }
}
