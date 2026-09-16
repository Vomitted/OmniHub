namespace OmniHub.Core.Diagnostics;

/// <summary>What Windows recorded, reduced to the kinds that matter for a hang.</summary>
public enum StabilityEventKind
{
    /// <summary>Kernel-Power 41 or EventLog 6008: the machine stopped without shutting down.</summary>
    UncleanShutdown,

    /// <summary>A bugcheck. Distinguishes a crash Windows diagnosed from one it never saw.</summary>
    Bugcheck,

    /// <summary>The event log started. In practice, a boot.</summary>
    Boot,

    /// <summary>The event log stopped. In practice, a deliberate shutdown or restart.</summary>
    CleanShutdown,

    /// <summary>Entering modern standby.</summary>
    SleepEnter,

    /// <summary>Leaving modern standby.</summary>
    SleepExit,
}

/// <summary>One record from the Windows event log, with the fields a hang investigation needs.</summary>
/// <param name="AtUtc">When the record was written, which for a recovery record is at the next boot.</param>
/// <param name="SleepInProgress">
/// Kernel-Power 41's own field. Non-null only on that event. Carried verbatim rather than
/// interpreted: this project has already read too much into its value once.
/// </param>
public readonly record struct StabilityEvent(
    DateTime AtUtc,
    StabilityEventKind Kind,
    string Detail,
    int? BugcheckCode = null,
    int? SleepInProgress = null);

/// <summary>One hang, reconstructed.</summary>
/// <param name="StoppedUtc">
/// The last moment there is evidence the machine was executing, from OmniHub's own thermal
/// trace. Null when nothing was logging at the time.
/// </param>
/// <param name="RecoveredUtc">The boot that followed.</param>
/// <param name="Silence">How long the machine was down, if the stop time is known.</param>
/// <param name="NearSleep">Whether a sleep transition happened within the correlation window.</param>
/// <param name="Context">What OmniHub itself recorded in the minutes before it stopped.</param>
public sealed record StabilityIncident(
    DateTime? StoppedUtc,
    DateTime RecoveredUtc,
    TimeSpan? Silence,
    int? SleepInProgress,
    int? BugcheckCode,
    bool NearSleep,
    IReadOnlyList<string> Context);

/// <summary>
/// Turns event records, a thermal trace and OmniHub's own power log into a list of incidents.
///
/// Pure and static, like RunStats and AdaptiveTuning.Direction, and for the same reason: the
/// part worth testing is the reasoning, and keeping it away from the event log is what makes it
/// testable at all.
///
/// The reasoning it encodes was worked out by hand during an actual investigation on this
/// machine, and two of its rules exist because the obvious approach gave the wrong answer.
///
/// FIRST: a Kernel-Power 41 record is written at the NEXT BOOT, not when the machine died. Its
/// timestamp is a recovery time. Reading it as the moment of failure put the hangs an hour or
/// more away from where they actually happened and made them look uncorrelated with everything.
///
/// SECOND: the moment of failure is better recovered from this application's own thermal trace
/// than from Windows. The trace writes a row about every two seconds, so the last row before a
/// boot is the last moment there is evidence the machine was executing. That is far sharper than
/// EventLog 6008's own "stopped at" field, which Windows samples coarsely and which was observed
/// naming the previous boot's timestamp rather than the failure.
/// </summary>
public static class StabilityCorrelator
{
    /// <summary>
    /// How close a sleep transition must be to count as near a hang.
    ///
    /// Thirty minutes is generous on purpose. The question this answers is whether a hang is
    /// plausibly a sleep-transition failure, and too tight a window would answer no for a machine
    /// that took several minutes to wedge. On the incidents examined here every modern-standby
    /// entry exited within twenty-six seconds and none fell inside this window of a hang, which
    /// is a much stronger statement for having been made with a generous threshold.
    /// </summary>
    public static readonly TimeSpan SleepProximity = TimeSpan.FromMinutes(30);

    /// <summary>How far back to look for what the application was doing before it stopped.</summary>
    public static readonly TimeSpan ContextWindow = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Builds one incident per unclean shutdown.
    ///
    /// <paramref name="activityUtc"/> is any series of instants proving the machine was running,
    /// in ascending order. The thermal trace is the intended source.
    /// </summary>
    public static IReadOnlyList<StabilityIncident> Correlate(
        IReadOnlyList<StabilityEvent> events,
        IReadOnlyList<DateTime> activityUtc,
        IReadOnlyList<(DateTime AtUtc, string Text)> appLog)
    {
        var incidents = new List<StabilityIncident>();

        foreach (var recovery in events.Where(e => e.Kind == StabilityEventKind.UncleanShutdown)
                                       .OrderBy(e => e.AtUtc))
        {
            // The last evidence the machine was executing before this recovery boot.
            DateTime? stopped = null;
            for (int i = activityUtc.Count - 1; i >= 0; i--)
            {
                if (activityUtc[i] >= recovery.AtUtc) continue;
                stopped = activityUtc[i];
                break;
            }

            // A hang that happened while nothing was logging leaves no stop time, and saying so
            // is better than substituting the recovery time and calling the outage zero.
            TimeSpan? silence = stopped is { } s ? recovery.AtUtc - s : null;

            DateTime anchor = stopped ?? recovery.AtUtc;

            bool nearSleep = events.Any(e =>
                (e.Kind == StabilityEventKind.SleepEnter || e.Kind == StabilityEventKind.SleepExit)
                && (anchor - e.AtUtc).Duration() <= SleepProximity);

            int? bugcheck = events
                .Where(e => e.Kind == StabilityEventKind.Bugcheck
                            && (e.AtUtc - recovery.AtUtc).Duration() <= TimeSpan.FromMinutes(5))
                .Select(e => e.BugcheckCode)
                .FirstOrDefault();

            var context = appLog
                .Where(l => l.AtUtc <= anchor && anchor - l.AtUtc <= ContextWindow)
                .OrderBy(l => l.AtUtc)
                .Select(l => l.Text)
                .ToList();

            incidents.Add(new StabilityIncident(
                stopped, recovery.AtUtc, silence, recovery.SleepInProgress, bugcheck, nearSleep, context));
        }

        return incidents;
    }

    /// <summary>
    /// What the set of incidents supports saying, and nothing more.
    ///
    /// Deliberately conservative. This application has already blamed itself twice for a fault it
    /// did not cause, and read a diagnosis into a field it had misunderstood, and each time the
    /// confident sentence was the expensive part. The rule here is that a pattern is reported as
    /// a count, never as a cause.
    /// </summary>
    public static string Summarise(IReadOnlyList<StabilityIncident> incidents)
    {
        if (incidents.Count == 0) return "No unclean shutdowns recorded in this range.";

        int nearSleep = incidents.Count(i => i.NearSleep);
        int bugchecked = incidents.Count(i => i.BugcheckCode is > 0);

        string text = $"{incidents.Count} unclean shutdown(s). ";

        text += bugchecked == 0
            ? "None produced a bugcheck, so Windows did not diagnose any of them: they are hangs "
              + "rather than crashes it caught. "
            : $"{bugchecked} produced a bugcheck. ";

        text += nearSleep == 0
            ? $"None happened within {SleepProximity.TotalMinutes:0} minutes of a sleep transition."
            : $"{nearSleep} happened within {SleepProximity.TotalMinutes:0} minutes of a sleep transition.";

        return text;
    }
}
