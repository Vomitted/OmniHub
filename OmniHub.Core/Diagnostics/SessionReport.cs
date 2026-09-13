using System.Globalization;

namespace OmniHub.Core.Diagnostics;

/// <summary>One row of the thermal log. Fields that could not be parsed stay null rather than defaulting.</summary>
public readonly record struct ThermalRow(
    DateTime Utc, double TempC, double? ForecastC, int CommandedPercent,
    bool Throttling, string Mode, string Sensor, double SoakPercent = 0,
    string BindingLimit = "", double BindingLimitPercent = 0, double? PackageWatts = null,
    string GpuPState = "", string GpuPowerState = "");

/// <summary>One row of the connection log. RttMs is null for a probe that never returned.</summary>
public readonly record struct NetworkRow(DateTime Utc, double? RttMs, bool Lost);

/// <summary>What a session amounted to. Pure summary, no hardware and no files.</summary>
public sealed record SessionReport(
    DateTime From,
    DateTime To,
    int Samples,
    double MedianTempC,
    double P90TempC,
    double MaxTempC,
    TimeSpan Throttled,
    TimeSpan SensorBlind,
    double MedianFanPercent,
    double MaxFanPercent,
    double HotAndQuietFraction,
    double CoolAndLoudFraction,
    double SoakActiveFraction,
    IReadOnlyList<(string Limit, double Fraction)> LimitBreakdown,
    int NetworkSamples,
    double NetworkLossPercent,
    double NetworkWorstRttMs,
    IReadOnlyList<string> Findings)
{
    public TimeSpan Duration => To - From;

    /// <summary>Nothing was logged, so there is nothing to report. Distinct from a quiet session.</summary>
    ///
    /// Built with NAMED arguments. This record has grown twice and both times the positional
    /// form here broke, because adding a field in the middle silently shifts every value after
    /// it onto the wrong property -- a failure that compiles perfectly whenever the types line
    /// up. Naming them costs a few characters and removes the entire class of mistake.
    public static SessionReport Empty { get; } = new(
        From: default,
        To: default,
        Samples: 0,
        MedianTempC: 0,
        P90TempC: 0,
        MaxTempC: 0,
        Throttled: TimeSpan.Zero,
        SensorBlind: TimeSpan.Zero,
        MedianFanPercent: 0,
        MaxFanPercent: 0,
        HotAndQuietFraction: 0,
        CoolAndLoudFraction: 0,
        SoakActiveFraction: 0,
        LimitBreakdown: Array.Empty<(string, double)>(),
        NetworkSamples: 0,
        NetworkLossPercent: 0,
        NetworkWorstRttMs: 0,
        Findings: new[] { "No log rows for this day. Turn on thermal logging in Settings to record one." });
}

/// <summary>
/// Turns the logs this application already writes into something a person can act on.
///
/// The logs were the gap. OmniHub has recorded a row every two seconds for weeks and nothing has
/// ever read one back: diagnosing anything meant exporting a CSV and squinting at it, which is
/// why several real problems in this project were found by hand from a spreadsheet rather than by
/// the application that collected the evidence. The numbers were always there.
///
/// Everything here is derived from logged values and nothing is invented. Where a conclusion
/// needs a threshold the threshold is named in the finding itself, so a reader can disagree with
/// it: "hot and quiet" is a definition, not a measurement, and stating it is the difference
/// between a diagnosis and an opinion.
/// </summary>
public static class SessionAnalysis
{
    /// <summary>Temperature at or above which the fan being slow is worth remarking on.</summary>
    public const double HotC = 78.0;

    /// <summary>Fan level below which, when hot, the curve is arguably too quiet.</summary>
    public const int QuietPercent = 55;

    /// <summary>Temperature below which a loud fan has nothing to cool.</summary>
    public const double CoolC = 65.0;

    /// <summary>Fan level above which, when cool, the fan is arguably working for nothing.</summary>
    public const int LoudPercent = 70;

    public static SessionReport Analyse(
        IReadOnlyList<ThermalRow> thermal,
        IReadOnlyList<NetworkRow>? network = null)
    {
        if (thermal.Count == 0) return SessionReport.Empty;

        var temps = thermal.Select(r => r.TempC).ToList();
        var fans = thermal.Select(r => (double)r.CommandedPercent).ToList();

        // Each row stands for the interval between it and the next, so elapsed time is measured
        // from the timestamps rather than assumed to be the nominal two seconds. The poll loop
        // re-arms after each tick rather than on a fixed period, so the real cadence drifts --
        // measured at 2.31s during the BIOS call work -- and multiplying a row count by 2 would
        // quietly understate every duration here.
        TimeSpan Span(Func<ThermalRow, bool> predicate)
        {
            var total = TimeSpan.Zero;
            for (int i = 0; i < thermal.Count - 1; i++)
            {
                if (!predicate(thermal[i])) continue;
                var gap = thermal[i + 1].Utc - thermal[i].Utc;

                // A gap larger than a minute is the application having been closed or asleep,
                // not a sixty-second sample. Counting it would inflate a throttle total with
                // time the machine spent switched off.
                if (gap > TimeSpan.Zero && gap <= TimeSpan.FromMinutes(1)) total += gap;
            }
            return total;
        }

        double Fraction(Func<ThermalRow, bool> predicate) =>
            thermal.Count == 0 ? 0 : (double)thermal.Count(predicate) / thermal.Count;

        var throttled = Span(r => r.Throttling);
        var blind = Span(r => r.Sensor.Equals("AcpiThermalZone", StringComparison.OrdinalIgnoreCase)
                              && r.TempC >= 84.5);

        double hotQuiet = Fraction(r => r.TempC >= HotC && r.CommandedPercent < QuietPercent);
        double coolLoud = Fraction(r => r.TempC <= CoolC && r.CommandedPercent > LoudPercent);

        var netSamples = network ?? Array.Empty<NetworkRow>();
        int netCount = netSamples.Count;
        double loss = netCount == 0 ? 0 : 100.0 * netSamples.Count(n => n.Lost) / netCount;
        double worstRtt = netSamples.Where(n => n.RttMs is not null).Select(n => n.RttMs!.Value)
                                    .DefaultIfEmpty(0).Max();

        var report = new SessionReport(
            From: thermal[0].Utc,
            To: thermal[^1].Utc,
            Samples: thermal.Count,
            MedianTempC: RunStats.Median(temps) ?? 0,
            P90TempC: RunStats.Percentile(temps, 0.90) ?? 0,
            MaxTempC: temps.Max(),
            Throttled: throttled,
            SensorBlind: blind,
            MedianFanPercent: RunStats.Median(fans) ?? 0,
            MaxFanPercent: fans.Max(),
            HotAndQuietFraction: hotQuiet,
            CoolAndLoudFraction: coolLoud,
            SoakActiveFraction: Fraction(r => r.SoakPercent >= 1),

            // Only samples where a limit was genuinely read. Rows from before this column
            // existed, or from a session where the SMU never opened, carry an empty name, and
            // counting those as "no limit" would dilute every fraction toward a reassuring zero.
            LimitBreakdown: thermal
                .Where(r => !string.IsNullOrEmpty(r.BindingLimit))
                .GroupBy(r => r.BindingLimit)
                .Select(g => (Limit: g.Key, Fraction: (double)g.Count()
                    / thermal.Count(r => !string.IsNullOrEmpty(r.BindingLimit))))
                .OrderByDescending(x => x.Fraction)
                .ToList(),
            NetworkSamples: netCount,
            NetworkLossPercent: loss,
            NetworkWorstRttMs: worstRtt,
            Findings: Array.Empty<string>());

        return report with { Findings = Findings(report, thermal) };
    }

    /// <summary>
    /// The conclusions, in the order they are worth reading.
    ///
    /// Kept separate from the arithmetic so the rules can be tested against constructed sessions
    /// rather than against whatever the machine happened to do. Each one names the threshold it
    /// used, because a reader who disagrees with the threshold should be able to see it rather
    /// than having to trust the sentence.
    /// </summary>
    private static List<string> Findings(SessionReport r, IReadOnlyList<ThermalRow> thermal)
    {
        var findings = new List<string>();

        if (r.Throttled > TimeSpan.Zero)
            findings.Add($"The processor reported throttling for {Describe(r.Throttled)} of a "
                       + $"{Describe(r.Duration)} session. That is the firmware saying it could not "
                       + "sustain what was asked of it.");

        if (r.SensorBlind > TimeSpan.FromMinutes(1))
            findings.Add($"For {Describe(r.SensorBlind)} the reading came from the ACPI zone sitting on "
                       + "its 85C ceiling, so the real die temperature was unknown and at least that "
                       + "high. Those samples are floors, not measurements.");

        // The failure this whole application exists to prevent. Checked explicitly rather than
        // left to the hot-and-quiet rule, because zero while hot is a different thing from slow
        // while hot, and it should never appear at all.
        int stalled = thermal.Count(t => t.CommandedPercent == 0 && t.TempC >= 60);
        if (stalled > 0)
            findings.Add($"{stalled} sample(s) commanded 0% fan while the machine was at or above 60C. "
                       + "That is the stock-BIOS failure this application exists to prevent, and it "
                       + "should not happen under the curve.");

        if (r.HotAndQuietFraction >= 0.05)
            findings.Add($"{r.HotAndQuietFraction:P0} of samples were at or above {HotC:0}C with the fan "
                       + $"under {QuietPercent}%. If the machine felt hot, raising the curve through "
                       + "that band is the change that would show up.");

        if (r.CoolAndLoudFraction >= 0.05)
            findings.Add($"{r.CoolAndLoudFraction:P0} of samples were at or below {CoolC:0}C with the fan "
                       + $"above {LoudPercent}%. That is airflow bought for nothing, and it is what a "
                       + "long predictive lead does on a chip that bursts.");

        // What actually held the machine back, which is the question that decides whether
        // tuning or cooling is the useful thing to work on at all. Being pinned at core current
        // while the power limit has headroom says plainly that raising the power limit will
        // achieve nothing, and no wattage on its own conveys that.
        if (r.LimitBreakdown.Count > 0)
        {
            var top = r.LimitBreakdown[0];
            string rest = r.LimitBreakdown.Count > 1
                ? ", then " + string.Join(", ", r.LimitBreakdown.Skip(1).Take(2)
                    .Select(x => $"{x.Limit.ToLowerInvariant()} {x.Fraction:P0}"))
                : "";
            findings.Add($"The closest constraint was {top.Limit.ToLowerInvariant()} for {top.Fraction:P0} "
                       + $"of samples{rest}. That is the limit worth attacking; the others have room.");
        }

        // Reported whenever it did anything, including when it did nothing measurable. A
        // control term that silently contributes is one nobody can evaluate, and this one adds
        // airflow the curve did not ask for -- that has to be attributable after the fact.
        if (r.SoakActiveFraction >= 0.02)
        {
            double peak = thermal.Count == 0 ? 0 : thermal.Max(t => t.SoakPercent);
            findings.Add($"Thermal soak added airflow on {r.SoakActiveFraction:P0} of samples, "
                       + $"peaking at {peak:0.#} points above the curve. That is heat still in the "
                       + "heatsink after a load, which the die temperature alone does not show.");
        }

        // The "stuck at P4" report, made checkable. A discrete GPU sitting in a high performance
        // state while doing nothing is burning 12 to 14 watts on this machine for no work, and it
        // is intermittent enough that it was previously only ever a memory.
        var gpuStates = thermal.Where(t => t.GpuPState.Length > 0).ToList();
        if (gpuStates.Count > 0)
        {
            // P8 is idle, P0 is flat out. Anything numerically below P8 while the machine is not
            // gaming is the condition worth naming.
            int busy = gpuStates.Count(t => t.GpuPState.Length >= 2
                && int.TryParse(t.GpuPState.AsSpan(1), out int n) && n <= 5);

            double share = (double)busy / gpuStates.Count;
            if (share >= 0.2)
                findings.Add($"The discrete GPU sat in a high performance state for {share:P0} of the "
                           + "samples that could see it. If you were not gaming for that long, something "
                           + "is holding a GPU context open: an animated wallpaper and a hardware-accelerated "
                           + "browser both do it, and on battery that is 12 to 14 watts for no work.");
        }

        if (r.NetworkSamples > 0 && r.NetworkLossPercent >= 1)
            findings.Add($"The connection lost {r.NetworkLossPercent:0.#}% of probes over {r.NetworkSamples} "
                       + "samples. Above about 1% is felt in a game as rubber-banding and shots that do "
                       + "not register.");

        if (findings.Count == 0)
            findings.Add($"Nothing stands out. Median {r.MedianTempC:0}C, peak {r.MaxTempC:0.#}C, fan "
                       + $"typically {r.MedianFanPercent:0}%, no throttling recorded.");

        return findings;
    }

    private static string Describe(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hours"
        : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} minutes"
        : $"{t.TotalSeconds:0} seconds";

    // ---- reading the files -------------------------------------------------

    /// <summary>
    /// Reads a log that is very probably being written to right now.
    ///
    /// File.ReadLines opens with FileShare.Read, which refuses a file another process holds open
    /// for writing -- and the log for TODAY is always held open by the running application, which
    /// is the report anyone actually wants. Sharing ReadWrite is what makes "summarise this
    /// session" work during the session rather than only after a restart.
    ///
    /// A torn final line is possible as a result, since the writer flushes on an interval. That is
    /// handled by the per-row parsing below skipping anything it cannot read, which it has to do
    /// anyway for a file truncated by a hard power-off.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>
    /// Parses a thermal log. Unparseable rows are skipped rather than throwing.
    ///
    /// A log is evidence written by an older build, sometimes truncated by a hard power-off --
    /// which on this machine is the exact scenario worth reading a log about. Refusing the whole
    /// file over one short final line would lose the rows that matter most.
    /// </summary>
    public static List<ThermalRow> ReadThermal(string path)
    {
        var rows = new List<ThermalRow>();
        if (!File.Exists(path)) return rows;

        foreach (string line in ReadLinesShared(path).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 9) continue;

            if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;
            if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double temp)) continue;

            double? forecast = double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double fc) && fc >= 0
                ? fc : null;
            int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int commanded);

            // The tenth column is optional, because every log written before the soak term
            // existed has nine. Those files are still the evidence for everything that happened
            // before today and must keep parsing; a reader that rejected them would throw away
            // the history at the moment it became worth comparing against.
            double soak = 0;
            if (f.Length >= 10)
                double.TryParse(f[9], NumberStyles.Float, CultureInfo.InvariantCulture, out soak);

            // Columns eleven and twelve are optional for the same reason the tenth is: they did
            // not exist when most of this history was written.
            string limit = f.Length >= 11 ? f[10] : "";
            double limitPct = 0;
            if (f.Length >= 12) double.TryParse(f[11], NumberStyles.Float, CultureInfo.InvariantCulture, out limitPct);

            double? watts = f.Length >= 13
                && double.TryParse(f[12], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                ? w : null;

            rows.Add(new ThermalRow(utc, temp, forecast, commanded,
                f[6].Equals("True", StringComparison.OrdinalIgnoreCase), f[7], f[8], soak, limit, limitPct, watts,
                f.Length >= 14 ? f[13] : "", f.Length >= 15 ? f[14].TrimEnd() : ""));
        }

        return rows;
    }

    /// <summary>Parses a connection log. An empty rtt_ms means the probe was lost, never that it took no time.</summary>
    public static List<NetworkRow> ReadNetwork(string path)
    {
        var rows = new List<NetworkRow>();
        if (!File.Exists(path)) return rows;

        foreach (string line in ReadLinesShared(path).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 4) continue;

            if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) continue;

            double? rtt = double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
            rows.Add(new NetworkRow(utc, rtt, f[3].Trim() == "1"));
        }

        return rows;
    }

    /// <summary>Reads and analyses one day's logs together.</summary>
    public static SessionReport ForDate(string logDirectory, DateTime date)
    {
        var thermal = ReadThermal(Path.Combine(logDirectory, $"thermal-{date:yyyy-MM-dd}.csv"));
        var network = ReadNetwork(Path.Combine(logDirectory, $"network-{date:yyyy-MM-dd}.csv"));
        return Analyse(thermal, network);
    }

    /// <summary>Days that have a thermal log, newest first.</summary>
    public static List<DateTime> AvailableDates(string logDirectory)
    {
        var dates = new List<DateTime>();
        if (!Directory.Exists(logDirectory)) return dates;

        foreach (string file in Directory.EnumerateFiles(logDirectory, "thermal-*.csv"))
        {
            string stem = Path.GetFileNameWithoutExtension(file).Replace("thermal-", "");

            // Some files carry a "-2" suffix from a second run on the same day. Taking the first
            // ten characters keeps those on the date they belong to rather than dropping them.
            if (stem.Length >= 10 && DateTime.TryParseExact(stem[..10], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && !dates.Contains(d))
                dates.Add(d);
        }

        dates.Sort((a, b) => b.CompareTo(a));
        return dates;
    }
}
