// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
using RadioButton = System.Windows.Controls.RadioButton;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Pen = System.Windows.Media.Pen;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The Neumorphic look's Dashboard, after "Tesla Smart App" by Gavrisov Dmitri (TECHNICAL-v5-ui.md,
/// section 15): the reference's three screens side by side -- the machine as the hero with its die
/// temperature large; its status and information; and the climate screen as cooling, a blue dial against
/// the hot point over the fan speed and three round modes. Surfaces are raised by a gradient fill and a
/// gradient edge, never a blurred shadow, which WPF would draw in software.
/// </summary>
public sealed class NeumorphicDashboardView : LookDashboard
{
    private readonly Controls.LaptopArt _laptop = new(Controls.LaptopArt.Finish.Graphite, 48, caption: null);
    private readonly TextBlock _big = Figure(96), _name = Words(size: 26, brush: "TextPrimaryBrush", weight: FontWeights.SemiBold),
        _fanWords = Words(size: 13), _holdNote = Words(size: 12.5),
        _battery = Value(), _package = Value(), _graphics = Value(),
        _clock = Value(15), _load = Words(size: 12), _limit = Value(15), _limitOf = Words(size: 12), _mem = Value(15), _memOf = Words(size: 12),
        _presetNote = Words(size: 12.5), _curve = Words(size: 12.5, brush: "TextPrimaryBrush", weight: FontWeights.SemiBold), _curveNote = Words(size: 12),
        _speedTop = Words(size: 11.5), _speedNote = Words(size: 11.5);
    private readonly CoolingDial _dial = new() { Width = 300, Height = 300 };
    private readonly Controls.Trace _trace;
    private readonly (Border Track, Action<double?> Set) _speed = Meter("AccentBrush", 8);
    private readonly RadioButton _hold;
    private readonly RadioButton[] _presets, _modes;

    public NeumorphicDashboardView(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics, Action<FanControlMode> setFanMode)
        : base(ctx, service, settings, metrics, setFanMode)
    {
        _trace = new Controls.Trace(metrics, () => OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find("cpu")!, null), ("cpu", "AccentBrush"));
        var grid = Lattice(new[] { 1, 1.15, 1 }, new double[] { 1 }, 30, new double[] { 600 });
        var pad = new Thickness(28, 26, 28, 26);
        Raise(Tile(grid, Machine(out _hold), 0, 0, padding: pad));
        Raise(Tile(grid, Status(out _presets), 0, 1, padding: pad));
        Raise(Tile(grid, Cooling(out _modes), 0, 2, padding: pad));
        Host(grid);
    }

    // ---------------------------------------------------------------- the three screens

    private UIElement Machine(out RadioButton hold)
    {
        var p = new DockPanel();
        _name.Text = HardwareNames.ShortModel(Ctx.Model.Manufacturer, Ctx.Model.Product) ?? "This machine";
        _name.HorizontalAlignment = HorizontalAlignment.Center;
        p.Children.Add(Docked(new StackPanel { Children = { Centred(Words(Ctx.Model.Manufacturer ?? "", 12.5)), _name } }, Dock.Top));

        // The hold: one round button, as the reference's one round button opens the car. It toggles on a
        // click, since a chosen radio button raises nothing when pressed again.
        hold = Pill(Glyph("M12 2 V22 M2 12 H22 M5 5 L19 19 M19 5 L5 19"), "hold", Settings.FanControlMode == FanControlMode.Max,
                    () => { }, round: 64, accent: true);
        hold.Click += (_, _) => { if (IsLoaded) SetFanMode(Settings.FanControlMode == FanControlMode.Max ? FanControlMode.Auto : FanControlMode.Max); };
        System.Windows.Automation.AutomationProperties.SetName(hold, "Hold the fans at maximum");
        hold.HorizontalAlignment = HorizontalAlignment.Center;
        var foot = new StackPanel { Children = { Centred(_fanWords), hold, Centred(_holdNote) } };
        _fanWords.Margin = new Thickness(0, 0, 0, 14);
        _holdNote.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(Docked(foot, Dock.Bottom));

        var figure = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        figure.Children.Add(_big);
        var unit = Words("°C", 22);
        unit.Margin = new Thickness(4, 14, 0, 0);
        figure.Children.Add(unit);
        _laptop.Margin = new Thickness(0, 16, 0, 16);
        p.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { figure, Centred(Words("processor die · hot at 90 °C", 12.5)), _laptop },
        });
        return p;
    }

    private UIElement Status(out RadioButton[] presets)
    {
        var s = new StackPanel();
        var title = Words(HardwareNames.ShortModel(Ctx.Model.Manufacturer, Ctx.Model.Product) ?? "This machine", 15, "TextPrimaryBrush", FontWeights.SemiBold);
        s.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { Centred(Words("OmniHub", 12)), Centred(title) } });

        s.Children.Add(Heading("Status", 26));
        s.Children.Add(Row(Inset("Battery", _battery), Inset("Package", _package), Inset("Graphics", _graphics)));

        s.Children.Add(Heading("Information", 22));
        s.Children.Add(Row(Card("Processor", _clock, _load), Card("Limit", _limit, _limitOf), Card("Memory", _mem, _memOf)));

        s.Children.Add(Heading("Presets", 22));
        var row = new UniformGrid3();
        presets = PresetActions.All.Select((preset, i) => Pill(preset.Name, "presets", i == ActivePreset, () => ApplyPreset(i), accent: true)).ToArray();
        foreach (var b in presets) { b.Height = 44; b.Margin = new Thickness(0, 0, 10, 0); row.Children.Add(b); }
        s.Children.Add(row);
        _presetNote.TextWrapping = TextWrapping.Wrap;
        _presetNote.Margin = new Thickness(0, 10, 0, 0);
        s.Children.Add(_presetNote);

        // The last five minutes of the die against its hot point, in a raised card of its own.
        s.Children.Add(Heading("Last 5 minutes", 22));
        _trace.Height = 150;
        var chart = new Border { CornerRadius = new CornerRadius(22), Padding = new Thickness(16), Child = _trace };
        Raise(chart);
        s.Children.Add(chart);
        return s;
    }

    private UIElement Cooling(out RadioButton[] modes)
    {
        var s = new StackPanel();
        s.Children.Add(new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Children = { Centred(Words("Cooling", 12)), Centred(Words("Fans", 15, "TextPrimaryBrush", FontWeights.SemiBold)) } });

        var state = new Border { CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 20, 0, 0) };
        Raise(state);
        _curveNote.Margin = new Thickness(0, 3, 0, 0);
        state.Child = new StackPanel { Children = { _curve, _curveNote } };
        s.Children.Add(state);

        _dial.Margin = new Thickness(0, 14, 0, 6);
        _dial.HorizontalAlignment = HorizontalAlignment.Center;
        s.Children.Add(_dial);

        s.Children.Add(Centred(Words("Fan speed", 13, "TextPrimaryBrush", FontWeights.SemiBold)));
        _speed.Track.Margin = new Thickness(0, 12, 0, 6);
        s.Children.Add(_speed.Track);
        s.Children.Add(new DockPanel { Children = { Docked(_speedTop, Dock.Right), _speedNote } });

        s.Children.Add(Heading("Mode", 20, centred: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        modes = new[] { (FanControlMode.Auto, "Auto"), (FanControlMode.BiosDefault, "BIOS"), (FanControlMode.Max, "Max") }
            .Select(m => Pill(m.Item2, "modes", Settings.FanControlMode == m.Item1, () => SetFanMode(m.Item1), round: 58, accent: true)).ToArray();
        foreach (var m in modes) { m.Margin = new Thickness(12, 0, 12, 0); m.FontSize = 12; row.Children.Add(m); }
        s.Children.Add(row);
        return s;
    }

    // ---------------------------------------------------------------- the readings

    protected override void Update()
    {
        double? cpu = Metrics.Value("cpu"), fan = Metrics.Value("fan");
        _big.Text = Fig(cpu, "0.0");
        _fanWords.Text = FanWords();
        bool max = Settings.FanControlMode == FanControlMode.Max;
        Show(new[] { _hold }, max ? 0 : -1);
        _holdNote.Text = max ? "Held at maximum · press to go back to the curve" : "Press to hold the fans at maximum";

        var (charge, _, _) = Power();
        _battery.Text = Fig(charge, "0", " %");
        _package.Text = Fig(Metrics.Value("pkg"), "0.0", " W");
        _graphics.Text = Metrics.Value("gpu") is null && GpuState is { } asleep ? asleep : Fig(Metrics.Value("gpu"), "0", " °C");
        _clock.Text = Fig(Metrics.Value("cpuclk"), "0.00", " GHz");
        _load.Text = $"{Fig(Metrics.Value("cpuload"), "0", " %")} load";
        _limit.Text = Metrics.BindingLimitName ?? "None reported";
        _limitOf.Text = Metrics.Value("limit") is { } l ? $"{l:0} % of it" : "";
        _mem.Text = Fig(Metrics.Value("mem"), "0.0", " GB");
        _memOf.Text = Metrics.MemoryTotalGB is { } t ? $"of {t:0.0} GB" : "";

        Show(_presets, ActivePreset);
        _presetNote.Text = $"{PresetActions.All[ActivePreset].Name} is on: {PresetActions.All[ActivePreset].Note}";

        bool steering = Service.IsRunning && Service.HasCommanded;
        _curve.Text = Settings.FanControlMode switch { FanControlMode.Auto => steering ? "Curve is on" : "Curve is stopped", FanControlMode.Max => "Held at maximum", _ => "The BIOS is steering" };
        _curveNote.Text = $"{FanWords()} · {Fig(fan, "0", " rpm")} now";
        _dial.Show(cpu, OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find("cpu")!, null), State().Words);

        // Against the board's measured top speed where the calibration has one; the fastest in the
        // last five minutes where it has not, and said so either way.
        double? top = Ctx.FanBackend.Calibration.MaxRpm is > 0 and var m ? m : null;
        string of = "calibrated top";
        if (top is null)
        {
            var recent = Metrics.Recent("fan");
            top = recent.Count > 0 ? recent.Max(p => p.Value) : null;
            of = "fastest in 5 min";
        }
        _speed.Set(Gauge.Fraction(fan, top));
        _speedNote.Text = Fig(fan, "0", " rpm");
        _speedTop.Text = top is null ? "" : $"{of} {top.Value.ToString("0", CultureInfo.InvariantCulture)} rpm";
        Show(_modes, (int)Settings.FanControlMode);
    }

    protected override void OnEnter(Func<int, int> delayOf)
    {
        _laptop.Open();
        _dial.SweepIn(delayOf(2) + 80);
        Controls.Motion.Reveal(_trace, delayOf(1) + 120);
    }

    // ---------------------------------------------------------------- pieces

    /// <summary>
    /// Soft relief without a shadow: the look's panel lit from above (CardGradientBrush) and an edge that
    /// is light along the top and settles into the border (PaneEdgeBrush) -- both theme brushes, so the
    /// glass setting still reaches them.
    /// </summary>
    private static void Raise(Border b)
    {
        b.SetResourceReference(Border.BackgroundProperty, "CardGradientBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "PaneEdgeBrush");
        b.BorderThickness = new Thickness(1);
    }

    /// <summary>A recessed well: the ground's tone inside a raised panel.</summary>
    private static Border Inset(string label, TextBlock value)
    {
        var b = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 10, 0), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        value.Margin = new Thickness(0, 3, 0, 0);
        b.Child = new StackPanel { Children = { Words(label, 12), value } };
        return b;
    }

    private static Border Card(string label, TextBlock value, TextBlock note)
    {
        var b = new Border { CornerRadius = new CornerRadius(22), Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 0), Height = 112 };
        Raise(b);
        b.Child = new DockPanel { Children = { Docked(Words(label, 12), Dock.Top), new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { value, note } } } };
        return b;
    }

    private static TextBlock Heading(string text, double top, bool centred = false)
    {
        var t = Words(text, 14, "TextPrimaryBrush", FontWeights.SemiBold);
        t.Margin = new Thickness(0, top, 0, 10);
        if (centred) t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }

    private static UniformGrid3 Row(params UIElement[] children)
    {
        var g = new UniformGrid3();
        foreach (var c in children) g.Children.Add(c);
        return g;
    }

    /// <summary>Three equal columns, the last cell's right margin cancelled so the row fills its width.</summary>
    private sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid3() { Columns = 3; Margin = new Thickness(0, 0, -10, 0); }
    }

    private static TextBlock Figure(double size)
    {
        var t = new TextBlock { FontSize = size, FontWeight = FontWeights.Light, FontFamily = new FontFamily("Segoe UI Variable Display Light, Segoe UI Light, Segoe UI") };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return t;
    }

    private static TextBlock Value(double size = 17) => Words(size: size, brush: "TextPrimaryBrush", weight: FontWeights.SemiBold);

    private static T Centred<T>(T e) where T : FrameworkElement { e.HorizontalAlignment = HorizontalAlignment.Center; return e; }

    private static T Docked<T>(T e, Dock dock) where T : UIElement { DockPanel.SetDock(e, dock); return e; }

    /// <summary>
    /// The reference's climate dial as cooling: the die against its hot point in a blue sweep, ticks round
    /// it, the reading and the state inset. One drawing, redrawn when the reading moves.
    /// </summary>
    private sealed class CoolingDial : FrameworkElement
    {
        public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
            nameof(Sweep), typeof(double), typeof(CoolingDial), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>How much of the reading's arc is drawn, for the entrance. One at rest.</summary>
        public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

        private double? _value, _full;
        private string _state = "";

        private static readonly Brush Blue = Frozen(new LinearGradientBrush(Color.FromRgb(0x0A, 0x5F, 0xD6), Color.FromRgb(0x39, 0xB3, 0xFF), new Point(0, 1), new Point(1, 0)));

        public void Show(double? value, double? full, string state)
        {
            _value = value; _full = full; _state = state;
            InvalidateVisual();
        }

        public void SweepIn(int delayMs)
        {
            if (!Controls.Motion.On) return;
            BeginAnimation(SweepProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(OmniHub.Core.Theming.MotionBudget.EnterMs + 260))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
            });
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight), c = size / 2, r = size * 0.36, stroke = size * 0.08;
            if (size < 40) return;
            var ground = (Brush)FindResource("BackgroundBrush");
            var accent = (Brush)FindResource("AccentBrush");
            var text = (Brush)FindResource("TextPrimaryBrush");
            var muted = (Brush)FindResource("TextMutedBrush");
            var center = new Point(c, c);

            // Ticks round the outside, in the accent, every tenth of the scale.
            var tick = new Pen(accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i <= 10; i++)
            {
                double a = -130 + 26 * i;
                dc.DrawLine(tick, Polar(center, size * 0.47, a), Polar(center, size * 0.5 - 2, a));
            }

            // The track, then the reading's sweep against the hot point: no scale, no sweep.
            var track = new Pen(ground, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, track, Arc(center, r, -130, 130));
            if (Gauge.Fraction(_value, _full) is { } f && f > 0)
            {
                var sweep = new Pen(Blue, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawGeometry(null, sweep, Arc(center, r, -130, -130 + 260 * f * Sweep));
            }

            // The inset well with the reading and the state.
            dc.DrawEllipse(ground, null, center, r * 0.72, r * 0.72);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var figure = new FormattedText(_value is { } v ? $"{v:0}°C" : "--", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, face, size * 0.12, text, dpi);
            dc.DrawText(figure, new Point(c - figure.Width / 2, c - figure.Height / 2 - size * 0.02));
            var words = new FormattedText(_state, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), size * 0.042, muted, dpi);
            dc.DrawText(words, new Point(c - words.Width / 2, c + figure.Height / 2 - size * 0.01));
        }

        private static Point Polar(Point c, double r, double deg)
        {
            double a = (deg - 90) * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        private static Geometry Arc(Point c, double r, double from, double to)
        {
            if (to - from < 0.5) to = from + 0.5;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(Polar(c, r, from), false, false);
                ctx.ArcTo(Polar(c, r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    }
}
