namespace OmniHub.Core.Telemetry;

/// <summary>
/// One row from a log, typed.
///
/// Every field that a log can leave empty is nullable here, and stays null rather than being
/// filled with a zero or a last-known value. That is the same rule the writers follow -- an
/// unreadable sensor writes an empty field, never a 0 -- and a reader that collapsed the two
/// would quietly undo it on the way back in. A gap is visible; a plausible number is not.
/// </summary>
public readonly record struct ThermalSample(
    DateTime AtUtc,
    double? TempC,
    double? ForecastC,
    byte? Fan1Raw,
    byte? Fan2Raw,
    int? CommandedPercent,
    bool? Throttling,
    string? Mode,
    string? Sensor,

    /// <summary>The discrete GPU, null on every row written before these columns existed.</summary>
    double? GpuTempC = null,
    double? GpuWatts = null,

    /// <summary>Sustained package power, and which constraint was binding at the time.</summary>
    double? PackageWatts = null,
    string? Limit = null,
    double? LimitPercent = null);

/// <summary><see cref="RttMs"/> is null for a lost probe, which <see cref="Lost"/> states separately.</summary>
public readonly record struct NetworkSample(
    DateTime AtUtc,
    string Target,
    double? RttMs,
    bool Lost,
    double? AvgMs,
    double? JitterMs,
    double? LossPercent);

/// <summary>One power transition, and what the application did about it.</summary>
public readonly record struct PowerEvent(
    DateTime AtUtc,
    string Source,
    string Event,
    string Detail,
    string Action);

/// <summary>Averaged cost of the hardware poll, one row per thirty ticks.</summary>
public readonly record struct PollTimingSample(
    DateTime AtUtc,
    int Ticks,
    double? AvgTotalMs,
    double? AvgTempMs,
    double? AvgFanMs,
    double? AvgSlowMs,
    double? AvgDispatchMs);

/// <summary>
/// One sample from a load run.
///
/// <see cref="AtUtc"/> is null for runs recorded before the writer carried a timestamp column.
/// Those files anchor only to their filename, which was written in LOCAL time while every row
/// inside is UTC -- so aligning an old run against the thermal trace is wrong by the offset, and
/// differently wrong across a DST boundary. Null says "this run cannot be placed on a shared
/// time axis" rather than placing it somewhere plausible.
/// </summary>
public readonly record struct LoadRunSample(
    DateTime? AtUtc,
    TimeSpan Elapsed,
    double? TempC,
    string? Sensor,
    double? PackageWatts,
    double? CpuGhz,
    int? FanRpm,
    bool? Throttling);

/// <summary>Which stream a query is about.</summary>
public enum TelemetryStream
{
    Thermal,
    Network,
    Power,
    PollTiming,
}
