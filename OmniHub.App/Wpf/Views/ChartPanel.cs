using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.App.Wpf.Controls;
using OmniHub.Core.Telemetry;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// One subject from the thermal log, charted, at the size of a panel.
///
/// A glance rather than an instrument. The History tab already has the windows, the markers, the
/// hover readout and the export; this is the version somebody puts beside three metric cards to
/// see where a number has been over the last hour, and it deliberately has no controls of its own.
/// </summary>
public sealed class ChartPanel : UserControl, IDisposable
{
    /// <summary>
    /// An hour, refreshed every half minute.
    ///
    /// The log ticks at about two and a third seconds, so an hour is roughly 1,550 rows -- enough
    /// to show the shape of a gaming session without the two-hundred-thousand-row read a fourteen
    /// day window costs. Refreshing faster would not show more: the file is flushed on a
    /// ten-second interval.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    private readonly ChartSubject _subject;
    private readonly TimeSeriesChart _chart = new();
    private readonly TelemetryHistory _history = new();
    private readonly TextBlock _status = new();
    private readonly int[] _lines;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = Refresh };

    private CancellationTokenSource? _loading;

    public ChartPanel(string key)
    {
        Focusable = false;

        _subject = ChartSubjects.Find(key)
            ?? new ChartSubject(key, $"Unknown chart: {key}", Array.Empty<ChartLine>());

        _lines = new int[_subject.Lines.Count];

        for (int i = 0; i < _subject.Lines.Count; i++)
        {
            var line = _subject.Lines[i];

            _lines[i] = _chart.AddSeries(new ChartSeries
            {
                Name = line.Name,
                Stroke = StrokeFor(i),
                Unit = line.Unit,
                Format = line.Format,
                Min = line.Min,
                Max = line.Max,
                Fill = line.Primary,
                IsPrimary = line.Primary,
            });
        }

        _status.FontFamily = Token<FontFamily>("MonoFont") ?? new FontFamily("Consolas");
        _status.FontSize = 9.5;
        _status.Foreground = Brush("TextFaintBrush");
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = _subject.Title.ToUpperInvariant(),
            FontFamily = Token<FontFamily>("MonoFont") ?? new FontFamily("Consolas"),
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextFaintBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        stack.Children.Add(new ContentControl { Content = _chart, Height = 180, Focusable = false });
        stack.Children.Add(_status);

        Content = PanelChrome.Card(stack);

        _timer.Tick += (_, _) => _ = Load();

        // Loaded/Unloaded, not the constructor: switching workspaces re-parents this panel, and a
        // fourteen-day-capable reader left running behind a screen nobody is looking at is exactly
        // the loose end HistoryView added its own cancellation for.
        Loaded += (_, _) => { _timer.Start(); _ = Load(); };
        Unloaded += (_, _) => { _timer.Stop(); _loading?.Cancel(); _loading = null; };
    }

    private async Task Load()
    {
        if (_subject.Lines.Count == 0)
        {
            _status.Text = "THIS VERSION DOES NOT HAVE THIS CHART";
            return;
        }

        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;

        DateTime to = DateTime.UtcNow;
        DateTime from = to - Window;

        try
        {
            var samples = await _history.ReadThermalAsync(from, to, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            for (int i = 0; i < _subject.Lines.Count; i++)
            {
                var select = _subject.Lines[i].Select;

                // A point only where there is a reading. A sample whose sensor did not answer
                // contributes nothing rather than a zero, which is the rule the writer followed.
                _chart.SetSeriesData(_lines[i], samples
                    .Where(s => select(s) is not null)
                    .Select(s => new TimePoint(s.AtUtc, select(s)!.Value))
                    .ToList());
            }

            _chart.SetWindow(from, to);

            // Says how much of the window actually has data in it. A chart of forty rows and a
            // chart of fifteen hundred look alike once they are drawn, and they are not alike.
            int covered = samples.Count(s => _subject.Lines.Any(l => l.Select(s) is not null));

            _status.Text = covered == 0
                ? "NO READINGS IN THE LAST HOUR"
                : $"{covered} READINGS IN THE LAST HOUR";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later load, or the panel went off screen. The newer one owns the
            // status line.
        }
        catch (Exception ex)
        {
            _status.Text = $"COULD NOT READ THE LOG ({ex.Message})";
        }
    }

    /// <summary>
    /// Three lines is as many as any subject has, and the order matches the History tab so the two
    /// screens do not disagree about which colour the temperature is.
    /// </summary>
    private static Brush StrokeFor(int index) => index switch
    {
        0 => Brush("DangerBrush"),
        1 => Brush("AccentBrush"),
        _ => Brush("MetricMemBrush"),
    };

    private static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static T? Token<T>(string key) where T : class =>
        System.Windows.Application.Current?.TryFindResource(key) as T;

    public void Dispose()
    {
        _timer.Stop();
        _loading?.Cancel();
        _chart.Dispose();
    }
}
