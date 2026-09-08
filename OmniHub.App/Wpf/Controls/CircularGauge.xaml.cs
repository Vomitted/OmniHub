using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Controls;

public partial class CircularGauge : UserControl
{
    /// <summary>
    /// The arc's drawn position, as a dependency property so WPF can animate it.
    ///
    /// This is what makes the dial feel like an instrument rather than a progress bar. The
    /// previous version redrew straight to the new number, so every 2-second poll made the
    /// ring jump: a needle that teleports reads as a redraw, one that sweeps reads as a
    /// measurement changing.
    /// </summary>
    public static readonly DependencyProperty ArcValueProperty =
        DependencyProperty.Register(nameof(ArcValue), typeof(double), typeof(CircularGauge),
            new PropertyMetadata(0.0, OnArcValueChanged));

    public double ArcValue
    {
        get => (double)GetValue(ArcValueProperty);
        set => SetValue(ArcValueProperty, value);
    }

    // Only the arc moves per frame. See RedrawArc for why this is not Redraw().
    private static void OnArcValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularGauge)d).RedrawArc();

    /// <summary>The latest value handed in, independent of where the animation currently is.</summary>
    public double Value { get; private set; } = 100;

    public string Label { get => LabelText.Text; set => LabelText.Text = value.ToUpperInvariant(); }
    public string Sub { get => SubText.Text; set => SubText.Text = value; }

    public CircularGauge()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildTicks();
        Redraw();
        StartOrbitAnimations();
    }

    private void StartOrbitAnimations()
    {
        // Purely ambient, so it is the first thing to go when the user has asked the system to
        // stop animating. Continuous rotation is exactly what that preference targets.
        if (!SystemParameters.ClientAreaAnimation)
        {
            OrbitRingOuter.Opacity = 0.25;
            OrbitRingInner.Opacity = 0.15;
            return;
        }

        var outer = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(48)) { RepeatBehavior = RepeatBehavior.Forever };
        OrbitOuterRotate.BeginAnimation(RotateTransform.AngleProperty, outer);

        var inner = new DoubleAnimation(360, 0, TimeSpan.FromSeconds(31)) { RepeatBehavior = RepeatBehavior.Forever };
        OrbitInnerRotate.BeginAnimation(RotateTransform.AngleProperty, inner);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        BuildTicks();
        Redraw();
    }

    public void SetValue(double value)
    {
        Value = Math.Clamp(value, 0, 100);

        // Eased on the same curve as the arc. These are one instrument, and the number
        // snapping while the ring around it glided was the most visible mismatch in the app.
        Animate.To(ValueText, Value, "0.0");

        // Recolour from the value itself. This gauge shows thermal HEADROOM, so low is bad --
        // the inverse of the temperature cards. Worth stating explicitly, because otherwise
        // the two look like they disagree about which end is dangerous.
        var brush = HeadroomBrush(Value);
        ProgressPath.Stroke = brush;
        TipDot.Fill = brush;
        if (brush is SolidColorBrush solid)
        {
            ProgressGlow.Color = solid.Color;
            GlowStop.Color = Color.FromArgb(0x26, solid.Color.R, solid.Color.G, solid.Color.B);
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            ArcValue = Value;
            return;
        }

        // Eased sweep from wherever the arc currently sits. Duration sits under the 2s poll
        // interval so a fast-moving reading never queues animations behind itself.
        var sweep = new DoubleAnimation(ArcValue, Value, TimeSpan.FromMilliseconds(750))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(ArcValueProperty, sweep);
    }

    // Headroom: 100 means cool and idle, 0 means out of thermal room.
    private Brush HeadroomBrush(double headroom) => (Brush)FindResource(
        // Amber tier removed: at a 45% headroom threshold this gauge was yellow through the
        // machine's entire normal operating range. See ThermalBrushFor in DashboardView.
        headroom <= 20 ? "DangerBrush"
        : "AccentBrush");

    private void BuildTicks()
    {
        TickCanvas.Children.Clear();

        double size = Math.Min(Root.ActualWidth, Root.ActualHeight);
        if (size <= 0) return;

        double strokeWidth = Math.Max(6, size * 0.055);
        double outer = (size - strokeWidth) / 2.0 - strokeWidth * 0.9;
        var center = new Point(Root.ActualWidth / 2.0, Root.ActualHeight / 2.0);
        var tickBrush = (Brush)FindResource("BorderStrongBrush");

        // Every 5%, with every fourth mark longer: the same convention as a real dial face,
        // and it gives the eye something to measure the arc against.
        for (int i = 0; i < 20; i++)
        {
            bool major = i % 4 == 0;
            double angle = -90 + i * 18.0;
            double inner = outer - (major ? size * 0.045 : size * 0.022);

            var p1 = PointOnCircle(center, inner, angle);
            var p2 = PointOnCircle(center, outer, angle);

            TickCanvas.Children.Add(new Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = tickBrush,
                StrokeThickness = major ? 1.6 : 1,
                Opacity = major ? 0.9 : 0.45,
            });
        }
    }

    /// <summary>
    /// Everything whose size depends on the control's size. Runs on load and on resize, not on
    /// every value change.
    /// </summary>
    private void Redraw()
    {
        double size = Math.Min(Root.ActualWidth, Root.ActualHeight);
        if (size <= 0) return;

        double strokeWidth = Math.Max(6, size * 0.055);
        double radius = (size - strokeWidth) / 2.0;
        var center = new Point(Root.ActualWidth / 2.0, Root.ActualHeight / 2.0);

        GlowEllipse.Width = size * 0.82;
        GlowEllipse.Height = size * 0.82;

        OrbitRingOuter.Width = size * 1.18;
        OrbitRingOuter.Height = size * 1.18;
        OrbitRingInner.Width = size * 1.06;
        OrbitRingInner.Height = size * 1.06;

        TrackEllipse.Width = radius * 2;
        TrackEllipse.Height = radius * 2;
        TrackEllipse.StrokeThickness = strokeWidth;
        TrackEllipse.Margin = new Thickness(center.X - radius, center.Y - radius, 0, 0);
        TrackEllipse.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        TrackEllipse.VerticalAlignment = System.Windows.VerticalAlignment.Top;

        ProgressPath.StrokeThickness = strokeWidth;

        RedrawArc();
    }

    /// <summary>
    /// The part that actually changes as the needle sweeps: the arc, and the dot on its leading
    /// edge.
    ///
    /// Separated from <see cref="Redraw"/> because the value is animated over 750ms and WPF
    /// raises the property-changed callback once per frame. Doing the whole redraw there meant
    /// roughly nineteen times a second writing eight Width/Height/Margin properties whose values
    /// had not changed since the last resize -- each one invalidating layout for a measure and
    /// arrange pass that could only arrive at the same answer.
    ///
    /// The geometry is frozen before it is assigned. It is rebuilt from scratch every frame and
    /// never mutated afterwards, so there is nothing for WPF to gain by tracking it for changes,
    /// and freezing lets it skip the change-notification machinery entirely.
    /// </summary>
    private void RedrawArc()
    {
        double size = Math.Min(Root.ActualWidth, Root.ActualHeight);
        if (size <= 0) return;

        double strokeWidth = Math.Max(6, size * 0.055);
        double radius = (size - strokeWidth) / 2.0;
        var center = new Point(Root.ActualWidth / 2.0, Root.ActualHeight / 2.0);

        var arc = BuildArcGeometry(center, radius, ArcValue);
        arc.Freeze();
        ProgressPath.Data = arc;

        // Park the tip dot on the arc's leading edge.
        double tipDeg = -90 + Math.Clamp(ArcValue / 100.0 * 360.0, 1.0, 359.9);
        var tip = PointOnCircle(center, radius, tipDeg);
        double dot = strokeWidth * 0.62;
        TipDot.Width = dot;
        TipDot.Height = dot;
        TipDot.Margin = new Thickness(tip.X - dot / 2, tip.Y - dot / 2, 0, 0);
    }

    private static Geometry BuildArcGeometry(Point center, double radius, double value)
    {
        double sweepDeg = Math.Clamp(value / 100.0 * 360.0, 1.0, 359.9);
        double startDeg = -90;
        double endDeg = startDeg + sweepDeg;

        Point start = PointOnCircle(center, radius, startDeg);
        Point end = PointOnCircle(center, radius, endDeg);

        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepDeg > 180,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDeg)
    {
        double rad = Math.PI / 180.0 * angleDeg;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }
}
