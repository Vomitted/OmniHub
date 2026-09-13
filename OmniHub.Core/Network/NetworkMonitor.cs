namespace OmniHub.Core.Network;

/// <summary>
/// Samples one host continuously in the background and keeps a rolling picture of the connection.
///
/// The measurements on the Network screen all began as buttons, and a button is the wrong shape
/// for this particular question. The faults that ruin a game are intermittent: a connection that
/// drops two percent of packets for ten seconds every few minutes measures perfectly clean nine
/// times out of ten, so pressing "measure" is overwhelmingly likely to sample one of the nine.
/// Whoever is playing at the time is also not looking at a diagnostics tab. Something has to be
/// watching while nobody is.
///
/// Deliberately cheap. One ICMP echo per tick, a handful of bytes, and the statistics come from a
/// rolling window rather than from a burst of probes each time. At the default five seconds that
/// is far less traffic than a single web page, which is what makes leaving it on all day
/// defensible.
///
/// It also stands down across sleep rather than polling through it, for the same reason the fan
/// service does: this machine is Modern Standby, so a background loop is not suspended when the
/// lid closes, and a timer firing into a suspending network stack is exactly the kind of thing
/// that turns a sleep transition into a hang.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    /// <summary>
    /// How many samples the rolling statistics cover. Sixty at the default five-second cadence is
    /// five minutes: long enough for an intermittent fault to show up in the loss figure, short
    /// enough that the numbers still describe now rather than an average of the whole session.
    /// </summary>
    public const int WindowSize = 60;

    private readonly object _lock = new();

    /// <summary>
    /// Null marks a probe that never came back, and the nulls are kept rather than dropped.
    ///
    /// That is the whole loss calculation. A window holding only the successes would report a
    /// beautifully steady latency for a connection losing half its packets, which is precisely
    /// the connection worth reporting.
    /// </summary>
    private readonly double?[] _window = new double?[WindowSize];

    private int _count;
    private int _next;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Host being sampled. Changing it clears the window, since the old samples describe somewhere else.</summary>
    public string Target
    {
        get => _target;
        set
        {
            if (string.Equals(_target, value, StringComparison.OrdinalIgnoreCase)) return;
            _target = value;
            Reset();
        }
    }
    private string _target = "1.1.1.1";

    /// <summary>True only while the loop is genuinely alive, not merely while a token is uncancelled.</summary>
    public bool IsRunning => _cts is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    /// <summary>The rolling picture. <see cref="LatencyStats.None"/> until the first probe returns.</summary>
    public LatencyStats Current { get; private set; } = LatencyStats.None;

    /// <summary>What the last failing probe reported, or null. Kept so a monitor that runs and achieves nothing is diagnosable.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised after each sample with the updated rolling statistics, on the sampling thread.</summary>
    public event Action<LatencyStats>? OnSample;

    /// <summary>Raised with each individual probe, for callers that log every row rather than the summary.</summary>
    public event Action<DateTime, double?>? OnProbe;

    public void Start(TimeSpan interval)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();

        // Task.Run rather than a bare call: the first statement of the loop is a network probe,
        // and an async method runs synchronously on the calling thread until its first await.
        // Started from a UI handler, that would block the interface on a DNS lookup.
        _loop = Task.Run(() => RunAsync(interval, _cts.Token));
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        try { cts.Cancel(); cts.Dispose(); } catch { }
    }

    /// <summary>Empties the window. Used when the target changes and after a resume, where the samples either side describe different conditions.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_window);
            _count = 0;
            _next = 0;
            Current = LatencyStats.None;
        }
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // One probe per tick, with a short timeout. A probe still outstanding when the
                // next tick falls due is a lost probe for practical purposes, and waiting longer
                // would stretch the cadence the window's timing assumes.
                var stats = await NetworkProbe.PingAsync(
                    Target, count: 1, timeoutMs: 1200, intervalMs: 0, ct: token).ConfigureAwait(false);

                double? rtt = stats.IsEmpty ? null : stats.AvgMs;
                Record(rtt);
                LastError = null;
                OnProbe?.Invoke(DateTime.UtcNow, rtt);
                OnSample?.Invoke(Current);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Broad and non-fatal, like the fan loop: a monitor that dies silently is worse
                // than one recording a bad tick, because nothing else will notice it stopped.
                LastError = ex.Message;
                Record(null);
            }

            try { await Task.Delay(interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Folds one probe into the rolling window.
    ///
    /// Internal rather than private so it can be driven directly by a test. The ordering rule
    /// below is the kind of thing that is invisible when it is wrong -- the numbers stay
    /// plausible, they are just computed from a sequence that never happened -- and it is not
    /// reachable from outside without waiting on a real network.
    /// </summary>
    internal void Record(double? rtt)
    {
        lock (_lock)
        {
            _window[_next] = rtt;
            _next = (_next + 1) % WindowSize;
            if (_count < WindowSize) _count++;

            // Rebuilt in arrival order, oldest first, because jitter is defined on consecutive
            // samples. Reading the ring straight out would start mid-sequence and manufacture one
            // false jump per window at the wrap point.
            var ordered = new List<double>(_count);
            int sent = 0;
            int start = _count < WindowSize ? 0 : _next;
            for (int i = 0; i < _count; i++)
            {
                sent++;
                if (_window[(start + i) % WindowSize] is double v) ordered.Add(v);
            }

            Current = LatencyStats.From(sent, ordered);
        }
    }

    public void Dispose() => Stop();
}
