namespace OmniHub.Core.Diagnostics;

/// <summary>One day's measurement of how well the machine shed heat.</summary>
/// <param name="Date">The day, as the log file names it (UTC).</param>
/// <param name="DegreesPerWatt">Temperature rise above the day's own idle floor, per watt.</param>
/// <param name="LoadSamples">How many samples were loaded enough to count.</param>
/// <param name="MedianWatts">Median power across those samples, so unlike days are comparable.</param>
/// <param name="MedianFanPercent">Median fan level across them, for the same reason.</param>
public sealed record CoolingSample(
    DateTime Date, double DegreesPerWatt, int LoadSamples, double MedianWatts, double MedianFanPercent);

/// <summary>
/// Tracks whether this machine's cooling is getting worse.
///
/// Temperature alone cannot answer that. Eighty degrees drawing 15 W and eighty degrees drawing
/// 50 W describe a blocked heatsink and a healthy one, and no fan curve distinguishes them. The
/// quantity that does is thermal resistance: how many degrees of rise each watt buys. That is a
/// property of the heatsink, the paste and the dust in the fins, and it degrades slowly enough
/// that nobody notices by feel until the machine is already throttling.
///
/// The inputs have been in the thermal log all along except for power, which is now recorded
/// beside them. Nothing here needs new hardware access; it is arithmetic over a file.
///
/// WHAT THIS IS NOT. It is an index, not a laboratory measurement, and the caveats belong in the
/// output rather than in a footnote:
///
///   Ambient is not measured. The day's own idle floor stands in for it, which is reasonable on a
///   laptop that idles in the same room and wrong the week somebody moves it somewhere hotter. A
///   step in the trend that coincides with a change of location is the room, not the heatsink.
///
///   Fan level and power are reported beside each day, because a day spent at a different fan
///   level is not comparable and the reader has to be able to see that rather than take a single
///   number on trust.
/// </summary>
public static class CoolingHealth
{
    /// <summary>Below this a sample is not under enough load for the ratio to mean anything.</summary>
    public const double MinimumLoadWatts = 12.0;

    /// <summary>A day needs at least this many loaded samples before it is worth plotting.</summary>
    public const int MinimumLoadSamples = 60;

    /// <summary>
    /// Measures one day.
    ///
    /// Returns null rather than a number when the day never ran a real load. That is the common
    /// case for a laptop used for browsing, and a thermal resistance derived from fifteen watts of
    /// idle would be a trend invented out of noise.
    /// </summary>
    public static CoolingSample? Measure(DateTime date, IReadOnlyList<ThermalRow> rows)
    {
        if (rows.Count == 0) return null;

        var loaded = rows
            .Where(r => r.PackageWatts is double w && w >= MinimumLoadWatts)
            .ToList();

        if (loaded.Count < MinimumLoadSamples) return null;

        // Idle floor as an ambient proxy: the coolest the machine got that day. Taken as a low
        // percentile rather than the outright minimum, because a single cold sample right after a
        // resume is not the idle temperature, it is the sensor waking up.
        var allTemps = rows.Select(r => r.TempC).ToList();
        double idleFloor = RunStats.Percentile(allTemps, 0.05) ?? allTemps.Min();

        var temps = loaded.Select(r => r.TempC).ToList();
        var watts = loaded.Select(r => r.PackageWatts!.Value).ToList();

        double medianTemp = RunStats.Median(temps) ?? 0;
        double medianWatts = RunStats.Median(watts) ?? 0;
        if (medianWatts <= 0) return null;

        double rise = medianTemp - idleFloor;
        if (rise <= 0) return null;

        return new CoolingSample(
            Date: date,
            DegreesPerWatt: rise / medianWatts,
            LoadSamples: loaded.Count,
            MedianWatts: medianWatts,
            MedianFanPercent: RunStats.Median(loaded.Select(r => (double)r.CommandedPercent).ToList()) ?? 0);
    }

    /// <summary>Measures every day that has a log, oldest first.</summary>
    public static List<CoolingSample> History(string logDirectory, int maxDays = 60)
    {
        var samples = new List<CoolingSample>();

        foreach (var date in SessionAnalysis.AvailableDates(logDirectory).Take(maxDays))
        {
            var rows = SessionAnalysis.ReadThermal(
                Path.Combine(logDirectory, $"thermal-{date:yyyy-MM-dd}.csv"));

            if (Measure(date, rows) is { } sample) samples.Add(sample);
        }

        samples.Sort((a, b) => a.Date.CompareTo(b.Date));
        return samples;
    }

    /// <summary>
    /// What the history amounts to, in words.
    ///
    /// Compares the earliest third against the latest third rather than first day against last.
    /// Two individual days differ for a dozen reasons that have nothing to do with the heatsink; a
    /// shift between two groups of days is the thing worth acting on. With too few days to form
    /// groups it says so rather than drawing a line through two points.
    /// </summary>
    public static string Summarise(IReadOnlyList<CoolingSample> history)
    {
        if (history.Count == 0)
            return "No day has enough sustained load to measure cooling performance. This needs a "
                 + "session that actually works the processor; browsing will not produce one.";

        if (history.Count < 6)
            return $"{history.Count} day(s) measured so far, currently {history[^1].DegreesPerWatt:0.00} "
                 + "degrees of rise per watt. A trend needs about a week of loaded sessions before it "
                 + "means anything.";

        int group = Math.Max(1, history.Count / 3);
        double early = history.Take(group).Average(s => s.DegreesPerWatt);
        double late = history.Skip(history.Count - group).Average(s => s.DegreesPerWatt);
        double changePercent = (late - early) / early * 100.0;

        string when = $"{history[0].Date:d MMM} to {history[^1].Date:d MMM}";

        if (changePercent >= 15)
            return $"Cooling is {changePercent:0}% worse than it was ({when}): {early:0.00} degrees per "
                 + $"watt then, {late:0.00} now. That is the pattern dust in the fins or pumped-out "
                 + "paste makes, and no fan curve compensates for it. Worth cleaning the heatsink. "
                 + "Check the fan levels beside each day first: a shift this size can also mean the "
                 + "room got hotter.";

        if (changePercent <= -15)
            return $"Cooling is {-changePercent:0}% better than it was ({when}), {early:0.00} to "
                 + $"{late:0.00} degrees per watt. Something improved: a clean, a repaste, a cooler "
                 + "room, or a curve change moving more air.";

        return $"Cooling is holding steady across {when}: {early:0.00} degrees per watt then, "
             + $"{late:0.00} now. Nothing here suggests the heatsink needs attention.";
    }
}
