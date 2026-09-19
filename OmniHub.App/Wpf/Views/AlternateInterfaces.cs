// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// What every interface that is not Workspaces has in common.
///
/// Three things, and the first is here because getting it wrong shipped a trap. An interface that
/// replaces the whole window also replaces the sidebar, and the sidebar is the only route to
/// Settings, so every one of these MUST carry its own way out. Putting that on the base class
/// rather than trusting each view to remember is the difference between a convention and a
/// guarantee.
///
/// The second is the metric subscription, which has to be released: these come and go as the user
/// switches between them, and a view that forgets to unsubscribe keeps being called for the life
/// of the process.
///
/// The third is resource lookup. All of these are built in code rather than XAML, so the palette
/// is reached by key, and a missing key has to degrade to something visible rather than throwing
/// inside a constructor running on the UI thread.
/// </summary>
public abstract class AlternateInterface : UserControl, IDisposable
{
    protected readonly HardwareContext Ctx;
    protected readonly MetricSource Metrics;
    private readonly Action? _leave;

    protected AlternateInterface(HardwareContext ctx, MetricSource metrics, Action? leave)
    {
        Ctx = ctx;
        Metrics = metrics;
        _leave = leave;
    }

    /// <summary>Call at the end of a subclass constructor, once its fields are ready.</summary>
    protected void Start()
    {
        Content = Build();
        Metrics.Updated += Tick;
        Tick();
    }

    protected abstract UIElement Build();
    protected abstract void Tick();

    protected Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    protected FontFamily Font(string key) => TryFindResource(key) as FontFamily ?? new FontFamily("Consolas");

    /// <summary>
    /// The way back to the sidebar interface.
    ///
    /// Visible, not a keyboard shortcut. A hidden escape from a full-window mode is still a trap
    /// for anybody who does not already know it exists, which is exactly how the first of these
    /// shipped: choosing it hid the sidebar, the sidebar was the only route to Settings, and the
    /// only way back was editing settings.json by hand.
    /// </summary>
    protected Button Escape()
    {
        var b = new Button
        {
            Content = "WORKSPACES",
            Height = 26,
            Padding = new Thickness(11, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("FlatButtonStyle") as Style,
            ToolTip = "Back to the sidebar interface. Also in Settings, Interface.",
        };
        b.Click += (_, _) => _leave?.Invoke();

        // Hidden when there is nowhere to go. These same views are also offered as panels inside
        // a workspace, where they are not covering the navigation and so are not trapping anybody;
        // a button that does nothing would be worse than no button.
        if (_leave is null) b.Visibility = Visibility.Collapsed;
        return b;
    }

    /// <summary>A metric's current value formatted as it should be shown, or null when unread.</summary>
    protected string? Shown(MetricDefinition m) =>
        Metrics.Value(m.Key)?.ToString(m.Format, CultureInfo.InvariantCulture);

    /// <summary>
    /// The colour a figure should take at its current value.
    ///
    /// On the figure only. Threshold colouring of a numeric readout is data visualisation; the
    /// same colour on the label beside it would be decoration pretending to be a warning.
    /// </summary>
    protected Brush FigureBrush(MetricDefinition m)
    {
        double? v = Metrics.Value(m.Key);
        if (v is null) return Brush("TextFaintBrush");
        if (m.HotAt is { } hot && v >= hot) return Brush("DangerBrush");
        if (m.WarnAt is { } warn && v >= warn) return Brush("WarnBrush");
        return Brush("TextPrimaryBrush");
    }

    protected static MetricDefinition? Metric(string key) =>
        OmniHub.Core.Telemetry.Metrics.All.FirstOrDefault(m => m.Key == key);

    /// <summary>
    /// What counts as full for this reading on this machine.
    ///
    /// The rule itself lives in Core, where it can be tested, and where two interfaces needing the
    /// same answer cannot drift apart. The only per-machine part is the fan's top speed, which
    /// comes from the board's measured band.
    /// </summary>
    protected double? Ceiling(MetricDefinition m) =>
        OmniHub.Core.Telemetry.Metrics.FullScale(m, Ctx.FanBackend.Calibration.MaxRpm);

    public virtual void Dispose() => Metrics.Updated -= Tick;
}

// =====================================================================================
//  COCKPIT
// =====================================================================================

/// <summary>
/// An instrument cluster: one large arc for the temperature that matters, two flanking it, and a
/// quiet strip of figures underneath.
///
/// The tone is a car or aircraft cluster rather than a control panel, and the defining choice is
/// what gets the middle of the screen. A cluster does not give equal space to everything it knows;
/// it gives the middle to the number you would brake for. Here that is the die temperature,
/// because on this machine every other reading is downstream of it: the clocks fall because of it,
/// the fans rise because of it, and the binding limit is usually it.
/// </summary>
public sealed class CockpitView : AlternateInterface
{
    private readonly Dictionary<string, (Path Arc, TextBlock Value, double Diameter)> _dials = new();
    private readonly List<(MetricDefinition Metric, TextBlock Value)> _strip = new();
    private readonly TextBlock _limit = new();

    public CockpitView(HardwareContext ctx, MetricSource metrics, Action? leave = null)
        : base(ctx, metrics, leave) => Start();

    protected override UIElement Build()
    {
        var page = new Grid { Margin = new Thickness(26, 16, 26, 20) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Label("OMNIHUB", Font("MonoFont"), 12, FontWeights.Bold, Brush("TextPrimaryBrush")));
        name.Children.Add(Label("     " + Ctx.Model.Product.Trim(), Font("UiFont"), 11.5, FontWeights.Normal, Brush("TextFaintBrush")));
        Grid.SetColumn(name, 0);
        top.Children.Add(name);

        var esc = Escape();
        Grid.SetColumn(esc, 1);
        top.Children.Add(esc);
        Grid.SetRow(top, 0);
        page.Children.Add(top);

        var cluster = new Grid { VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 3; i++)
            cluster.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Add(cluster, 0, Dial("fan", 100, 28));
        Add(cluster, 1, Dial("cpu", 164, 48));
        Add(cluster, 2, Dial("gpu", 100, 28));

        Grid.SetRow(cluster, 1);
        page.Children.Add(cluster);

        // Everything a cluster knows but would not put in the middle. One weight, evenly spaced,
        // deliberately unremarkable: if these competed with the dials there would be no cluster,
        // only a grid with three big cells in it.
        var strip = new Grid { Margin = new Thickness(0, 18, 0, 14) };
        string[] keys = { "pkg", "cpuclk", "cpuload", "mem" };
        for (int i = 0; i < keys.Length; i++)
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < keys.Length; i++)
            if (Metric(keys[i]) is { } m)
                Add(strip, i, StripCell(m));

        Grid.SetRow(strip, 2);
        page.Children.Add(strip);

        _limit.FontFamily = Font("UiFont");
        _limit.FontSize = 12;
        _limit.Foreground = Brush("TextMutedBrush");
        _limit.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(_limit, 3);
        page.Children.Add(_limit);

        return page;
    }

    private static void Add(Grid g, int column, UIElement child)
    {
        Grid.SetColumn(child, column);
        g.Children.Add(child);
    }

    private static TextBlock Label(string text, FontFamily family, double size, FontWeight weight, Brush brush) => new()
    {
        Text = text,
        FontFamily = family,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>One dial: a track arc, a value arc over it, and the figure in the middle.</summary>
    private UIElement Dial(string key, double diameter, double figureSize)
    {
        var m = Metric(key);
        var host = new Grid { Width = diameter, Height = diameter + 24, HorizontalAlignment = HorizontalAlignment.Center };

        var canvas = new Canvas { Width = diameter, Height = diameter, VerticalAlignment = VerticalAlignment.Top };
        canvas.Children.Add(ArcPath(diameter, 1.0, Brush("TrackBrush"), 7));   // the full sweep, so the value reads as a proportion
        var value = ArcPath(diameter, 0.0, Brush("AccentGradientBrush"), 7);
        canvas.Children.Add(value);
        host.Children.Add(canvas);

        var figure = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = figureSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 20),
        };
        host.Children.Add(figure);

        host.Children.Add(new TextBlock
        {
            Text = m?.Label ?? key,
            FontFamily = Font("UiFont"),
            FontSize = 11,
            Foreground = Brush("TextFaintBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        });

        if (m is not null) _dials[key] = (value, figure, diameter);
        return host;
    }

    internal static Path ArcPath(double diameter, double fraction, Brush stroke, double thickness) => new()
    {
        Stroke = stroke,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Data = ArcGeometry(diameter, fraction),
    };

    /// <summary>
    /// A 240 degree arc, open at the bottom, filled to <paramref name="fraction"/>.
    ///
    /// Open at the bottom because a closed ring has no start, and a gauge whose zero is ambiguous
    /// is decoration. This starts bottom left and sweeps clockwise, the convention every physical
    /// instrument it is imitating already uses.
    /// </summary>
    internal static Geometry ArcGeometry(double diameter, double fraction)
    {
        const double startDeg = 150, totalDeg = 240;
        double r = (diameter / 2) - 5;
        double cx = diameter / 2, cy = diameter / 2;

        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.001) return Geometry.Empty;

        double sweep = totalDeg * fraction;
        Point At(double deg) => new(
            cx + r * Math.Cos(deg * Math.PI / 180),
            cy + r * Math.Sin(deg * Math.PI / 180));

        var figure = new PathFigure { StartPoint = At(startDeg), IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            At(startDeg + sweep), new Size(r, r), 0,
            isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true));

        var g = new PathGeometry();
        g.Figures.Add(figure);
        return g;
    }

    private UIElement StripCell(MetricDefinition m)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        var value = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stack.Children.Add(value);
        stack.Children.Add(new TextBlock
        {
            Text = m.Label,
            FontFamily = Font("UiFont"),
            FontSize = 10.5,
            Foreground = Brush("TextFaintBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 0),
        });

        _strip.Add((m, value));
        return stack;
    }

    protected override void Tick()
    {
        foreach ((string key, (Path arc, TextBlock figure, double diameter)) in _dials)
        {
            if (Metric(key) is not { } m) continue;

            double? v = Metrics.Value(key);
            figure.Text = v?.ToString(m.Format, CultureInfo.InvariantCulture) ?? "--";
            figure.Foreground = FigureBrush(m);

            // The fraction is against the metric's OWN hot point where it has one, so a full arc
            // means the same thing on every dial: as bad as this reading gets. A shared scale
            // would make 46% fan and 90 C look equally serious.
            arc.Data = v is null || Ceiling(m) is not { } ceiling
                ? Geometry.Empty
                : ArcGeometry(diameter, v.Value / ceiling);
        }

        foreach ((MetricDefinition m, TextBlock value) in _strip)
        {
            string? shown = Shown(m);
            value.Text = shown ?? "unavailable";
            value.FontSize = shown is null ? 11 : 19;
            value.Foreground = FigureBrush(m);
        }

        _limit.Text = Metrics.BindingLimitName is { } b
            ? $"Limited by {b}."
            : "Nothing is limiting the processor.";
    }
}

/// <summary>
/// The cockpit's cluster, compacted into a band that sits above the navigation.
///
/// This is what makes Cockpit an interface for the whole application rather than one more screen.
/// The cluster stays pinned across the top while the pages change underneath it, so opening the
/// fan curve does not mean losing sight of the temperature that sent you there. The first attempt
/// got this wrong: it made the cluster a destination, which meant choosing it gave up every other
/// part of the program.
/// </summary>
public sealed class CockpitBand : AlternateInterface
{
    private readonly Dictionary<string, (Path Arc, TextBlock Value, double Diameter)> _dials = new();
    private readonly List<(MetricDefinition Metric, TextBlock Value)> _figures = new();
    private readonly TextBlock _limit = new();

    public CockpitBand(HardwareContext ctx, MetricSource metrics) : base(ctx, metrics, null) => Start();

    protected override UIElement Build()
    {
        // A grid rather than a horizontal stack. Stacked, everything bunched into the left half of
        // an eleven-hundred pixel window and the remaining six hundred pixels sat empty with one
        // sentence floating in them. A band across the top of the screen should use the top of the
        // screen, and the space it was wasting is exactly where more readings belong.
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // dials
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // figures
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });            // what is limiting

        var dials = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        dials.Children.Add(Dial("cpu", 84, 23));
        dials.Children.Add(Dial("fan", 68, 17));
        dials.Children.Add(Dial("gpu", 68, 17));
        Grid.SetColumn(dials, 0);
        row.Children.Add(dials);

        // Six rather than four, spread evenly instead of packed. The two GPU figures were already
        // being read every tick and were simply not being shown.
        var figures = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 18, 0) };
        string[] keys = { "pkg", "cpuclk", "cpuload", "mem", "gpuw", "gpuclk" };
        for (int i = 0; i < keys.Length; i++)
            figures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < keys.Length; i++)
        {
            if (Metric(keys[i]) is not { } m) continue;
            var cell = Figure(m);
            Grid.SetColumn(cell, i);
            figures.Children.Add(cell);
        }
        Grid.SetColumn(figures, 1);
        row.Children.Add(figures);

        // A chip at the right edge, not a sentence adrift in the middle. It is the one line that
        // explains the others, so it gets an edge of its own rather than being mistaken for a
        // caption on whichever figure it happened to land beside.
        _limit.FontFamily = Font("UiFont");
        _limit.FontSize = 11.5;
        _limit.Foreground = Brush("TextMutedBrush");
        _limit.VerticalAlignment = VerticalAlignment.Center;
        _limit.TextTrimming = TextTrimming.CharacterEllipsis;
        _limit.MaxWidth = 220;

        var chip = new Border
        {
            Background = Brush("PanelBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _limit,
        };
        Grid.SetColumn(chip, 2);
        row.Children.Add(chip);

        return row;
    }

    private UIElement Dial(string key, double diameter, double figureSize)
    {
        var m = Metric(key);
        var host = new Grid { Width = diameter, Height = diameter + 14, Margin = new Thickness(0, 0, 14, 0) };

        var canvas = new Canvas { Width = diameter, Height = diameter, VerticalAlignment = VerticalAlignment.Top };
        canvas.Children.Add(CockpitView.ArcPath(diameter, 1.0, Brush("TrackBrush"), 5));
        var arc = CockpitView.ArcPath(diameter, 0.0, Brush("AccentGradientBrush"), 5);
        canvas.Children.Add(arc);
        host.Children.Add(canvas);

        var figure = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = figureSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12),
        };
        host.Children.Add(figure);

        host.Children.Add(new TextBlock
        {
            Text = m?.ShortLabel ?? m?.Label ?? key,
            FontFamily = Font("UiFont"),
            FontSize = 9.5,
            Foreground = Brush("TextFaintBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        });

        if (m is not null) _dials[key] = (arc, figure, diameter);
        return host;
    }

    private UIElement Figure(MetricDefinition m)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var value = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
        };
        stack.Children.Add(value);
        stack.Children.Add(new TextBlock
        {
            Text = m.ShortLabel ?? m.Label,
            FontFamily = Font("UiFont"),
            FontSize = 9.5,
            Foreground = Brush("TextFaintBrush"),
        });

        _figures.Add((m, value));
        return stack;
    }

    protected override void Tick()
    {
        foreach ((string key, (Path arc, TextBlock figure, double diameter)) in _dials)
        {
            if (Metric(key) is not { } m) continue;

            double? v = Metrics.Value(key);
            figure.Text = v?.ToString(m.Format, CultureInfo.InvariantCulture) ?? "--";
            figure.Foreground = FigureBrush(m);
            arc.Data = v is null || Ceiling(m) is not { } ceiling
                ? Geometry.Empty
                : CockpitView.ArcGeometry(diameter, v.Value / ceiling);
        }

        foreach ((MetricDefinition m, TextBlock value) in _figures)
        {
            string? shown = Shown(m);
            value.Text = shown ?? "--";
            value.Foreground = FigureBrush(m);
        }

        _limit.Text = Metrics.BindingLimitName is { } b ? $"Limited by {b}" : "Nothing limiting";
    }
}

// =====================================================================================
//  EDITORIAL
// =====================================================================================

/// <summary>
/// The machine, written up.
///
/// A printed technical report rather than an instrument: a measure narrow enough to read
/// comfortably, headings with rules under them, a sentence stating the situation in words, and the
/// figures set as small multiples beneath it.
///
/// The sentence is the point. Every other surface in this application shows numbers and leaves the
/// reading of them to you; this one says "the die is at 81.1 °C, limited by temperature" and then
/// shows its working. Which is only honest if the sentence is assembled from the same readings
/// shown below it and never written ahead of them, so a clause whose number is missing says it is
/// missing rather than quietly disappearing.
/// </summary>
public sealed class EditorialView : AlternateInterface
{
    private readonly TextBlock _lede = new();
    private readonly List<(MetricDefinition Metric, TextBlock Value, Polyline Trace, Border Host)> _plates = new();

    public EditorialView(HardwareContext ctx, MetricSource metrics, Action? leave = null)
        : base(ctx, metrics, leave) => Start();

    protected override UIElement Build()
    {
        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new Grid { Margin = new Thickness(0, 14, 26, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var esc = Escape();
        Grid.SetColumn(esc, 1);
        top.Children.Add(esc);
        Grid.SetRow(top, 0);
        outer.Children.Add(top);

        var column = new StackPanel
        {
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(26, 10, 26, 30),
        };

        column.Children.Add(Heading("Thermal"));

        _lede.FontFamily = Font("UiFont");
        _lede.FontSize = 17;
        _lede.Foreground = Brush("TextPrimaryBrush");
        _lede.TextWrapping = TextWrapping.Wrap;
        _lede.LineHeight = 26;
        _lede.Margin = new Thickness(0, 2, 0, 22);
        column.Children.Add(_lede);
        column.Children.Add(Plates("cpu", "fan", "pkg", "cpuclk"));

        column.Children.Add(Heading("Graphics"));
        column.Children.Add(Plates("gpu", "gpuw", "gpuclk", "gpuload"));

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = column };
        Grid.SetRow(scroll, 1);
        outer.Children.Add(scroll);
        return outer;
    }

    private UIElement Heading(string text)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
        stack.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = Font("UiFontSemibold"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 7),
        });
        stack.Children.Add(new Border { Height = 1, Background = Brush("BorderBrush") });
        return stack;
    }

    /// <summary>A row of small multiples: same size, same treatment, read as a set.</summary>
    private UIElement Plates(params string[] keys)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 26) };
        for (int i = 0; i < keys.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < keys.Length; i++)
        {
            if (Metric(keys[i]) is not { } m) continue;

            var cell = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 14, 0, 0, 0) };

            var trace = new Polyline { Stroke = Brush("AccentBrush"), StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round };
            var host = new Border { Height = 34, Child = trace };
            cell.Children.Add(host);

            var value = new TextBlock
            {
                FontFamily = Font("MonoFont"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextPrimaryBrush"),
                Margin = new Thickness(0, 6, 0, 0),
            };
            cell.Children.Add(value);
            cell.Children.Add(new TextBlock
            {
                Text = m.Label,
                FontFamily = Font("UiFont"),
                FontSize = 11,
                Foreground = Brush("TextFaintBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            _plates.Add((m, value, trace, host));
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }

        return grid;
    }

    protected override void Tick()
    {
        // Assembled from the readings, not written ahead of them. This interface states the
        // situation in words, and must not be the one surface in the application that states one
        // nobody measured.
        string temp = Metric("cpu") is { } die && Shown(die) is { } t
            ? $"{t} °C"
            : "a temperature it could not read";
        string limited = Metrics.BindingLimitName is { } b
            ? $", limited by {b.ToLowerInvariant()}"
            : ", with nothing currently limiting it";

        _lede.Text = $"The die is at {temp}{limited}.";

        foreach ((MetricDefinition m, TextBlock value, Polyline trace, Border host) in _plates)
        {
            string? shown = Shown(m);
            value.Text = shown is null ? "unavailable" : shown + " " + m.Unit;
            value.FontSize = shown is null ? 12 : 20;
            value.Foreground = FigureBrush(m);

            double width = host.ActualWidth > 8 ? host.ActualWidth : 150;
            var longest = Metrics.History(m.Key).Segments(width, 34).OrderByDescending(s => s.Count).FirstOrDefault();
            trace.Points = longest is null
                ? new PointCollection()
                : new PointCollection(longest.Select(p => new Point(p.X, p.Y)));
        }
    }
}

// =====================================================================================
//  COMMAND
// =====================================================================================

/// <summary>
/// One focused surface, and a command palette for everything else.
///
/// Quiet at rest: a single large reading, a few bars, nothing competing. Ctrl+K opens a list over
/// the top of what can be done from here.
///
/// The palette only offers things that work. Filling it with plausible entries that do nothing
/// would be the same fault as a fabricated reading: something on screen that looks like a
/// capability and is not one.
/// </summary>
public sealed class CommandView : AlternateInterface
{
    private readonly TextBlock _headline = new();
    private readonly TextBlock _headlineNote = new();
    private readonly List<(MetricDefinition Metric, TextBlock Value, Grid Track)> _bars = new();
    private readonly Border _palette = new();
    private readonly Action<InterfaceMode>? _switchTo;

    public CommandView(HardwareContext ctx, MetricSource metrics, Action? leave = null,
                       Action<InterfaceMode>? switchTo = null)
        : base(ctx, metrics, leave)
    {
        _switchTo = switchTo;
        Start();

        Focusable = true;
        Loaded += (_, _) => Keyboard.Focus(this);
        KeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.K)
        {
            _palette.Visibility = Visibility.Visible;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _palette.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    protected override UIElement Build()
    {
        var root = new Grid();

        var page = new Grid { Margin = new Thickness(34, 20, 34, 24) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "Ctrl+K",
            FontFamily = Font("MonoFont"),
            FontSize = 11,
            Foreground = Brush("TextFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(hint, 1);
        top.Children.Add(hint);

        var esc = Escape();
        Grid.SetColumn(esc, 2);
        top.Children.Add(esc);
        Grid.SetRow(top, 0);
        page.Children.Add(top);

        var headline = new StackPanel { Margin = new Thickness(0, 26, 0, 30) };
        _headline.FontFamily = Font("MonoFont");
        _headline.FontSize = 58;
        _headline.FontWeight = FontWeights.SemiBold;
        _headline.Foreground = Brush("TextPrimaryBrush");
        headline.Children.Add(_headline);

        _headlineNote.FontFamily = Font("UiFont");
        _headlineNote.FontSize = 12.5;
        _headlineNote.Foreground = Brush("TextMutedBrush");
        _headlineNote.Margin = new Thickness(2, 2, 0, 0);
        headline.Children.Add(_headlineNote);
        Grid.SetRow(headline, 1);
        page.Children.Add(headline);

        var bars = new StackPanel();
        foreach (string key in new[] { "fan", "fan2", "pkg", "cpuload", "gpuload" })
            if (Metric(key) is { } m)
                bars.Children.Add(Bar(m));

        Grid.SetRow(bars, 2);
        page.Children.Add(bars);
        root.Children.Add(page);
        root.Children.Add(BuildPalette());
        return root;
    }

    private UIElement Bar(MetricDefinition m)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = m.Label,
            FontFamily = Font("UiFont"),
            FontSize = 12.5,
            Foreground = Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var value = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });

        var fill = new Border { Background = Brush("AccentGradientBrush"), CornerRadius = new CornerRadius(3) };
        var rest = new Border { Background = Brush("TrackBrush"), CornerRadius = new CornerRadius(3) };
        Grid.SetColumn(fill, 0);
        Grid.SetColumn(rest, 1);
        track.Children.Add(fill);
        track.Children.Add(rest);
        Grid.SetColumn(track, 2);
        row.Children.Add(track);

        _bars.Add((m, value, track));
        return row;
    }

    private UIElement BuildPalette()
    {
        _palette.Visibility = Visibility.Collapsed;
        _palette.Background = Brush("PanelBrush");
        _palette.BorderBrush = Brush("BorderStrongBrush");
        _palette.BorderThickness = new Thickness(1);
        _palette.CornerRadius = new CornerRadius(10);
        _palette.Padding = new Thickness(6);
        _palette.Width = 420;
        _palette.VerticalAlignment = VerticalAlignment.Center;
        _palette.HorizontalAlignment = HorizontalAlignment.Center;

        var list = new StackPanel();
        list.Children.Add(new TextBlock
        {
            Text = "SWITCH INTERFACE",
            FontFamily = Font("MonoFont"),
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextFaintBrush"),
            Margin = new Thickness(10, 8, 0, 6),
        });

        foreach ((InterfaceMode mode, string name) in new[]
                 {
                     (InterfaceMode.Workspaces, "Workspaces"),
                     (InterfaceMode.Instrument, "Instrument"),
                     (InterfaceMode.Cockpit, "Cockpit"),
                     (InterfaceMode.Editorial, "Editorial"),
                 })
        {
            var item = new Button
            {
                Content = name,
                Height = 32,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 0, 0, 0),
                Style = TryFindResource("FlatButtonStyle") as Style,
                Margin = new Thickness(0, 0, 0, 2),
            };
            item.Click += (_, _) => { _palette.Visibility = Visibility.Collapsed; _switchTo?.Invoke(mode); };
            list.Children.Add(item);
        }

        _palette.Child = list;
        return _palette;
    }

    protected override void Tick()
    {
        if (Metric("cpu") is { } die)
        {
            string? shown = Shown(die);
            _headline.Text = shown is null ? "unavailable" : shown + "°";
            _headline.FontSize = shown is null ? 22 : 58;
            _headline.Foreground = FigureBrush(die);
            _headlineNote.Text = Metrics.BindingLimitName is { } b
                ? $"die temperature · limited by {b.ToLowerInvariant()}"
                : "die temperature · nothing limiting";
        }

        foreach ((MetricDefinition m, TextBlock value, Grid track) in _bars)
        {
            string? shown = Shown(m);
            value.Text = shown ?? "--";
            value.Foreground = FigureBrush(m);

            double? v = Metrics.Value(m.Key);
            double pct = v is null || Ceiling(m) is not { } ceiling
                ? 0
                : Math.Clamp(v.Value / ceiling * 100, 0, 100);
            track.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
            track.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
        }
    }
}
