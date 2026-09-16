using System.Globalization;
using System.Text;

namespace OmniHub.Core.Network;

/// <summary>
/// Appends a rolling CSV of what the connection was actually doing, one row per probe.
///
/// The same argument as the thermal log, applied to the other half of the machine: "the game was
/// lagging" is not a diagnosable statement and a trace is. It is worth more here than for
/// temperature, because network faults are intermittent in a way thermals are not -- by the time
/// anyone opens a diagnostics screen the bad thirty seconds is over, and nothing kept it.
///
/// Every column is measured. rtt_ms is blank rather than zero for a probe that never returned,
/// because zero is a real round trip and "no answer" is not a fast one; the lost column states it
/// outright so the distinction survives being read by something naive.
///
/// One file per day under %AppData%\OmniHub\logs. Writes are best-effort: a logging failure must
/// never take down the monitor that calls it, so a failure stops logging for the session rather
/// than propagating.
/// </summary>
public sealed class NetworkLog : IDisposable
{
    // The per-probe value AND the rolling figures are both recorded, which is mild duplication and
    // worth it. The rolling columns are what a person reads when skimming for the moment it went
    // wrong; the raw column is what allows the window to be recomputed differently afterwards
    // without the evidence having been thrown away.
    private const string Header = "timestamp,target,rtt_ms,lost,avg_ms,jitter_ms,loss_pct";

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _openedForDate;
    private bool _failed;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "logs");

    /// <summary>Full path of the file being written, or null when logging is off or has failed.</summary>
    public string? CurrentPath { get; private set; }

    public void Append(DateTime utcNow, string target, double? rttMs, LatencyStats rolling)
    {
        if (_failed) return;

        lock (_lock)
        {
            try
            {
                EnsureWriterFor(utcNow);
                if (_writer is null) return;

                var sb = new StringBuilder(96);
                sb.Append(utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',');
                sb.Append(target).Append(',');

                // Empty, not 0. Anything reading this must not average a timeout in as a
                // zero-millisecond reply, which would make a failing connection look fast.
                sb.Append(rttMs is double v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "").Append(',');
                sb.Append(rttMs is null ? "1" : "0").Append(',');

                if (rolling.IsEmpty)
                {
                    sb.Append(",,");
                }
                else
                {
                    sb.Append(rolling.AvgMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(rolling.JitterMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(rolling.LossPercent.ToString("0.0", CultureInfo.InvariantCulture));
                }

                _writer.WriteLine(sb.ToString());

                // Flushed on an interval rather than per row. Per row costs a disk write every few
                // seconds for the life of the session; never flushing loses the tail of the file
                // on a hard power-off, which on this machine is not hypothetical -- it is the
                // failure the sleep work was chasing, and the last rows before a freeze are
                // exactly the ones worth having.
                if (utcNow - _lastFlushUtc >= FlushInterval)
                {
                    _writer.Flush();
                    _lastFlushUtc = utcNow;
                }
            }
            catch
            {
                _failed = true;
                CurrentPath = null;
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    private void EnsureWriterFor(DateTime utcNow)
    {
        if (_writer is not null && _openedForDate == utcNow.Date) return;

        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;

        Directory.CreateDirectory(LogDirectory);
        string path = Path.Combine(LogDirectory, $"network-{utcNow:yyyy-MM-dd}.csv");
        bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        if (fresh) _writer.WriteLine(Header);

        CurrentPath = path;
        _openedForDate = utcNow.Date;

        // Same retention as the thermal trace, and for the same reason: one file a day, kept
        // forever, is unbounded growth in a folder the user is invited to open.
        Diagnostics.LogRetention.Prune(LogDirectory, "network-*.csv", keepPath: path);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
            CurrentPath = null;
        }
    }
}
