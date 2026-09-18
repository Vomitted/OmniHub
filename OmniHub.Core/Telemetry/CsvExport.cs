// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Text;

namespace OmniHub.Core.Telemetry;

/// <summary>
/// Measurements, in a form that can leave the window.
///
/// The point of this release is being able to show that a change helped, and a proof nobody can
/// send anywhere is half a proof. A chart on screen cannot be attached to a message, opened in a
/// spreadsheet, or diffed against next week's.
///
/// The thermal export deliberately writes the same nine columns as the log it came from, so an
/// exported window and the raw file open side by side in the same tool with the same header. The
/// alternative -- a tidier bespoke shape -- would be a second format to learn for no gain.
///
/// Nulls stay empty. That is the rule the writers follow and the reader depends on: an empty
/// fan1_raw means the level was not reported and a zero means a stopped fan, and an export that
/// collapsed the two would undo the whole point of the distinction on the way out of the door.
/// </summary>
public static class CsvExport
{
    /// <summary>The thermal log's own header, so an export is readable by everything that reads the log.</summary>
    public const string ThermalHeader =
        "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor,gpu_c,gpu_w,pkg_w,limit,limit_pct";

    /// <summary>
    /// Thermal samples, in the log's own shape.
    ///
    /// Invariant culture throughout, for the same reason the writer uses it: a decimal comma
    /// inside a comma-separated file silently destroys the column layout, and this file is going
    /// to be opened somewhere nobody controls.
    /// </summary>
    public static string Thermal(IReadOnlyList<ThermalSample> samples)
    {
        var text = new StringBuilder(ThermalHeader.Length + samples.Count * 64);
        text.Append(ThermalHeader).Append('\n');

        foreach (var s in samples)
        {
            text.Append(s.AtUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append(',')
                .Append(Number(s.TempC, "0.#")).Append(',')
                .Append(Number(s.ForecastC, "0.#")).Append(',')
                .Append(s.Fan1Raw?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(s.Fan2Raw?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(s.CommandedPercent?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
                .Append(s.Throttling is { } t ? (t ? "True" : "False") : "").Append(',')
                .Append(Escape(s.Mode)).Append(',')
                .Append(Escape(s.Sensor)).Append(',')
                .Append(Number(s.GpuTempC, "0.#")).Append(',')
                .Append(Number(s.GpuWatts, "0.#")).Append(',')
                .Append(Number(s.PackageWatts, "0.#")).Append(',')
                .Append(Escape(s.Limit)).Append(',')
                .Append(Number(s.LimitPercent, "0.#")).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// A comparison, as a table somebody can paste into a message.
    ///
    /// Two columns of measurements and the verdict beneath them, rather than one row per metric
    /// with the verdict lost in a cell. The interval is included because it is the honest part --
    /// the sentence is a reading of the interval, not a substitute for it.
    /// </summary>
    public static string Comparison(RunComparisonResult result)
    {
        var text = new StringBuilder();

        text.Append("metric,baseline,recent\n");

        Row(text, "samples", result.A.Samples.ToString(CultureInfo.InvariantCulture),
                             result.B.Samples.ToString(CultureInfo.InvariantCulture));
        Row(text, "measured_seconds",
            result.A.Covered.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
            result.B.Covered.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
        Row(text, "mean_temp_c", Number(result.A.MeanTempC, "0.0"), Number(result.B.MeanTempC, "0.0"));
        Row(text, "p90_temp_c", Number(result.A.P90TempC, "0.0"), Number(result.B.P90TempC, "0.0"));
        Row(text, "max_temp_c", Number(result.A.MaxTempC, "0.0"), Number(result.B.MaxTempC, "0.0"));
        Row(text, "median_fan_percent",
            Number(result.A.MedianFanPercent, "0"), Number(result.B.MedianFanPercent, "0"));

        text.Append('\n');

        WriteInterval(text, "temperature_difference_c", result.TemperatureDifference);
        WriteInterval(text, "fan_difference_percent", result.FanDifference);

        text.Append('\n').Append("verdict,").Append(Escape(result.Verdict)).Append('\n');

        return text.ToString();
    }

    private static void Row(StringBuilder text, string name, string a, string b) =>
        text.Append(Escape(name)).Append(',').Append(a).Append(',').Append(b).Append('\n');

    /// <summary>
    /// An interval as its two bounds, or as nothing at all.
    ///
    /// An absent interval writes empty bounds rather than zeros: "no difference measurable" and
    /// "a difference of exactly zero" are opposite claims, and a spreadsheet cannot tell them
    /// apart once both are written as 0.
    /// </summary>
    private static void WriteInterval(StringBuilder text, string name, Interval? interval)
    {
        text.Append(Escape(name)).Append("_low,").Append(Escape(name)).Append("_high\n");

        text.Append(interval is { } low ? low.Low.ToString("0.000", CultureInfo.InvariantCulture) : "")
            .Append(',')
            .Append(interval is { } high ? high.High.ToString("0.000", CultureInfo.InvariantCulture) : "")
            .Append('\n');
    }

    private static string Number(double? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    /// <summary>
    /// A field, quoted where it has to be.
    ///
    /// The verdict is a whole sentence and contains commas, so writing it raw would split it
    /// across columns and, worse, shift everything after it. Quoting is not decoration here.
    /// </summary>
    internal static string Escape(string? field)
    {
        if (field is not { Length: > 0 }) return "";

        bool needsQuotes = field.Contains(',') || field.Contains('"') || field.Contains('\n');

        return needsQuotes ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
    }
}
