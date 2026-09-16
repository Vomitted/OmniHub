using OmniHub.Core.Fan;

namespace OmniHub.Core.Telemetry;

/// <summary>
/// Reads this machine's own logs back.
///
/// The application has written six of them for months and read none of them back. Fourteen days
/// of thermal trace at 2.31 s, every network probe, every power transition and every poll-timing
/// average sit in a folder that nothing but a text editor has ever opened. Everything the
/// measurement work does -- history, sessions, A/B comparison -- starts here, and none of it
/// needs a new format or a new writer, because the data already exists.
///
/// Every method is async and returns a list rather than streaming. A 24-hour thermal window is
/// roughly 40,000 rows and 1.5 MB, which is about 100 ms of parsing: a visible hitch on the UI
/// thread and nothing at all off it. Callers await; they do not parse on the dispatcher.
/// </summary>
public sealed class TelemetryHistory
{
    private readonly string _directory;

    public TelemetryHistory(string? logDirectory = null) =>
        _directory = logDirectory ?? ThermalLog.LogDirectory;

    /// <summary>
    /// Files that could not be read during the last query, by name.
    ///
    /// Surfaced rather than swallowed so a view can say "3 of 14 files could not be read"
    /// instead of drawing a suspiciously short chart and letting the reader assume the machine
    /// was off. A silent short chart is the failure this exists to prevent.
    /// </summary>
    public IReadOnlyList<string> LastReadWarnings { get; private set; } = Array.Empty<string>();

    public Task<IReadOnlyList<ThermalSample>> ReadThermalAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        ReadAsync("thermal", fromUtc, toUtc, ct, row =>
        {
            if (row.Timestamp("timestamp") is not { } at) return (ThermalSample?)null;
            return new ThermalSample(
                at,
                row.Number("temp_c"),
                row.NumberUnlessSentinel("forecast_c", -1),
                row.Byte("fan1_raw"),
                row.Byte("fan2_raw"),
                row.NumberUnlessSentinel("commanded_pct", -1) is { } p ? (int)p : null,
                row.Bool("throttling"),
                row.Text("mode"),
                row.Text("sensor"));
        });

    public Task<IReadOnlyList<NetworkSample>> ReadNetworkAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        ReadAsync("network", fromUtc, toUtc, ct, row =>
        {
            if (row.Timestamp("timestamp") is not { } at) return (NetworkSample?)null;
            return new NetworkSample(
                at,
                row.Text("target") ?? "",
                row.Number("rtt_ms"),
                row.Int("lost") == 1,
                row.Number("avg_ms"),
                row.Number("jitter_ms"),
                row.Number("loss_pct"));
        });

    public Task<IReadOnlyList<PowerEvent>> ReadPowerEventsAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        ReadAsync("power", fromUtc, toUtc, ct, row =>
        {
            if (row.Timestamp("timestamp") is not { } at) return (PowerEvent?)null;
            return new PowerEvent(
                at,
                row.Text("source") ?? "",
                row.Text("event") ?? "",
                row.Text("detail") ?? "",
                row.Text("action") ?? "");
        });

    public Task<IReadOnlyList<PollTimingSample>> ReadPollTimingAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        ReadAsync("polltiming", fromUtc, toUtc, ct, row =>
        {
            if (row.Timestamp("timestamp") is not { } at) return (PollTimingSample?)null;
            return new PollTimingSample(
                at,
                row.Int("ticks") ?? 0,
                row.Number("avg_total_ms"),
                row.Number("avg_temp_ms"),
                row.Number("avg_fan_ms"),
                row.Number("avg_slow_ms"),
                row.Number("avg_dispatch_ms"));
        });

    /// <summary>
    /// The oldest and newest instant on disk for a stream, or null when there is nothing.
    ///
    /// What a window selector needs in order to offer ranges that contain data instead of
    /// ranges that merely look plausible.
    /// </summary>
    public async Task<(DateTime From, DateTime To)?> CoverageAsync(
        TelemetryStream stream, CancellationToken ct = default)
    {
        DateTime wide = DateTime.UtcNow.AddYears(-5);
        DateTime now = DateTime.UtcNow.AddDays(1);

        IReadOnlyList<DateTime> stamps = stream switch
        {
            TelemetryStream.Thermal => (await ReadThermalAsync(wide, now, ct).ConfigureAwait(false)).Select(s => s.AtUtc).ToList(),
            TelemetryStream.Network => (await ReadNetworkAsync(wide, now, ct).ConfigureAwait(false)).Select(s => s.AtUtc).ToList(),
            TelemetryStream.Power => (await ReadPowerEventsAsync(wide, now, ct).ConfigureAwait(false)).Select(s => s.AtUtc).ToList(),
            _ => (await ReadPollTimingAsync(wide, now, ct).ConfigureAwait(false)).Select(s => s.AtUtc).ToList(),
        };

        return stamps.Count == 0 ? null : (stamps[0], stamps[^1]);
    }

    /// <summary>
    /// Load-test runs on disk, newest first.
    ///
    /// The instant comes from the filename, which the writer produced in LOCAL time while every
    /// row inside is UTC. That is reported as a local stamp rather than silently corrected: a
    /// run written before the writer carried a timestamp column genuinely cannot be placed on a
    /// shared axis, and shifting it by the current offset would place it somewhere plausible and
    /// wrong, differently wrong across a DST boundary.
    /// </summary>
    public IReadOnlyList<(string Path, DateTime LocalStamp)> ListLoadRuns()
    {
        var runs = new List<(string, DateTime)>();

        try
        {
            if (!Directory.Exists(_directory)) return runs;

            foreach (string path in Directory.EnumerateFiles(_directory, "loadtest-*.csv"))
            {
                string tail = Path.GetFileNameWithoutExtension(path)["loadtest-".Length..];
                if (DateTime.TryParseExact(tail, "yyyy-MM-dd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var stamp))
                    runs.Add((path, stamp));
            }
        }
        catch
        {
            // An unreadable directory yields nothing, not an exception.
        }

        runs.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return runs;
    }

    /// <summary>Reads one load run. Rows carry a UTC instant only if that run's writer wrote one.</summary>
    public Task<IReadOnlyList<LoadRunSample>> ReadLoadRunAsync(string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var result = CsvLogReader.Read(path);
            var samples = new List<LoadRunSample>(result.Rows.Count);

            foreach (var row in result.Rows)
            {
                ct.ThrowIfCancellationRequested();

                double? elapsed = row.Number("elapsed_s");
                if (elapsed is null) continue;

                samples.Add(new LoadRunSample(
                    row.Timestamp("timestamp"),
                    TimeSpan.FromSeconds(elapsed.Value),
                    row.Number("temp_c"),
                    row.Text("sensor"),
                    row.Number("package_w"),
                    row.Number("cpu_ghz"),
                    row.Int("fan_rpm"),
                    row.Bool("throttling")));
            }

            LastReadWarnings = Array.Empty<string>();
            return (IReadOnlyList<LoadRunSample>)samples;
        }, ct);

    /// <summary>
    /// The shared path: gather the day files, parse each, keep what falls in range, sort.
    ///
    /// Sorted by timestamp rather than trusted in file order, because the -N roll files of a
    /// single day are not chronological among themselves. thermal-2026-09-13-2.csv can hold rows
    /// earlier than thermal-2026-09-13.csv, depending on when the header changed.
    /// </summary>
    private Task<IReadOnlyList<T>> ReadAsync<T>(
        string prefix, DateTime fromUtc, DateTime toUtc, CancellationToken ct, Func<CsvRow, T?> parse)
        where T : struct
    {
        return Task.Run(() =>
        {
            var samples = new List<(DateTime At, T Value)>();
            var warnings = new List<string>();

            foreach (string path in CsvLogReader.FilesFor(_directory, prefix, fromUtc, toUtc))
            {
                ct.ThrowIfCancellationRequested();

                var result = CsvLogReader.Read(path);

                // Rows read but nothing parsed means a file whose shape this build does not
                // understand, which is worth saying -- unlike a single torn tail line.
                if (result.Rows.Count == 0 && SafeLength(path) > 0)
                    warnings.Add(Path.GetFileName(path));

                foreach (var row in result.Rows)
                {
                    if (parse(row) is not { } value) continue;

                    DateTime at = row.Timestamp("timestamp")!.Value;
                    if (at < fromUtc || at > toUtc) continue;

                    samples.Add((at, value));
                }
            }

            samples.Sort((a, b) => a.At.CompareTo(b.At));
            LastReadWarnings = warnings;
            return (IReadOnlyList<T>)samples.Select(s => s.Value).ToList();
        }, ct);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
