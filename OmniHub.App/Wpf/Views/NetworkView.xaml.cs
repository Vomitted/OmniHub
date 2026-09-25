// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using OmniHub.Core.Network;

// Aliased per file, as every other view here does: this project sets both UseWindowsForms and
// UseWPF, so these names exist in both stacks and a bare reference is ambiguous.
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// Connection measurement.
///
/// Everything on this screen is a measurement or a statement about one. There is no button
/// claiming to make the connection faster, because a local application cannot make a connection
/// faster and the ones that say otherwise are selling something. What it can do is answer the
/// questions that actually decide whether a game plays well -- how much the delay varies, how
/// much is lost, how far the link falls apart when something else is using it, and how much of a
/// distant server's latency is simply the width of the planet -- and answer them with numbers a
/// person can act on.
/// </summary>
public partial class NetworkView : UserControl, IDisposable
{
    /// <summary>
    /// Default probe target. A resolver rather than a game server, because the headline
    /// measurement is about the SHAPE of the connection (variance, loss) rather than distance,
    /// and a near, reliable, always-up host measures that with the least borrowed noise.
    /// </summary>
    private const string DefaultTarget = "1.1.1.1";

    private CancellationTokenSource? _busy;
    private CancellationTokenSource? _watch;
    private bool _disposed;
    private bool _regionsMeasured;

    /// <summary>
    /// The application-wide monitor, or null when the user has switched it off.
    ///
    /// Shared rather than owned. A view that started its own sampler would measure only while
    /// somebody had this tab open, which is the opposite of what is wanted: the faults worth
    /// catching happen while a game is full-screen and nobody is looking at diagnostics.
    /// </summary>
    private readonly NetworkMonitor? _monitor;

    public NetworkView(NetworkMonitor? monitor = null)
    {
        InitializeComponent();
        _monitor = monitor;
        TargetBox.Text = monitor?.Target ?? DefaultTarget;

        // The adapter audit is a local WMI read with no network in it, so it can run immediately
        // rather than waiting for a button. Off the UI thread all the same: WMI is not fast, and
        // this view is built on the idle callback that constructs the rest of the tabs.
        _ = LoadAdapterFindingsAsync();

        if (_monitor is not null)
        {
            _monitor.OnSample += OnMonitorSample;

            // Paint immediately from whatever the monitor already has rather than showing dashes
            // until the next tick. It has usually been running since launch, so the answer exists
            // before this view does.
            if (!_monitor.Current.IsEmpty) ShowStats(_monitor.Current, ProbeMethod.Icmp, _monitor.Target);
        }

        UpdateMonitorStatus();

        // Regions measure themselves the first time this screen is opened. It is nine short TCP
        // handshakes, it is the slowest thing here, and "which server should I join" is the
        // question somebody opened this tab to answer -- making them press a button first is
        // making them ask twice.
        Loaded += async (_, _) =>
        {
            if (_regionsMeasured) return;
            _regionsMeasured = true;
            await MeasureRegionsAsync().ConfigureAwait(true);
        };

        Unloaded += (_, _) => StopWatching();
    }

    /// <summary>Marshals a sample from the monitor's thread onto the UI.</summary>
    private void OnMonitorSample(LatencyStats stats)
    {
        if (_disposed) return;
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_disposed || _busy is not null) return;   // a manual run owns the tiles while it lasts
                ShowStats(stats, ProbeMethod.Icmp, _monitor?.Target ?? DefaultTarget);
                UpdateMonitorStatus();
            });
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TaskCanceledException) { }
    }

    private void UpdateMonitorStatus()
    {
        if (_monitor is null)
        {
            MonitorStatus.Text = "CONTINUOUS MONITORING IS OFF";
            WatchBtn.Content = "Resume monitoring";
            WatchBtn.IsEnabled = false;
            return;
        }

        var s = _monitor.Current;
        MonitorStatus.Text = _monitor.IsRunning
            // The span comes from the interval actually in use, not from the default. This
            // read "5" for years -- the default probe interval -- while the real one is a
            // setting clamped to 2..60, so changing it made the label state a duration the
            // window had never covered.
            ? $"WATCHING {_monitor.Target.ToUpperInvariant()} CONTINUOUSLY / {s.Received} OF {s.Sent} SAMPLES IN THE LAST {_monitor.Window.TotalMinutes:0} MINUTES"
            : "MONITORING PAUSED";
        WatchBtn.Content = _monitor.IsRunning ? "Pause monitoring" : "Resume monitoring";
        WatchBtn.IsEnabled = true;
    }

    // ---- connection quality ------------------------------------------------

    private async void MeasureBtn_Click(object sender, RoutedEventArgs e)
    {
        StopWatching();
        await MeasureOnceAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Pauses or resumes the shared monitor.
    ///
    /// Worth having in reach rather than only in Settings: the one time somebody genuinely wants
    /// this off is while they are chasing something else on the same connection, and that is
    /// exactly when they are already looking at this screen.
    /// </summary>
    private void WatchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor is null) return;

        if (_monitor.IsRunning) _monitor.Stop();
        else _monitor.Start(_monitor.Interval);

        UpdateMonitorStatus();
    }

    private void StopWatching()
    {
        var cts = Interlocked.Exchange(ref _watch, null);
        if (cts is null) return;
        try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    private async Task MeasureOnceAsync(CancellationToken ct = default)
    {
        string host = string.IsNullOrWhiteSpace(TargetBox.Text) ? DefaultTarget : TargetBox.Text.Trim();

        MeasureBtn.IsEnabled = false;
        QualityStatus.Text = $"Measuring {host}...";

        try
        {
            var probe = await Task.Run(
                () => NetworkProbe.MeasureAsync(host, count: 20, intervalMs: 150, ct: ct), ct)
                .ConfigureAwait(true);

            if (ct.IsCancellationRequested) return;
            ShowQuality(probe);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowQuality(ProbeResult.Unreachable(host));
            QualityStatus.Text = ex.Message;
        }
        finally
        {
            if (!_disposed) MeasureBtn.IsEnabled = true;
        }
    }

    private void ShowQuality(ProbeResult probe) => ShowStats(probe.Stats, probe.Method, probe.Host);

    /// <summary>
    /// Renders one set of statistics.
    ///
    /// Split out from ShowQuality so the continuous monitor and a manual burst reach the tiles by
    /// the same route. The monitor produces a LatencyStats with no ProbeResult around it, and
    /// duplicating this formatting for it would be the usual way two displays of the same number
    /// drift apart.
    /// </summary>
    private void ShowStats(LatencyStats s, ProbeMethod method, string host)
    {

        // An unreachable host renders as absent, never as zero. A connection answering in 0 ms
        // with 0% loss is not what "no answer" means, and this project does not dress an
        // unavailable reading up as a plausible number.
        if (s.IsEmpty)
        {
            RttValue.Text = JitterValue.Text = LossValue.Text = P95Value.Text = "--";
            RttFoot.Text = JitterFoot.Text = LossFoot.Text = P95Foot.Text = "";
            QualityStatus.Text =
                $"{host} did not answer, over ICMP or TCP. That is either the host refusing "
                + "to be measured or a real path problem, and this cannot tell you which.";
            return;
        }

        RttValue.Text = $"{s.AvgMs:0}";
        RttFoot.Text = $"MIN {s.MinMs:0} / MAX {s.MaxMs:0}";

        JitterValue.Text = $"{s.JitterMs:0.0}";
        JitterFoot.Text = JitterVerdict(s.JitterMs);

        LossValue.Text = $"{s.LossPercent:0.#}";
        LossFoot.Text = $"{s.Received} OF {s.Sent} RETURNED";

        P95Value.Text = $"{s.P95Ms:0}";
        P95Foot.Text = "19 IN 20 ARE FASTER";

        string how = method switch
        {
            ProbeMethod.Icmp => "ICMP echo",
            ProbeMethod.Tcp => $"TCP handshake, since {host} ignores ping (reads slightly high)",
            _ => "unavailable",
        };

        QualityStatus.Text = $"{host} over {how}. " + Interpretation(s);
    }

    /// <summary>
    /// Says what the numbers mean in words, because the reason this screen exists is that people
    /// read a ping figure and cannot tell a good connection from a bad one.
    ///
    /// Loss is reported before jitter and jitter before the mean, which is the reverse of how
    /// every speed test orders them and the right order for a game: any loss at all outranks any
    /// amount of steady delay.
    /// </summary>
    private static string Interpretation(LatencyStats s)
    {
        if (s.LossPercent >= 1)
            return $"Losing {s.LossPercent:0.#}% of packets, which matters more than the delay does. "
                 + "Anything above about 1% is felt as rubber-banding and shots that do not register.";

        if (s.JitterMs >= 15)
            return $"Delay varies by {s.JitterMs:0.0} ms between packets. That variance, not the "
                 + $"{s.AvgMs:0} ms average, is what a game's prediction keeps having to correct for.";

        if (s.LossPercent > 0)
            return $"Steady, with {s.LossPercent:0.#}% loss. Worth watching if it repeats, since "
                 + "occasional loss is usually intermittent rather than absent.";

        return $"Steady and complete: {s.JitterMs:0.0} ms of variation and nothing lost. At this "
             + $"point the {s.AvgMs:0} ms average is mostly distance, which no setting changes.";
    }

    private static string JitterVerdict(double jitterMs) => jitterMs switch
    {
        < 3 => "STEADY",
        < 8 => "SLIGHT VARIATION",
        < 15 => "NOTICEABLE",
        _ => "ERRATIC",
    };

    // ---- latency under load ------------------------------------------------

    private async void LoadTestBtn_Click(object sender, RoutedEventArgs e)
    {
        StopWatching();

        var cts = new CancellationTokenSource();
        _busy = cts;

        LoadTestBtn.IsEnabled = false;
        LoadCancelBtn.IsEnabled = true;
        LoadDetail.Children.Clear();
        LoadHeadline.Text = "--";
        LoadStatus.Text = "Measuring idle, then filling the connection. About fifteen seconds, and it downloads a few hundred megabytes to do it.";

        try
        {
            string target = string.IsNullOrWhiteSpace(TargetBox.Text) ? DefaultTarget : TargetBox.Text.Trim();
            var result = await Task.Run(
                () => LoadedLatencyTest.RunAsync(target, ct: cts.Token), cts.Token).ConfigureAwait(true);

            if (!cts.IsCancellationRequested) ShowLoadResult(result);
        }
        catch (OperationCanceledException)
        {
            LoadStatus.Text = "Stopped before it finished, so there is no result.";
        }
        catch (Exception ex)
        {
            LoadStatus.Text = $"Could not complete: {ex.Message}";
        }
        finally
        {
            if (!_disposed)
            {
                LoadTestBtn.IsEnabled = true;
                LoadCancelBtn.IsEnabled = false;
            }
            if (ReferenceEquals(_busy, cts)) _busy = null;
            cts.Dispose();
        }
    }

    private void LoadCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        try { _busy?.Cancel(); } catch { }
    }

    private void ShowLoadResult(LoadedLatencyResult r)
    {
        LoadDetail.Children.Clear();

        LoadHeadline.Text = r.Verdict == LoadVerdict.Inconclusive
            ? "No result"
            : $"+{Math.Max(0, r.AddedMs):0} ms under load";

        LoadStatus.Text = r.Verdict switch
        {
            LoadVerdict.Bloated =>
                $"Latency rises {r.AddedMs:0} ms when the connection is busy, peaking {r.WorstAddedMs:0} ms above idle. "
                + "This is bufferbloat: queues in your modem or at your ISP are holding packets rather than dropping them. "
                + "It is the most likely reason a game feels fine alone and terrible while anything else downloads. "
                + "The fix is SQM or QoS in the router, running fq_codel or CAKE shaped to about 90% of your real line rate.",

            LoadVerdict.Clean =>
                $"Latency held steady while {r.ThroughputMbps:0} Mbps was flowing, moving only {r.AddedMs:0.0} ms. "
                + "Your queueing is genuinely in good shape, so a busy connection is not what costs you here.",

            _ => r.Note ?? "The test could not reach a conclusion.",
        };

        AddDetail("Idle", r.Idle.IsEmpty ? "unavailable" : $"{r.Idle.AvgMs:0.0} ms average, {r.Idle.JitterMs:0.0} ms jitter");
        AddDetail("Under load", r.Loaded.IsEmpty ? "unavailable" : $"{r.Loaded.AvgMs:0.0} ms average, peak {r.Loaded.MaxMs:0} ms");
        AddDetail("Throughput reached", r.ThroughputMbps >= 0.1 ? $"{r.ThroughputMbps:0.0} Mbps" : "none");

        // Stated even on a good result, because a flat latency reading only means anything if the
        // link was genuinely busy, and a reader has no way to know that unless it is shown.
        AddDetail("Test valid", r.Verdict == LoadVerdict.Inconclusive
            ? "no, see above"
            : $"yes, {r.ThroughputMbps:0.0} Mbps was moving while sampling");
    }

    private void AddDetail(string label, string value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Style = (Style)FindResource("TileLabel"),
            Width = 150,
        });
        row.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("BodyText"),
            FontSize = 11.5,
        });
        LoadDetail.Children.Add(row);
    }

    // ---- server regions ----------------------------------------------------

    private async void RegionBtn_Click(object sender, RoutedEventArgs e)
        => await MeasureRegionsAsync().ConfigureAwait(true);

    private async Task MeasureRegionsAsync()
    {
        RegionBtn.IsEnabled = false;
        RegionRows.Children.Clear();
        RegionStatus.Text = "Measuring each region in turn. One at a time, so they do not compete with each other for the same uplink.";

        var cts = new CancellationTokenSource();
        _busy = cts;

        try
        {
            var progress = new Progress<RegionLatency>(AddRegionRow);
            var all = await Task.Run(
                () => ServerRegions.MeasureAllAsync(6, progress, cts.Token), cts.Token).ConfigureAwait(true);

            var reached = all.Where(r => !r.Probe.Stats.IsEmpty).ToList();
            var best = reached.OrderBy(r => r.Probe.Stats.MinMs).FirstOrDefault();
            var worstOverhead = reached.OrderByDescending(r => r.OverheadMs).FirstOrDefault();

            RegionStatus.Text = best is null
                ? "No region answered, which is odd enough to suggest the measurement is being blocked rather than the network being down."
                : $"Nearest is {best.Region.Name} at {best.Probe.Stats.MinMs:0} ms. "
                  + (worstOverhead is null ? "" :
                     $"The largest routing overhead is to {worstOverhead.Region.Name}, {worstOverhead.OverheadMs:0} ms above its physical floor. That gap is the only part any routing service could sell back to you.");
        }
        catch (OperationCanceledException) { RegionStatus.Text = "Stopped."; }
        catch (Exception ex) { RegionStatus.Text = ex.Message; }
        finally
        {
            if (!_disposed) RegionBtn.IsEnabled = true;
            if (ReferenceEquals(_busy, cts)) _busy = null;
            cts.Dispose();
        }
    }

    private void AddRegionRow(RegionLatency row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        foreach (var w in new[] { 1.4, 1.0, 1.0, 1.0, 1.0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

        bool has = !row.Probe.Stats.IsEmpty;

        Cell(grid, 0, row.Region.Name, "BodyText");
        Cell(grid, 1, has ? $"{row.Probe.Stats.MinMs:0} ms" : "no answer", "BodyText");
        Cell(grid, 2, has ? $"{row.Probe.Stats.JitterMs:0.0} ms" : "--", "MutedText");
        Cell(grid, 3, $"{row.PhysicsFloorMs:0} ms", "MutedText");
        Cell(grid, 4, has ? $"+{row.OverheadMs:0} ms" : "--", "MutedText");

        RegionRows.Children.Add(grid);
    }

    private void Cell(Grid grid, int column, string text, string styleKey)
    {
        var tb = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource(styleKey),
            FontSize = 11.5,
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    // ---- adapter audit -----------------------------------------------------

    private async Task LoadAdapterFindingsAsync()
    {
        List<AdapterFinding> findings;
        try { findings = await Task.Run(NetworkAdapterAudit.Run).ConfigureAwait(true); }
        catch { return; }

        if (_disposed) return;

        AdapterRows.Children.Clear();

        if (findings.Count == 0)
        {
            AdapterStatus.Text = "Nothing to report: none of the settings known to cost latency are switched on.";
            return;
        }

        foreach (var f in findings) AdapterRows.Children.Add(BuildFindingRow(f));

        int worthwhile = findings.Count(f => f.Weight == FindingWeight.Worthwhile);
        AdapterStatus.Text = worthwhile == findings.Count
            ? $"{worthwhile} found. Each drops the link briefly when changed, so do it between sessions rather than during one."
            : $"{worthwhile} worth acting on; {findings.Count - worthwhile} marginal, listed only for completeness.";
    }

    private Border BuildFindingRow(AdapterFinding f)
    {
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        head.Children.Add(new TextBlock
        {
            Text = f.Setting,
            Style = (Style)FindResource("BodyText"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0),
        });
        head.Children.Add(new TextBlock
        {
            // The weight rides alongside the name rather than being encoded as a colour: this
            // project states a judgement in words, where it can be read rather than decoded.
            Text = f.Weight == FindingWeight.Worthwhile
                ? $"currently {f.CurrentValue}"
                : $"currently {f.CurrentValue} / marginal",
            Style = (Style)FindResource("TileFoot"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock
        {
            Text = f.Why,
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 660,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 6),
        });

        stack.Children.Add(new TextBox
        {
            Text = f.FixCommand,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 10.5,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = (System.Windows.Media.Brush)FindResource("TextFaintBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var copy = new Button
        {
            Content = "Copy command",
            Width = 140,

            // No Height.
            //
            // It was set to 28, and FlatButtonStyle cannot fit in 28: its padding is 14,8 and its
            // border is 1, so 13px text needs about 35. The button rendered taller than the slot
            // it was given and had its bottom edge cut off, which is what the clipping was.
            //
            // Left unset rather than corrected to 34 (the figure the XAML views use) so it is
            // measured from the style's own padding and font size. A fixed height here is a copy
            // of numbers that live in the style, and it silently clips again the moment either
            // changes -- which is precisely how this happened.
            MinHeight = 34,

            // Qualified: inside an object initializer the bare name binds to the property being
            // assigned rather than to the enum type.
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("FlatButtonStyle"),
        };
        copy.Click += (_, _) =>
        {
            // Clipboard access fails when another process holds it open, which is common enough
            // and not worth taking the view down for.
            try { Clipboard.SetText(f.FixCommand); copy.Content = "Copied"; }
            catch { copy.Content = "Could not copy"; }
        };
        stack.Children.Add(copy);

        return new Border
        {
            Child = stack,
            Margin = new Thickness(0, 0, 0, 14),
            Padding = new Thickness(0, 0, 0, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWatching();
        if (_monitor is not null) _monitor.OnSample -= OnMonitorSample;
        try { _busy?.Cancel(); } catch { }
    }
}
