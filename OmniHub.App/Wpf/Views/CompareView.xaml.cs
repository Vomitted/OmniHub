using System.Windows;
using System.Windows.Controls;
using OmniHub.Core.Telemetry;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// Did the thing I just changed actually do anything?
///
/// That question is the reason this release exists, and answering it needs two stretches of trace
/// and an honest opinion about whether they differ. Both halves live in Core, where they are
/// tested; this screen picks the ranges and shows what comes back.
///
/// No sessions to start or stop. A session would be a name and a time range, and it would only
/// ever work forwards -- whereas the fourteen days already on disk hold the answer to changes made
/// before anybody thought to press record. So the ranges are chosen here, the common case being a
/// window against the window immediately before it, which is exactly the shape of "I changed
/// something a few minutes ago".
/// </summary>
public partial class CompareView : UserControl, IDisposable
{
    private readonly TelemetryHistory _history = new();

    private CancellationTokenSource? _loading;

    /// <summary>
    /// How much trace to put on each side.
    ///
    /// Twenty minutes is the shortest offered because the comparison needs three hundred samples
    /// a side to say anything, and at two seconds a sample that is ten minutes -- so a shorter
    /// option would exist only to be refused.
    /// </summary>
    private static readonly (string Label, TimeSpan Span)[] Windows =
    {
        ("20 MIN", TimeSpan.FromMinutes(20)),
        ("1 H", TimeSpan.FromHours(1)),
        ("3 H", TimeSpan.FromHours(3)),
    };

    /// <summary>
    /// Where the baseline sits.
    ///
    /// Immediately before is "did my change help". A day earlier is "is this machine behaving
    /// differently than it was", which is the other question people actually ask, and it needs a
    /// baseline that is not adjacent.
    /// </summary>
    private static readonly (string Label, TimeSpan Offset)[] Baselines =
    {
        ("THE PERIOD JUST BEFORE", TimeSpan.Zero),
        ("24 H EARLIER", TimeSpan.FromHours(24)),
    };

    private TimeSpan _window = Windows[0].Span;
    private TimeSpan _baselineOffset = TimeSpan.Zero;

    public CompareView()
    {
        InitializeComponent();

        BuildPills(WindowPills, "CompareWindow", Windows.Select(w => w.Label),
                   i => { _window = Windows[i].Span; Load(); }, selected: 0);

        BuildPills(BaselinePills, "CompareBaseline", Baselines.Select(b => b.Label),
                   i =>
                   {
                       _baselineOffset = Baselines[i].Offset;

                       // A pill and a named run are two answers to one question. Choosing a pill
                       // means the pill, or picking one would appear to do nothing while the run
                       // silently kept overriding it.
                       _baselineSession = null;

                       _suppressSessionPicker = true;
                       if (SessionPicker.Items.Count > 0) SessionPicker.SelectedIndex = 0;
                       _suppressSessionPicker = false;

                       Load();
                   }, selected: 0);

        RefreshSessions();

        Loaded += (_, _) => { RefreshSessions(); Load(); };
        Unloaded += (_, _) => { _loading?.Cancel(); _loading = null; };
    }

    // ---------- recording a run ----------

    private OmniHub.Core.Telemetry.SessionLog _sessions = OmniHub.Core.Telemetry.SessionLog.Empty;
    private OmniHub.Core.Telemetry.Session? _baselineSession;
    private bool _suppressSessionPicker;

    /// <summary>
    /// Re-reads the sessions and rebuilds everything that shows them.
    ///
    /// Read from disk rather than held, because the file is the shared state: a session started
    /// here, left open through a crash, and closed by the next start has to be seen as it is
    /// rather than as this screen last remembered it.
    /// </summary>
    private void RefreshSessions()
    {
        _sessions = OmniHub.Core.Telemetry.SessionLog.Load();

        var open = _sessions.Open;

        SessionBtn.Content = open is null ? "START" : "STOP";
        SessionNameBox.IsEnabled = open is null;

        SessionStatus.Text = open is null
            ? "Name a run and start it before making a change, then stop it afterwards. The trace behind a saved run is kept past the usual fourteen days, so it is still there to compare against."
            : $"Recording \"{open.Name}\", started {open.StartedUtc.ToLocalTime():HH:mm}.";

        _suppressSessionPicker = true;
        SessionPicker.Items.Clear();
        SessionPicker.Items.Add("Compare against a named run...");

        // Newest first: the one somebody wants is almost always the one they just recorded.
        foreach (var session in _sessions.Sessions.Where(x => x.IsClosed).Reverse())
            SessionPicker.Items.Add($"{session.Name}  ({session.StartedUtc.ToLocalTime():MMM d HH:mm}, {Describe(session.Duration())})");

        SessionPicker.SelectedIndex = 0;
        SessionPicker.IsEnabled = SessionPicker.Items.Count > 1;
        _suppressSessionPicker = false;
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#} h" : $"{span.TotalMinutes:0} min";

    private void SessionBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sessions.Open is not null)
        {
            _sessions = _sessions.Stop(DateTime.UtcNow);
        }
        else
        {
            string name = SessionNameBox.Text.Trim();
            if (name.Length == 0) { SessionStatus.Text = "Give the run a name first."; return; }

            // The rail and the mode are recorded with it because a comparison across them is not a
            // comparison: two runs on different power sources differ by the rail before they
            // differ by anything that was deliberately changed.
            var settings = AppSettings.Load();

            _sessions = _sessions.Start(new OmniHub.Core.Telemetry.Session(
                name,
                DateTime.UtcNow,
                null,
                OmniHub.Core.Optimize.PowerSourceWatcher.Read().ToString(),
                settings.FanControlMode.ToString(),
                System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? ""));

            SessionNameBox.Text = "";
        }

        try { _sessions.Save(); }
        catch (Exception ex) { SessionStatus.Text = $"Could not save the run ({ex.Message})."; return; }

        RefreshSessions();
    }

    private void SessionPicker_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSessionPicker) return;

        // Index zero is the prompt, which means "no named baseline" rather than a run called that.
        int index = SessionPicker.SelectedIndex - 1;
        var closed = _sessions.Sessions.Where(x => x.IsClosed).Reverse().ToList();

        _baselineSession = index >= 0 && index < closed.Count ? closed[index] : null;

        Load();
    }

    /// <summary>Cancels any read in flight. MainWindow disposes whichever views can be.</summary>
    public void Dispose()
    {
        try { _loading?.Cancel(); } catch { }
        _loading = null;
    }

    private void BuildPills(
        WrapPanel host, string group, IEnumerable<string> labels, Action<int> chosen, int selected)
    {
        int index = 0;

        foreach (string label in labels)
        {
            int captured = index;

            var pill = new RadioButton
            {
                Content = label,
                GroupName = group,
                Style = (Style)FindResource("PillRadioStyle"),
                Height = 28,
                MinWidth = 62,
                Margin = new Thickness(0, 0, 3, 3),
                IsChecked = index == selected,
            };

            pill.Checked += (_, _) => chosen(captured);
            host.Children.Add(pill);
            index++;
        }
    }

    /// <summary>
    /// Reads both ranges and shows the comparison.
    ///
    /// Cancellable, because three hours of trace is several thousand rows a side and somebody
    /// clicking through the pills should not queue four of those behind each other.
    /// </summary>
    private async void Load()
    {
        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;

        DateTime now = DateTime.UtcNow;

        DateTime recentFrom = now - _window;

        // A named run replaces the offset entirely, and brings its own length with it: the whole
        // point of naming it was that its boundaries are the ones that matter, not a window of
        // somebody else's choosing laid over the top.
        DateTime baselineTo, baselineFrom;

        if (_baselineSession is { EndedUtc: { } ended } session)
        {
            baselineFrom = session.StartedUtc;
            baselineTo = ended;
        }
        else
        {
            baselineTo = _baselineOffset == TimeSpan.Zero ? recentFrom : now - _baselineOffset;
            baselineFrom = baselineTo - _window;
        }

        Status.Text = "Reading...";
        Verdict.Text = "";
        IntervalNote.Text = "";
        MetricRows.Children.Clear();

        try
        {
            var baseline = await _history.ReadThermalAsync(baselineFrom, baselineTo, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            var recent = await _history.ReadThermalAsync(recentFrom, now, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            var result = RunComparison.Compare(baseline, recent);
            _lastResult = result;

            Status.Text =
                $"Baseline {baselineFrom.ToLocalTime():MMM d HH:mm} to {baselineTo.ToLocalTime():HH:mm}, "
                + $"recent {recentFrom.ToLocalTime():MMM d HH:mm} to {now.ToLocalTime():HH:mm}.";

            Verdict.Text = result.Verdict;
            IntervalNote.Text = DescribeIntervals(result);

            AddRow("Samples", $"{result.A.Samples:N0}", $"{result.B.Samples:N0}");
            AddRow("Measured", Humanise(result.A.Covered), Humanise(result.B.Covered));
            AddRow("Mean temperature", Degrees(result.A.MeanTempC), Degrees(result.B.MeanTempC));
            AddRow("90th percentile", Degrees(result.A.P90TempC), Degrees(result.B.P90TempC));
            AddRow("Hottest", Degrees(result.A.MaxTempC), Degrees(result.B.MaxTempC));
            AddRow("Median fan", Percent(result.A.MedianFanPercent), Percent(result.B.MedianFanPercent));
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later click. The newer load owns the status line.
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not read the trace ({ex.Message}).";
        }
    }

    /// <summary>
    /// The last comparison, kept so it can be exported without being recomputed.
    ///
    /// Recomputing would re-read the trace, and between the two reads the "recent" window would
    /// have moved -- so the exported file would not be the one on screen. A proof that differs
    /// from what was shown is not much of a proof.
    /// </summary>
    private RunComparisonResult? _lastResult;

    private void ExportCsvBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is not { } result)
        {
            Status.Text = "Nothing to export yet.";
            return;
        }

        try
        {
            string? path = Export.SaveText(
                $"omnihub-compare-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv",
                "CSV file (*.csv)|*.csv",
                CsvExport.Comparison(result));

            if (path is { Length: > 0 }) Status.Text = $"Saved to {path}.";
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not save ({ex.Message}).";
        }
    }

    private void ExportPngBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = Export.SavePng(this, $"omnihub-compare-{DateTime.Now:yyyy-MM-dd-HHmmss}.png");

            if (path is { Length: > 0 }) Status.Text = $"Saved to {path}.";
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not save the image ({ex.Message}).";
        }
    }

    /// <summary>
    /// The intervals themselves, for somebody who wants the range rather than the sentence.
    ///
    /// Shown because the interval is the honest part: a difference of "between -0.3 and +1.1 C"
    /// says far more about how much to trust it than any adjective could.
    /// </summary>
    private static string DescribeIntervals(RunComparisonResult result)
    {
        var parts = new List<string>();

        if (result.TemperatureDifference is { } temp)
            parts.Add($"temperature {temp.Low:+0.0;-0.0} to {temp.High:+0.0;-0.0} C");

        if (result.FanDifference is { } fan)
            parts.Add($"fan duty {fan.Low:+0.0;-0.0} to {fan.High:+0.0;-0.0} %");

        if (parts.Count == 0) return "";

        return "95% intervals for the change from baseline to recent: " + string.Join(", ", parts)
             + ". Each is widened to account for how strongly consecutive samples track each other "
             + "-- on this machine's trace a sample is 98% predictable from the one before it, so a "
             + "run of six hundred readings carries far less independent information than it appears to.";
    }

    private void AddRow(string label, string baseline, string recent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(Cell(label, "MutedText", 0));
        grid.Children.Add(Cell(baseline, "BodyText", 1));
        grid.Children.Add(Cell(recent, "BodyText", 2));

        MetricRows.Children.Add(grid);
    }

    private TextBlock Cell(string text, string style, int column)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(StyleProperty, style);
        Grid.SetColumn(block, column);
        return block;
    }

    /// <summary>Two dashes for an absent reading, the same as everywhere else here.</summary>
    private static string Degrees(double? value) => value is { } v ? $"{v:0.0} C" : "--";

    private static string Percent(double? value) => value is { } v ? $"{v:0} %" : "--";

    private static string Humanise(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:0} min"
        : $"{span.TotalHours:0.#} h";
}
