using System.Globalization;
using System.Text;

namespace OmniHub.Core.Fan;

/// <summary>
/// Appends a rolling CSV of what the machine was actually doing, one row per poll.
///
/// This is the "honest thermal log" from the OmniControlSuite teardown, and it exists for
/// one reason: "it still overheats" is not a diagnosable statement, and a trace is. Every
/// column is a value genuinely read from or commanded to the hardware -- nothing is derived,
/// smoothed or scored on the way in, because the whole point is to be able to trust it later
/// when reconstructing what happened.
///
/// One file per day under %AppData%\OmniHub\logs. Writes are best-effort: a logging failure
/// must never take down the fan-control loop that calls it, so failures stop logging for the
/// session rather than propagating.
/// </summary>
public sealed class ThermalLog : IDisposable
{
    // forecast_c records what the predictive lead projected for this tick, so the feature can
    // be checked against what actually happened instead of taken on trust. -1 when prediction
    // is disabled.
    // sensor records WHICH sensor produced temp_c. Added because working out that a whole
    // session had run without Tctl -- and so had been pinned to the ACPI zone's 85 C ceiling
    // with the fans at 100% throughout -- meant inferring it from whether the temperatures had
    // decimal places. That is a real diagnosis from an accidental signal; the column makes it
    // a recorded fact instead.
    private const string Header = "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor";

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _openedForDate;
    private bool _failed;

    /// <summary>How long a written row may sit unflushed. See EnsureWriterFor for the trade.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "logs");

    /// <summary>Full path of the file currently being written, or null if logging is off or failed.</summary>
    public string? CurrentPath { get; private set; }

    // tempC is a double, not an int: Tctl resolves to 0.125C, and rounding it here made the log
    // ambiguous about which sensor produced a row -- an ACPI 85 and a die 85.2 looked identical,
    // which is exactly what made diagnosing a "temp is wrong" report harder than it needed to be.
    public void Append(DateTime utcNow, double tempC, double forecastC, byte fan1Raw, byte fan2Raw,
                       int commandedPercent, bool throttling, string mode, string sensor)
    {
        if (_failed) return;

        lock (_lock)
        {
            try
            {
                EnsureWriterFor(utcNow);
                if (_writer is null) return;

                // InvariantCulture throughout: this file gets read back by tooling and by
                // whoever is debugging the machine, and a decimal comma inside a CSV would
                // silently corrupt the column layout on a non-English system.
                var sb = new StringBuilder(96);
                sb.Append(utcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',')
                  .Append(tempC.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(forecastC.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                  .Append(fan1Raw.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(fan2Raw.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(commandedPercent.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(throttling ? "True" : "False").Append(',')
                  .Append(Sanitize(mode)).Append(',')
                  .Append(Sanitize(sensor));

                _writer.WriteLine(sb.ToString());

                if (utcNow - _lastFlushUtc >= FlushInterval)
                {
                    _writer.Flush();
                    _lastFlushUtc = utcNow;
                }
            }
            catch
            {
                // Disk full, permissions, roaming profile unavailable. Stop trying rather
                // than throwing a logging error into the cooling loop every 2 seconds.
                _failed = true;
                CloseWriter();
            }
        }
    }

    private void EnsureWriterFor(DateTime utcNow)
    {
        if (_writer is not null && _openedForDate == utcNow.Date) return;

        CloseWriter();
        Directory.CreateDirectory(LogDirectory);

        // If today's file was written by a build with a different column set, appending to it
        // would produce a file whose rows do not all match its own header -- silently
        // unparseable by anything reading it later, which defeats the point of keeping a
        // trace. Roll to a suffixed file instead of corrupting the existing one.
        var path = Path.Combine(LogDirectory, $"thermal-{utcNow:yyyy-MM-dd}.csv");
        for (int suffix = 2; suffix < 100 && HasDifferentHeader(path); suffix++)
            path = Path.Combine(LogDirectory, $"thermal-{utcNow:yyyy-MM-dd}-{suffix}.csv");

        bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;

        // Flushed on a short interval rather than per row.
        //
        // AutoFlush was a syscall for every line, about 0.43 a second, forever, and inside the
        // poll callback. The reason it was there is still right, and is why this is an interval
        // rather than a plain buffer: the process is routinely exited from the tray or lost to a
        // crash, and the tail of a trace is exactly the part worth having after a thermal event.
        // The trade is bounded instead of taken -- at most FlushInterval of rows can be lost,
        // four or five lines at this poll rate, and Dispose flushes whatever is left.
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
        _lastFlushUtc = utcNow;
        _openedForDate = utcNow.Date;
        CurrentPath = path;

        if (isNew) _writer.WriteLine(Header);

        PruneOldLogs(path);
    }

    /// <summary>
    /// How long a thermal trace is kept. Long enough to compare a machine against itself a
    /// fortnight ago, which is the span these files actually get used over.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    /// <summary>
    /// Deletes traces older than <see cref="Retention"/>.
    ///
    /// One file a day, kept forever, was the only unbounded growth in the application -- a
    /// megabyte and a half on a busy day, and nothing anywhere pruned it.
    ///
    /// Only thermal-*.csv is touched. Load-test runs live in the same directory and are
    /// deliberate artefacts someone created in order to compare against later; deleting one
    /// would throw away the baseline half of a measurement. The file currently open is never a
    /// candidate either.
    /// </summary>
    private static void PruneOldLogs(string currentPath)
    {
        try
        {
            var cutoff = DateTime.UtcNow - Retention;
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "thermal-*.csv"))
            {
                if (string.Equals(file, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* locked, or vanished under us: leave it and try again another day */ }
            }
        }
        catch
        {
            // Housekeeping must never be able to stop logging, which is the part that matters.
        }
    }

    /// <summary>True when the file exists, has content, and its header is not the current one.</summary>
    private static bool HasDifferentHeader(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length == 0) return false;

            using var reader = new StreamReader(path, Encoding.UTF8);
            var first = reader.ReadLine();
            return first is not null && !string.Equals(first, Header, StringComparison.Ordinal);
        }
        catch
        {
            // Unreadable: treat as matching rather than spawning an endless chain of new
            // files on every open.
            return false;
        }
    }

    private void CloseWriter()
    {
        // Dispose flushes on its own, but doing it explicitly first means a failure to write the
        // tail cannot be mistaken for a failure to close the file.
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        CurrentPath = null;
    }

    // Mode is a short enum name today, but it is the one free-text-ish column; keep a stray
    // comma or newline from shifting every field to its right.
    private static string Sanitize(string value) =>
        string.IsNullOrEmpty(value) ? "" : value.Replace(',', ' ').Replace('\r', ' ').Replace('\n', ' ');

    public void Dispose()
    {
        lock (_lock) CloseWriter();
    }
}
