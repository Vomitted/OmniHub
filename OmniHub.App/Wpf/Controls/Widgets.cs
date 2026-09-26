// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using OmniHub.Core.Telemetry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace OmniHub.App.Wpf.Controls;

// The widgets (technical/TECHNICAL-v5-ui.md, sections 7 and 8): readings drawn rather than written.
// Each one draws against a real full scale or not at all -- Gauge.Fraction returns null without one,
// and a null draws the track alone -- and each moves only while it can be seen.

/// <summary>
/// Figures that count to a new reading rather than jumping to it.
///
/// Over 240 ms, the application's figure for a readout moving, and only while the figure can be seen;
/// hidden, or with Windows' animations turned off, it simply takes the value. The count passes through
/// numbers nobody measured on its way, which is why it is this short: long enough to show the size and
/// direction of a change, too short for a figure in passing to be read as a reading.
/// </summary>
internal static class Roll
{
    private sealed record Form(string Format, string Suffix);

    private static readonly DependencyProperty FormProperty =
        DependencyProperty.RegisterAttached("RollForm", typeof(Form), typeof(Roll));

    private static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "RollValue", typeof(double), typeof(Roll), new PropertyMetadata(double.NaN, (d, e) =>
        {
            if (d is TextBlock text && text.GetValue(FormProperty) is Form form && e.NewValue is double v && !double.IsNaN(v))
                text.Text = v.ToString(form.Format, System.Globalization.CultureInfo.InvariantCulture) + form.Suffix;
        }));

    /// <param name="animate">False to set at once -- a figure following the pointer must not trail it.</param>
    public static void To(TextBlock text, double? value, string format, string suffix = "", bool animate = true)
    {
        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v))
        {
            text.BeginAnimation(ValueProperty, null);
            text.SetValue(ValueProperty, double.NaN);
            text.Text = Metrics.Unavailable;
            return;
        }

        text.SetValue(FormProperty, new Form(format, suffix));

        if (!animate || double.IsNaN((double)text.GetValue(ValueProperty)) || !text.IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            text.BeginAnimation(ValueProperty, null);
            text.SetValue(ValueProperty, v);
            // Set as well, because an unchanged value raises nothing and the format may be new.
            text.Text = v.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix;
            return;
        }

        text.BeginAnimation(ValueProperty, new DoubleAnimation(v, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}

/// <summary>
/// The colour a drawn reading glows in: its own, so a warning arc glows amber rather than blue.
///
/// A glow is light the mark gives off, which is why it is on the arcs, the bars and the fan and
/// nowhere near the words. Built from the brush at the moment it is drawn, so a theme switch reaches
/// the glow on the next reading.
/// </summary>
internal static class Glow
{
    public static DropShadowEffect Make(double blur, double opacity) =>
        new() { ShadowDepth = 0, BlurRadius = blur, Opacity = opacity };

    public static Color Of(Brush brush)
    {
        switch (brush)
        {
            case SolidColorBrush solid:
                return solid.Color;

            case GradientBrush { GradientStops.Count: > 0 } gradient:
                double r = 0, g = 0, b = 0;
                foreach (var stop in gradient.GradientStops) { r += stop.Color.R; g += stop.Color.G; b += stop.Color.B; }
                int n = gradient.GradientStops.Count;
                return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));

            default:
                return Colors.Transparent;
        }
    }
}

/// <summary>
/// A 240 degree arc, open at the bottom, filled to a fraction.
///
/// Open at the bottom because a closed ring has no start, and a gauge whose zero is ambiguous is
/// decoration. It starts bottom left and sweeps clockwise, the convention every physical instrument
/// it imitates already uses. Shared by the ring gauges here and the Cockpit interface's dials.
/// </summary>
internal static class Arc
{
    private const double StartDeg = 150, TotalDeg = 240;

    public static Geometry Geometry(double diameter, double fraction, double inset = 5) =>
        Stretch(diameter, 0, fraction, inset);

    /// <summary>The arc between two fractions of the way round -- a zone on the dial.</summary>
    public static Geometry Stretch(double diameter, double from, double to, double inset = 5)
    {
        double r = diameter / 2 - inset;
        double c = diameter / 2;

        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        if (to - from <= 0.001 || r <= 0) return System.Windows.Media.Geometry.Empty;

        double sweep = TotalDeg * (to - from);
        var figure = new PathFigure { StartPoint = At(c, r, from), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(c, r, to), new System.Windows.Size(r, r), 0,
                                           isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true));
        var g = new PathGeometry();
        g.Figures.Add(figure);
        return g;
    }

    /// <summary>The point a fraction of the way round, at radius <paramref name="r"/> from the centre.</summary>
    public static Point At(double c, double r, double fraction)
    {
        double rad = (StartDeg + TotalDeg * fraction) * Math.PI / 180;
        return new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
    }

    public static Path Path(double diameter, double fraction, Brush stroke, double thickness, double inset = 5) => new()
    {
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Data = Geometry(diameter, fraction, inset),
    };
}

/// <summary>
/// The ring itself: the track, the stretch past the warning threshold, a tick every tenth, and the
/// value arc with its glow, eased to each reading.
///
/// Marked like the instrument it imitates: the warning stretch is tinted on the track the way a
/// tachometer carries its red zone, so how close a value is to trouble is drawn before it gets there.
/// Shared by the gauges on the cards and the Dashboard's centre dial, which differ only in what they
/// set around it.
/// </summary>
internal sealed class ArcDial : Canvas
{
    private readonly Path _track, _value;
    private readonly Path _zone = new() { Opacity = 0.45 };
    private readonly Path _ticks = new() { StrokeThickness = 1 };
    private readonly DropShadowEffect _glow;
    private readonly double _stroke;
    private double _diameter;
    private double? _warnFrom;

    private static readonly DependencyProperty DrawnProperty = DependencyProperty.Register(
        nameof(Drawn), typeof(double), typeof(ArcDial),
        new PropertyMetadata(0.0, (d, _) => ((ArcDial)d).Redraw()));

    /// <summary>The fraction on screen right now, which eases towards the reading.</summary>
    private double Drawn
    {
        get => (double)GetValue(DrawnProperty);
        set => SetValue(DrawnProperty, value);
    }

    public ArcDial(double diameter, double stroke, double glowBlur)
    {
        _diameter = diameter;
        _stroke = stroke;
        _glow = Glow.Make(blur: glowBlur, opacity: 0.65);
        HorizontalAlignment = HorizontalAlignment.Center;

        // The border tone rather than TrackBrush: on a pane, the track brush is one step from the
        // pane itself and the unfilled arc all but disappears, so a gauge stops reading as a proportion.
        _track = Arc.Path(diameter, 1, System.Windows.Media.Brushes.Transparent, stroke, stroke / 2 + 1);
        _track.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        _value = Arc.Path(diameter, 0, System.Windows.Media.Brushes.Transparent, stroke, stroke / 2 + 1);
        _value.Effect = _glow;

        _zone.StrokeThickness = stroke;
        _zone.SetResourceReference(Shape.StrokeProperty, "WarnBrush");
        _ticks.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");

        Children.Add(_track);
        Children.Add(_zone);
        Children.Add(_ticks);
        Children.Add(_value);

        Resize();
    }

    public double Diameter
    {
        get => _diameter;
        set { _diameter = value; Resize(); }
    }

    /// <summary>
    /// Draws a reading. A null <paramref name="fraction"/> draws the track alone: without a real full
    /// scale there is no arc.
    /// </summary>
    public void Show(double? fraction, Brush arc, double? warnFrom)
    {
        _value.Stroke = arc;
        _glow.Color = Glow.Of(arc);

        if (warnFrom != _warnFrom)
        {
            _warnFrom = warnFrom;
            DrawMarks();
        }

        double target = fraction ?? 0;

        // Eased in 220 ms, the application's figure for a readout moving, and only while it can be
        // seen; hidden, it simply takes the value.
        if (IsVisible && SystemParameters.ClientAreaAnimation)
            BeginAnimation(DrawnProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        else
        {
            BeginAnimation(DrawnProperty, null);
            Drawn = target;
        }
    }

    private void Resize()
    {
        Width = Height = _diameter;
        _track.Data = Arc.Geometry(_diameter, 1, _stroke / 2 + 1);
        DrawMarks();
        Redraw();
    }

    private void DrawMarks()
    {
        _zone.Data = _warnFrom is { } w ? Arc.Stretch(_diameter, w, 1, _stroke / 2 + 1) : System.Windows.Media.Geometry.Empty;

        double c = _diameter / 2, inner = c - _stroke - 4;
        var ticks = new GeometryGroup();
        for (int i = 0; i <= 10; i++)
            ticks.Children.Add(new LineGeometry(Arc.At(c, inner, i / 10.0), Arc.At(c, inner - (i % 5 == 0 ? 5 : 3), i / 10.0)));
        ticks.Freeze();
        _ticks.Data = ticks;
    }

    private void Redraw() => _value.Data = Arc.Geometry(_diameter, Drawn, _stroke / 2 + 1);
}

/// <summary>
/// A reading as an arc: the ring, the figure and its unit in the middle, and what it is under the ring.
/// </summary>
public sealed class RingGauge : Grid
{
    private readonly ArcDial _dial = new(116, 9, 14);
    private readonly TextBlock _figure = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _unit = new() { HorizontalAlignment = HorizontalAlignment.Center };
    // In the arc's open bottom, where a physical gauge carries its label.
    private readonly TextBlock _caption = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };

    public RingGauge()
    {
        _figure.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _figure.FontWeight = FontWeights.SemiBold;
        _unit.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _unit.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _unit.FontSize = 11;
        _caption.SetResourceReference(StyleProperty, "CellNote");

        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        middle.Children.Add(_figure);
        middle.Children.Add(_unit);

        var face = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(_dial);
        face.Children.Add(middle);
        face.Children.Add(_caption);
        Children.Add(face);

        _figure.FontSize = Math.Round(_dial.Diameter * 0.22);
    }

    /// <summary>The ring's size; the figure scales with it.</summary>
    public double Diameter
    {
        get => _dial.Diameter;
        set { _dial.Diameter = value; _figure.FontSize = Math.Round(value * 0.22); }
    }

    /// <summary>What the reading is, under the ring.</summary>
    public string Caption
    {
        get => _caption.Text;
        set => _caption.Text = value;
    }

    /// <summary>
    /// Shows a reading. A null <paramref name="fraction"/> draws the track alone: without a real full
    /// scale there is no arc, only the figure.
    /// </summary>
    /// <param name="warnFrom">Where on the scale the reading's warning threshold falls, or null for none.</param>
    public void Show(double? fraction, double? value, string format, string unit, Brush arc, Brush figureBrush,
                     double? warnFrom = null)
    {
        Roll.To(_figure, value, format);
        _figure.Foreground = figureBrush;
        _unit.Text = unit;
        _dial.Show(fraction, arc, warnFrom);
    }

    /// <summary>
    /// A temperature from the shared readings, against its own thresholds: the full scale is the
    /// reading's hot point, and past its warning point the arc and the figure take the warning
    /// colours. The colour is on the drawing and the number, never on the words.
    /// </summary>
    public void ShowTemperature(MetricSource source, string key)
    {
        if (Metrics.Find(key) is not { } metric) return;

        double? value = source.Value(key);
        var level = Metrics.LevelOf(key, value);
        double? full = Metrics.FullScale(metric, null);

        Show(Gauge.Fraction(value, full), value, metric.Format, "°C",
             (Brush)FindResource(level switch { MetricLevel.Hot => "DangerBrush", MetricLevel.Warn => "WarnBrush", _ => "AccentGradientBrush" }),
             (Brush)FindResource(value is null ? "TextFaintBrush"
                 : level switch { MetricLevel.Hot => "DangerBrush", MetricLevel.Warn => "WarnBrush", _ => "TextPrimaryBrush" }),
             Gauge.Fraction(metric.WarnAt, full));
    }
}

/// <summary>
/// A labelled reading over a bar filled against its real limit. No limit, no bar: the figure alone.
/// </summary>
public sealed class Meter : Grid
{
    private readonly TextBlock _label = new();
    private readonly TextBlock _value = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _bar = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _rest = new() { Width = new GridLength(100, GridUnitType.Star) };
    private readonly Border _fill = new();
    private readonly DropShadowEffect _glow = Glow.Make(blur: 8, opacity: 0.55);

    public Meter()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _label.SetResourceReference(StyleProperty, "CellNote");
        _value.SetResourceReference(StyleProperty, "CellFigure");
        _value.FontSize = 11.5;
        Children.Add(_label);
        Children.Add(_value);

        _bar.SetResourceReference(HeightProperty, "TrackHeight");
        _bar.ColumnDefinitions.Add(_filled);
        _bar.ColumnDefinitions.Add(_rest);

        var track = new Border();
        track.SetResourceReference(Border.BackgroundProperty, "BorderBrush");   // visible on a pane; see RingGauge
        track.SetResourceReference(Border.CornerRadiusProperty, "RadiusPill");
        SetColumnSpan(track, 2);

        _fill.SetResourceReference(Border.BackgroundProperty, "AccentGradientBrush");
        _fill.SetResourceReference(Border.CornerRadiusProperty, "RadiusPill");
        _fill.Effect = _glow;

        _bar.Children.Add(track);
        _bar.Children.Add(_fill);
        SetRow(_bar, 1);
        Children.Add(_bar);
    }

    public string Label
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    /// <summary>Shows a value; a null <paramref name="fraction"/> hides the bar rather than inventing a limit.</summary>
    public void Show(string value, double? fraction, Brush? fill = null)
    {
        _value.Text = value;
        _bar.Visibility = fraction is null ? Visibility.Collapsed : Visibility.Visible;

        double pct = Math.Round((fraction ?? 0) * 100, 1);
        _filled.Width = new GridLength(pct, GridUnitType.Star);
        _rest.Width = new GridLength(100 - pct, GridUnitType.Star);

        if (fill is null) _fill.SetResourceReference(Border.BackgroundProperty, "AccentGradientBrush");
        else _fill.Background = fill;
        _glow.Color = Glow.Of(_fill.Background);
    }
}

/// <summary>
/// A fan that turns faster when the real one does.
///
/// The rate is a legible mapping of the real speed, not the real speed (see Gauge.SecondsPerTurn);
/// the figure beside it carries the number. It turns only while it can be seen, like every endless
/// animation in this application, and when it stops it holds its angle rather than snapping back.
/// </summary>
public sealed class FanGlyph : Viewbox
{
    private readonly RotateTransform _turn = new() { CenterX = 50, CenterY = 50 };
    private double? _secondsPerTurn;

    // A ring of the accent's light around the blades, brightest where they sweep. A static fill under
    // a mask rather than a blur on the rotor: the rotor redraws every frame it turns, and a blur there
    // would be paid every one of them.
    private readonly Ellipse _halo = new() { Width = 100, Height = 100, Opacity = 0.5, Visibility = Visibility.Hidden, OpacityMask = HaloMask };

    private static readonly Brush HaloMask = Frozen(new RadialGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0x00, 0, 0, 0), 0.30),
        new(Color.FromArgb(0xFF, 0, 0, 0), 0.66),
        new(Color.FromArgb(0x00, 0, 0, 0), 1.00),
    }));

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    private readonly Path _rotor = new();
    private readonly Ellipse _cap = new() { Width = 10, Height = 10 };

    /// <summary>
    /// The theme brush the blades, the cap and the halo take. The accent by default; a card drawing
    /// one component gives its own colour, so the cooling card's fan turns in cooling's amber.
    /// </summary>
    public string ColourKey
    {
        set
        {
            _rotor.SetResourceReference(Shape.FillProperty, value);
            _cap.SetResourceReference(Shape.FillProperty, value);
            _halo.SetResourceReference(Shape.FillProperty, value);
        }
    }

    public FanGlyph()
    {
        var canvas = new Canvas { Width = 100, Height = 100 };

        _halo.SetResourceReference(Shape.FillProperty, "AccentBrush");
        canvas.Children.Add(_halo);

        var housing = new Ellipse { Width = 96, Height = 96, StrokeThickness = 3 };
        housing.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        Canvas.SetLeft(housing, 2);
        Canvas.SetTop(housing, 2);
        canvas.Children.Add(housing);

        // An impeller, the shape of the blower fans in this chassis: eleven thin blades rooted on a
        // large hub and swept back along the turn. Blades radiating from a point read as a flower.
        // Each is the same swept sliver -- root at radius 17, tip at 44, trailing about 35 degrees --
        // turned about the centre.
        var blades = new GeometryGroup();
        var blade = System.Windows.Media.Geometry.Parse("M 67,50 Q 82,55 88.1,72 L 82.7,79.4 Q 76,62 66.3,54.7 Z");
        for (int i = 0; i < 11; i++)
        {
            var copy = blade.Clone();
            copy.Transform = new RotateTransform(360.0 / 11 * i, 50, 50);
            blades.Children.Add(copy);
        }

        _rotor.Data = blades;
        _rotor.RenderTransform = _turn;
        _rotor.SetResourceReference(Shape.FillProperty, "AccentGradientBrush");
        canvas.Children.Add(_rotor);

        var hub = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2 };
        hub.SetResourceReference(Shape.FillProperty, "PanelAltBrush");
        hub.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");
        Canvas.SetLeft(hub, 33);
        Canvas.SetTop(hub, 33);
        canvas.Children.Add(hub);

        var cap = _cap;
        cap.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Canvas.SetLeft(cap, 45);
        Canvas.SetTop(cap, 45);
        canvas.Children.Add(cap);

        Child = canvas;
        IsVisibleChanged += (_, _) => Spin();
    }

    /// <summary>Follows the real fan. Restarted only when the change is big enough to see.</summary>
    public void SetSpeed(double? rpm)
    {
        double? next = Gauge.SecondsPerTurn(rpm);

        // Lit while it turns. A glowing fan that is standing still would say it was running.
        _halo.Visibility = next is null ? Visibility.Hidden : Visibility.Visible;

        if (next == _secondsPerTurn) return;

        // A new clock every reading would restart the turn each time; small drifts are left alone.
        if (next is { } n && _secondsPerTurn is { } now && Math.Abs(n - now) / now < 0.08) return;

        _secondsPerTurn = next;
        Spin();
    }

    // Forever, so only while it can be seen: started and stopped by a visibility change, the rule
    // AmbientMotionTests holds for every endless animation in this application.
    private void Spin()
    {
        double angle = _turn.Angle % 360;

        if (!IsVisible || _secondsPerTurn is not { } seconds || !SystemParameters.ClientAreaAnimation)
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, null);
            _turn.Angle = angle;
            return;
        }

        _turn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(angle, angle + 360, TimeSpan.FromSeconds(seconds)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}

/// <summary>
/// Parts of a whole in one bar, each as wide as its share, with a legend naming them.
///
/// For totals made of pieces -- a shader cache spread over three drivers' folders, a cleanup over
/// five locations. The whole is the full scale by construction, so this is one bar that can never
/// lack one. Shades of the accent rather than the series colours: those mean the processor and the
/// graphics card everywhere else, and a temp folder is neither.
/// </summary>
public sealed class ShareBar : StackPanel
{
    private static readonly double[] Shades = { 1.0, 0.7, 0.5, 0.36, 0.26, 0.18 };
    private readonly Grid _bar = new() { Height = 8 };
    private readonly StackPanel _legend = new() { Margin = new Thickness(0, 8, 0, 0) };

    public ShareBar()
    {
        Children.Add(_bar);
        Children.Add(_legend);
    }

    /// <summary>Draws the parts; nothing at all when there is nothing, rather than an empty bar.</summary>
    public void Show(IReadOnlyList<(string Label, double Value, string Detail)> parts)
    {
        _bar.ColumnDefinitions.Clear();
        _bar.Children.Clear();
        _legend.Children.Clear();

        var drawn = parts.Select((p, i) => (Part: p, Shade: Shades[Math.Min(i, Shades.Length - 1)])).ToList();
        var filled = drawn.Where(d => d.Part.Value > 0).ToList();
        Visibility = drawn.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        for (int i = 0; i < filled.Count; i++)
        {
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(filled[i].Part.Value, GridUnitType.Star) });

            var segment = new Border
            {
                Opacity = filled[i].Shade,
                Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                CornerRadius = filled.Count == 1 ? new CornerRadius(3)
                    : i == 0 ? new CornerRadius(3, 0, 0, 3)
                    : i == filled.Count - 1 ? new CornerRadius(0, 3, 3, 0)
                    : new CornerRadius(0),
                ToolTip = $"{filled[i].Part.Label}\n{filled[i].Part.Detail}",
            };
            segment.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Grid.SetColumn(segment, i);
            _bar.Children.Add(segment);
        }

        // Every part in the legend, an empty one included: a location scanned and found clean is
        // a finding, and a list that dropped it would read as one that was never looked at.
        foreach (var (part, shade) in drawn)
        {
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Opacity = shade, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

            var name = new TextBlock { Text = part.Label, FontSize = 11.5 };
            name.SetResourceReference(StyleProperty, "CellLabel");

            var detail = new TextBlock { Text = part.Detail, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
            detail.SetResourceReference(StyleProperty, "CellNote");

            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0), LastChildFill = true };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(detail, Dock.Right);
            row.Children.Add(dot);
            row.Children.Add(detail);
            row.Children.Add(name);
            _legend.Children.Add(row);
        }
    }
}

/// <summary>A battery, filled to its charge, with a bolt while it is charging.</summary>
public sealed class BatteryGlyph : Grid
{
    private readonly ColumnDefinition _filled = new() { Width = new GridLength(0, GridUnitType.Star) };
    private readonly ColumnDefinition _rest = new() { Width = new GridLength(100, GridUnitType.Star) };
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(2) };
    private readonly Path _bolt;

    public BatteryGlyph()
    {
        Width = 92;
        Height = 42;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });

        var body = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(6), Padding = new Thickness(3) };
        body.SetResourceReference(Border.BorderBrushProperty, "TextMutedBrush");

        var cells = new Grid();
        cells.ColumnDefinitions.Add(_filled);
        cells.ColumnDefinitions.Add(_rest);
        cells.Children.Add(_fill);

        _bolt = new Path
        {
            Data = System.Windows.Media.Geometry.Parse("M 9,0 L 0,13 L 7,13 L 4,24 L 14,9 L 7,9 Z"),
            Stretch = Stretch.Uniform,
            Height = 20,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _bolt.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        _bolt.SetResourceReference(Shape.StrokeProperty, "PanelBrush");

        var inside = new Grid();
        inside.Children.Add(cells);
        inside.Children.Add(_bolt);
        body.Child = inside;
        Children.Add(body);

        var nub = new Border { Width = 4, Height = 14, CornerRadius = new CornerRadius(0, 2, 2, 0), HorizontalAlignment = HorizontalAlignment.Left };
        nub.SetResourceReference(Border.BackgroundProperty, "TextMutedBrush");
        SetColumn(nub, 1);
        Children.Add(nub);
    }

    /// <summary>Shows the charge; null is an empty shell, never an empty battery.</summary>
    public void Show(double? percent, bool charging)
    {
        double pct = Math.Clamp(percent ?? 0, 0, 100);
        _filled.Width = new GridLength(pct, GridUnitType.Star);
        _rest.Width = new GridLength(100 - pct, GridUnitType.Star);
        _fill.Visibility = percent is null ? Visibility.Collapsed : Visibility.Visible;

        // The colour is on the fill, the one place colour is allowed to mean something: low is a
        // warning, very low is a danger, and anything else is simply charge.
        _fill.SetResourceReference(Border.BackgroundProperty,
            pct < 10 ? "DangerBrush" : pct < 25 ? "WarnBrush" : "GoodGradientBrush");
        _bolt.Visibility = charging ? Visibility.Visible : Visibility.Collapsed;
    }
}
