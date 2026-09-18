// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Text;

namespace OmniHub.Core.Telemetry;

/// <summary>
/// One row, addressed by the column names that file declared.
///
/// Never by position. Four different thermal layouts exist on this machine's disk right now --
/// 8, 9, 13 and 15 columns -- because the writer rolls to a suffixed file whenever its header
/// changes, and two of those layouts came from a diagnostic build that was later reverted. A
/// positional reader would shift `mode` into `sensor` and look entirely plausible doing it.
/// </summary>
public readonly struct CsvRow
{
    private readonly IReadOnlyDictionary<string, int> _columns;
    private readonly string[] _fields;

    internal CsvRow(IReadOnlyDictionary<string, int> columns, string[] fields)
    {
        _columns = columns;
        _fields = fields;
    }

    /// <summary>The raw field, or null when this file has no such column or left it empty.</summary>
    public string? Text(string column) =>
        _columns.TryGetValue(column, out int i) && i < _fields.Length && _fields[i].Length > 0
            ? _fields[i]
            : null;

    public double? Number(string column) =>
        double.TryParse(Text(column), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    public int? Int(string column) =>
        int.TryParse(Text(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    public byte? Byte(string column) =>
        byte.TryParse(Text(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : null;

    /// <summary>Tri-state: true, false, or null when the column is absent or unparseable.</summary>
    public bool? Bool(string column) =>
        Text(column) is { } s && bool.TryParse(s, out bool v) ? v : null;

    /// <summary>
    /// A number that the writer uses a sentinel for.
    ///
    /// forecast_c is -1 when prediction was off and commanded_pct is -1 when the fan service had
    /// not commanded anything -- 8,694 of them in one day's file on this machine. Neither is a
    /// temperature or a percentage, and returning them as values would put a -1 degree reading
    /// on a chart.
    /// </summary>
    public double? NumberUnlessSentinel(string column, double sentinel)
    {
        double? v = Number(column);
        return v is null || Math.Abs(v.Value - sentinel) < 0.0001 ? null : v;
    }

    /// <summary>
    /// A UTC instant, parsed strictly.
    ///
    /// AssumeUniversal | AdjustToUniversal rather than a plain parse: the plain one returns
    /// Kind.Local and silently shifts an entire history by the machine's offset, which on this
    /// one is seven hours. A history quietly wrong by seven hours is worse than one that refuses
    /// to load.
    /// </summary>
    public DateTime? Timestamp(string column) =>
        DateTime.TryParse(Text(column), CultureInfo.InvariantCulture,
                          DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
}

/// <summary>What a read found, and what it could not use.</summary>
/// <param name="Rows">Parsed rows, in file order.</param>
/// <param name="SkippedRows">Rows that could not be used. A torn last line is normal.</param>
public readonly record struct CsvReadResult(IReadOnlyList<CsvRow> Rows, int SkippedRows);

/// <summary>
/// Reads the CSV logs this application writes, tolerantly enough to be useful on real files.
///
/// Four things about those files decide this design, and all four were verified against the
/// logs on disk rather than assumed:
///
///   - four thermal header layouts coexist, so rows are keyed by the file's own header;
///   - thermal-*.csv carries a UTF-8 BOM and the others do not, because ThermalLog constructs
///     its writer with Encoding.UTF8 while the rest wrap a FileStream;
///   - -1 is a sentinel in two columns, not a value;
///   - ThermalLog holds its file FileAccess.Write, FileShare.Read, so a reader MUST ask for
///     FileShare.ReadWrite. FileShare.Read fails against the live writer, which would make the
///     current day -- the day anyone actually wants -- the one day that cannot be read.
///
/// A torn final row is expected rather than exceptional: the writers flush on a ten-second
/// interval, so the tail of today's file is routinely a partial line. It is skipped and counted,
/// never thrown.
/// </summary>
public static class CsvLogReader
{
    /// <summary>Reads one file. Returns no rows rather than throwing when it cannot be read.</summary>
    public static CsvReadResult Read(string path)
    {
        var rows = new List<CsvRow>();
        int skipped = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

            string? headerLine = reader.ReadLine();
            if (headerLine is null) return new CsvReadResult(rows, 0);

            // Belt and braces on the BOM. detectEncodingFromByteOrderMarks handles it, but a
            // stray U+FEFF surviving into the first column name would make every lookup of
            // "timestamp" miss on exactly the files that have one -- which is the thermal trace,
            // the largest and most useful of them.
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string[] names = headerLine.TrimStart('\uFEFF').Split(',');
            for (int i = 0; i < names.Length; i++) columns[names[i].Trim()] = i;

            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;

                string[] fields = line.Split(',');

                // More fields than the header is a genuinely malformed row. FEWER is the normal
                // shape of a torn last line, and is tolerated: the missing columns read as
                // absent, which is what they are.
                if (fields.Length > names.Length) { skipped++; continue; }

                rows.Add(new CsvRow(columns, fields));
            }
        }
        catch
        {
            // Locked, vanished, or not a file. The caller gets what was read and a count. A
            // history view that refuses to draw because one of fourteen days is unreadable is
            // worse than one that draws thirteen and says so.
        }

        return new CsvReadResult(rows, skipped);
    }

    /// <summary>
    /// Every file for a prefix whose day falls in [from, to], including the -N roll files.
    ///
    /// The date comes from the filename because that is what the writers key on, and the range
    /// is widened by a day at each end: a file named for one day can hold rows either side of a
    /// boundary, and the roll files break the one-file-per-day assumption entirely.
    ///
    /// Returned in name order, which is chronological for this scheme except among the roll
    /// files of a single day. Callers needing strict ordering sort by timestamp.
    /// </summary>
    public static IReadOnlyList<string> FilesFor(string directory, string prefix, DateTime fromUtc, DateTime toUtc)
    {
        var found = new List<string>();

        try
        {
            if (!Directory.Exists(directory)) return found;

            DateTime first = fromUtc.Date.AddDays(-1);
            DateTime last = toUtc.Date.AddDays(1);

            foreach (string path in Directory.EnumerateFiles(directory, prefix + "-*.csv"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length < prefix.Length + 11) continue;

                // "2026-09-15" or "2026-09-15-2". Ten characters either way.
                string tail = name[(prefix.Length + 1)..];
                if (tail.Length < 10) continue;
                if (!DateTime.TryParseExact(tail[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var day))
                    continue;

                if (day >= first && day <= last) found.Add(path);
            }
        }
        catch
        {
            // Same reasoning as Read: an unreadable directory yields nothing, not an exception.
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }
}
