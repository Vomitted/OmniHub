// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Telemetry;
using Path = System.Windows.Shapes.Path;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// A card's last five minutes, in the space between its name and its figure: Studio's answer to a card
/// that was mostly empty on a wide screen, and the project's rule that history sits beside every
/// current value, drawn rather than written.
///
/// Against a real top only -- the same full scale as the card's bar, from zero. A trace whose top is not
/// known draws nothing rather than fitting itself to the data, which would make a flat line and a
/// storm look the same size. Subscribed while loaded, so a hidden Dashboard costs nothing.
/// </summary>
public sealed class Trace : Canvas
{
    private readonly MetricSource _source;
    private readonly Func<double?> _top;
    private readonly (string Key, string BrushKey)[] _series;
    private readonly Path[] _bases, _areas, _lines;
    private readonly Color?[] _tints;

    public Trace(MetricSource source, Func<double?> top, params (string Key, string BrushKey)[] series)
    {
        _source = source;
        _top = top;
        _series = series;
        _bases = series.Select(_ => new Path()).ToArray();
        _areas = series.Select(_ => new Path()).ToArray();
        _lines = series.Select(_ => new Path { StrokeThickness = 1.5 }).ToArray();
        _tints = new Color?[series.Length];

        ClipToBounds = true;
        IsHitTestVisible = false;
        MinHeight = 36;

        // As the live chart bands them: each fill over an opaque copy of its shape, so a lower curve's
        // colour covers a higher one's instead of mixing with it; the lines over every fill.
        for (int i = 0; i < series.Length; i++)
        {
            _bases[i].SetResourceReference(Shape.FillProperty, "PanelBrush");
            Children.Add(_bases[i]);
            Children.Add(_areas[i]);
        }
        foreach (var line in _lines) Children.Add(line);

        SizeChanged += (_, _) => Redraw();
        Loaded += (_, _) => { _source.Updated -= Redraw; _source.Updated += Redraw; Redraw(); };
        Unloaded += (_, _) => _source.Updated -= Redraw;
    }

    private void Redraw()
    {
        double w = ActualWidth, h = ActualHeight;
        bool drawable = w >= 20 && h >= 12 && _top() is > 0;
        for (int i = 0; i < _series.Length; i++)
        {
            if (!drawable)
            {
                _bases[i].Data = _areas[i].Data = _lines[i].Data = null;
                continue;
            }

            double top = _top()!.Value;
            double Y(double v) => h - 1 - Math.Clamp(v / top, 0, 1) * (h - 2);

            var (key, brushKey) = _series[i];
            var brush = (Brush)FindResource(brushKey);
            var to = DateTime.UtcNow;
            var (line, area) = Curves.Draw(key, _source.Recent(key), to - Curves.Window, to, w, h, Y, (int)(w / 4));

            _lines[i].Stroke = brush;
            _lines[i].Data = line;
            _bases[i].Data = _areas[i].Data = area;

            // From the brush as it is now, so a theme switch reaches the fill on the next reading.
            Color colour = brush is SolidColorBrush s ? s.Color : Colors.Gray;
            if (_tints[i] != colour)
            {
                _tints[i] = colour;
                _areas[i].Fill = Curves.Fill(colour, 0x38);
            }
        }
    }
}
