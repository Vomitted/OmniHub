using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// Reconstructing a hang from what survives it.
///
/// Both rules tested here were learned the expensive way on this machine. Kernel-Power 41 is
/// written at the NEXT BOOT, so its timestamp is a recovery time -- reading it as the moment of
/// failure put nine hangs an hour or more from where they happened and made them look
/// uncorrelated with everything. And the moment of failure is recovered better from this
/// application's own two-second thermal trace than from Windows, whose 6008 "stopped at" field
/// is sampled coarsely and was observed naming the previous boot rather than the failure.
/// </summary>
public class StabilityCorrelatorTests
{
    private static readonly DateTime Base = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static List<DateTime> Trace(DateTime from, int count, double seconds = 2) =>
        Enumerable.Range(0, count).Select(i => from.AddSeconds(i * seconds)).ToList();

    private static readonly (DateTime AtUtc, string Text)[] NoLog = Array.Empty<(DateTime, string)>();

    /// <summary>
    /// The headline rule: the stop time comes from the last trace row, not from the event.
    ///
    /// Here the machine stopped logging at 12:09:58 and the recovery boot was at 13:00. An
    /// implementation reading the event timestamp as the failure would report a hang at 13:00
    /// and an outage of zero.
    /// </summary>
    [Fact]
    public void TheStopTimeComesFromTheTraceNotFromTheRecoveryEvent()
    {
        var trace = Trace(Base, 300);                 // 12:00:00 to 12:09:58
        var recovery = Base.AddMinutes(60);           // 13:00

        var incidents = StabilityCorrelator.Correlate(
            new[] { new StabilityEvent(recovery, StabilityEventKind.UncleanShutdown, "41") },
            trace,
            NoLog);

        var incident = Assert.Single(incidents);

        Assert.Equal(trace[^1], incident.StoppedUtc);
        Assert.Equal(recovery, incident.RecoveredUtc);
        Assert.Equal(recovery - trace[^1], incident.Silence);
    }

    /// <summary>
    /// A hang while nothing was logging leaves no stop time, and says so. Substituting the
    /// recovery time would report an outage of zero seconds for a machine down for hours.
    /// </summary>
    [Fact]
    public void AHangWithNoTraceHasNoStopTime()
    {
        var incidents = StabilityCorrelator.Correlate(
            new[] { new StabilityEvent(Base, StabilityEventKind.UncleanShutdown, "41") },
            Array.Empty<DateTime>(),
            NoLog);

        Assert.Null(incidents[0].StoppedUtc);
        Assert.Null(incidents[0].Silence);
    }

    /// <summary>
    /// Sleep proximity is measured against the STOP time, not the recovery. The same mistake as
    /// the first test in a different place: the recovery can be hours later, so measuring from
    /// it would find sleep transitions near the reboot rather than near the hang.
    /// </summary>
    [Fact]
    public void SleepProximityIsMeasuredFromTheStopNotTheRecovery()
    {
        var trace = Trace(Base, 300);                            // stops 12:09:58
        var sleepNearStop = Base.AddMinutes(5);                  // inside 30 min of the stop
        var recovery = Base.AddHours(4);                         // far from it

        var incidents = StabilityCorrelator.Correlate(
            new[]
            {
                new StabilityEvent(sleepNearStop, StabilityEventKind.SleepEnter, "42"),
                new StabilityEvent(recovery, StabilityEventKind.UncleanShutdown, "41"),
            },
            trace,
            NoLog);

        Assert.True(incidents[0].NearSleep);
    }

    /// <summary>A sleep far from the stop is not counted, however close it is to the reboot.</summary>
    [Fact]
    public void ASleepNearOnlyTheRebootDoesNotCount()
    {
        var trace = Trace(Base, 300);
        var recovery = Base.AddHours(4);

        var incidents = StabilityCorrelator.Correlate(
            new[]
            {
                new StabilityEvent(recovery.AddMinutes(-2), StabilityEventKind.SleepExit, "107"),
                new StabilityEvent(recovery, StabilityEventKind.UncleanShutdown, "41"),
            },
            trace,
            NoLog);

        Assert.False(incidents[0].NearSleep);
    }

    /// <summary>
    /// SleepInProgress is carried through verbatim rather than interpreted. This project has
    /// already read a diagnosis into that field once and been wrong; the correlator's job is to
    /// preserve it, not to explain it.
    /// </summary>
    [Fact]
    public void TheRawSleepInProgressValueIsPreserved()
    {
        var incidents = StabilityCorrelator.Correlate(
            new[] { new StabilityEvent(Base, StabilityEventKind.UncleanShutdown, "41", SleepInProgress: 6) },
            Array.Empty<DateTime>(),
            NoLog);

        Assert.Equal(6, incidents[0].SleepInProgress);
    }

    /// <summary>What the application recorded just before it stopped is attached to the incident.</summary>
    [Fact]
    public void TheApplicationsOwnLastWordsAreAttached()
    {
        var trace = Trace(Base, 300);                 // stops 12:09:58
        var stop = trace[^1];

        var log = new[]
        {
            (stop.AddMinutes(-40), "far too early"),
            (stop.AddMinutes(-2), "display off"),
            (stop.AddSeconds(-20), "power source change"),
            (stop.AddMinutes(5), "after the fact"),
        };

        var incidents = StabilityCorrelator.Correlate(
            new[] { new StabilityEvent(Base.AddHours(2), StabilityEventKind.UncleanShutdown, "41") },
            trace,
            log);

        Assert.Equal(new[] { "display off", "power source change" }, incidents[0].Context);
    }

    /// <summary>One incident per unclean shutdown, in order.</summary>
    [Fact]
    public void EachUncleanShutdownBecomesOneIncident()
    {
        var events = new[]
        {
            new StabilityEvent(Base.AddHours(1), StabilityEventKind.UncleanShutdown, "41"),
            new StabilityEvent(Base.AddHours(5), StabilityEventKind.UncleanShutdown, "41"),
            new StabilityEvent(Base.AddHours(3), StabilityEventKind.Boot, "6005"),
        };

        var incidents = StabilityCorrelator.Correlate(events, Array.Empty<DateTime>(), NoLog);

        Assert.Equal(2, incidents.Count);
        Assert.True(incidents[0].RecoveredUtc < incidents[1].RecoveredUtc);
    }

    /// <summary>
    /// The summary reports counts, never causes.
    ///
    /// This application has blamed itself twice for a fault it did not cause and read a
    /// diagnosis into a misunderstood field once. Each time the confident sentence was the
    /// expensive part, so the summary is allowed to count and not to conclude.
    /// </summary>
    [Fact]
    public void TheSummaryCountsAndDoesNotConclude()
    {
        var incidents = StabilityCorrelator.Correlate(
            new[]
            {
                new StabilityEvent(Base, StabilityEventKind.UncleanShutdown, "41", SleepInProgress: 6),
                new StabilityEvent(Base.AddHours(6), StabilityEventKind.UncleanShutdown, "41", SleepInProgress: 6),
            },
            Array.Empty<DateTime>(),
            NoLog);

        string summary = StabilityCorrelator.Summarise(incidents);

        Assert.Contains("2 unclean shutdown", summary);
        Assert.Contains("did not diagnose", summary);      // no bugcheck on either
        Assert.Contains("None happened within", summary);  // no sleep events supplied

        // No causal language at all.
        Assert.DoesNotContain("caused", summary);
        Assert.DoesNotContain("because", summary);
    }

    [Fact]
    public void NoShutdownsIsSaidPlainly() =>
        Assert.Contains("No unclean shutdowns",
            StabilityCorrelator.Summarise(Array.Empty<StabilityIncident>()));

    /// <summary>
    /// Read-only, against this machine's actual System log, in the same spirit as
    /// HardwareReadTests.
    ///
    /// The correlator is pure and tested above; this checks the half that talks to Windows --
    /// that EventLogReader opens, that the named fields parse, and that a real log produces
    /// sensible records rather than an exception. It asserts shape, not content: a healthy
    /// machine with no incidents must pass too.
    /// </summary>
    [Fact]
    public void TheRealEventLogOnThisMachineReads()
    {
        var events = StabilityHistory.Read(DateTime.UtcNow.AddDays(-14), out string? error);

        // A managed machine can refuse the System log by policy. That is reported, not thrown.
        if (error is { Length: > 0 }) return;

        Assert.All(events, e => Assert.Equal(DateTimeKind.Utc, e.AtUtc.Kind));

        for (int i = 1; i < events.Count; i++)
            Assert.True(events[i].AtUtc >= events[i - 1].AtUtc, "events come back oldest first");

        // SleepInProgress only ever appears on an unclean shutdown record.
        Assert.All(events.Where(e => e.SleepInProgress is not null),
                   e => Assert.Equal(StabilityEventKind.UncleanShutdown, e.Kind));

        // Kernel-Power 41 and EventLog 6008 describe one stop from two providers, seconds apart
        // at the same boot. Counting both would double every incident on the timeline.
        var unclean = events.Where(e => e.Kind == StabilityEventKind.UncleanShutdown)
                            .Select(e => e.AtUtc)
                            .ToList();

        for (int i = 1; i < unclean.Count; i++)
            Assert.True((unclean[i] - unclean[i - 1]) > TimeSpan.FromMinutes(2),
                        "two unclean-shutdown records within two minutes means de-duplication failed");
    }
}
