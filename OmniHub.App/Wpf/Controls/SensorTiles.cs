// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OmniHub.Core.Telemetry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// Every reading as a tile: its name, its figure, its recent trace filled beneath the line, and the
/// lowest and highest it has been since the application started.
///
/// This replaced a table with the same figures (technical/TECHNICAL-v5-ui.md, section 8). The table
/// was HWiNFO's shape and read like a spreadsheet; the tiles carry the same numbers with the trace
/// large enough to show a shape rather than a squiggle. The session figures are still counted by
/// <see cref="RunningStats"/> whether or not the window was open, and the mean and the source are in
/// each tile's tooltip.
///
/// A reading that did not answer keeps its tile, as a dash. "GPU power, unavailable" is what says the
/// card is asleep, and a grid that quietly closed the gap would say nothing at all.
/// </summary>
public sealed class SensorTiles : UserControl
{
    private const double MinTileWidth = 150, Gap = 8, TraceHeight = 26;

    private readonly MetricSource _source;
    private readonly UniformGrid _grid = new();
    private readonly List<Tile> _tiles = new();

    private sealed record Tile(MetricDefinition Metric, Border Surface, TextBlock Figure, TextBlock Low, TextBlock High,
                               Grid Trace, Path Line, Path Area);

    public SensorTiles(MetricSource source, params string[] keys)
    {
        _source = source;
        Focusable = false;

        if (keys.Length == 0) keys = Metrics.All.Select(m => m.Key).ToArray();
        foreach (var key in keys)
            if (Metrics.Find(key) is { } metric) _grid.Children.Add(Build(metric));

        // Each tile carries half the gap on every side; this takes the outer half back.
        _grid.Margin = new Thickness(-Gap / 2);
        Content = _grid;

        Loaded += (_, _) => { _source.Updated -= Refresh; _source.Updated += Refresh; Refresh(); };
        Unloaded += (_, _) => _source.Updated -= Refresh;
        SizeChanged += (_, e) => _grid.Columns = Math.Max(1, (int)((e.NewSize.Width + Gap) / (MinTileWidth + Gap)));
    }

    /// <summary>
    /// The colour a reading's trace is drawn in: the processor's, the graphics card's, the fans' or
    /// the system's -- the same four the history charts use, so a colour means one thing everywhere.
    /// </summary>
    private static string BrushFor(string key) => key switch
    {
        "gpu" or "gpuclk" or "gpuload" or "gpuw" => "MetricGpuBrush",
        "fan" or "fan2" => "MetricFanBrush",
        "mem" or "selfcpu" or "selfmem" => "MetricMemBrush",
        _ => "MetricCpuBrush",
    };

    private FrameworkElement Build(MetricDefinition metric)
    {
        var figure = new TextBlock { Style = Named("CellFigure"), FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Left };
        var reading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        reading.Children.Add(figure);
        reading.Children.Add(new TextBlock { Text = UnitOf(metric), Style = Named("CellNote"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 3) });

        var line = new Path { Stroke = Brush(BrushFor(metric.Key)), StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        var area = new Path { IsHitTestVisible = false };
        var trace = new Grid { Height = TraceHeight, Margin = new Thickness(0, 6, 0, 4), ClipToBounds = true };
        trace.Children.Add(area);
        trace.Children.Add(line);

        var low = new TextBlock { Style = Named("CellNote") };
        var high = new TextBlock { Style = Named("CellNote"), HorizontalAlignment = HorizontalAlignment.Right };
        var range = new Grid();
        range.Children.Add(low);
        range.Children.Add(high);

        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = metric.Name, Style = Named("CellNote") });
        body.Children.Add(reading);
        body.Children.Add(trace);
        body.Children.Add(range);

        // A well like the charts', so a tile reads as a small instrument rather than more card.
        var surface = new Border { Child = body, BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 7), Margin = new Thickness(Gap / 2) };
        surface.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        surface.SetResourceReference(Border.CornerRadiusProperty, "RadiusSm");

        var tile = new Tile(metric, surface, figure, low, high, trace, line, area);
        trace.SizeChanged += (_, _) => DrawTrace(tile);
        _tiles.Add(tile);
        return surface;
    }

    private void Refresh()
    {
        foreach (var tile in _tiles)
        {
            var metric = tile.Metric;
            double? now = _source.Value(metric.Key);
            var stats = _source.Stats(metric.Key);

            Roll.To(tile.Figure, now, metric.Format);

            // Threshold colour on the figure only; the name and the range stay the colours text is.
            tile.Figure.Foreground = now is null
                ? Brush("TextFaintBrush")
                : Metrics.LevelOf(metric.Key, now) switch
                {
                    MetricLevel.Hot => Brush("DangerBrush"),
                    MetricLevel.Warn => Brush("WarnBrush"),
                    _ => Brush("TextPrimaryBrush"),
                };

            tile.Low.Text = stats.Min is { } lo ? "min " + Figure(metric, lo) : "";
            tile.High.Text = stats.Max is { } hi ? "max " + Figure(metric, hi) : "";
            tile.Surface.ToolTip = $"{metric.Name}\n"
                + (stats.Mean is { } mean ? $"mean {Figure(metric, mean)} {UnitOf(metric)} since {_source.StatsSince:HH:mm}\n" : "")
                + metric.Source;

            DrawTrace(tile);
        }
    }

    /// <summary>The recent trace, with the area under it filled in the line's colour fading out.</summary>
    private void DrawTrace(Tile tile)
    {
        double width = tile.Trace.ActualWidth;
        if (width < 8) return;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            foreach (var segment in _source.History(tile.Metric.Key).Segments(width, TraceHeight))
            {
                var points = segment.Select(p => new Point(p.X, p.Y)).ToList();

                l.BeginFigure(points[0], false, false);
                l.PolyLineTo(points.Skip(1).ToList(), true, true);

                a.BeginFigure(new Point(points[0].X, TraceHeight), true, true);
                a.PolyLineTo(points.Append(new Point(points[^1].X, TraceHeight)).ToList(), false, false);
            }
        }
        line.Freeze();
        area.Freeze();
        tile.Line.Data = line;
        tile.Area.Data = area;

        // From the stroke's colour as it is now, so a theme switch reaches the fill as well as the line.
        if (tile.Line.Stroke is SolidColorBrush s)
        {
            var top = Color.FromArgb(0x48, s.Color.R, s.Color.G, s.Color.B);
            if ((tile.Area.Fill as LinearGradientBrush)?.GradientStops[0].Color != top)
                tile.Area.Fill = new LinearGradientBrush(top, Color.FromArgb(0, s.Color.R, s.Color.G, s.Color.B), 90);
        }
    }

    private static string Figure(MetricDefinition metric, double value) =>
        value.ToString(metric.Format, CultureInfo.InvariantCulture);

    /// <summary>"°C" where the catalogue writes a bare degree, "rpm" for its capitals.</summary>
    private static string UnitOf(MetricDefinition metric) => metric.Unit.Trim() switch
    {
        "°" => "°C",
        "RPM" => "rpm",
        var unit => unit,
    };

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;
    private Style? Named(string key) => TryFindResource(key) as Style;
}
