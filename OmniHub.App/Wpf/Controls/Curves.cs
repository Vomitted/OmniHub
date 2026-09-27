// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Media;
using OmniHub.Core.Telemetry;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// A series drawn as a smooth line with a filled area under it -- the live chart's curves and the
/// cards' traces, one implementation so the two cannot come to disagree about what a gap is.
///
/// Min/max bucketing keeps every extreme (TimeSeriesDecimator), the spline never passes a value that
/// was not read (Spline), and a reading missing for longer than its series tolerates ends the curve
/// rather than being bridged.
/// </summary>
internal static class Curves
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // Read every tick; anything else is refreshed slower and allowed a longer silence before a break.
    private static readonly HashSet<string> FastKeys = new() { "cpu", "fan", "fan2" };

    public static TimeSpan GapFor(string key) => FastKeys.Contains(key) ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(95);

    /// <summary>
    /// The line and the area between it and <paramref name="floor"/>, across <paramref name="width"/>
    /// for the window [from, to], with <paramref name="y"/> mapping a value to its height.
    /// </summary>
    public static (StreamGeometry Line, StreamGeometry Area) Draw(
        string key, IReadOnlyList<TimePoint> points, DateTime from, DateTime to,
        double width, double floor, Func<double, double> y, int columns)
    {
        double span = Math.Max(1, (to - from).TotalSeconds);
        var segments = TimeSeriesDecimator.Decimate(points, from, to, Math.Max(8, columns), GapFor(key));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            foreach (var segment in segments)
            {
                if (segment.Count < 2) continue;
                var at = segment.Select(p => (X: (p.AtUtc - from).TotalSeconds / span * width, Y: y(p.Value))).ToList();

                l.BeginFigure(new Point(at[0].X, at[0].Y), false, false);
                a.BeginFigure(new Point(at[0].X, floor), true, true);
                a.LineTo(new Point(at[0].X, at[0].Y), false, false);
                foreach (var c in Spline.Through(at))
                {
                    l.BezierTo(new Point(c.C1.X, c.C1.Y), new Point(c.C2.X, c.C2.Y), new Point(c.End.X, c.End.Y), true, true);
                    a.BezierTo(new Point(c.C1.X, c.C1.Y), new Point(c.C2.X, c.C2.Y), new Point(c.End.X, c.End.Y), false, true);
                }
                a.LineTo(new Point(at[^1].X, floor), false, false);
            }
        }
        line.Freeze();
        area.Freeze();
        return (line, area);
    }

    /// <summary>The fill under a curve: its own colour fading to nothing at the floor.</summary>
    public static Brush Fill(Color colour, byte top = 0x4C)
    {
        var fill = new LinearGradientBrush(Color.FromArgb(top, colour.R, colour.G, colour.B),
                                           Color.FromArgb(0x00, colour.R, colour.G, colour.B), 90);
        fill.Freeze();
        return fill;
    }
}
