// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Hardware;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The console's top bar, as the reference draws it (technical/TECHNICAL-v5-ui.md, section 9): a
/// status pill at the left, a few readings at the right in their components' colours, and the way
/// to Settings.
///
/// The console look keeps it sparse, as the reference does: three readings, the page below doing
/// the rest. That was argued as calm, and the user's answer was to switch back to the classic look,
/// whose bar has six readings with a trace each, within minutes of seeing it. Studio's bar is dense
/// again for that reason -- six readings, each trace in its component's colour -- and keeps the
/// reference's shape around them. The reference's bell is left out of both: this application has no
/// notifications, and a bell that does nothing would be decoration pretending to be a feature.
/// </summary>
public sealed class ConsoleBar : AlternateInterface
{
    private readonly Func<(string Words, string DotKey)> _status;
    private readonly Action _openSettings;
    private readonly bool _dense;

    private const double TraceWidth = 44, TraceHeight = 14;

    private readonly Ellipse _dot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new();
    private readonly List<(string Key, string Unit, TextBlock Value, Path? Trace, FrameworkElement Cell)> _readings = new();

    /// <param name="status">Who is steering the fans, in words, and the colour of the dot beside them.</param>
    /// <param name="openSettings">Where the settings button goes.</param>
    /// <param name="dense">
    /// Studio's bar: six readings, each with its recent trace, instead of the console's three. The user
    /// went back to the look whose bar had them within minutes of seeing the sparse one.
    /// </param>
    public ConsoleBar(HardwareContext ctx, MetricSource metrics, Func<(string Words, string DotKey)> status, Action openSettings, bool dense = false)
        : base(ctx, metrics, null)
    {
        _status = status;
        _openSettings = openSettings;
        _dense = dense;
        Start();
    }

    protected override UIElement Build()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // The pill: a dot and the fan state in small capitals.
        _statusText.FontFamily = Font("MonoFont");
        _statusText.FontSize = 10.5;
        _statusText.FontWeight = FontWeights.Bold;
        _statusText.Foreground = Brush("TextMutedBrush");
        _statusText.Margin = new Thickness(8, 0, 0, 0);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_dot);
        pillRow.Children.Add(_statusText);
        row.Children.Add(new Border
        {
            Child = pillRow,
            Padding = new Thickness(12, 6, 14, 6),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = Brush("PanelAltBrush"),
            BorderBrush = Brush("BorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // The readings, each in its component's colour, divided by hairlines.
        var readings = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var sparse = new[]
        {
            ("cpu", "CPU DIE", " °C", "MetricCpuBrush"),
            ("gpuclk", "GPU CLOCK", " MHz", "MetricGpuBrush"),
            ("fan", "FANS", " RPM", "MetricFanBrush"),
        };
        var dense = new[]
        {
            ("cpu", "CPU DIE", " °C", "MetricCpuBrush"),
            ("gpu", "GPU", " °C", "MetricGpuBrush"),
            ("fan", "FANS", " RPM", "MetricFanBrush"),
            ("pkg", "PACKAGE", " W", "MetricCpuBrush"),
            ("gpuw", "GPU POWER", " W", "MetricGpuBrush"),
            ("cpuload", "CPU LOAD", " %", "MetricCpuBrush"),
        };
        foreach (var (key, caption, unit, colour) in _dense ? dense : sparse)
        {
            if (readings.Children.Count > 0)
                readings.Children.Add(new Border { Width = 1, Height = 24, Margin = new Thickness(_dense ? 14 : 18, 0, _dense ? 14 : 18, 0), Background = Brush("BorderBrush") });

            var cell = new StackPanel();
            cell.Children.Add(new TextBlock
            {
                Text = caption,
                FontFamily = Font("MonoFont"),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("TextFaintBrush"),
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            var value = new TextBlock
            {
                FontFamily = Font("MonoFont"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(colour),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 2, 0, 0),
            };

            // Dense: the trace beside the figure, scaled to its own window as the classic bar's are,
            // so it shows movement and the figure carries the level.
            Path? trace = null;
            if (_dense)
            {
                trace = new Path
                {
                    Width = TraceWidth, Height = TraceHeight, StrokeThickness = 1.25,
                    StrokeLineJoin = PenLineJoin.Round, Stroke = Brush(colour), Opacity = 0.9,
                    Margin = new Thickness(0, 0, 8, 1), VerticalAlignment = VerticalAlignment.Bottom,
                };
                var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                line.Children.Add(trace);
                line.Children.Add(value);
                cell.Children.Add(line);
            }
            else cell.Children.Add(value);

            readings.Children.Add(cell);
            _readings.Add((key, unit, value, trace, cell));
        }

        // ponytail: two fixed breakpoints for the dense bar -- traces go below 1,250 px, the last two
        // readings below 1,000 -- rather than measuring each cell; measure if the readings change.
        if (_dense)
            row.SizeChanged += (_, e) =>
            {
                for (int i = 0; i < _readings.Count; i++)
                {
                    var (_, _, _, trace, cell) = _readings[i];
                    if (trace is not null) trace.Visibility = e.NewSize.Width >= 1250 ? Visibility.Visible : Visibility.Collapsed;
                    bool shown = i < 4 || e.NewSize.Width >= 1000;
                    cell.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                    if (i > 0) readings.Children[i * 2 - 1].Visibility = cell.Visibility;   // its divider
                }
            };

        var settings = new Button
        {
            Width = 34,
            Height = 30,
            Margin = new Thickness(22, 0, 0, 0),
            Padding = new Thickness(0),
            Style = TryFindResource("FlatButtonStyle") as Style,
            ToolTip = "Settings",
            Content = new Path
            {
                Data = Geometry.Parse("M2,4 H14 M2,8 H14 M2,12 H14 M5,2.5 V5.5 M11,6.5 V9.5 M7,10.5 V13.5"),
                Stroke = Brush("TextMutedBrush"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
            },
        };
        settings.Click += (_, _) => _openSettings();

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(readings);
        right.Children.Add(settings);
        Grid.SetColumn(right, 2);
        row.Children.Add(right);

        return row;
    }

    protected override void Tick()
    {
        var (words, dotKey) = _status();
        _statusText.Text = words;
        _dot.Fill = Brush(dotKey);

        foreach (var (key, unit, value, trace, _) in _readings)
        {
            var metric = OmniHub.Core.Telemetry.Metrics.Find(key);
            double? v = Metrics.Value(key);
            value.Text = v is { } x && metric is not null
                ? x.ToString(metric.Format, System.Globalization.CultureInfo.InvariantCulture) + unit
                : OmniHub.Core.Telemetry.Metrics.Unavailable;

            if (trace is null) continue;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                foreach (var segment in Metrics.History(key).Segments(TraceWidth, TraceHeight))
                {
                    g.BeginFigure(new Point(segment[0].X, segment[0].Y), false, false);
                    g.PolyLineTo(segment.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, true);
                }
            }
            geometry.Freeze();
            trace.Data = geometry;
        }
    }
}
