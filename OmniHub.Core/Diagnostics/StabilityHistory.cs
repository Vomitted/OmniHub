// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace OmniHub.Core.Diagnostics;

/// <summary>
/// Reads what Windows recorded about this machine stopping.
///
/// The I/O half of the stability work; StabilityCorrelator holds the reasoning and the tests.
///
/// EventLogReader rather than WMI. Win32_NTLogEvent is reachable through System.Management,
/// which this project already references, and it was rejected on two counts: it scans the whole
/// System log rather than seeking, taking tens of seconds, and it flattens the event payload
/// into positional insertion strings. SleepInProgress -- the field that distinguishes "died
/// mid-sleep" from "lost power", and the entire reason for reading these records -- would then
/// be an index into an array whose shape is not contractual.
/// </summary>
public static class StabilityHistory
{
    /// <summary>
    /// Event ids read, and what each means here.
    ///
    /// Kernel-Power 41 and EventLog 6008 both mark the same failure from different providers, so
    /// a single stop usually produces both. They are de-duplicated on the way out; keeping both
    /// sources is what makes the read survive one of them being absent.
    /// </summary>
    private const string Query =
        "*[System[(EventID=41 or EventID=1001 or EventID=6005 or EventID=6006 or EventID=6008"
        + " or EventID=42 or EventID=107)]]";

    /// <summary>
    /// Everything relevant since <paramref name="sinceUtc"/>, oldest first.
    ///
    /// Never throws. The System log can be unreadable for policy reasons on a managed machine,
    /// and a diagnostics screen that fails to open because of that is worse than one reporting
    /// what it could get.
    /// </summary>
    public static IReadOnlyList<StabilityEvent> Read(DateTime sinceUtc, out string? error)
    {
        var events = new List<StabilityEvent>();
        error = null;

        try
        {
            var query = new EventLogQuery("System", PathType.LogName, Query) { ReverseDirection = false };
            using var reader = new EventLogReader(query);

            while (reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    if (record.TimeCreated is not { } local) continue;

                    DateTime at = local.ToUniversalTime();
                    if (at < sinceUtc) continue;

                    switch (record.Id)
                    {
                        case 41:
                            events.Add(new StabilityEvent(
                                at, StabilityEventKind.UncleanShutdown,
                                "Kernel-Power 41: rebooted without shutting down cleanly",
                                SleepInProgress: NamedInt(record, "SleepInProgress"),
                                BugcheckCode: NamedInt(record, "BugcheckCode")));
                            break;

                        case 1001:
                            events.Add(new StabilityEvent(
                                at, StabilityEventKind.Bugcheck, "Bugcheck recorded",
                                BugcheckCode: NamedInt(record, "BugcheckCode")));
                            break;

                        case 6005:
                            events.Add(new StabilityEvent(at, StabilityEventKind.Boot, "Event log started"));
                            break;

                        case 6006:
                            events.Add(new StabilityEvent(at, StabilityEventKind.CleanShutdown, "Event log stopped"));
                            break;

                        case 6008:
                            events.Add(new StabilityEvent(
                                at, StabilityEventKind.UncleanShutdown, "Previous shutdown was unexpected"));
                            break;

                        case 42:
                            events.Add(new StabilityEvent(at, StabilityEventKind.SleepEnter, "Entering sleep"));
                            break;

                        case 107:
                            events.Add(new StabilityEvent(at, StabilityEventKind.SleepExit, "Resumed from sleep"));
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return Deduplicate(events);
    }

    /// <summary>
    /// Kernel-Power 41 and EventLog 6008 describe one stop from two providers and are written
    /// seconds apart at the same boot. Counting both would double every incident.
    ///
    /// The 41 is kept where there is a choice, because it is the one carrying SleepInProgress
    /// and BugcheckCode.
    /// </summary>
    private static IReadOnlyList<StabilityEvent> Deduplicate(List<StabilityEvent> events)
    {
        events.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));

        var result = new List<StabilityEvent>(events.Count);

        foreach (var e in events)
        {
            if (e.Kind == StabilityEventKind.UncleanShutdown)
            {
                int existing = result.FindIndex(r =>
                    r.Kind == StabilityEventKind.UncleanShutdown
                    && (e.AtUtc - r.AtUtc).Duration() <= TimeSpan.FromMinutes(2));

                if (existing >= 0)
                {
                    // Prefer whichever record actually carries the diagnostic fields.
                    if (result[existing].SleepInProgress is null && e.SleepInProgress is not null)
                        result[existing] = e;
                    continue;
                }
            }

            result.Add(e);
        }

        return result;
    }

    /// <summary>
    /// One named field from the event payload, or null.
    ///
    /// By name, which is the whole reason for taking the EventLogReader dependency. The same
    /// value through WMI would be a positional index into an insertion-string array whose order
    /// is not contractual and has changed across Windows versions.
    /// </summary>
    private static int? NamedInt(EventRecord record, string name)
    {
        try
        {
            string xml = record.ToXml();

            string open = "Name='" + name + "'>";
            int start = xml.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;

            start += open.Length;
            int end = xml.IndexOf('<', start);
            if (end < 0) return null;

            string text = xml[start..end].Trim();

            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec) ? dec : null;
        }
        catch
        {
            return null;
        }
    }
}
