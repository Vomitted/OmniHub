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
using Rectangle = System.Windows.Shapes.Rectangle;

namespace OmniHub.App.Wpf.Controls;

// The Dashboard's console, built to the reference the user supplied (technical/TECHNICAL-v5-ui.md,
// section 9): a card per component down each side, a dial in the centre.

/// <summary>
/// One component of the machine, as the reference draws it: its colour as a stripe along the outer
/// edge and on its icon, a small category over its name, one large light figure with a raised unit,
/// thin bars in its colour, and two small labelled figures at its foot.
/// </summary>
public sealed class ComponentCard : Border
{
    /// <summary>Line drawings on a 16-unit square, stroked in the component's colour.</summary>
    public static class Icons
    {
        public const string Processor =
            "M4,4 H12 V12 H4 Z M6.5,6.5 H9.5 V9.5 H6.5 Z M6,2 V4 M8,2 V4 M10,2 V4 M6,12 V14 M8,12 V14 M10,12 V14 " +
            "M2,6 H4 M2,8 H4 M2,10 H4 M12,6 H14 M12,8 H14 M12,10 H14";

        public const string Graphics =
            "M1,4 H15 V12 H1 Z M3.5,8 A2.5,2.5 0 1 1 8.5,8 A2.5,2.5 0 1 1 3.5,8 M10.5,6.5 H13 M10.5,9.5 H13 M3,12 V14 M6,12 V14";

        public const string Memory =
            "M1,5 H15 V11 H1 Z M3.5,7 H5.5 V9 H3.5 Z M7,7 H9 V9 H7 Z M10.5,7 H12.5 V9 H10.5 Z M3,11 V13 M6,11 V13 M10,11 V13 M13,11 V13";

        public const string Thermometer =
            "M5.5,9.8 V3 A1.5,1.5 0 0 1 8.5,3 V9.8 A3,3 0 1 1 5.5,9.8 Z M7,6 V11.5 M11,3.5 H14 M11,6 H14 M11,8.5 H13";
    }

    // Faded at both ends, solid through the middle of the card, as the reference draws it. A mask
    // rather than a gradient of the colour, so the stripe stays the shared theme brush and follows a
    // theme switch.
    private static readonly Brush StripeMask = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0x00, 0, 0, 0), 0.18),
        new(Color.FromArgb(0xFF, 0, 0, 0), 0.32),
        new(Color.FromArgb(0xFF, 0, 0, 0), 0.72),
        new(Color.FromArgb(0x00, 0, 0, 0), 0.86),
    }, new Point(0, 0), new Point(0, 1)));

    private static Brush Frozen(Brush brush) { brush.Freeze(); return brush; }

    private readonly TextBlock _category = new();
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _iconSlot = new() { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly Path _icon = new()
    {
        Stretch = Stretch.Uniform, StrokeThickness = 1.5,
        StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };
    private readonly Border _stripe = new() { Width = 3, Opacity = 0.85, OpacityMask = StripeMask };
    private readonly TextBlock _figure = new() { Text = Metrics.Unavailable };
    private readonly TextBlock _unit = new();
    private readonly StackPanel _names = new();
    private readonly StackPanel _reading = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _bars = new();
    private readonly StackPanel[] _stats = { new(), new() };
    private readonly TextBlock[] _statLabels = { new(), new() };
    private readonly TextBlock[] _statValues = { new(), new() };
    private string _colourKey = "AccentBrush";
    private bool _stripeRight;
    private bool _studio;
    private System.Windows.Media.Effects.DropShadowEffect? _stripeGlow;

    /// <summary>
    /// Studio's card (TECHNICAL-v5-ui.md, section 12): wider padding, a larger name, the stripe inside
    /// the card glowing in its component's colour as the reference draws it, and a lift under the
    /// pointer. The console look's card stays as it was.
    /// </summary>
    public bool Studio
    {
        get => _studio;
        set
        {
            if (!value || _studio) return;
            _studio = true;
            Padding = new Thickness(22, 20, 22, 20);
            _title.FontSize = 17;
            _stripeGlow = Glow.Make(blur: 14, opacity: 0.85);
            _stripe.Effect = _stripeGlow;
            PlaceStripe();
            ApplyColour();
            Motion.Lift(this);
        }
    }

    public ComponentCard()
    {
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(CornerRadiusProperty, "RadiusMd");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(18, 16, 18, 16);
        MinHeight = 292;   // taller than wide, as the reference draws them, the figure at the foot

        // The category in the monospace face over the name, as the reference sets "Compute Unit"
        // over "CPU Core".
        _category.SetResourceReference(StyleProperty, "CellNote");
        _category.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _category.FontSize = 10.5;
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "UiFontSemibold");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _title.FontSize = 16;
        _title.Margin = new Thickness(0, 3, 32, 0);

        _names.Children.Add(_category);
        _names.Children.Add(_title);
        _iconSlot.Children.Add(_icon);
        var head = new Grid();
        head.Children.Add(_names);
        head.Children.Add(_iconSlot);

        // Regular weight, not light: the reference's figures are a plain grotesque at size, and a
        // hairline weight read as faint rather than as refined.
        _figure.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        _figure.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _figure.FontWeight = FontWeights.Normal;
        _figure.FontSize = 42;
        _unit.SetResourceReference(StyleProperty, "CellNote");
        _unit.FontSize = 13;
        _unit.VerticalAlignment = VerticalAlignment.Top;
        _unit.Margin = new Thickness(5, 9, 0, 0);
        _reading.Children.Add(_figure);
        _reading.Children.Add(_unit);

        var rule = new Border { Height = 1, Margin = new Thickness(0, 14, 0, 0) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");

        var stats = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        for (int i = 0; i < 2; i++)
        {
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            _statLabels[i].SetResourceReference(StyleProperty, "CapsLabel");
            _statValues[i].SetResourceReference(StyleProperty, "CellFigure");
            _statValues[i].HorizontalAlignment = HorizontalAlignment.Left;
            _statValues[i].FontWeight = FontWeights.SemiBold;
            _statValues[i].Margin = new Thickness(0, 4, 0, 0);
            _stats[i].Children.Add(_statLabels[i]);
            _stats[i].Children.Add(_statValues[i]);
            Grid.SetColumn(_stats[i], i);
            stats.Children.Add(_stats[i]);
        }

        var foot = new StackPanel();
        foot.Children.Add(rule);
        foot.Children.Add(stats);

        var body = new Grid();
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });

        // Out across the padding and the border to the card's own edge, which it replaces there.
        Grid.SetRowSpan(_stripe, 5);
        body.Children.Add(_stripe);
        body.Children.Add(head);
        Grid.SetRow(_reading, 2);
        body.Children.Add(_reading);
        Grid.SetRow(_bars, 3);
        body.Children.Add(_bars);
        Grid.SetRow(foot, 4);
        body.Children.Add(foot);
        Child = body;

        PlaceStripe();
        ApplyColour();
    }

    public string Category { get => _category.Text; set => _category.Text = value; }
    public string Title { get => _title.Text; set => _title.Text = value; }

    /// <summary>The theme brush this component is drawn in: its stripe, its icon and its bars.</summary>
    public string ColourKey
    {
        get => _colourKey;
        set { _colourKey = value; ApplyColour(); }
    }

    /// <summary>The stripe on the right edge, for a card in the right-hand column.</summary>
    public bool StripeRight
    {
        get => _stripeRight;
        set { _stripeRight = value; PlaceStripe(); }
    }

    /// <summary>The icon, as one of <see cref="Icons"/>.</summary>
    public string IconData
    {
        get => _icon.Data?.ToString() ?? "";
        set => _icon.Data = Geometry.Parse(value);
    }

    /// <summary>Something to stand where the icon does -- the cooling card's fan, which turns.</summary>
    public void SetIcon(UIElement element)
    {
        _iconSlot.Children.Clear();
        _iconSlot.Width = _iconSlot.Height = 30;
        _iconSlot.Children.Add(element);
    }

    /// <summary>
    /// Whether the card carries a large figure. The reference's sensor card has none -- its readings
    /// are the badged bars -- and an empty figure line would be a dash waiting for nothing.
    /// </summary>
    public bool HasFigure
    {
        get => _reading.Visibility == Visibility.Visible;
        set => _reading.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Studio's trace (see <see cref="Trace"/>), in the free row between the names and the figure.</summary>
    public void SetTrace(FrameworkElement trace)
    {
        // Against the figure it is the history of, and no taller than a glance needs: stretched over
        // a portrait card's whole free row a two-degree wobble drew as a cliff.
        trace.Margin = new Thickness(0, 14, 0, 12);
        trace.VerticalAlignment = VerticalAlignment.Bottom;
        trace.Height = 110;
        Grid.SetRow(trace, 1);
        ((Grid)Child).Children.Add(trace);
    }

    /// <summary>Adds a bar under the figure and returns it, to be filled with <see cref="ShowBar"/>.</summary>
    /// <param name="badged">The value in a glowing outlined badge, as the reference's sensor readings are.</param>
    public Meter AddBar(string label, bool badged = false)
    {
        var bar = new Meter { Label = label, LabelStyle = "CapsLabel", Badged = badged, Margin = new Thickness(0, 11, 0, 0) };
        _bars.Children.Add(bar);
        return bar;
    }

    public void ShowFigure(double? value, string format, string unit)
    {
        Roll.To(_figure, value, format);
        _unit.Text = unit;
    }

    /// <summary>A bar in the component's colour, or in another's where it is another component's reading.</summary>
    public void ShowBar(Meter bar, string text, double? fraction, string? colourKey = null)
    {
        bar.Show(text, fraction, (Brush)FindResource(colourKey ?? _colourKey));
        if (_stripeGlow is not null) ApplyColour();
    }

    public void ShowStats(string label1, string value1, string label2, string value2)
    {
        _statLabels[0].Text = label1;
        _statValues[0].Text = value1;
        _statLabels[1].Text = label2;
        _statValues[1].Text = value2;
    }

    /// <summary>
    /// The stripe on the card's outer edge, and for a right-hand card the reference's mirror image:
    /// the icon at the left, the names and the figure set to the right, the second figure at the foot
    /// against the right edge.
    /// </summary>
    private void PlaceStripe()
    {
        bool right = _stripeRight;
        _stripe.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        // At the card's own edge, across the padding and the border; Studio's sits inside, 10 px in.
        double outset = _studio ? -12 : -19;
        _stripe.Margin = right ? new Thickness(0, 0, outset, 0) : new Thickness(outset, 0, 0, 0);

        var side = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _iconSlot.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _names.HorizontalAlignment = side;
        _category.HorizontalAlignment = side;
        _title.HorizontalAlignment = side;
        _title.Margin = right ? new Thickness(32, 3, 0, 0) : new Thickness(0, 3, 32, 0);
        _reading.HorizontalAlignment = side;

        _stats[1].HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _statLabels[1].HorizontalAlignment = _stats[1].HorizontalAlignment;
        _statValues[1].HorizontalAlignment = _stats[1].HorizontalAlignment;
    }

    private void ApplyColour()
    {
        _stripe.SetResourceReference(BackgroundProperty, _colourKey);
        _icon.SetResourceReference(Shape.StrokeProperty, _colourKey);

        // Built from the brush as it is now; ShowBar calls this on every reading, so a theme switch
        // reaches the glow on the next one.
        if (_stripeGlow is not null && TryFindResource(_colourKey) is Brush brush) _stripeGlow.Color = Glow.Of(brush);
    }
}

/// <summary>
/// The centre of the Dashboard, drawn as the reference draws its dial: a ring round almost the whole
/// circle with a notch at the top, a ring of short dashes inside it, a plate carrying one bold figure
/// with a smaller decimal, a status under the figure, and two readouts in a box across the ring's
/// foot -- on a field of squares turned to three angles, one dashed, one tinted.
///
/// The reference puts a composite "System Index" here. This application does not show a number the
/// hardware did not give, so the ring carries a real reading against its real scale; the caller
/// chooses which (the die temperature, on the Dashboard). The stretch past the reading's warning
/// threshold is a thin line just outside the ring, a redline, so how near it is to trouble is drawn
/// before it gets there without breaking the ring the reference draws whole.
/// </summary>
public sealed class HeroDial : Grid
{
    // The field the lattice is drawn on and the ring centred in it; the readout box sits in the same
    // coordinates, across the ring's foot. Proportions measured off the reference: a stroke about a
    // fortieth of the ring, a notch of a few degrees.
    // 440 rather than the 520 first used: in Studio's one-row arrangement the dial's field sets the
    // row's height, and at 440 the cards come out 0.65 wide to tall, the reference's proportion.
    private const double Field = 440, Size = 320, Stroke = 8;
    private const double Notch = 3, Sweep = 360 - 2 * Notch;   // degrees; the gap is at twelve o'clock

    private readonly Canvas _ring = new() { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _track = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Path _zone = new() { StrokeThickness = 2, Opacity = 0.9 };
    private readonly Path _value = new() { StrokeThickness = Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly System.Windows.Media.Effects.DropShadowEffect _glow = Glow.Make(blur: 26, opacity: 0.75);

    private readonly TextBlock _label = new();
    private readonly TextBlock _whole = new();
    private readonly TextBlock _fraction = new();
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
    private readonly TextBlock _status = new();
    private readonly TextBlock[] _chipLabels = { new(), new() };
    private readonly TextBlock[] _chipValues = { new(), new() };
    private double? _warnFrom;
    private double _target;
    private readonly List<(RotateTransform Turn, double From, double Degrees, int PeriodMs)> _turning = new();
    private bool _alive;
    private string _unit = "";
    private string _format = "0.0";

    private static readonly DependencyProperty DrawnProperty = DependencyProperty.Register(
        nameof(Drawn), typeof(double), typeof(HeroDial), new PropertyMetadata(0.0, (d, _) => ((HeroDial)d).Redraw()));

    private static readonly DependencyProperty FigureProperty = DependencyProperty.Register(
        nameof(Figure), typeof(double), typeof(HeroDial), new PropertyMetadata(double.NaN, (d, _) => ((HeroDial)d).WriteFigure()));

    private double Drawn { get => (double)GetValue(DrawnProperty); set => SetValue(DrawnProperty, value); }
    private double Figure { get => (double)GetValue(FigureProperty); set => SetValue(FigureProperty, value); }

    public HeroDial()
    {
        var stage = new Grid { Width = Field, Height = Field, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stage.Children.Add(Lattice());

        _track.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        _zone.SetResourceReference(Shape.StrokeProperty, "WarnBrush");
        _value.Effect = _glow;
        _ring.Children.Add(_track);
        _ring.Children.Add(_zone);
        _ring.Children.Add(Dashes());
        _ring.Children.Add(_value);
        stage.Children.Add(_ring);

        // The plate: the figure, what it is, and its status.
        _label.SetResourceReference(StyleProperty, "CapsLabel");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _label.HorizontalAlignment = HorizontalAlignment.Center;

        foreach (var part in new[] { _whole, _fraction })
        {
            part.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
            part.FontWeight = FontWeights.Bold;
            part.VerticalAlignment = VerticalAlignment.Bottom;
        }
        _whole.FontSize = 72;
        _whole.Text = Metrics.Unavailable;
        _fraction.FontSize = 28;
        _fraction.Margin = new Thickness(1, 0, 0, 12);
        _fraction.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        var figure = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, -6) };
        figure.Children.Add(_whole);
        figure.Children.Add(_fraction);

        _status.SetResourceReference(StyleProperty, "CapsLabel");
        _status.FontWeight = FontWeights.Bold;
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        status.Children.Add(_dot);
        status.Children.Add(_status);

        var face = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(_label);
        face.Children.Add(figure);
        face.Children.Add(status);

        var plate = new Border
        {
            Width = Size * 0.7, Height = Size * 0.62, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14), Child = face,
        };
        plate.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        plate.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        stage.Children.Add(plate);

        // The readouts, boxed across the ring's foot.
        var chips = new Grid();
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 2; i++)
        {
            _chipLabels[i].SetResourceReference(StyleProperty, "CapsLabel");
            _chipLabels[i].HorizontalAlignment = HorizontalAlignment.Center;
            _chipValues[i].SetResourceReference(StyleProperty, "CellFigure");
            _chipValues[i].HorizontalAlignment = HorizontalAlignment.Center;
            _chipValues[i].FontSize = 14;
            _chipValues[i].FontWeight = FontWeights.Bold;
            _chipValues[i].Margin = new Thickness(0, 4, 0, 0);
            var chip = new StackPanel { Margin = new Thickness(18, 0, 18, 0) };
            chip.Children.Add(_chipLabels[i]);
            chip.Children.Add(_chipValues[i]);
            Grid.SetColumn(chip, i * 2);
            chips.Children.Add(chip);
        }
        var divider = new Border { Margin = new Thickness(0, 2, 0, 2) };
        divider.SetResourceReference(Border.BackgroundProperty, "BorderStrongBrush");
        Grid.SetColumn(divider, 1);
        chips.Children.Add(divider);

        var box = new Border
        {
            // Across the ring's foot with the ring still showing under it, as the reference places it.
            Child = chips, Width = Size * 0.84, BorderThickness = new Thickness(1), Padding = new Thickness(4, 10, 4, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Field / 2 + Size * 0.31, 0, 0), CornerRadius = new CornerRadius(12),
        };
        box.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        stage.Children.Add(box);

        Children.Add(stage);
        _track.Data = Ring(0, 1);
        Redraw();
    }

    /// <summary>What the dial is, set in spaced capitals: "DIE TEMPERATURE".</summary>
    public string Label
    {
        get => _label.Text;
        set
        {
            // Spaced with thin spaces, the nearest WPF comes to tracking; the plain words are what a
            // screen reader is given.
            _label.Text = string.Join(' ', value.ToCharArray());
            System.Windows.Automation.AutomationProperties.SetName(_label, value);
        }
    }

    /// <summary>A reading against its scale. A null fraction draws the ring's track alone.</summary>
    public void Show(double? fraction, double? value, string format, string unit, Brush arc, Brush figure, double? warnFrom)
    {
        _format = format;
        _target = fraction ?? 0;
        _unit = unit;
        _whole.Foreground = figure;
        _value.Stroke = arc;
        _glow.Color = Glow.Of(arc);

        if (warnFrom != _warnFrom)
        {
            _warnFrom = warnFrom;
            _zone.Data = warnFrom is { } w ? Ring(w, 1, outset: Stroke / 2 + 5) : System.Windows.Media.Geometry.Empty;
        }

        bool animate = IsVisible && OmniHub.App.Wpf.Controls.Motion.On;
        Ease(DrawnProperty, fraction ?? 0, animate, 220);

        if (value is not { } v || !double.IsFinite(v))
        {
            BeginAnimation(FigureProperty, null);
            Figure = double.NaN;
            WriteFigure();
        }
        else Ease(FigureProperty, v, animate && double.IsFinite(Figure), 240);
    }

    /// <summary>A word under the figure; the colour is on the dot, and on the word only when all is well.</summary>
    public void ShowStatus(string word, Brush dot, bool nominal)
    {
        _status.Text = word;
        _dot.Fill = dot;
        if (nominal) _status.Foreground = dot;
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
    }

    public void ShowChips(string label1, string value1, string label2, string value2)
    {
        _chipLabels[0].Text = label1;
        _chipValues[0].Text = value1;
        _chipLabels[1].Text = label2;
        _chipValues[1].Text = value2;
    }

    private void Ease(DependencyProperty property, double to, bool animate, int ms)
    {
        if (animate)
            BeginAnimation(property, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
        else
        {
            BeginAnimation(property, null);
            SetValue(property, to);
        }
    }

    /// <summary>"82" large and ".9 °C" small beside it, as the reference sets "98" and ".4".</summary>
    private void WriteFigure()
    {
        double v = Figure;
        if (!double.IsFinite(v))
        {
            _whole.Text = Metrics.Unavailable;
            _fraction.Text = "";
            return;
        }

        string text = v.ToString(_format, System.Globalization.CultureInfo.InvariantCulture);
        int dot = text.IndexOf('.');
        _whole.Text = dot < 0 ? text : text[..dot];
        _fraction.Text = (dot < 0 ? "" : text[dot..]) + (_unit.Length > 0 ? " " + _unit : "");
    }

    /// <summary>
    /// The ring sweeping from its notch to the reading it holds, as the dial enters. It ends at the
    /// measured value; the figure is not counted up, so no temperature is shown that was not read.
    /// </summary>
    public void SweepIn(int delayMs)
    {
        if (!Motion.On) return;
        var sweep = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(delayMs + 480) };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(_target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + 480)),
                                                     new CubicEase { EasingMode = EasingMode.EaseOut }));
        BeginAnimation(DrawnProperty, sweep);   // held at its end; the next reading eases on from there
    }

    /// <summary>
    /// Studio's dial is alive while it is on screen: the ring's glow breathes and the squares behind it
    /// turn, slowly, in opposite directions. Nothing here moves with a reading.
    /// </summary>
    public bool Alive
    {
        get => _alive;
        set
        {
            if (!value || _alive) return;
            _alive = true;
            Motion.Loop(this, _glow, System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, 0.9, 0.45, 2600, autoReverse: true);
            foreach (var (turn, from, degrees, period) in _turning)
                Motion.Loop(this, turn, RotateTransform.AngleProperty, from, from + degrees, period, autoReverse: false);
        }
    }

    private void Redraw() => _value.Data = Ring(0, Drawn);

    /// <summary>
    /// The stretch of the ring between two fractions of its sweep, clockwise from the notch;
    /// <paramref name="outset"/> draws it that far outside the ring instead.
    /// </summary>
    private static Geometry Ring(double from, double to, double outset = 0)
    {
        from = Math.Clamp(from, 0, 1);
        to = Math.Clamp(to, 0, 1);
        if (to - from <= 0.001) return System.Windows.Media.Geometry.Empty;

        double r = Size / 2 - Stroke / 2 - 1 + outset, c = Size / 2;
        Point At(double f)
        {
            double deg = -90 + Notch + Sweep * f;
            double rad = deg * Math.PI / 180;
            return new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
        }

        double sweep = Sweep * (to - from);
        var figure = new PathFigure { StartPoint = At(from), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(to), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>The ring of short dashes inside the dial, in the second accent.</summary>
    private static UIElement Dashes()
    {
        double inner = Size * 0.82;   // wider than the plate, so the dashes show at its sides
        var dashes = new Ellipse
        {
            Width = inner, Height = inner, StrokeThickness = 2, Opacity = 0.8,
            StrokeDashArray = new DoubleCollection { 2, 9 }, StrokeDashCap = PenLineCap.Round,
        };
        dashes.SetResourceReference(Shape.StrokeProperty, "AccentBrush2");
        Canvas.SetLeft(dashes, (Size - inner) / 2);
        Canvas.SetTop(dashes, (Size - inner) / 2);
        return dashes;
    }

    /// <summary>
    /// Squares turned to three angles behind the dial: the largest on its corner and faint, one dashed,
    /// one tinted with the second accent. It carries no reading; it is the one thing on the page that
    /// is only there to be looked at, which is what the user asked the page to have.
    /// </summary>
    private UIElement Lattice()
    {
        var canvas = new Canvas { Width = Field, Height = Field, IsHitTestVisible = false };
        // Sides and angles measured off the reference, as multiples of the ring; faint enough that the
        // ring stays the only bright thing in the field.
        foreach (var (scale, angle, dashed, tint, opacity) in new[]
                 {
                     (1.16, 45.0, false, "BorderStrongBrush", 0.5),
                     (1.26, 28.0, true, "BorderStrongBrush", 0.45),
                     (1.00, 56.0, false, "AccentBrush2", 0.12),
                 })
        {
            double side = Size * scale;
            var square = new Rectangle
            {
                Width = side, Height = side, StrokeThickness = 1, Opacity = opacity, RadiusX = 6, RadiusY = 6,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle),
            };
            if (dashed) square.StrokeDashArray = new DoubleCollection { 4, 5 };
            square.SetResourceReference(Shape.StrokeProperty, tint);
            Canvas.SetLeft(square, (Field - side) / 2);
            Canvas.SetTop(square, (Field - side) / 2);
            canvas.Children.Add(square);

            // The dashed and the tinted squares turn when the dial is alive, the one against the other,
            // one turn in two minutes and in two minutes forty; the large one stays as the frame.
            if (dashed) _turning.Add(((RotateTransform)square.RenderTransform, angle, 360, 120_000));
            else if (tint == "AccentBrush2") _turning.Add(((RotateTransform)square.RenderTransform, angle, -360, 160_000));
        }
        return canvas;
    }
}
