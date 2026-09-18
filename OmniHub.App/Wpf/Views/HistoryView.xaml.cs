// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.App.Wpf.Controls;
using OmniHub.Core.Fan;
using OmniHub.Core.Telemetry;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The machine's own thermal trace, read back.
///
/// This application has written a row roughly every two seconds for months -- 200,169 of them
/// across the fourteen days currently on disk -- and nothing has ever read one. The trace was
/// written for exactly this and then only ever opened in a text editor.
///
/// It is also the first screen the new chart control renders, and deliberately so: proving it
/// here rather than on the Dashboard means the landing tab is not where a rendering regression
/// would show up first.
/// </summary>
public partial class HistoryView : UserControl, IDisposable
{
    private readonly TelemetryHistory _history = new();
    private readonly TimeSeriesChart _chart = new();

    private readonly int _temp;
    private readonly int _fan;
    private readonly int _commanded;

    private CancellationTokenSource? _loading;

    /// <summary>
    /// The windows worth offering, from "what just happened" to the whole retained trace.
    ///
    /// Fourteen days is the retention policy, so the last entry is genuinely everything there
    /// is; offering a month would present an empty left half as though the machine had been off.
    /// </summary>
    private static readonly (string Label, TimeSpan Span)[] Windows =
    {
        ("30 MIN", TimeSpan.FromMinutes(30)),
        ("6 H", TimeSpan.FromHours(6)),
        ("24 H", TimeSpan.FromHours(24)),
        ("3 D", TimeSpan.FromDays(3)),
        ("14 D", TimeSpan.FromDays(14)),
    };

    private static readonly TimeSpan Initial = TimeSpan.FromHours(6);

    /// <summary>
    /// The fan band that converts a logged raw level into a percentage.
    ///
    /// Passed in rather than read from a process-wide static, because this view draws rows that
    /// were written in the past and the band is a property of the machine that can change under
    /// it -- calibrate the fans and every historical point would silently be redrawn against a
    /// different scale.
    /// </summary>
    private readonly OmniHub.Core.Hardware.FanCalibration _calibration;

    public HistoryView(OmniHub.Core.Hardware.FanCalibration calibration)
    {
        _calibration = calibration;
        InitializeComponent();

        _temp = _chart.AddSeries(new ChartSeries
        {
            Name = "die temp",
            Stroke = (Brush)FindResource("DangerBrush"),
            Unit = " C",
            Format = "0.#",
            Fill = true,
            IsPrimary = true,
        });

        _fan = _chart.AddSeries(new ChartSeries
        {
            Name = "fan duty",
            Stroke = (Brush)FindResource("AccentBrush"),
            Unit = "%",
            Format = "0",
            Min = 0,
            Max = 100,
        });

        _commanded = _chart.AddSeries(new ChartSeries
        {
            Name = "commanded",
            Stroke = (Brush)FindResource("MetricMemBrush"),
            Unit = "%",
            Format = "0",
            Min = 0,
            Max = 100,
        });

        ChartHost.Content = _chart;

        BuildWindowPills();
        Loaded += (_, _) => Load(Initial);
        Unloaded += (_, _) => { _loading?.Cancel(); _loading = null; };
    }

    /// <summary>
    /// Cancels any read in flight and stops the chart.
    ///
    /// Without this the view was invisible to shutdown: MainWindow disposes whatever in its view
    /// dictionary is IDisposable and GroupView forwards to whichever of its sections are, so a
    /// view that implements nothing is simply skipped. A fourteen-day read is around 200,000 rows,
    /// and leaving one running while the application tears down is a loose end whether or not it
    /// is the one that cost a clean exit.
    /// </summary>
    public void Dispose()
    {
        try { _loading?.Cancel(); } catch { }
        _loading = null;
        try { _chart.Dispose(); } catch { }
    }

    private void BuildWindowPills()
    {
        foreach (var (label, span) in Windows)
        {
            var pill = new RadioButton
            {
                Content = label,
                GroupName = "HistoryWindow",
                Style = (Style)FindResource("PillRadioStyle"),
                Height = 28,
                MinWidth = 62,
                Margin = new Thickness(0, 0, 3, 0),
                IsChecked = span == Initial,
            };

            var captured = span;
            pill.Checked += (_, _) => Load(captured);

            WindowPills.Children.Add(pill);
        }
    }

    /// <summary>
    /// Loads a window and hands it to the chart.
    ///
    /// Cancellable, because a fourteen-day read is roughly 200,000 rows and somebody clicking
    /// through the window pills should not queue five of those behind each other.
    /// </summary>
    private async void Load(TimeSpan span)
    {
        _loading?.Cancel();
        var cts = new CancellationTokenSource();
        _loading = cts;

        DateTime to = DateTime.UtcNow;
        DateTime from = to - span;

        Status.Text = "Reading...";

        try
        {
            var thermal = await _history.ReadThermalAsync(from, to, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            var events = await _history.ReadPowerEventsAsync(from, to, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;

            // Retained for the export, so the file is the window on screen.
            _shown = thermal;

            // Points only where a reading exists. A sample whose sensor failed contributes
            // nothing rather than a zero, which is the rule the writer followed too.
            _chart.SetSeriesData(_temp, Series(thermal, s => s.TempC));
            _chart.SetSeriesData(_fan, Series(thermal, s => s.Fan1Raw is { } raw ? _calibration.RawToPercent(raw) : null));
            _chart.SetSeriesData(_commanded, Series(thermal, s => s.CommandedPercent));

            _chart.SetMarkers(events.Select(e => (e.AtUtc, $"{e.Source} {e.Event}")));
            _chart.SetWindow(from, to);

            Status.Text = Describe(thermal, events.Count, _history.LastReadWarnings);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a later click. The newer load owns the status line.
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not read the history ({ex.Message}).";
        }
    }

    /// <summary>
    /// The samples currently drawn, kept so the export is the window on screen.
    ///
    /// Re-reading at export time would fetch a window that has since moved, so the file would not
    /// be the chart somebody was looking at when they pressed the button.
    /// </summary>
    private IReadOnlyList<ThermalSample> _shown = Array.Empty<ThermalSample>();

    private void ExportCsvBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_shown.Count == 0)
        {
            Status.Text = "Nothing to export: this window holds no samples.";
            return;
        }

        try
        {
            string? path = Export.SaveText(
                $"omnihub-thermal-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv",
                "CSV file (*.csv)|*.csv",
                CsvExport.Thermal(_shown));

            if (path is { Length: > 0 })
                Status.Text = $"Exported {_shown.Count:N0} samples to {path}.";
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
            string? path = Export.SavePng(_chart, $"omnihub-thermal-{DateTime.Now:yyyy-MM-dd-HHmmss}.png");

            if (path is { Length: > 0 }) Status.Text = $"Saved the chart to {path}.";
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not save the image ({ex.Message}).";
        }
    }

    private static IReadOnlyList<TimePoint> Series(
        IReadOnlyList<ThermalSample> samples, Func<ThermalSample, double?> select)
    {
        var points = new List<TimePoint>(samples.Count);

        foreach (var s in samples)
            if (select(s) is { } v)
                points.Add(new TimePoint(s.AtUtc, v));

        return points;
    }

    /// <summary>
    /// What was read, including the holes.
    ///
    /// The gaps are named rather than left to be inferred from the chart. On this machine the
    /// application runs for a few hours a day, so a fourteen-day window is mostly hole -- and a
    /// reader who assumed otherwise would take a sparse trace for a quiet machine.
    /// </summary>
    private static string Describe(
        IReadOnlyList<ThermalSample> samples, int eventCount, IReadOnlyList<string> warnings)
    {
        if (samples.Count == 0)
            return "No thermal trace in this range. Logging is switched on from Settings; it is off by default.";

        var stamps = samples.Select(s => s.AtUtc).ToList();
        var gaps = SampleGaps.Find(stamps);
        var median = SampleGaps.MedianInterval(stamps);

        string text =
            $"{samples.Count:N0} samples at {median?.TotalSeconds ?? 0:0.0} s, "
            + $"{stamps[0].ToLocalTime():MMM d HH:mm} to {stamps[^1].ToLocalTime():MMM d HH:mm}. "
            + $"{gaps.Count} gap(s) where nothing was recorded. {eventCount} power event(s) marked.";

        if (warnings.Count > 0)
            text += $" {warnings.Count} file(s) could not be read: {string.Join(", ", warnings)}.";

        return text;
    }
}
