// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Telemetry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// Every reading as a row: what it is now, the lowest, mean and highest since the application
/// started, its recent trace, and where it came from.
///
/// This is HWiNFO's shape, and the reason is the columns rather than the look. A number on its own
/// answers "how hot is it"; the same number beside its session maximum answers "how hot did it get
/// while I was playing", which is most of why anybody opens a sensor window. The figures are
/// counted by <see cref="RunningStats"/> in Core, whether or not this window was open.
///
/// A reading that did not answer stays in the table, dimmed, as a dash. The absence is a finding:
/// "GPU power, unavailable" is what says the card is asleep, and a table that quietly dropped the
/// row would say nothing at all.
/// </summary>
public sealed class SensorTable : UserControl
{
    private const double TraceWidth = 76, TraceHeight = 12;

    // Below this the source column is left to the row's tooltip. A source cut to a few characters
    // is worse than none, and the tooltip always carries it in full.
    private const double SourceColumnFrom = 700;

    private readonly MetricSource _source;
    private readonly List<Row> _rows = new();
    private readonly ColumnDefinition _nameColumn;
    private readonly ColumnDefinition _sourceColumn;

    private sealed record Row(
        MetricDefinition Metric, TextBlock Name, TextBlock Now, TextBlock Min, TextBlock Mean, TextBlock Max, Path Trace);

    /// <param name="source">Where the values and the session figures come from.</param>
    /// <param name="groups">
    /// The readings to list, in groups separated by a hairline -- processor, graphics, cooling.
    /// None means every reading in the catalogue, as one group.
    /// </param>
    public SensorTable(MetricSource source, params string[][] groups)
    {
        _source = source;
        Focusable = false;

        if (groups.Length == 0) groups = new[] { Metrics.All.Select(m => m.Key).ToArray() };

        var grid = new Grid();
        _nameColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 104 };
        grid.ColumnDefinitions.Add(_nameColumn);
        foreach (double width in new[] { 80.0, 56, 56, 56, TraceWidth + 20 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        _sourceColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(_sourceColumn);

        Header(grid);

        bool first = true;
        foreach (var group in groups)
        {
            if (!first) Separator(grid);
            first = false;

            foreach (var key in group)
                if (Metrics.Find(key) is { } metric) AddRow(grid, metric);
        }

        Content = grid;

        // Loaded and Unloaded rather than the constructor: a view is detached every time the user
        // leaves it, and a subscription that survives that keeps drawing a table nobody can see.
        Loaded += (_, _) => { _source.Updated -= Refresh; _source.Updated += Refresh; Refresh(); };
        Unloaded += (_, _) => _source.Updated -= Refresh;
        SizeChanged += (_, e) => ShowSourceColumn(e.NewSize.Width >= SourceColumnFrom);
    }

    private void Header(Grid grid)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string[] titles = { "SENSOR", "NOW", "MIN", "AVG", "MAX", "TREND", "SOURCE" };

        for (int column = 0; column < titles.Length; column++)
        {
            bool figure = column is >= 1 and <= 4;
            var text = new TextBlock
            {
                Text = titles[column],
                Style = Named("DataLabelText"),
                Foreground = Brush("TextFaintBrush"),
                HorizontalAlignment = figure ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = new Thickness(column == 0 ? 8 : 0, 0, figure ? 10 : 8, 6),
            };
            Place(grid, text, 0, column);
        }

        var rule = new Border { Height = 1, Background = Brush("BorderBrush"), VerticalAlignment = VerticalAlignment.Bottom };
        Place(grid, rule, 0, 0, span: titles.Length);
    }

    private void Separator(Grid grid)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var rule = new Border { Height = 1, Margin = new Thickness(8, 3, 8, 3), Background = Brush("BorderBrush") };
        Place(grid, rule, grid.RowDefinitions.Count - 1, 0, span: 7);
    }

    private void AddRow(Grid grid, MetricDefinition metric)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;

        // The row's own surface carries the height, the hover and the tooltip. Everything drawn on
        // top of it is transparent to the pointer, so the whole row answers as one thing.
        var surface = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = $"{metric.Name}\n{metric.Source}",
        };
        surface.SetResourceReference(HeightProperty, "TableRowHeight");
        surface.MouseEnter += (_, _) => surface.Background = Brush("PanelHoverBrush");
        surface.MouseLeave += (_, _) => surface.Background = System.Windows.Media.Brushes.Transparent;
        Place(grid, surface, row, 0, span: 7);

        var name = Cell(new TextBlock { Text = metric.Name, Style = Named("CellLabel"), Margin = new Thickness(8, 0, 8, 0) });
        var now = Cell(new TextBlock { Style = Named("CellFigure"), Margin = new Thickness(0, 0, 10, 0) });
        var min = Cell(new TextBlock { Style = Named("CellFigureMuted"), Margin = new Thickness(0, 0, 10, 0) });
        var mean = Cell(new TextBlock { Style = Named("CellFigureMuted"), Margin = new Thickness(0, 0, 10, 0) });
        var max = Cell(new TextBlock { Style = Named("CellFigureMuted"), Margin = new Thickness(0, 0, 10, 0) });
        var source = Cell(new TextBlock { Text = metric.Source, Style = Named("CellNote"), Margin = new Thickness(0, 0, 8, 0) });

        Place(grid, name, row, 0);
        Place(grid, now, row, 1);
        Place(grid, min, row, 2);
        Place(grid, mean, row, 3);
        Place(grid, max, row, 4);
        Place(grid, source, row, 6);

        var trace = new Path
        {
            Stroke = Brush("AccentBrush"),
            StrokeThickness = 1.25,
            StrokeLineJoin = PenLineJoin.Round,
            Width = TraceWidth,
            Height = TraceHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        Place(grid, trace, row, 5);

        _rows.Add(new Row(metric, name, now, min, mean, max, trace));
    }

    private void Refresh()
    {
        foreach (var row in _rows)
        {
            var metric = row.Metric;
            double? now = _source.Value(metric.Key);
            var stats = _source.Stats(metric.Key);

            row.Now.Text = NowText(metric, now);

            // Threshold colour on the current figure only: data visualisation, not decoration. The
            // name, the session figures and the source stay the colours text always is.
            row.Now.Foreground = now is null
                ? Brush("TextFaintBrush")
                : Metrics.LevelOf(metric.Key, now) switch
                {
                    MetricLevel.Hot => Brush("DangerBrush"),
                    MetricLevel.Warn => Brush("WarnBrush"),
                    _ => Brush("TextPrimaryBrush"),
                };

            // A reading that has never once answered is a different fact from one that is missing
            // right now, and the row says which by how dim its name is.
            row.Name.Foreground = now is null && stats.Count == 0 ? Brush("TextFaintBrush") : Brush("TextPrimaryBrush");

            row.Min.Text = Figure(metric, stats.Min);
            row.Mean.Text = Figure(metric, stats.Mean);
            row.Max.Text = Figure(metric, stats.Max);

            DrawTrace(row);
        }
    }

    /// <summary>
    /// The current value with its unit, spaced the way a table column reads: "4.30 GHz" and
    /// "3600 RPM" rather than the catalogue's "4.30GHz" beside "3600 RPM". Degrees and per cent
    /// stay attached, as they are written everywhere.
    /// </summary>
    private static string NowText(MetricDefinition metric, double? value)
    {
        if (value is not { } v || double.IsNaN(v)) return Metrics.Unavailable;

        string figure = v.ToString(metric.Format, CultureInfo.InvariantCulture);
        string unit = metric.Unit.Trim();
        return unit is "°" or "%" or "" ? figure + unit : $"{figure} {unit}";
    }

    /// <summary>A session figure without its unit: the row's current value already carries it.</summary>
    private static string Figure(MetricDefinition metric, double? value) =>
        value is { } v ? v.ToString(metric.Format, CultureInfo.InvariantCulture) : Metrics.Unavailable;

    private void DrawTrace(Row row)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            foreach (var segment in _source.History(row.Metric.Key).Segments(TraceWidth, TraceHeight))
            {
                g.BeginFigure(new Point(segment[0].X, segment[0].Y), false, false);
                g.PolyLineTo(segment.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, true);
            }
        }
        geometry.Freeze();
        row.Trace.Data = geometry;
    }

    /// <summary>
    /// The source gets a column when the table is wide enough to set it on one line, and the name
    /// column stops stretching so the two do not split the spare width between them.
    /// </summary>
    private void ShowSourceColumn(bool show)
    {
        _sourceColumn.Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        _nameColumn.Width = show ? new GridLength(150) : new GridLength(1, GridUnitType.Star);
    }

    private static T Cell<T>(T element) where T : FrameworkElement
    {
        element.IsHitTestVisible = false;
        return element;
    }

    private static void Place(Grid grid, UIElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        if (span > 1) Grid.SetColumnSpan(element, span);
        grid.Children.Add(element);
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;
    private Style? Named(string key) => TryFindResource(key) as Style;
}
