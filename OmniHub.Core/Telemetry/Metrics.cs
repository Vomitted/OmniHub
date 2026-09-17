using System.Globalization;

namespace OmniHub.Core.Telemetry;

/// <summary>How a reading stands against its own thresholds.</summary>
public enum MetricLevel
{
    /// <summary>No reading, or nothing to compare it against.</summary>
    Unknown,
    Normal,
    Warn,
    Hot,
}

/// <summary>
/// One reading this application can show: what to call it, how to write it, and where its
/// thresholds are.
/// </summary>
/// <param name="WarnAt">
/// Null where there is no defensible threshold. Most of these readings have none: a wattage or a
/// clock is not good or bad on its own, and inventing a number to colour it by would be the same
/// fabrication this project refuses elsewhere, just in a different currency.
/// </param>
public sealed record MetricDefinition(
    string Key,
    string Label,
    string Unit,
    string Format,
    string? ShortLabel = null,
    double? WarnAt = null,
    double? HotAt = null)
{
    /// <summary>
    /// The name to use where there is no room for the full one.
    ///
    /// The overlay is a card drawn over somebody's game and is about a hundred and thirty pixels
    /// wide; "GPU CLOCK" does not fit and "GPU MHz" does. The two are the same reading, which is
    /// the point of them living on one definition rather than in two lists that can drift.
    /// </summary>
    public string Compact => ShortLabel ?? Label;
}

/// <summary>
/// The catalogue of readings, and how to render one.
///
/// It lives here rather than beside any one screen because three surfaces now show the same
/// figures -- the overlay, the dashboard and a workspace panel -- and a metric whose unit or
/// threshold differs between them is worse than one that is missing from two of them.
///
/// This defines presentation only. Where a value comes from is the application's business, since
/// that means hardware; what "CPU" is called, that it is a temperature, and that eighty degrees is
/// where it starts being worth noticing, is the same wherever it is drawn.
/// </summary>
public static class Metrics
{
    /// <summary>
    /// Every reading, in the order a picker should offer them.
    ///
    /// Thresholds exist on three. The two temperatures, because a die above eighty is worth
    /// noticing and above ninety is worth acting on. And the binding limit's percentage, at the
    /// same ninety-five per cent that <see cref="LimitHistory"/> already uses to decide a
    /// constraint is binding -- one number, one definition.
    /// </summary>
    public static IReadOnlyList<MetricDefinition> All { get; } = new[]
    {
        new MetricDefinition("cpu",     "CPU",       "°", "0.0", WarnAt: 80, HotAt: 90),
        new MetricDefinition("gpu",     "GPU",       "°", "0",   WarnAt: 80, HotAt: 87),
        new MetricDefinition("fan",     "FAN",       " RPM",   "0"),
        new MetricDefinition("fan2",    "FAN 2",     " RPM",   "0"),
        new MetricDefinition("pkg",     "PACKAGE",   "W",      "0.0", ShortLabel: "PKG"),
        new MetricDefinition("gpuw",    "GPU POWER", "W",      "0.0", ShortLabel: "GPU W"),
        new MetricDefinition("gpuclk",  "GPU CLOCK", "MHz",    "0",   ShortLabel: "GPU MHz"),
        new MetricDefinition("gpuload", "GPU LOAD",  "%",      "0",   ShortLabel: "GPU %"),
        new MetricDefinition("limit",   "LIMIT",     "%",      "0",
                             WarnAt: LimitHistory.BindingPercent, HotAt: 99),
        new MetricDefinition("cpuload", "CPU LOAD",  "%",      "0",   ShortLabel: "CPU %"),
        new MetricDefinition("cpuclk",  "CPU CLOCK", "GHz",    "0.00", ShortLabel: "CPU GHz"),
        new MetricDefinition("mem",     "MEMORY",    "GB",     "0.0", ShortLabel: "MEM"),
    };

    public static MetricDefinition? Find(string key)
    {
        foreach (var metric in All)
            if (metric.Key == key) return metric;

        return null;
    }

    /// <summary>What a missing reading renders as, everywhere.</summary>
    public const string Unavailable = "--";

    /// <summary>
    /// The value as it should be written, or "--" when there is nothing to write.
    ///
    /// Invariant culture, for the same reason the logs use it: a decimal comma in a figure that
    /// sits beside a unit reads as a thousands separator, and this machine is not configured in
    /// English.
    /// </summary>
    public static string Text(string key, double? value)
    {
        if (Find(key) is not { } metric || value is not { } v || double.IsNaN(v)) return Unavailable;

        return v.ToString(metric.Format, CultureInfo.InvariantCulture) + metric.Unit;
    }

    /// <summary>
    /// Where a reading stands against its thresholds.
    ///
    /// Unknown for a reading that is missing and for one whose metric has no thresholds, and the
    /// caller is expected to treat those the same: leave the figure its ordinary colour. A metric
    /// with nothing to compare against is not "fine", it is simply not that kind of number.
    /// </summary>
    public static MetricLevel LevelOf(string key, double? value)
    {
        if (Find(key) is not { } metric || value is not { } v || double.IsNaN(v)) return MetricLevel.Unknown;
        if (metric.WarnAt is not { } warn) return MetricLevel.Unknown;

        if (metric.HotAt is { } hot && v >= hot) return MetricLevel.Hot;

        return v >= warn ? MetricLevel.Warn : MetricLevel.Normal;
    }
}
