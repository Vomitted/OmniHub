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
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// The console's live chart, as the reference draws its "Live Heartbeat": the processor and the
/// graphics card over the last five minutes as two smooth filled curves, a legend, and the values
/// under the pointer -- no axes and no gridlines, because the reference has none and the figures
/// are one hover away.
///
/// Smooth without inventing anything: Spline keeps every stretch of curve between the two readings
/// it joins. The data is MetricSource.Recent, recorded while the window was hidden too, and a missing
/// reading ends a curve rather than being joined across (TimeSeriesDecimator). One chart covers what
/// the four history strips did through three small tabs; the strips stay whole under the previous
/// look.
/// </summary>
public sealed class LiveChart : UserControl
{
    private const double PlotHeight = 132, Inset = 8;

    private sealed record Subject(string Name, (string Key, string Label, string BrushKey)[] Series, double? Min, double? Max, string Unit);

    private static readonly Subject[] Subjects =
    {
        // Against the hot points, as the dial and the sensor bars draw them: a scale hugging the data
        // turned a two-degree wobble into a cliff.
        new("TEMPERATURE", new[] { ("cpu", "CPU", "MetricCpuBrush"), ("gpu", "GPU", "MetricGpuBrush") },
            0, new[] { "cpu", "gpu" }.Max(k => Metrics.FullScale(Metrics.Find(k)!, null)), "°C"),
        new("LOAD", new[] { ("cpuload", "CPU", "MetricCpuBrush"), ("gpuload", "GPU", "MetricGpuBrush") }, 0, 100, "%"),
        new("POWER", new[] { ("pkg", "CPU", "MetricCpuBrush"), ("gpuw", "GPU", "MetricGpuBrush") }, 0, null, "W"),
    };

    private readonly MetricSource _source;
    private readonly Subject[] _subjects;
    private readonly bool _detailed;
    private int _subject;
    private TimeSpan _window = Curves.Window;

    private readonly Canvas _plot = new() { Height = PlotHeight, ClipToBounds = true, Background = System.Windows.Media.Brushes.Transparent };
    private readonly Canvas _grid = new() { IsHitTestVisible = false };
    private readonly Canvas _times = new() { Height = 18, IsHitTestVisible = false };
    private readonly Path[] _areas = { new() { IsHitTestVisible = false }, new() { IsHitTestVisible = false } };
    private readonly Path[] _bases = { new() { IsHitTestVisible = false }, new() { IsHitTestVisible = false } };
    private readonly Path[] _lines = { new() { StrokeThickness = 2, IsHitTestVisible = false }, new() { StrokeThickness = 2, IsHitTestVisible = false } };
    private readonly Color?[] _tints = new Color?[2];
    private readonly Line _cross = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Ellipse _crossDot = new() { Width = 9, Height = 9, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Border _tip = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false, Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBlock _tipText = new();
    private readonly (Border Swatch, TextBlock Label)[] _legend = { (new Border(), new TextBlock()), (new Border(), new TextBlock()) };

    // The scale and window of the last drawing, for reading the pointer back into values.
    private double _lo, _hi = 1;
    private DateTime _from, _to;

    /// <param name="detailed">
    /// Studio's chart: the fans as a fourth subject, the last 5, 15 or 30 minutes (all that is kept),
    /// and the values and times labelled -- what the classic look's four strips carried, and the
    /// console's chart did not.
    /// </param>
    /// <param name="fanTop">The fan band's measured maximum, the fans' real full scale, where known.</param>
    public LiveChart(MetricSource source, bool detailed = false, double? fanTop = null)
    {
        _source = source;
        _detailed = detailed;
        _subjects = detailed
            ? Subjects.Append(new Subject("FANS", new[] { ("fan", "FAN 1", "MetricFanBrush"), ("fan2", "FAN 2", "TextMutedBrush") },
                                          0, fanTop, "rpm")).ToArray()
            : Subjects;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var live = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        live.SetResourceReference(Shape.FillProperty, "AccentBrush");
        title.Children.Add(live);
        if (detailed) Motion.Pulse(live);   // it is live: the dot says so while the chart is on screen
        var name = new TextBlock { Text = "Live", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        name.SetResourceReference(StyleProperty, "PaneTitle");
        title.Children.Add(name);

        for (int i = 0; i < _subjects.Length; i++)
        {
            var tab = new RadioButton
            {
                Content = _subjects[i].Name, Tag = i, GroupName = "LiveSubject", Height = 24, MinWidth = 64,
                Margin = new Thickness(0, 0, 2, 0), IsChecked = i == 0, FontSize = 9.5,
            };
            tab.SetResourceReference(StyleProperty, "PillRadioStyle");
            tab.Checked += (sender, _) =>
            {
                if (sender is RadioButton { Tag: int index }) { _subject = index; Refresh(); if (_detailed) Motion.Refresh(_plot); }
            };
            title.Children.Add(tab);
        }
        header.Children.Add(title);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (detailed)
        {
            foreach (int minutes in new[] { 5, 15, 30 })
            {
                var pill = new RadioButton
                {
                    Content = $"{minutes} MIN", Tag = minutes, GroupName = "LiveWindow", Height = 24, MinWidth = 56,
                    Margin = new Thickness(2, 0, 0, 0), IsChecked = minutes == 5, FontSize = 9.5,
                };
                pill.SetResourceReference(StyleProperty, "PillRadioStyle");
                pill.Checked += (sender, _) =>
                {
                    if (sender is RadioButton { Tag: int m }) { _window = TimeSpan.FromMinutes(m); Refresh(); Motion.Refresh(_plot); }
                };
                legend.Children.Add(pill);
            }
            var divider = new Border { Width = 1, Height = 16, Margin = new Thickness(14, 0, 0, 0) };
            divider.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
            legend.Children.Add(divider);
        }
        foreach (var (swatch, label) in _legend)
        {
            swatch.Width = 12;
            swatch.Height = 2;
            swatch.VerticalAlignment = VerticalAlignment.Center;
            swatch.Margin = new Thickness(16, 0, 6, 0);
            label.SetResourceReference(StyleProperty, "CapsLabel");
            legend.Children.Add(swatch);
            legend.Children.Add(label);
        }
        header.Children.Add(legend);

        // Each fill over an opaque copy of its own shape in the panel's colour, so the lower curve's
        // band covers the upper one's instead of mixing with it: pink between the lines and the
        // second colour under its own, as the reference bands them. The lines are drawn after every
        // fill, so no fill can hide a line.
        for (int i = 0; i < _areas.Length; i++)
        {
            _bases[i].SetResourceReference(Shape.FillProperty, "PanelBrush");
            _plot.Children.Add(_bases[i]);
            _plot.Children.Add(_areas[i]);
        }
        // The rules and their labels over the fills (whose bases are opaque) and under the lines.
        _plot.Children.Add(_grid);
        foreach (var line in _lines) _plot.Children.Add(line);
        _cross.SetResourceReference(Shape.StrokeProperty, "TextFaintBrush");
        _plot.Children.Add(_cross);
        _crossDot.SetResourceReference(Shape.FillProperty, "TextPrimaryBrush");
        _plot.Children.Add(_crossDot);
        _tipText.SetResourceReference(StyleProperty, "CellFigure");
        _tipText.FontSize = 11;
        _tip.Child = _tipText;
        _tip.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        _tip.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
        _plot.Children.Add(_tip);

        _plot.MouseMove += OnHover;
        _plot.MouseLeave += (_, _) => { _cross.Visibility = _crossDot.Visibility = _tip.Visibility = Visibility.Collapsed; };
        _plot.SizeChanged += (_, _) => Refresh();

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        if (detailed)
        {
            DockPanel.SetDock(_times, Dock.Bottom);
            root.Children.Add(_times);
        }
        root.Children.Add(_plot);
        Content = root;

        Loaded += (_, _) => { _source.Updated -= Refresh; _source.Updated += Refresh; Refresh(); };
        Unloaded += (_, _) => _source.Updated -= Refresh;
    }

    private void Refresh()
    {
        double width = _plot.ActualWidth;
        var subject = _subjects[_subject];

        for (int i = 0; i < 2; i++)
        {
            var (_, label, brushKey) = subject.Series[i];
            _legend[i].Swatch.SetResourceReference(Border.BackgroundProperty, brushKey);
            _legend[i].Label.Text = label;
        }
        if (width < 20) return;

        _to = DateTime.UtcNow;
        _from = _to - _window;

        // One scale for both curves: they share a unit. Fixed where the reading has a real range.
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var (key, _, _) in subject.Series)
            foreach (var p in _source.Recent(key))
                if (p.AtUtc >= _from && double.IsFinite(p.Value)) { lo = Math.Min(lo, p.Value); hi = Math.Max(hi, p.Value); }
        if (lo > hi) { lo = 0; hi = 1; }
        double pad = Math.Max((hi - lo) * 0.15, 1);
        var scale = ValueAxis.Tightest(subject.Min ?? lo - pad, subject.Max ?? hi + pad, 3);
        _lo = subject.Min ?? scale.Lo;
        _hi = subject.Max ?? scale.Hi;

        if (_detailed)
        {
            DrawValues(width, subject.Unit);
            DrawTimes(width);
        }

        for (int i = 0; i < 2; i++)
        {
            var (key, _, brushKey) = subject.Series[i];
            var brush = (Brush)FindResource(brushKey);
            _lines[i].Stroke = brush;

            var (line, area) = Curves.Draw(key, _source.Recent(key), _from, _to, width, PlotHeight, Y, (int)(width / 6));
            _lines[i].Data = line;
            _areas[i].Data = area;
            _bases[i].Data = area;

            // The fill from the line's colour as it is now, so a theme switch reaches it too.
            Color colour = brush is SolidColorBrush s ? s.Color : Colors.Gray;
            if (_tints[i] != colour)
            {
                _tints[i] = colour;
                _areas[i].Fill = Curves.Fill(colour);
            }
        }
    }

    /// <summary>Two or three quiet rules at round values of the scale in force, each labelled with its unit.</summary>
    private void DrawValues(double width, string unit)
    {
        _grid.Children.Clear();
        var axis = ValueAxis.Nice(_lo, _hi, 3);
        foreach (double value in axis.Ticks())
        {
            if (value < _lo || value > _hi) continue;
            double y = Math.Round(Y(value)) + 0.5;

            var rule = new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 } };
            rule.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
            _grid.Children.Add(rule);

            var label = new TextBlock { Text = value.ToString("F" + axis.Decimals, System.Globalization.CultureInfo.InvariantCulture) + " " + unit };
            label.SetResourceReference(StyleProperty, "CapsLabel");
            Canvas.SetLeft(label, 2);
            Canvas.SetTop(label, y < 18 ? y + 3 : y - 15);   // under the top rule, over the others
            _grid.Children.Add(label);
        }
    }

    /// <summary>Clock times under the plot: every minute across five, every five across fifteen or thirty.</summary>
    private void DrawTimes(double width)
    {
        _times.Children.Clear();
        var step = TimeSpan.FromMinutes(_window.TotalMinutes <= 5 ? 1 : 5);
        var at = new DateTime(_from.Ticks - _from.Ticks % step.Ticks + step.Ticks, DateTimeKind.Utc);
        for (; at < _to; at += step)
        {
            double x = X(at, width);
            if (x < 24 || x > width - 24) continue;
            var label = new TextBlock { Text = at.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) };
            label.SetResourceReference(StyleProperty, "CapsLabel");
            Canvas.SetLeft(label, x - 14);
            Canvas.SetTop(label, 5);
            _times.Children.Add(label);
        }
    }

    private double X(DateTime at, double width) => (at - _from).TotalSeconds / Math.Max(1, (_to - _from).TotalSeconds) * width;

    private double Y(double value) => PlotHeight - Inset - (value - _lo) / Math.Max(1e-9, _hi - _lo) * (PlotHeight - 2 * Inset);

    /// <summary>The instant under the pointer, and each curve's reading there -- or a dash inside a gap.</summary>
    private void OnHover(object sender, System.Windows.Input.MouseEventArgs e)
    {
        double width = _plot.ActualWidth;
        if (width < 20) return;

        double x = Math.Clamp(e.GetPosition(_plot).X, 0, width);
        DateTime at = _from + TimeSpan.FromSeconds((_to - _from).TotalSeconds * x / width);
        var subject = _subjects[_subject];

        var parts = new List<string>();
        double? firstY = null;
        foreach (var (key, label, _) in subject.Series)
        {
            double? value = Nearest(key, at);
            var metric = Metrics.Find(key);
            parts.Add(value is { } v && metric is not null
                ? $"{label} {v.ToString(metric.Format, CultureInfo.InvariantCulture)}{metric.Unit.Trim() switch { "°" => " °C", var u => " " + u }}"
                : $"{label} {Metrics.Unavailable}");
            firstY ??= value is { } fv ? Y(fv) : null;
        }

        _tipText.Text = string.Join("   ", parts) + "   " + at.ToLocalTime().ToString("HH:mm:ss");
        _cross.X1 = _cross.X2 = x;
        _cross.Y1 = 0;
        _cross.Y2 = PlotHeight;
        _cross.Visibility = Visibility.Visible;

        if (firstY is { } y)
        {
            Canvas.SetLeft(_crossDot, x - 4.5);
            Canvas.SetTop(_crossDot, y - 4.5);
            _crossDot.Visibility = Visibility.Visible;
        }
        else _crossDot.Visibility = Visibility.Collapsed;

        _tip.Visibility = Visibility.Visible;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_tip, Math.Clamp(x - _tip.DesiredSize.Width / 2, 0, Math.Max(0, width - _tip.DesiredSize.Width)));
        Canvas.SetTop(_tip, 2);
    }

    private double? Nearest(string key, DateTime at)
    {
        TimePoint? best = null;
        foreach (var p in _source.Recent(key))
            if (best is null || Math.Abs((p.AtUtc - at).TotalSeconds) < Math.Abs((best.Value.AtUtc - at).TotalSeconds)) best = p;

        // Within one slow interval of the pointer, and a reading rather than a break, or nothing.
        return best is { } b && Math.Abs((b.AtUtc - at).TotalSeconds) <= 35 && double.IsFinite(b.Value) ? b.Value : null;
    }
}
