// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// A short rolling history, turned into points inside a box.
///
/// This exists so the overlay can show whether a number is climbing rather than only what it is
/// right now, which is the difference between "82 degrees" and "82 degrees and still going up".
/// The arithmetic lives here rather than in the control for the same reason the chart's does: the
/// test project cannot reference the App project, so anything left in a WPF control cannot be
/// asserted.
///
/// The range is the window's own minimum and maximum, not a fixed scale. A sparkline this small
/// cannot show an absolute level usefully, so it shows movement instead, and the number beside it
/// carries the level.
/// </summary>
public sealed class Sparkline
{
    /// <summary>Forty samples is about three and a half minutes at the overlay's five-second tick.</summary>
    public const int DefaultCapacity = 40;

    private readonly double?[] _ring;
    private int _next;
    private int _count;

    /// <param name="capacity">How many samples the window holds.</param>
    /// <param name="minSpan">
    /// The smallest range the box represents, in the reading's own unit. See <see cref="MinSpan"/>.
    /// </param>
    public Sparkline(int capacity = DefaultCapacity, double minSpan = 0)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "A sparkline needs room for at least two samples.");
        if (minSpan < 0 || double.IsNaN(minSpan)) throw new ArgumentOutOfRangeException(nameof(minSpan), "A span cannot be negative.");
        _ring = new double?[capacity];
        MinSpan = minSpan;
    }

    public int Capacity => _ring.Length;

    /// <summary>
    /// The smallest range the box stands for, so that noise stays small.
    ///
    /// Scaling to the window's own minimum and maximum is right for showing movement, and wrong
    /// when there is none: a die temperature wandering between 67.1 and 68.6 C, which is sensor
    /// noise, was stretched to the full height of the box and drawn as a jagged series of spikes
    /// beside a figure that had not really moved. A window narrower than this is centred in a band
    /// this wide instead, so a ripple reads as a ripple and a genuine swing still fills the box.
    /// </summary>
    public double MinSpan { get; }

    /// <summary>
    /// Records one sample. Null is an ordinary value here: it means the reading was unavailable at
    /// that moment, and it is kept as a hole rather than dropped, so the gap stays in the right
    /// place on the time axis instead of the line closing over it.
    /// </summary>
    public void Push(double? value)
    {
        _ring[_next] = value;
        _next = (_next + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    /// <summary>The window, oldest first.</summary>
    public IReadOnlyList<double?> Samples
    {
        get
        {
            var ordered = new double?[_count];
            int start = _count < _ring.Length ? 0 : _next;

            for (int i = 0; i < _count; i++) ordered[i] = _ring[(start + i) % _ring.Length];

            return ordered;
        }
    }

    /// <summary>
    /// The window as polyline segments inside a <paramref name="width"/> by <paramref name="height"/>
    /// box, with y already inverted for screen coordinates.
    ///
    /// Returned as segments rather than one list because an unavailable reading must not be drawn
    /// through. A line that closes over a gap says the value passed smoothly between the two ends,
    /// which is precisely the claim the missing sample failed to make. Segments shorter than two
    /// points are dropped -- a single point is not a trend, and a polyline of one draws nothing.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Segments(double width, double height)
    {
        var samples = Samples;

        double min = double.MaxValue, max = double.MinValue;
        int present = 0;

        foreach (var s in samples)
            if (s is { } v && !double.IsNaN(v)) { min = Math.Min(min, v); max = Math.Max(max, v); present++; }

        var segments = new List<IReadOnlyList<(double X, double Y)>>();
        if (present < 2) return segments;

        // A window that never moved is a real answer -- an idle machine holding one temperature --
        // so it draws down the middle rather than dividing by a zero range or pinning to an edge.
        double span = max - min;
        if (span < MinSpan)
        {
            min = (min + max - MinSpan) / 2;
            span = MinSpan;
        }

        double Y(double v) => span > 0 ? height - (v - min) / span * height : height / 2;

        // Spaced over the capacity, not over the samples taken so far, so a window that is still
        // filling grows from the left instead of stretching a handful of points across the box and
        // redrawing them all every tick.
        double step = width / (_ring.Length - 1);

        var run = new List<(double X, double Y)>();

        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i] is { } v && !double.IsNaN(v))
            {
                run.Add((i * step, Y(v)));
                continue;
            }

            if (run.Count >= 2) segments.Add(run);
            run = new List<(double X, double Y)>();
        }

        if (run.Count >= 2) segments.Add(run);

        return segments;
    }
}
