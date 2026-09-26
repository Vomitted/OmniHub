// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// The last stretch of one reading, each value stamped with when it was read.
///
/// The dashboard's history charts draw from this, and the reason it exists rather than the charts
/// collecting for themselves is when they would collect. A chart appends only while it is on
/// screen, and this application lives in the tray: opened after a game, a self-collecting chart
/// showed the few seconds since the window appeared -- empty at exactly the moment its history was
/// the thing worth seeing. This keeps recording whether or not anything is drawn.
///
/// A missing reading adds nothing, so a stretch without one reaches the chart as a gap in time,
/// which the chart already draws as a gap. Recording a zero or the last value instead would draw a
/// measurement nobody took.
/// </summary>
public sealed class RecentSeries
{
    private readonly List<TimePoint> _points = new();

    public RecentSeries(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(span), "A window has to have some length.");
        Span = span;
    }

    /// <summary>How far back the series reaches from its newest point.</summary>
    public TimeSpan Span { get; }

    /// <summary>The points, oldest first.</summary>
    public IReadOnlyList<TimePoint> Points => _points;

    public void Add(DateTime atUtc, double? value)
    {
        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return;

        _points.Add(new TimePoint(atUtc, v));

        // ponytail: RemoveRange shifts the list, O(n) per trim; n is under a thousand at the poll rate.
        DateTime cutoff = atUtc - Span;
        int stale = 0;
        while (stale < _points.Count && _points[stale].AtUtc < cutoff) stale++;
        if (stale > 0) _points.RemoveRange(0, stale);
    }
}
