using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OmniHub.Core.Telemetry;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Controls;

/// <summary>One line on the chart, and how to read it.</summary>
public sealed class ChartSeries
{
    public required string Name { get; init; }
    public required Brush Stroke { get; init; }

    public string Unit { get; init; } = "";
    public string Format { get; init; } = "0.#";

    /// <summary>Fixed range, or null to scale to what is visible.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }

    /// <summary>At most one series should fill, or the fills hide each other.</summary>
    public bool Fill { get; init; }

    /// <summary>Null means three times this series' own median interval.</summary>
    public TimeSpan? GapThreshold { get; init; }

    /// <summary>The value axis is labelled for exactly one series. This is it.</summary>
    public bool IsPrimary { get; init; }
}

/// <summary>
/// A multi-series time-series chart.
///
/// The application had exactly one chart before this: WaveformChart, sixty samples of one metric
/// on one tab, no time axis, no second series, no way to look at anything but the last two
/// minutes. Meanwhile six logs were being written and none read back. This is the instrument
/// that makes fourteen days of history, and a comparison between two runs, possible at all.
///
/// The arithmetic lives in OmniHub.Core.Telemetry -- decimation, gap detection, axis ticks --
/// because OmniHub.Tests cannot reference OmniHub.App, so anything here is untestable by
/// construction. This class is a renderer and nothing else, which is also why it is short for
/// what it does.
///
/// Three things about how it draws are deliberate:
///
///   - Decimation and geometry building happen OFF the UI thread, and the geometry is Freeze()d
///     there. A frozen Freezable is safe to hand across threads, and that is what makes tens of
///     thousands of points acceptable at all: only the assignment touches the dispatcher.
///   - Redraws are coalesced through a timer rather than run per Append. At the 2 s poll rate
///     that is irrelevant; for frame-time data at 165 Hz it is the difference between working
///     and not.
///   - A gap is drawn as a gap. The Core decimator returns segments, one figure per segment, and
///     nothing joins them. Drawing through the 21,512-second hole in this machine's own
///     15 September trace would put a straight line across a quarter of a one-day view, on data
///     that does not exist.
/// </summary>
public partial class TimeSeriesChart : UserControl, IDisposable
{
    private sealed class SeriesState
    {
        public required ChartSeries Definition { get; init; }
        public List<TimePoint> Points { get; } = new();
        public Path Line { get; } = new() { StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round };
        public Path Area { get; } = new();
    }

    private readonly List<SeriesState> _series = new();
    private readonly List<(DateTime AtUtc, string Label)> _markers = new();

    private readonly Canvas _grid = new();
    private readonly Canvas _markerLayer = new();
    private readonly Line _crosshair = new() { StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _emptyNote = new()
    {
        Text = "No data in this range.",
        Visibility = Visibility.Collapsed,
        FontSize = 11.5,
    };

    private DateTime _from = DateTime.UtcNow.AddMinutes(-2);
    private DateTime _to = DateTime.UtcNow;
    private TimeSpan? _liveSpan = TimeSpan.FromMinutes(2);

    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _dirty;
    private int _renderToken;

    /// <summary>Fires with the hovered instant and each series' value there, null where absent.</summary>
    public event Action<DateTime, IReadOnlyList<double?>>? OnHover;

    public TimeSeriesChart()
    {
        InitializeComponent();

        Plot.Children.Add(_grid);
        Plot.Children.Add(_markerLayer);
        Plot.Children.Add(_crosshair);
        Plot.Children.Add(_emptyNote);

        _emptyNote.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
        _emptyNote.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        _crosshair.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");

        SizeChanged += (_, _) => Invalidate();
        Loaded += (_, _) => { _redraw.Start(); Invalidate(); };
        Unloaded += (_, _) => _redraw.Stop();

        _redraw.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            _ = RenderAsync();
        };
    }

    /// <summary>Adds a series and returns its index, which is how data is later addressed.</summary>
    public int AddSeries(ChartSeries series)
    {
        var state = new SeriesState { Definition = series };
        state.Line.Stroke = series.Stroke;

        if (series.Fill)
        {
            state.Area.Fill = new LinearGradientBrush(
                ColorAt(series.Stroke, 0x40), ColorAt(series.Stroke, 0x00), 90);
            Plot.Children.Insert(0, state.Area);
        }

        Plot.Children.Add(state.Line);

        _series.Add(state);
        BuildLegend();
        Invalidate();
        return _series.Count - 1;
    }

    /// <summary>A fixed historical window. Stops live following.</summary>
    public void SetWindow(DateTime fromUtc, DateTime toUtc)
    {
        _liveSpan = null;
        _from = fromUtc;
        _to = toUtc;
        Invalidate();
    }

    /// <summary>Follow the clock, showing the trailing span.</summary>
    public void SetLiveWindow(TimeSpan span)
    {
        _liveSpan = span;
        Invalidate();
    }

    public void SetSeriesData(int index, IReadOnlyList<TimePoint> points)
    {
        if (index < 0 || index >= _series.Count) return;

        var state = _series[index];
        state.Points.Clear();
        state.Points.AddRange(points);
        Invalidate();
    }

    /// <summary>
    /// Appends one live sample.
    ///
    /// Trims to twice the live window rather than to the window itself, so widening the window
    /// does not instantly blank the left half of the chart.
    /// </summary>
    public void Append(int index, DateTime atUtc, double value)
    {
        if (index < 0 || index >= _series.Count) return;

        var points = _series[index].Points;
        points.Add(new TimePoint(atUtc, value));

        if (_liveSpan is { } span)
        {
            DateTime cutoff = DateTime.UtcNow - span - span;
            int drop = 0;
            while (drop < points.Count && points[drop].AtUtc < cutoff) drop++;
            if (drop > 0) points.RemoveRange(0, drop);
        }

        Invalidate();
    }

    /// <summary>Discrete events drawn as ticks on the time axis -- power transitions, mostly.</summary>
    public void SetMarkers(IEnumerable<(DateTime AtUtc, string Label)> markers)
    {
        _markers.Clear();
        _markers.AddRange(markers);
        Invalidate();
    }

    /// <summary>Requests a redraw on the next coalescing tick.</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>
    /// Stops the redraw timer.
    ///
    /// Unloaded already does this when the control leaves the visual tree, but a view disposed
    /// during application shutdown is not necessarily unloaded first -- and a timer still firing
    /// into a dispatcher that is closing down is the kind of loose end that turns an orderly exit
    /// into an untidy one.
    /// </summary>
    public void Dispose()
    {
        try { _redraw.Stop(); } catch { }
        _dirty = false;
    }

    // ------------------------------------------------------------------ rendering

    private async Task RenderAsync()
    {
        double width = Plot.ActualWidth, height = Plot.ActualHeight;
        if (width < 8 || height < 8) return;

        if (_liveSpan is { } span)
        {
            _to = DateTime.UtcNow;
            _from = _to - span;
        }

        int token = ++_renderToken;
        DateTime from = _from, to = _to;

        // One bucket per pixel column, by construction: the decimator is told the width.
        int columns = Math.Max(16, (int)width);

        var work = _series.Select(s => (State: s, Points: (IReadOnlyList<TimePoint>)s.Points.ToArray())).ToArray();

        var built = await Task.Run(() => work.Select(w =>
        {
            TimeSpan gap = GapFor(w.State.Definition, w.Points);
            return (w.State, Segments: TimeSeriesDecimator.Decimate(w.Points, from, to, columns, gap));
        }).ToArray()).ConfigureAwait(true);

        // A later render started while this one was working: its result is the current one.
        if (token != _renderToken) return;

        bool anything = built.Any(b => b.Segments.Count > 0);
        _emptyNote.Visibility = anything ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(_emptyNote, Math.Max(0, width / 2 - 70));
        Canvas.SetTop(_emptyNote, Math.Max(0, height / 2 - 9));

        DrawGrid(width, height, from, to);
        DrawMarkers(width, height, from, to);

        foreach (var (state, segments) in built)
        {
            var (lo, hi) = RangeFor(state, segments);

            var geometry = new PathGeometry();
            var area = new PathGeometry();

            foreach (var segment in segments)
            {
                if (segment.Count == 0) continue;

                var start = Project(segment[0], from, to, lo, hi, width, height);
                var figure = new PathFigure { StartPoint = start, IsFilled = false };

                for (int i = 1; i < segment.Count; i++)
                    figure.Segments.Add(new LineSegment(Project(segment[i], from, to, lo, hi, width, height), true));

                geometry.Figures.Add(figure);

                if (state.Definition.Fill)
                {
                    var filled = figure.Clone();
                    var last = Project(segment[^1], from, to, lo, hi, width, height);
                    filled.Segments.Add(new LineSegment(new Point(last.X, height), true));
                    filled.Segments.Add(new LineSegment(new Point(start.X, height), true));
                    filled.IsClosed = true;
                    filled.IsFilled = true;
                    area.Figures.Add(filled);
                }
            }

            // Frozen so the cost of building it was genuinely paid off the UI thread, and so
            // WPF can render it without taking a lock per frame.
            geometry.Freeze();
            area.Freeze();

            state.Line.Data = geometry;
            state.Area.Data = area;

            if (state.Definition.IsPrimary || _series.Count == 1) DrawValueAxis(lo, hi, state, height);
        }
    }

    private static TimeSpan GapFor(ChartSeries definition, IReadOnlyList<TimePoint> points)
    {
        if (definition.GapThreshold is { } explicitGap) return explicitGap;

        var stamps = new List<DateTime>(points.Count);
        foreach (var p in points) stamps.Add(p.AtUtc);

        return SampleGaps.MedianInterval(stamps) is { } median
            ? median * SampleGaps.DefaultFactor
            : TimeSpan.FromDays(365);
    }

    private static (double Lo, double Hi) RangeFor(SeriesState state, IReadOnlyList<IReadOnlyList<TimePoint>> segments)
    {
        if (state.Definition.Min is { } fixedMin && state.Definition.Max is { } fixedMax && fixedMax > fixedMin)
            return (fixedMin, fixedMax);

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var segment in segments)
            foreach (var p in segment)
            {
                if (p.Value < lo) lo = p.Value;
                if (p.Value > hi) hi = p.Value;
            }

        if (lo > hi) return (0, 1);

        // A flat series would otherwise divide by zero and draw along the top edge.
        if (Math.Abs(hi - lo) < 0.001) { lo -= 1; hi += 1; }

        double pad = (hi - lo) * 0.08;
        return (state.Definition.Min ?? lo - pad, state.Definition.Max ?? hi + pad);
    }

    private static Point Project(TimePoint p, DateTime from, DateTime to, double lo, double hi,
                                 double width, double height)
    {
        double x = (p.AtUtc - from).TotalSeconds / Math.Max(0.001, (to - from).TotalSeconds) * width;
        double y = height - (p.Value - lo) / Math.Max(0.001, hi - lo) * height;
        return new Point(x, Math.Clamp(y, -2, height + 2));
    }

    private void DrawGrid(double width, double height, DateTime from, DateTime to)
    {
        _grid.Children.Clear();
        TimeAxisCanvas.Children.Clear();

        var gridBrush = (Brush)FindResource("GridLineBrush");
        string format = TimeAxis.LabelFormat(to - from);

        foreach (var tick in TimeAxis.Ticks(from, to))
        {
            double x = (tick - from).TotalSeconds / Math.Max(0.001, (to - from).TotalSeconds) * width;

            _grid.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height,
                Stroke = gridBrush, StrokeThickness = 1,
            });

            // Local time on the label, UTC everywhere underneath. Nobody reads a chart of their
            // own machine in UTC, and every stored instant stays UTC so the data is unambiguous.
            var label = new TextBlock { Text = tick.ToLocalTime().ToString(format), FontSize = 9.5 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

            Canvas.SetLeft(label, Math.Max(0, x - 22));
            TimeAxisCanvas.Children.Add(label);
        }
    }

    private void DrawValueAxis(double lo, double hi, SeriesState state, double height)
    {
        ValueAxis.Children.Clear();

        for (int i = 0; i <= 4; i++)
        {
            double value = lo + (hi - lo) * i / 4.0;
            double y = height - height * i / 4.0;

            var label = new TextBlock
            {
                Text = value.ToString(state.Definition.Format),
                FontSize = 9.5,
                TextAlignment = TextAlignment.Right,
                Width = 38,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
            label.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

            Canvas.SetTop(label, Math.Clamp(y - 7, 0, Math.Max(0, height - 14)));
            ValueAxis.Children.Add(label);
        }
    }

    private void DrawMarkers(double width, double height, DateTime from, DateTime to)
    {
        _markerLayer.Children.Clear();

        // A long window can hold hundreds of transitions, and drawing them all turns the axis
        // into a solid band that says nothing. Real density is a few dozen a day.
        const int Cap = 200;
        int drawn = 0;

        foreach (var (at, _) in _markers)
        {
            if (at < from || at > to) continue;
            if (drawn++ >= Cap) break;

            double x = (at - from).TotalSeconds / Math.Max(0.001, (to - from).TotalSeconds) * width;

            var tick = new Line { X1 = x, X2 = x, Y1 = height - 6, Y2 = height, StrokeThickness = 1 };
            tick.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            _markerLayer.Children.Add(tick);
        }
    }

    private void BuildLegend()
    {
        Legend.Children.Clear();

        foreach (var state in _series)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };

            row.Children.Add(new Ellipse
            {
                Width = 6, Height = 6,
                Fill = state.Definition.Stroke,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
            });

            var name = new TextBlock { Text = state.Definition.Name, FontSize = 10.5 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            name.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            row.Children.Add(name);

            Legend.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ hover

    private void Plot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        double width = Plot.ActualWidth, height = Plot.ActualHeight;
        if (width < 8) return;

        double x = e.GetPosition(Plot).X;
        DateTime at = _from + TimeSpan.FromSeconds((_to - _from).TotalSeconds * Math.Clamp(x / width, 0, 1));

        _crosshair.X1 = _crosshair.X2 = x;
        _crosshair.Y1 = 0;
        _crosshair.Y2 = height;
        _crosshair.Visibility = Visibility.Visible;

        var values = new List<double?>(_series.Count);
        var parts = new List<string>(_series.Count);

        foreach (var state in _series)
        {
            double? value = NearestValue(state, at);
            values.Add(value);

            parts.Add(value is { } v
                ? $"{state.Definition.Name} {v.ToString(state.Definition.Format)}{state.Definition.Unit}"
                : $"{state.Definition.Name} --");
        }

        Readout.Text = at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "   " + string.Join("   ", parts);
        OnHover?.Invoke(at, values);
    }

    /// <summary>
    /// The value at the hovered instant, or null.
    ///
    /// Null inside a gap rather than the nearest point on the far side of it. Snapping across a
    /// hole would report a reading from hours away as though it were the one under the pointer,
    /// which is worse than saying nothing, because the crosshair looks authoritative.
    /// </summary>
    private static double? NearestValue(SeriesState state, DateTime at)
    {
        var points = state.Points;
        if (points.Count == 0) return null;

        int lo = 0, hi = points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (points[mid].AtUtc < at) lo = mid + 1; else hi = mid;
        }

        var candidate = points[lo];
        if (lo > 0 && Math.Abs((points[lo - 1].AtUtc - at).TotalSeconds) < Math.Abs((candidate.AtUtc - at).TotalSeconds))
            candidate = points[lo - 1];

        TimeSpan tolerance = GapFor(state.Definition, points);

        return Math.Abs((candidate.AtUtc - at).TotalSeconds) <= tolerance.TotalSeconds
            ? candidate.Value
            : null;
    }

    private void Plot_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _crosshair.Visibility = Visibility.Collapsed;
        Readout.Text = "";
    }

    private static Color ColorAt(Brush brush, byte alpha)
    {
        Color c = brush is SolidColorBrush s ? s.Color : Colors.Gray;
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }
}
