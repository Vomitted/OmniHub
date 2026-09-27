// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// A smooth curve through measured points that never claims a value between them that was not
/// between them.
///
/// The console's live chart is drawn smooth, as the reference draws it. An ordinary Catmull-Rom
/// spline overshoots: between two readings it bulges past the higher one, drawing a peak no sensor
/// reported. Here each Bezier's control points are held to the height range of their own segment's
/// two ends, and a cubic Bezier never leaves the hull of its control points -- so every stretch of
/// the curve stays between the two readings it joins.
/// </summary>
public static class Spline
{
    /// <summary>One cubic Bezier: two control points and the point it ends at.</summary>
    public readonly record struct Cubic((double X, double Y) C1, (double X, double Y) C2, (double X, double Y) End);

    /// <summary>The curve through <paramref name="points"/>, one cubic per gap between them.</summary>
    public static IReadOnlyList<Cubic> Through(IReadOnlyList<(double X, double Y)> points)
    {
        var cubics = new List<Cubic>(Math.Max(0, points.Count - 1));

        for (int i = 0; i + 1 < points.Count; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Count - 1, i + 2)];

            double lo = Math.Min(p1.Y, p2.Y), hi = Math.Max(p1.Y, p2.Y);
            var c1 = (p1.X + (p2.X - p0.X) / 6, Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, lo, hi));
            var c2 = (p2.X - (p3.X - p1.X) / 6, Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, lo, hi));

            cubics.Add(new Cubic(c1, c2, p2));
        }

        return cubics;
    }
}
