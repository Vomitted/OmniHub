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
/// The Athletic look's Dashboard, after the "Whoop app redesign concept" by Taras Migulko
/// (TECHNICAL-v5-ui.md, section 15): three rounded-square rings in bold condensed figures -- what the
/// machine has in hand, how hard it is working, how hard the fans are asked to -- then cards that state a
/// condition first and the number it rests on second, and the last five minutes across the width.
/// </summary>
public sealed class AthleticDashboardView : LookDashboard
{
    private const string Condensed = "Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI";

    // The reference's three ring colours: graphics, drawn and never written, each ring's words in white.
    private static readonly Color Green = Color.FromRgb(0x22, 0xC3, 0x8E), Blue = Color.FromRgb(0x2F, 0x7D, 0xFF), Orange = Color.FromRgb(0xE0, 0x75, 0x2D);

    private readonly SquareRing _headroom = new(Green), _load = new(Blue), _fans = new(Orange);
    private readonly TextBlock _battery = Words(size: 13, brush: "TextPrimaryBrush", weight: FontWeights.SemiBold), _batteryNote = Words(size: 13),
        _within = Title(), _withinNote = Words(size: 12.5), _held = Title(), _heldNote = Words(size: 12.5),
        _die = Big(), _dieNote = Words(size: 12.5), _gpu = Big(), _gpuNote = Words(size: 12.5),
        _rpm = Big(), _rpmNote = Words(size: 12.5), _pkg = Big(), _pkgNote = Words(size: 12.5);
    private readonly RadioButton[] _modes, _presets;
    private readonly Controls.Trace _trace;

    public AthleticDashboardView(HardwareContext ctx, FanService service, AppSettings settings, MetricSource metrics, Action<FanControlMode> setFanMode)
        : base(ctx, service, settings, metrics, setFanMode)
    {
        var grid = Lattice(new double[] { 1, 1, 1, 1 }, new[] { 1.55, 0.9, 0.9, 1.2 }, 14, new double[] { 250, 128, 128, 170 });

        var top = Tile(grid, Top(out _modes, out _presets), 0, 0, columnSpan: 4, padding: new Thickness(4));
        top.Background = System.Windows.Media.Brushes.Transparent;
        top.BorderThickness = new Thickness(0);

        Tile(grid, Card(_within, _withinNote, "✓", "Health of the machine"), 1, 0, columnSpan: 2);
        Tile(grid, Card(_held, _heldNote, "◎", "What the processor answers to"), 1, 2, columnSpan: 2);
        Tile(grid, Card(_die, _dieNote, "♨", "Processor die"), 2, 0);
        Tile(grid, Card(_gpu, _gpuNote, "▣", "Graphics"), 2, 1);
        Tile(grid, Card(_rpm, _rpmNote, "✺", "Fans"), 2, 2);
        Tile(grid, Card(_pkg, _pkgNote, "ϟ", "Package power"), 2, 3);

        _trace = new Controls.Trace(metrics, () => OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find("cpu")!, null), ("cpu", "GoodBrush"))
        { Margin = new Thickness(0, 10, 0, 0) };
        var chart = new DockPanel();
        chart.Children.Add(Docked(new DockPanel
        {
            Children = { Docked(Words("the die, from 0 to its hot point", 12.5), Dock.Right), Words("Last 5 minutes", 21, "TextPrimaryBrush", FontWeights.SemiBold) },
        }, Dock.Top));
        chart.Children.Add(_trace);
        Tile(grid, chart, 3, 0, columnSpan: 4);
        Host(grid);
    }

    private UIElement Top(out RadioButton[] modes, out RadioButton[] presets)
    {
        var p = new DockPanel();

        // The pills along the top, as the reference's: who, the choices, and the charge.
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        bar.Children.Add(Docked(Chip(new StackPanel { Orientation = Orientation.Horizontal, Children = { _battery, _batteryNote } }), Dock.Right));
        _batteryNote.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(Docked(Chip(Words(HardwareNames.ShortModel(Ctx.Model.Manufacturer, Ctx.Model.Product) ?? "This machine", 13, "TextPrimaryBrush", FontWeights.SemiBold)), Dock.Left));
        var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        presets = PresetActions.All.Select((preset, i) => Pill(preset.Name, "presets", i == ActivePreset, () => ApplyPreset(i))).ToArray();
        modes = new[] { (FanControlMode.Auto, "Fans auto"), (FanControlMode.BiosDefault, "BIOS"), (FanControlMode.Max, "Max") }
            .Select(m => Pill(m.Item2, "modes", Settings.FanControlMode == m.Item1, () => SetFanMode(m.Item1))).ToArray();
        foreach (var b in presets.Concat(modes)) { b.Margin = new Thickness(0, 0, 6, 0); choices.Children.Add(b); }
        modes[0].Margin = new Thickness(18, 0, 6, 0);
        bar.Children.Add(choices);
        p.Children.Add(Docked(bar, Dock.Top));

        var rings = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        rings.Children.Add(RingWith(_headroom, "Headroom", "to 90 °C"));
        rings.Children.Add(RingWith(_load, "Load", "processor"));
        rings.Children.Add(RingWith(_fans, "Fans", "the curve asks"));
        p.Children.Add(rings);
        return p;
    }

    protected override void Update()
    {
        double? cpu = Metrics.Value("cpu"), hot = OmniHub.Core.Telemetry.Metrics.FullScale(OmniHub.Core.Telemetry.Metrics.Find("cpu")!, null);

        // Headroom: how far the die is from its hot point, as a share of it -- the reference's recovery.
        double? headroom = cpu is { } c && hot is > 0 ? Math.Max(0, (hot.Value - c) / hot.Value * 100) : null;
        _headroom.Show(headroom / 100, headroom is { } h ? $"{h:0}%" : "--");
        double? load = Metrics.Value("cpuload");
        _load.Show(load / 100, load is { } l ? $"{l:0}%" : "--");
        bool steering = Settings.FanControlMode == FanControlMode.Auto && Service.IsRunning && Service.HasCommanded;
        double? ask = steering ? Service.LastCommandedLevelPercent : Settings.FanControlMode == FanControlMode.Max ? 100 : null;
        _fans.Show(ask / 100, ask is { } a ? $"{a:0}%" : "BIOS");

        var (charge, charging, onAc) = Power();
        _battery.Text = Fig(charge, "0", " %");
        _batteryNote.Text = charging ? "charging" : onAc == true ? "on AC" : onAc == false ? "on battery" : "";

        // Within range: each reading that has a threshold, against it; the ones without a reading are left
        // out of the count rather than counted as fine.
        var checks = new List<(string Name, bool Ok)>();
        if (cpu is { } die) checks.Add(("die below 80 °C", die < 80));
        if (Metrics.Value("gpu") is { } g) checks.Add(("graphics below 80 °C", g < 80));
        if (Metrics.Value("limit") is { } lim) checks.Add(("no limit fully binding", lim < 100));
        if (Metrics.Value("mem") is { } mem && Metrics.MemoryTotalGB is { } total) checks.Add(("memory under 90 %", mem < total * 0.9));
        checks.Add(("firmware not throttling", !Throttling));
        int ok = checks.Count(x => x.Ok);
        _within.Text = ok == checks.Count ? "Within range" : "Out of range";
        _withinNote.Text = $"{ok}/{checks.Count} readings" + (ok == checks.Count ? "" : " · " + string.Join(", ", checks.Where(x => !x.Ok).Select(x => "not " + x.Name)));

        _held.Text = Metrics.BindingLimitName is { } binding ? $"Held by {binding.ToLowerInvariant()}" : "No limit reported";
        _heldNote.Text = Metrics.Value("limit") is { } pct ? $"{pct:0} % of the limit" : "";

        _die.Text = Fig(cpu, "0.0", " °C");
        var recent = Metrics.Recent("cpu");
        _dieNote.Text = recent.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"range {recent.Min(p => p.Value):0}–{recent.Max(p => p.Value):0} recently") : "";
        double? gpu = Metrics.Value("gpu");
        _gpu.Text = gpu is null && GpuState is not null ? "Asleep" : Fig(gpu, "0", " °C");
        _gpuNote.Text = $"{Fig(Metrics.Value("gpuload"), "0", " %")} load · {Fig(Metrics.Value("gpuw"), "0.0", " W")}";
        _rpm.Text = Fig(Metrics.Value("fan"), "0");
        _rpmNote.Text = $"rpm · {FanWords()}";
        _pkg.Text = Fig(Metrics.Value("pkg"), "0.0", " W");
        _pkgNote.Text = Fig(Metrics.Value("cpuclk"), "0.00", " GHz");

        Show(_modes, (int)Settings.FanControlMode);
        Show(_presets, ActivePreset);
    }

    protected override void OnEnter(Func<int, int> delayOf)
    {
        foreach (var ring in new[] { _headroom, _load, _fans }) ring.SweepIn(delayOf(0) + 80);
        Controls.Motion.Reveal(_trace, delayOf(Tiles.Count - 1) + 80);
    }

    // ---------------------------------------------------------------- pieces

    private static Border Chip(UIElement child)
    {
        var b = Round(new Border { Child = child, Padding = new Thickness(16, 8, 16, 8) });
        b.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        return b;
    }

    private static UIElement RingWith(SquareRing ring, string name, string note)
    {
        ring.Width = ring.Height = 190;
        var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        label.Children.Add(Words(name + " ›", 15, "TextPrimaryBrush", FontWeights.SemiBold));
        var n = Words(note, 13);
        n.Margin = new Thickness(6, 1, 0, 0);
        label.Children.Add(n);
        return new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { ring, label } };
    }

    private static UIElement Card(TextBlock head, TextBlock note, string icon, string about)
    {
        var p = new DockPanel();
        var chevron = Words("›", 18, "TextFaintBrush");
        p.Children.Add(Docked(new DockPanel { Children = { Docked(chevron, Dock.Right), head } }, Dock.Top));
        note.Margin = new Thickness(0, 2, 0, 0);
        p.Children.Add(Docked(note, Dock.Top));
        var glyph = Words(icon, 13, "TextPrimaryBrush");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var mark = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = glyph };
        mark.SetResourceReference(Border.BackgroundProperty, "PanelHoverBrush");
        var foot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Children = { mark } };
        var a = Words(about, 13, "TextPrimaryBrush");
        a.Margin = new Thickness(10, 0, 0, 0);
        a.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(a);
        p.Children.Add(foot);
        return p;
    }

    private static TextBlock Title() => Words(size: 21, brush: "TextPrimaryBrush", weight: FontWeights.SemiBold);

    private static TextBlock Big()
    {
        var t = new TextBlock { FontSize = 32, FontFamily = new FontFamily(Condensed), FontStretch = FontStretches.Condensed, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        return t;
    }

    private static T Docked<T>(T e, Dock dock) where T : UIElement { DockPanel.SetDock(e, dock); return e; }

    /// <summary>
    /// A rounded-square ring, as the reference draws its three: the track, and the reading's share of it
    /// from the top centre clockwise, the figure in bold condensed type inside. One path each.
    /// </summary>
    private sealed class SquareRing : FrameworkElement
    {
        public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
            nameof(Sweep), typeof(double), typeof(SquareRing), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

        private readonly Brush _colour;
        private double? _fraction;
        private string _figure = "--";

        public SquareRing(Color colour)
        {
            var b = new SolidColorBrush(colour);
            b.Freeze();
            _colour = b;
        }

        public void Show(double? fraction, string figure)
        {
            _fraction = fraction;
            _figure = figure;
            InvalidateVisual();
        }

        public void SweepIn(int delayMs)
        {
            if (!Controls.Motion.On) return;
            BeginAnimation(SweepProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(OmniHub.Core.Theming.MotionBudget.EnterMs + 300))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
            });
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight), thick = size * 0.075, inset = thick / 2 + 1;
            if (size < 40) return;
            var rect = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
            double r = rect.Width * 0.36;
            var outline = Outline(rect, r);

            dc.DrawGeometry(null, new Pen((Brush)FindResource("PanelBrush"), thick), outline);

            if (_fraction is { } f && f > 0)
            {
                // Dash lengths are in units of the pen's thickness: the drawn share, then the rest of the way round.
                double perimeter = 2 * (rect.Width - 2 * r) + 2 * (rect.Height - 2 * r) + 2 * Math.PI * r;
                double drawn = perimeter * Math.Clamp(f, 0, 1) * Sweep / thick;
                var pen = new Pen(_colour, thick) { DashStyle = new DashStyle(new[] { drawn, perimeter / thick + 1 }, 0), DashCap = PenLineCap.Round };
                dc.DrawGeometry(null, pen, outline);
            }

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface(new FontFamily(Condensed), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Condensed);
            var text = new FormattedText(_figure, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, face, size * 0.27, (Brush)FindResource("TextPrimaryBrush"), dpi);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        // The rounded square from its top centre, clockwise, so the share grows the way a clock hand turns.
        private static Geometry Outline(Rect b, double r)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double cx = b.Left + b.Width / 2;
                ctx.BeginFigure(new Point(cx, b.Top), false, false);
                ctx.LineTo(new Point(b.Right - r, b.Top), true, false);
                ctx.ArcTo(new Point(b.Right, b.Top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(b.Right, b.Bottom - r), true, false);
                ctx.ArcTo(new Point(b.Right - r, b.Bottom), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(b.Left + r, b.Bottom), true, false);
                ctx.ArcTo(new Point(b.Left, b.Bottom - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(b.Left, b.Top + r), true, false);
                ctx.ArcTo(new Point(b.Left + r, b.Top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(cx, b.Top), true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
