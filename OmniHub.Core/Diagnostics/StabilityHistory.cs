using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace OmniHub.Core.Diagnostics;

/// <summary>Why the machine stopped, as far as the event can tell.</summary>
public enum ShutdownKind
{
    /// <summary>Event 41 with nothing distinguishing in it. Usually a hard hang or a power cut.</summary>
    Unknown,

    /// <summary>Died during a sleep transition. SleepInProgress was non-zero.</summary>
    MidSleep,

    /// <summary>A bugcheck ran, so a dump should exist naming a driver.</summary>
    Bugcheck,

    /// <summary>The power button was held. A person did this on purpose.</summary>
    PowerButton,
}

/// <summary>One unexpected shutdown, and what the machine was doing just before it.</summary>
public sealed record UnexpectedShutdown(
    DateTime LocalTime,
    ShutdownKind Kind,
    int BugcheckCode,
    int SleepInProgress,
    double? LastTempC,
    int? LastFanPercent)
{
    public string Describe() => Kind switch
    {
        ShutdownKind.MidSleep => "died during a sleep transition",
        ShutdownKind.Bugcheck => $"bugcheck 0x{BugcheckCode:X}",
        ShutdownKind.PowerButton => "power button held",
        _ => "hard stop, no bugcheck",
    };
}

/// <summary>
/// Reads the machine's own record of every time it stopped without being asked to.
///
/// Windows logs Kernel-Power event 41 on the next boot after a shutdown it did not perform
/// cleanly, and the payload distinguishes the cases that matter. A non-zero BugcheckCode means a
/// crash dump exists somewhere naming a driver. A non-zero SleepInProgress means the machine died
/// mid-transition rather than during use, which is an entirely different fault with entirely
/// different causes. Both zero is a hard hang or a power cut, and those are the ones nobody can
/// otherwise investigate at all, because by definition nothing was written down.
///
/// This exists because that reasoning was done by hand, from a shell, to diagnose freezes on this
/// machine: three events carrying SleepInProgress=6 and no dumps at all, which is what pointed at
/// sleep transitions rather than at thermals. Doing it by hand once is diagnosis; leaving it done
/// is a feature, and it is the only way to answer "have the freezes actually stopped" with
/// anything better than an impression.
///
/// Nothing here is inferred beyond what the event states. Where the thermal log happens to cover
/// the moment, the last reading before the stop is attached, because "it was at 84C with the fan
/// at 30%" and "it was idle at 45C" point in opposite directions.
/// </summary>
public static class StabilityHistory
{
    /// <summary>Why the last read failed, or null when it succeeded. An empty result with this
    /// set means "could not look", which is not the same as "nothing found".</summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// Reads recent unexpected shutdowns, newest first.
    ///
    /// Returns an empty list rather than throwing when the log cannot be read. This is a panel on
    /// a diagnostics screen; it is not worth taking a view down over, and an empty list reads
    /// correctly as "none found".
    /// </summary>
    /// <param name="days">How far back to look.</param>
    /// <param name="logDirectory">Where the thermal logs live, for correlation. Null skips it.</param>
    public static List<UnexpectedShutdown> Read(int days = 60, string? logDirectory = null)
    {
        var found = new List<UnexpectedShutdown>();
        LastError = null;

        try
        {
            // Filtered in the query rather than in a loop. The System log holds hundreds of
            // thousands of records and event 41 is a handful of them; reading them all in order to
            // discard almost all of them is the difference between instant and a visible stall.
            long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
            var query = new EventLogQuery(
                "System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41) "
                + $"and TimeCreated[timediff(@SystemTime) <= {ms}]]]")
            {
                ReverseDirection = true,   // newest first
            };

            using var reader = new EventLogReader(query);
            for (EventRecord? record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated is not DateTime when) continue;

                    var data = ParseData(record);
                    int bugcheck = Get(data, "BugcheckCode");
                    int sleep = Get(data, "SleepInProgress");
                    int button = Get(data, "PowerButtonTimestamp");

                    // Order matters. A bugcheck during a sleep transition is still a bugcheck,
                    // because a bugcheck leaves a dump and the dump is the better evidence.
                    var kind = bugcheck != 0 ? ShutdownKind.Bugcheck
                             : sleep != 0 ? ShutdownKind.MidSleep
                             : button != 0 ? ShutdownKind.PowerButton
                             : ShutdownKind.Unknown;

                    var (temp, fan) = logDirectory is null
                        ? (null, null)
                        : LastReadingBefore(logDirectory, when);

                    found.Add(new UnexpectedShutdown(when, kind, bugcheck, sleep, temp, fan));
                }
            }
        }
        // Recorded, not swallowed.
        //
        // The first version caught these and returned an empty list, and an empty list is
        // rendered as "no unexpected shutdowns in the last 60 days" -- a reassuring sentence,
        // produced here by a query that had failed outright. It did exactly that during
        // development: the XPath carried an XML-escaped "&lt;=" where a bare "<=" belongs, matched
        // nothing, and reported a clean machine that had in fact stopped unexpectedly nine times.
        // Silence and zero are different answers and must not render the same.
        catch (EventLogException ex) { LastError = ex.Message; }
        catch (UnauthorizedAccessException ex) { LastError = ex.Message; }
        catch (PlatformNotSupportedException ex) { LastError = ex.Message; }

        return found;
    }

    private static Dictionary<string, string> ParseData(EventRecord record)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = XDocument.Parse(record.ToXml());
            XNamespace ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;

            foreach (var element in doc.Descendants(ns + "Data"))
            {
                string? name = element.Attribute("Name")?.Value;
                if (name is not null) map[name] = element.Value;
            }
        }
        catch { }
        return map;
    }

    private static int Get(Dictionary<string, string> data, string key) =>
        data.TryGetValue(key, out string? v) && int.TryParse(v, out int n) ? n : 0;

    /// <summary>
    /// The last thermal reading logged before a stop, if that day's log covers it.
    ///
    /// The log is written in UTC and the event carries local time, so the comparison is done in
    /// UTC after converting. Getting that backwards would silently pick a reading seven hours out
    /// on this machine, and it would still look entirely plausible.
    /// </summary>
    private static (double?, int?) LastReadingBefore(string logDirectory, DateTime localTime)
    {
        try
        {
            var utc = localTime.ToUniversalTime();
            var rows = SessionAnalysis.ReadThermal(
                Path.Combine(logDirectory, $"thermal-{utc:yyyy-MM-dd}.csv"));

            // Within two minutes, or it is not describing the same moment. A reading from hours
            // earlier is not "what the machine was doing when it died", it is merely the last row
            // in the file.
            var last = rows.LastOrDefault(r => r.Utc <= utc && utc - r.Utc <= TimeSpan.FromMinutes(2));
            return last == default ? (null, null) : (last.TempC, last.CommandedPercent);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// A plain-language summary of the pattern, or of its absence.
    ///
    /// <paramref name="since"/> is what lets a change be measured: given the date a fix went in,
    /// this reports what happened on each side of it, which is the only honest way to say whether
    /// the fix worked. Without it the answer is an impression, and an intermittent fault is
    /// exactly where impressions are worthless.
    /// </summary>
    public static string Summarise(IReadOnlyList<UnexpectedShutdown> events, int days, DateTime? since = null)
    {
        if (events.Count == 0 && LastError is { Length: > 0 } err)
            return $"The event log could not be read, so this says nothing about stability: {err}";

        if (events.Count == 0)
            return $"No unexpected shutdowns in the last {days} days. Windows logs one on the next "
                 + "boot after any stop it did not perform itself, so this covers hangs and power "
                 + "cuts as well as crashes.";

        int midSleep = events.Count(e => e.Kind == ShutdownKind.MidSleep);
        int bugchecks = events.Count(e => e.Kind == ShutdownKind.Bugcheck);

        var text = $"{events.Count} unexpected shutdown(s) in {days} days";
        if (midSleep > 0) text += $", {midSleep} of them during a sleep transition";
        if (bugchecks > 0) text += $", {bugchecks} with a bugcheck (a dump exists for those)";
        text += ".";

        if (since is DateTime cut)
        {
            int after = events.Count(e => e.LocalTime >= cut);
            int afterSleep = events.Count(e => e.LocalTime >= cut && e.Kind == ShutdownKind.MidSleep);

            text += after == 0
                ? $" None since {cut:d MMM}."
                : $" {after} since {cut:d MMM}" + (afterSleep > 0 ? $", {afterSleep} of those mid-sleep." : ".");
        }

        if (bugchecks == 0)
            text += " No bugchecks at all means no dumps were written, so no driver is named "
                  + "anywhere. That is what a hard hang looks like, and it is why these are hard "
                  + "to chase.";

        return text;
    }
}
