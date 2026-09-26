// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    }

    // Faded at both ends, strongest through the lower two thirds, as the reference draws it. A mask
    // rather than a gradient of the colour, so the stripe stays the shared theme brush and follows a
    // theme switch.
    private static readonly Brush StripeMask = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0x00, 0, 0, 0), 0.15),
        new(Color.FromArgb(0xFF, 0, 0, 0), 0.45),
        new(Color.FromArgb(0xFF, 0, 0, 0), 0.9),
        new(Color.FromArgb(0x00, 0, 0, 0), 1.0),
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
    private readonly StackPanel _bars = new();
    private readonly TextBlock[] _statLabels = { new(), new() };
    private readonly TextBlock[] _statValues = { new(), new() };
    private string _colourKey = "AccentBrush";
    private bool _stripeRight;

    public ComponentCard()
    {
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        SetResourceReference(CornerRadiusProperty, "RadiusMd");
        BorderThickness = new Thickness(1);
        Padding = new Thickness(18, 16, 18, 16);
        MinHeight = 236;

        _category.SetResourceReference(StyleProperty, "CellNote");
        _title.SetResourceReference(TextBlock.FontFamilyProperty, "UiFontSemibold");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _title.FontSize = 16;
        _title.Margin = new Thickness(0, 2, 32, 0);

        var names = new StackPanel();
        names.Children.Add(_category);
        names.Children.Add(_title);
        _iconSlot.Children.Add(_icon);
        var head = new Grid();
        head.Children.Add(names);
        head.Children.Add(_iconSlot);

        _figure.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        _figure.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _figure.FontWeight = FontWeights.Light;
        _figure.FontSize = 40;
        _unit.SetResourceReference(StyleProperty, "CellNote");
        _unit.FontSize = 13;
        _unit.VerticalAlignment = VerticalAlignment.Top;
        _unit.Margin = new Thickness(4, 9, 0, 0);
        var reading = new StackPanel { Orientation = Orientation.Horizontal };
        reading.Children.Add(_figure);
        reading.Children.Add(_unit);

        var stats = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        for (int i = 0; i < 2; i++)
        {
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            _statLabels[i].SetResourceReference(StyleProperty, "CapsLabel");
            _statValues[i].SetResourceReference(StyleProperty, "CellFigure");
            _statValues[i].HorizontalAlignment = HorizontalAlignment.Left;
            _statValues[i].Margin = new Thickness(0, 3, 0, 0);
            var stat = new StackPanel();
            stat.Children.Add(_statLabels[i]);
            stat.Children.Add(_statValues[i]);
            Grid.SetColumn(stat, i);
            stats.Children.Add(stat);
        }

        var body = new Grid();
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            body.RowDefinitions.Add(new RowDefinition { Height = height });

        // Out across the padding and the border to the card's own edge, which it replaces there.
        Grid.SetRowSpan(_stripe, 5);
        body.Children.Add(_stripe);
        body.Children.Add(head);
        Grid.SetRow(reading, 2);
        body.Children.Add(reading);
        Grid.SetRow(_bars, 3);
        body.Children.Add(_bars);
        Grid.SetRow(stats, 4);
        body.Children.Add(stats);
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

    /// <summary>Adds a bar under the figure and returns it, to be filled with <see cref="ShowBar"/>.</summary>
    public Meter AddBar(string label)
    {
        var bar = new Meter { Label = label, Margin = new Thickness(0, 10, 0, 0) };
        _bars.Children.Add(bar);
        return bar;
    }

    public void ShowFigure(double? value, string format, string unit)
    {
        Roll.To(_figure, value, format);
        _unit.Text = unit;
    }

    /// <summary>A bar in the component's colour, or in another's where it is another component's reading.</summary>
    public void ShowBar(Meter bar, string text, double? fraction, string? colourKey = null) =>
        bar.Show(text, fraction, (Brush)FindResource(colourKey ?? _colourKey));

    public void ShowStats(string label1, string value1, string label2, string value2)
    {
        _statLabels[0].Text = label1;
        _statValues[0].Text = value1;
        _statLabels[1].Text = label2;
        _statValues[1].Text = value2;
    }

    private void PlaceStripe()
    {
        _stripe.HorizontalAlignment = _stripeRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _stripe.Margin = _stripeRight ? new Thickness(0, 0, -19, 0) : new Thickness(-19, 0, 0, 0);
    }

    private void ApplyColour()
    {
        _stripe.SetResourceReference(BackgroundProperty, _colourKey);
        _icon.SetResourceReference(Shape.StrokeProperty, _colourKey);
    }
}

/// <summary>
/// The centre of the Dashboard: one reading as a large dial on a field of faint turned squares, its
/// figure on a plate in the middle, a status under it, and two readouts boxed under the dial's open
/// foot.
///
/// The reference puts a composite "System Index" here. This application does not show a number the
/// hardware did not give, so the dial carries a real reading against its real scale; the caller
/// chooses which (the die temperature, on the Dashboard).
/// </summary>
public sealed class HeroDial : Grid
{
    // The field the lattice is drawn on, and the dial centred in it. The readout box is placed in the
    // same coordinates, under the arc's open foot: the arc ends at 150 and 30 degrees, 56 units below
    // the centre, so the box starts clear of the stroke's rounded ends.
    private const double Field = 380, Size = 236, Stroke = 12, BoxTop = Field / 2 + 72;

    private readonly ArcDial _dial = new(Size, Stroke, 22);
    private readonly TextBlock _label = new();
    private readonly TextBlock _figure = new() { Text = Metrics.Unavailable };
    private readonly TextBlock _unit = new();
    private readonly Ellipse _dot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _status = new();
    private readonly TextBlock[] _chipLabels = { new(), new() };
    private readonly TextBlock[] _chipValues = { new(), new() };

    public HeroDial()
    {
        var stage = new Grid { Width = Field, Height = Field, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stage.Children.Add(Lattice());
        _dial.VerticalAlignment = VerticalAlignment.Center;
        stage.Children.Add(_dial);

        _label.SetResourceReference(StyleProperty, "CapsLabel");
        _label.HorizontalAlignment = HorizontalAlignment.Center;

        _figure.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        _figure.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _figure.FontWeight = FontWeights.Light;
        _figure.FontSize = 54;
        _unit.SetResourceReference(StyleProperty, "CellNote");
        _unit.FontSize = 15;
        _unit.VerticalAlignment = VerticalAlignment.Top;
        _unit.Margin = new Thickness(3, 12, 0, 0);
        var reading = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, -4) };
        reading.Children.Add(_figure);
        reading.Children.Add(_unit);

        _status.SetResourceReference(StyleProperty, "CapsLabel");
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        status.Children.Add(_dot);
        status.Children.Add(_status);

        var faceText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        faceText.Children.Add(_label);
        faceText.Children.Add(reading);
        faceText.Children.Add(status);

        // The plate the reference sets its figure on: a shade off the panel, inside the ring.
        var plate = new Border
        {
            Width = 150, Height = 128, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = faceText,
        };
        plate.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        plate.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        stage.Children.Add(plate);

        var chips = new Grid();
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        chips.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 2; i++)
        {
            _chipLabels[i].SetResourceReference(StyleProperty, "CapsLabel");
            _chipLabels[i].HorizontalAlignment = HorizontalAlignment.Center;
            _chipValues[i].SetResourceReference(StyleProperty, "CellFigure");
            _chipValues[i].HorizontalAlignment = HorizontalAlignment.Center;
            _chipValues[i].FontSize = 13;
            _chipValues[i].Margin = new Thickness(0, 3, 0, 0);
            var chip = new StackPanel { Margin = new Thickness(16, 0, 16, 0), MinWidth = 84 };
            chip.Children.Add(_chipLabels[i]);
            chip.Children.Add(_chipValues[i]);
            Grid.SetColumn(chip, i * 2);
            chips.Children.Add(chip);
        }
        var divider = new Border();
        divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        Grid.SetColumn(divider, 1);
        chips.Children.Add(divider);

        var box = new Border
        {
            Child = chips, BorderThickness = new Thickness(1), Padding = new Thickness(4, 8, 4, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, BoxTop, 0, 0),
        };
        box.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        box.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");
        stage.Children.Add(box);

        Children.Add(stage);
    }

    /// <summary>What the dial is: "DIE TEMPERATURE".</summary>
    public string Label { get => _label.Text; set => _label.Text = value; }

    /// <summary>A reading against its scale. A null fraction draws the ring's track alone.</summary>
    public void Show(double? fraction, double? value, string format, string unit, Brush arc, Brush figure, double? warnFrom)
    {
        Roll.To(_figure, value, format);
        _figure.Foreground = figure;
        _unit.Text = unit;
        _dial.Show(fraction, arc, warnFrom);
    }

    /// <summary>A word under the figure, with its colour on the dot beside it and never on the word.</summary>
    public void ShowStatus(string word, Brush dot)
    {
        _status.Text = word;
        _dot.Fill = dot;
    }

    public void ShowChips(string label1, string value1, string label2, string value2)
    {
        _chipLabels[0].Text = label1;
        _chipValues[0].Text = value1;
        _chipLabels[1].Text = label2;
        _chipValues[1].Text = value2;
    }

    /// <summary>
    /// Three squares turned on their corners and nested, fading outwards: the reference's geometry
    /// behind the dial. It carries no reading, and it is the one thing on the page that is only there
    /// to be looked at -- which is what the user asked the page to have.
    /// </summary>
    private static UIElement Lattice()
    {
        var canvas = new Canvas { Width = Field, Height = Field, IsHitTestVisible = false };
        for (int i = 0; i < 3; i++)
        {
            double side = 176 + i * 62;
            var square = new Rectangle
            {
                Width = side, Height = side, StrokeThickness = 1, Opacity = 0.9 - i * 0.28,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
            };
            square.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");
            Canvas.SetLeft(square, (Field - side) / 2);
            Canvas.SetTop(square, (Field - side) / 2);
            canvas.Children.Add(square);
        }
        return canvas;
    }
}
