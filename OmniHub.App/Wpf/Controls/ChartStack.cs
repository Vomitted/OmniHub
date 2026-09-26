// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Telemetry;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// Readings stacked on one clock, the way an overclocking tool draws them: a strip per kind of
/// reading, one time axis under the last, and one crosshair through all of them.
///
/// Stacked rather than overlaid because the question these answer is about coincidence -- did the
/// fans rise when the package power did, was the processor at its limit when the temperature
/// peaked -- and that is read down a vertical line. Overlaid on one plot, four units would need four
/// axes; stacked, each strip keeps its own scale and the clock is shared.
///
/// Each strip's series share its unit, so they share a scale (TimeSeriesChart.ShareScale): a line's
/// height means the same thing for every line in the strip. The figures in a strip's header are the
/// current readings, and while the pointer is over any strip, the readings at that instant.
///
/// The data is <see cref="MetricSource.Recent"/>, mirrored on every update rather than appended, so
/// the charts are full the moment they are opened -- the source kept recording while they were not.
/// </summary>
public sealed class ChartStack : StackPanel
{
    private const double PlotHeight = 70, TimeAxisHeight = 20;

    // The slow readings arrive every five seconds on screen and every thirty from the tray, and a
    // default gap rule of three median intervals would erase the whole tray stretch as missing.
    // A reading that did not answer is not left to this rule: RecentSeries records it as a break.
    private static readonly TimeSpan SlowGap = TimeSpan.FromSeconds(95);
    private static readonly HashSet<string> FastKeys = new() { "cpu", "fan", "fan2" };

    private sealed record Trace(string Key, ChartSeries Series, TextBlock Figure);
    private sealed record Strip(TimeSeriesChart Chart, List<Trace> Traces);

    private readonly MetricSource _source;
    private readonly List<Strip> _strips = new();
    private DateTime? _hovered;

    /// <summary>The instant under the pointer, or null when it leaves.</summary>
    public event Action<DateTime?>? HoverChanged;

    public ChartStack(MetricSource source)
    {
        _source = source;

        // Loaded and Unloaded, for the reason every panel on the shared source gives: a view is
        // detached whenever somebody leaves it, and a subscription that outlived that would keep
        // mirroring into charts nobody can see.
        Loaded += (_, _) => { _source.Updated -= Refresh; _source.Updated += Refresh; Refresh(); };
        Unloaded += (_, _) => _source.Updated -= Refresh;
    }

    /// <summary>Adds a strip: a title and the readings drawn in it, each with its colour.</summary>
    public void AddStrip(string title, params (string Key, string Name, string BrushKey, double? Min, double? Max)[] lines)
    {
        var chart = new TimeSeriesChart { ShowLegend = false, ShowReadout = false, ShareScale = true };

        // The value axis is 42 wide; the header starts where the plot does.
        var header = new Grid { Margin = new Thickness(42, _strips.Count == 0 ? 0 : 12, 0, 5) };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("DataLabelText"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        header.Children.Add(chips);

        var traces = new List<Trace>();
        foreach (var (key, name, brushKey, min, max) in lines)
        {
            if (Metrics.Find(key) is not { } metric) continue;

            var series = new ChartSeries
            {
                Name = name,
                Stroke = (Brush)FindResource(brushKey),
                Unit = UnitOf(metric),
                Format = metric.Format,
                Min = min,
                Max = max,
                Fill = true,
                IsPrimary = traces.Count == 0,
                GapThreshold = FastKeys.Contains(key) ? null : SlowGap,
            };
            chart.AddSeries(series);

            var figure = new TextBlock { Style = (Style)FindResource("CellFigure"), FontSize = 13, Margin = new Thickness(5, 0, 0, 0) };
            chips.Children.Add(Chip(series, figure));
            traces.Add(new Trace(key, series, figure));
        }

        chart.OnHover += (at, _) => Hover(at);
        chart.HoverEnded += () => Hover(null);

        Children.Add(header);
        Children.Add(chart);
        _strips.Add(new Strip(chart, traces));

        // One clock, labelled once, under the last strip.
        for (int i = 0; i < _strips.Count; i++)
        {
            bool last = i == _strips.Count - 1;
            _strips[i].Chart.ShowTimeAxis = last;
            _strips[i].Chart.Height = PlotHeight + (last ? TimeAxisHeight : 0);
        }
    }

    /// <summary>How far back the strips reach, up to <see cref="MetricSource.RecentSpan"/>.</summary>
    public void SetWindow(TimeSpan span)
    {
        foreach (var strip in _strips) strip.Chart.SetLiveWindow(span);
    }

    private void Refresh()
    {
        foreach (var strip in _strips)
            for (int i = 0; i < strip.Traces.Count; i++)
                strip.Chart.SetSeriesData(i, _source.Recent(strip.Traces[i].Key));

        ShowFigures();
    }

    private void Hover(DateTime? at)
    {
        _hovered = at;
        foreach (var strip in _strips) strip.Chart.ShowCrosshairAt(at);
        ShowFigures();
        HoverChanged?.Invoke(at);
    }

    private void ShowFigures()
    {
        foreach (var strip in _strips)
            for (int i = 0; i < strip.Traces.Count; i++)
            {
                var trace = strip.Traces[i];
                double? value = _hovered is { } at ? strip.Chart.ValueAt(i, at) : _source.Value(trace.Key);
                Roll.To(trace.Figure, value, trace.Series.Format, trace.Series.Unit, animate: _hovered is null);
            }
    }

    /// <summary>A dot in the line's colour, its name, and its figure.</summary>
    private FrameworkElement Chip(ChartSeries series, TextBlock figure)
    {
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0) };
        chip.Children.Add(new Ellipse
        {
            Width = 7, Height = 7,
            Fill = series.Stroke,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        chip.Children.Add(new TextBlock { Text = series.Name, Style = (Style)FindResource("CellNote") });
        chip.Children.Add(figure);
        return chip;
    }

    /// <summary>Degrees and per cent attached, everything else spaced -- "63.1°", "18.3 W", "2400 rpm".</summary>
    private static string UnitOf(MetricDefinition metric)
    {
        string unit = metric.Unit.Trim();
        if (unit is "°" or "%") return unit;
        return " " + (unit == "RPM" ? "rpm" : unit);
    }
}
