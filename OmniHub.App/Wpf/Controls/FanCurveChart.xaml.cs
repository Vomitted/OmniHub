// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using UserControl = System.Windows.Controls.UserControl;
using Brushes = System.Windows.Media.Brushes;
using OmniHub.Core.Fan;

namespace OmniHub.App.Wpf.Controls;

public partial class FanCurveChart : UserControl
{
    private const double MaxTempAxis = 100.0;
    private const double PadL = 34, PadR = 12, PadT = 10, PadB = 24;

    private Ellipse? _liveDot;
    private Ellipse? _liveHalo;
    private double? _liveTemp;
    private byte? _liveLevel;

    public IReadOnlyList<CurvePoint> Points { get; set; } = FanCurve.CreateDefault().Points;
    public double FloorTempC { get; set; } = 55.0;
    public byte FloorLevelPercent { get; set; } = 15;

    public FanCurveChart()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Redraw(animateEntrance: true);
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw(animateEntrance: false);

    public void RefreshData() => Redraw(animateEntrance: false);

    public void SetLive(double tempC, byte levelPercent)
    {
        _liveTemp = tempC;
        _liveLevel = levelPercent;
        UpdateLiveMarker(animate: true);
    }

    private (double X, double Y) PlotRect()
    {
        return (Root.ActualWidth - PadL - PadR, Root.ActualHeight - PadT - PadB);
    }

    private Point ToScreen(double tempC, double levelPercent)
    {
        var (w, h) = PlotRect();
        double x = PadL + Math.Clamp(tempC, 0, MaxTempAxis) / MaxTempAxis * w;
        double y = PadT + h - Math.Clamp(levelPercent, 0, 100) / 100.0 * h;
        return new Point(x, y);
    }

    private void Redraw(bool animateEntrance)
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0) return;
        Root.Children.Clear();
        _liveDot = null;
        _liveHalo = null;

        var gridBrush = (Brush)FindResource("BorderBrush");
        var mutedBrush = (Brush)FindResource("TextMutedBrush");
        var accentBrush = (Brush)FindResource("AccentBrush");
        var warnBrush = (Brush)FindResource("WarnBrush");
        var goodBrush = (Brush)FindResource("GoodBrush");
        var (w, h) = PlotRect();

        // Gridlines + axis labels
        for (int level = 0; level <= 100; level += 25)
        {
            var p = ToScreen(0, level);
            Root.Children.Add(new Line { X1 = PadL, Y1 = p.Y, X2 = PadL + w, Y2 = p.Y, Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 } });
            var lbl = new TextBlock { Text = $"{level}%", Foreground = mutedBrush, FontSize = 9 };
            Canvas.SetLeft(lbl, 0); Canvas.SetTop(lbl, p.Y - 7);
            Root.Children.Add(lbl);
        }
        for (int temp = 0; temp <= (int)MaxTempAxis; temp += 20)
        {
            var p = ToScreen(temp, 0);
            Root.Children.Add(new Line { X1 = p.X, Y1 = PadT, X2 = p.X, Y2 = PadT + h, Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 3 } });
            var lbl = new TextBlock { Text = $"{temp}\u00b0", Foreground = mutedBrush, FontSize = 9 };
            Canvas.SetLeft(lbl, p.X - 8); Canvas.SetTop(lbl, PadT + h + 4);
            Root.Children.Add(lbl);
        }

        // Safety floor. The protected region is shaded rather than just ruled with a line:
        // the floor's whole meaning is "the curve may not go below here past this
        // temperature", which describes an area, so an area is what gets drawn.
        if (FloorTempC < MaxTempAxis)
        {
            var topLeft = ToScreen(FloorTempC, FloorLevelPercent);
            var bottomRight = ToScreen(MaxTempAxis, 0);
            var warnColor = (warnBrush as SolidColorBrush)?.Color ?? Colors.Orange;

            var region = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(0, bottomRight.X - topLeft.X),
                Height = Math.Max(0, bottomRight.Y - topLeft.Y),
                Fill = new SolidColorBrush(Color.FromArgb(0x14, warnColor.R, warnColor.G, warnColor.B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(region, topLeft.X);
            Canvas.SetTop(region, topLeft.Y);
            Root.Children.Add(region);

            var a = ToScreen(FloorTempC, FloorLevelPercent);
            var b = ToScreen(MaxTempAxis, FloorLevelPercent);
            Root.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = warnBrush, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } });
        }

        // Curve line
        if (Points.Count >= 2)
        {
            var ordered = Points.OrderBy(p => p.TempC).ToList();
            var screen = ordered.Select(pt => ToScreen(pt.TempC, pt.LevelPercent)).ToList();
            var accentColor = (accentBrush as SolidColorBrush)?.Color ?? Colors.SkyBlue;

            // Area fill beneath the curve, matching the trend chart's treatment.
            var fillFigure = new PathFigure { StartPoint = new Point(screen[0].X, PadT + h) };
            foreach (var s in screen) fillFigure.Segments.Add(new LineSegment(s, true));
            fillFigure.Segments.Add(new LineSegment(new Point(screen[^1].X, PadT + h), true));
            fillFigure.IsClosed = true;

            var fillGeometry = new PathGeometry();
            fillGeometry.Figures.Add(fillFigure);
            fillGeometry.Freeze();

            var areaBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x44, accentColor.R, accentColor.G, accentColor.B), 0));
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, accentColor.R, accentColor.G, accentColor.B), 1));
            areaBrush.Freeze();

            Root.Children.Add(new System.Windows.Shapes.Path { Data = fillGeometry, Fill = areaBrush, IsHitTestVisible = false });

            // Straight segments, deliberately. Unlike the temperature trace, this chart is a
            // piecewise-linear lookup table: FanCurve.Interpolate walks between adjacent
            // points in a straight line. Smoothing it would draw a curve the fan does not
            // actually follow, which is the one thing a control chart must never do.
            var poly = new Polyline { Stroke = accentBrush, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
            foreach (var s in screen) poly.Points.Add(s);
            poly.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = accentColor,
                ShadowDepth = 0,
                BlurRadius = 12,
                Opacity = 0.5,
            };
            Root.Children.Add(poly);

            foreach (var s in screen)
            {
                var dot = new Ellipse { Width = 7, Height = 7, Fill = accentBrush };
                Canvas.SetLeft(dot, s.X - 3.5); Canvas.SetTop(dot, s.Y - 3.5);
                Root.Children.Add(dot);
            }

            if (animateEntrance && SystemParameters.ClientAreaAnimation)
            {
                poly.Opacity = 0;
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                poly.BeginAnimation(OpacityProperty, fade);
            }
        }

        // Live marker (halo + dot), created once so subsequent moves can animate
        _liveHalo = new Ellipse
        {
            Width = 18, Height = 18, Stroke = goodBrush, StrokeThickness = 1.5,
            Fill = Brushes.Transparent, Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _liveDot = new Ellipse { Width = 9, Height = 9, Fill = goodBrush, Opacity = 0 };
        Root.Children.Add(_liveHalo);
        Root.Children.Add(_liveDot);

        // Slow breathing halo. Without it the live point is just a fourth dot of a different
        // colour among the curve's own handles; the motion is what identifies it as "now"
        // rather than another editable point.
        if (SystemParameters.ClientAreaAnimation)
        {
            var scale = new ScaleTransform(1, 1);
            _liveHalo.RenderTransform = scale;
            var breathe = new DoubleAnimation(1.0, 1.35, TimeSpan.FromMilliseconds(1600))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }

        UpdateLiveMarker(animate: false);
    }

    private void UpdateLiveMarker(bool animate)
    {
        if (_liveDot is null || _liveHalo is null || _liveTemp is not double t || _liveLevel is not byte l) return;

        var p = ToScreen(t, l);
        double dotLeft = p.X - _liveDot.Width / 2, dotTop = p.Y - _liveDot.Height / 2;
        double haloLeft = p.X - _liveHalo.Width / 2, haloTop = p.Y - _liveHalo.Height / 2;

        var duration = TimeSpan.FromMilliseconds(500);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (animate && _liveDot.Opacity > 0)
        {
            AnimateDouble(_liveDot, Canvas.LeftProperty, dotLeft, duration, ease);
            AnimateDouble(_liveDot, Canvas.TopProperty, dotTop, duration, ease);
            AnimateDouble(_liveHalo, Canvas.LeftProperty, haloLeft, duration, ease);
            AnimateDouble(_liveHalo, Canvas.TopProperty, haloTop, duration, ease);
        }
        else
        {
            Canvas.SetLeft(_liveDot, dotLeft); Canvas.SetTop(_liveDot, dotTop);
            Canvas.SetLeft(_liveHalo, haloLeft); Canvas.SetTop(_liveHalo, haloTop);
            _liveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
            _liveHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        }
    }

    private static void AnimateDouble(UIElement target, DependencyProperty prop, double to, TimeSpan duration, IEasingFunction ease)
    {
        var anim = new DoubleAnimation(to, duration) { EasingFunction = ease };
        target.BeginAnimation(prop, anim);
    }
}
