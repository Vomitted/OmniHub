// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using OmniHub.Core.Telemetry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace OmniHub.App.Wpf.Controls;

// The widgets (technical/TECHNICAL-v5-ui.md, section 7): readings drawn rather than written. Each
// one draws against a real full scale or not at all -- Gauge.Fraction returns null without one, and
// a null draws the track alone -- and each moves only while it can be seen.

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

    public static Geometry Geometry(double diameter, double fraction, double inset = 5)
    {
        double r = diameter / 2 - inset;
        double c = diameter / 2;

        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.001 || r <= 0) return System.Windows.Media.Geometry.Empty;

        double sweep = TotalDeg * fraction;
        Point At(double deg) => new(c + r * Math.Cos(deg * Math.PI / 180), c + r * Math.Sin(deg * Math.PI / 180));

        var figure = new PathFigure { StartPoint = At(StartDeg), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(StartDeg + sweep), new System.Windows.Size(r, r), 0,
                                           isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true));
        var g = new PathGeometry();
        g.Figures.Add(figure);
        return g;
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
/// A reading as an arc: the track, the value over it, the figure and its unit in the middle, and
/// what it is under the ring.
/// </summary>
public sealed class RingGauge : Grid
{
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Path _track;
    private readonly Path _value;
    private readonly TextBlock _figure = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _unit = new() { HorizontalAlignment = HorizontalAlignment.Center };
    // In the arc's open bottom, where a physical gauge carries its label.
    private readonly TextBlock _caption = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2) };
    private double _diameter = 116;
    private const double Stroke = 9;

    private static readonly DependencyProperty DrawnProperty = DependencyProperty.Register(
        nameof(Drawn), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, (d, _) => ((RingGauge)d).Redraw()));

    /// <summary>The fraction on screen right now, which eases towards the reading.</summary>
    private double Drawn
    {
        get => (double)GetValue(DrawnProperty);
        set => SetValue(DrawnProperty, value);
    }

    public RingGauge()
    {
        // The border tone rather than TrackBrush: on a pane, the track brush is one step from the
        // pane itself and the unfilled arc all but disappears, so a gauge stops reading as a proportion.
        _track = Arc.Path(_diameter, 1, System.Windows.Media.Brushes.Transparent, Stroke, Stroke / 2 + 1);
        _track.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        _value = Arc.Path(_diameter, 0, System.Windows.Media.Brushes.Transparent, Stroke, Stroke / 2 + 1);

        _canvas.Children.Add(_track);
        _canvas.Children.Add(_value);

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
        face.Children.Add(_canvas);
        face.Children.Add(middle);
        face.Children.Add(_caption);
        Children.Add(face);

        Resize();
    }

    /// <summary>The ring's size; the figure scales with it.</summary>
    public double Diameter
    {
        get => _diameter;
        set { _diameter = value; Resize(); }
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
    public void Show(double? fraction, string figure, string unit, Brush arc, Brush figureBrush)
    {
        _figure.Text = figure;
        _figure.Foreground = figureBrush;
        _unit.Text = unit;
        _value.Stroke = arc;

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

        Show(Gauge.Fraction(value, Metrics.FullScale(metric, null)),
             value?.ToString(metric.Format, System.Globalization.CultureInfo.InvariantCulture) ?? Metrics.Unavailable,
             "°C",
             (Brush)FindResource(level switch { MetricLevel.Hot => "DangerBrush", MetricLevel.Warn => "WarnBrush", _ => "AccentGradientBrush" }),
             (Brush)FindResource(value is null ? "TextFaintBrush"
                 : level switch { MetricLevel.Hot => "DangerBrush", MetricLevel.Warn => "WarnBrush", _ => "TextPrimaryBrush" }));
    }

    private void Resize()
    {
        _canvas.Width = _canvas.Height = _diameter;
        _track.Data = Arc.Geometry(_diameter, 1, Stroke / 2 + 1);
        _figure.FontSize = Math.Round(_diameter * 0.22);
        Redraw();
    }

    private void Redraw() => _value.Data = Arc.Geometry(_diameter, Drawn, Stroke / 2 + 1);
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

    public FanGlyph()
    {
        var canvas = new Canvas { Width = 100, Height = 100 };

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

        var rotor = new Path { Data = blades, RenderTransform = _turn };
        rotor.SetResourceReference(Shape.FillProperty, "AccentGradientBrush");
        canvas.Children.Add(rotor);

        var hub = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2 };
        hub.SetResourceReference(Shape.FillProperty, "PanelAltBrush");
        hub.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");
        Canvas.SetLeft(hub, 33);
        Canvas.SetTop(hub, 33);
        canvas.Children.Add(hub);

        var cap = new Ellipse { Width = 10, Height = 10 };
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
