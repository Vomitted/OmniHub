using System.IO;
using OmniHub.Core.Diagnostics;
using Xunit;

namespace OmniHub.Tests;

public class SessionReportTests
{
    private static List<ThermalRow> Session(
        int count, double tempC, int fanPercent,
        bool throttling = false, string sensor = "SmuDieTctl", int gapSeconds = 2)
    {
        var start = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(i => new ThermalRow(start.AddSeconds(i * gapSeconds), tempC, null, fanPercent,
                throttling, "Auto", sensor))
            .ToList();
    }

    [Fact]
    public void NoRows_ReportsNothingRatherThanAQuietSession()
    {
        var r = SessionAnalysis.Analyse(Array.Empty<ThermalRow>());

        Assert.Equal(0, r.Samples);
        Assert.Contains("No log rows", r.Findings[0]);
    }

    /// <summary>
    /// Elapsed time comes from the timestamps, not from multiplying rows by the nominal interval.
    ///
    /// The poll loop re-arms after each tick rather than running on a fixed period, so the real
    /// cadence drifts -- it was measured at 2.31s during the BIOS work. Assuming two seconds a row
    /// would understate every duration here by whatever that drift happens to be.
    /// </summary>
    [Fact]
    public void DurationsComeFromTimestamps()
    {
        var rows = Session(count: 100, tempC: 90, fanPercent: 100, throttling: true, gapSeconds: 3);

        var r = SessionAnalysis.Analyse(rows);

        // 99 intervals of 3 seconds; the final row has no successor to span.
        Assert.Equal(TimeSpan.FromSeconds(297), r.Throttled);
    }

    /// <summary>
    /// A gap where the application was closed or asleep is not a sample of that length.
    ///
    /// Without this, one overnight gap turns eight hours of the machine being switched off into
    /// eight hours of recorded throttling, which is not merely wrong but wrong in the alarming
    /// direction.
    /// </summary>
    [Fact]
    public void LongGapsAreNotCountedAsElapsedTime()
    {
        var start = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        var rows = new List<ThermalRow>
        {
            new(start, 90, null, 100, true, "Auto", "SmuDieTctl"),
            new(start.AddHours(8), 90, null, 100, true, "Auto", "SmuDieTctl"),
            new(start.AddHours(8).AddSeconds(2), 90, null, 100, true, "Auto", "SmuDieTctl"),
        };

        var r = SessionAnalysis.Analyse(rows);

        // Only the 2-second interval counts. The 8-hour hole is absence of data, not data.
        Assert.Equal(TimeSpan.FromSeconds(2), r.Throttled);
    }

    /// <summary>
    /// The failure the whole application exists to prevent gets its own finding, separate from
    /// the general "hot and quiet" rule. Zero while hot is a different thing from slow while hot.
    /// </summary>
    [Fact]
    public void FanStoppedWhileHot_IsCalledOutExplicitly()
    {
        var rows = Session(count: 50, tempC: 72, fanPercent: 0);

        var r = SessionAnalysis.Analyse(rows);

        Assert.Contains(r.Findings, f => f.Contains("0% fan") && f.Contains("60C"));
    }

    [Fact]
    public void HotAndQuiet_IsReportedWithItsThreshold()
    {
        var r = SessionAnalysis.Analyse(Session(count: 50, tempC: 82, fanPercent: 30));

        Assert.True(r.HotAndQuietFraction > 0.9);
        Assert.Contains(r.Findings, f => f.Contains("78C") && f.Contains("55%"));
    }

    /// <summary>
    /// The opposite complaint, and the one this machine actually had: the fan running hard with
    /// nothing to cool, which is what a long predictive lead does on a chip that bursts.
    /// </summary>
    [Fact]
    public void CoolAndLoud_IsReportedToo()
    {
        var r = SessionAnalysis.Analyse(Session(count: 50, tempC: 55, fanPercent: 90));

        Assert.True(r.CoolAndLoudFraction > 0.9);
        Assert.Contains(r.Findings, f => f.Contains("airflow bought for nothing"));
    }

    /// <summary>
    /// An ACPI reading pinned at its ceiling is a floor, not a measurement, and a session spent
    /// there has to say so -- otherwise the peak temperature reads as a confident 85C when the
    /// die could have been anywhere above it.
    /// </summary>
    [Fact]
    public void SensorSittingOnItsCeiling_IsReportedAsBlindNotAsMeasured()
    {
        var rows = Session(count: 200, tempC: 85.05, fanPercent: 100, sensor: "AcpiThermalZone");

        var r = SessionAnalysis.Analyse(rows);

        Assert.True(r.SensorBlind > TimeSpan.FromMinutes(1));
        Assert.Contains(r.Findings, f => f.Contains("floors, not measurements"));
    }

    /// <summary>A Tctl reading of 85C is a real 85C and must not be reported as a blind sensor.</summary>
    [Fact]
    public void TctlAtTheSameTemperatureIsNotBlind()
    {
        var r = SessionAnalysis.Analyse(Session(count: 200, tempC: 85.05, fanPercent: 100, sensor: "SmuDieTctl"));

        Assert.Equal(TimeSpan.Zero, r.SensorBlind);
    }

    [Fact]
    public void NetworkLossIsSummarisedAlongside()
    {
        var start = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        var net = Enumerable.Range(0, 100)
            .Select(i => new NetworkRow(start.AddSeconds(i * 5), i % 20 == 0 ? null : 12.0, i % 20 == 0))
            .ToList();

        var r = SessionAnalysis.Analyse(Session(50, 60, 30), net);

        Assert.Equal(100, r.NetworkSamples);
        Assert.Equal(5, r.NetworkLossPercent, 3);
        Assert.Contains(r.Findings, f => f.Contains("lost 5% of probes"));
    }

    [Fact]
    public void AQuietSessionSaysSoRatherThanListingNothing()
    {
        var r = SessionAnalysis.Analyse(Session(count: 100, tempC: 58, fanPercent: 20));

        Assert.Single(r.Findings);
        Assert.Contains("Nothing stands out", r.Findings[0]);
    }

    // ---- stability summaries ------------------------------------------------

    private static UnexpectedShutdown Stop(string date, ShutdownKind kind, int sleep = 0, int bugcheck = 0)
        => new(DateTime.Parse(date), kind, bugcheck, sleep, null, null);

    /// <summary>
    /// "None found" and "could not look" must not read the same.
    ///
    /// This is not hypothetical: the first version of the query carried an XML-escaped "&lt;=" in
    /// a bare XPath, matched nothing, swallowed the failure, and reported a clean machine that had
    /// in fact stopped unexpectedly nine times in sixty days. A reassuring sentence produced by a
    /// broken read is worse than an error.
    /// </summary>
    [Fact]
    public void NoEventsReadsAsCleanOnlyWhenTheReadSucceeded()
    {
        string clean = StabilityHistory.Summarise(Array.Empty<UnexpectedShutdown>(), 60);

        Assert.Contains("No unexpected shutdowns", clean);
        Assert.DoesNotContain("could not be read", clean);
    }

    /// <summary>
    /// The point of the whole panel: whether a fix changed anything. Counting either side of the
    /// date it went in is the only honest answer for an intermittent fault.
    /// </summary>
    [Fact]
    public void SummaryCountsEventsEitherSideOfAFixDate()
    {
        var events = new[]
        {
            Stop("2026-09-12 20:47", ShutdownKind.MidSleep, sleep: 6),
            Stop("2026-09-10 11:25", ShutdownKind.MidSleep, sleep: 6),
            Stop("2026-08-29 07:56", ShutdownKind.MidSleep, sleep: 6),
        };

        string before = StabilityHistory.Summarise(events, 60, new DateTime(2026, 9, 13));
        Assert.Contains("3 of them during a sleep transition", before);
        Assert.Contains("None since 13 Sep", before);

        string after = StabilityHistory.Summarise(events, 60, new DateTime(2026, 9, 1));
        Assert.Contains("2 since 1 Sep", after);
    }

    /// <summary>A bugcheck during a sleep transition is still a bugcheck: it left a dump, and the
    /// dump is the better evidence.</summary>
    [Fact]
    public void BugchecksAreReportedSeparatelyFromSleepDeaths()
    {
        var events = new[]
        {
            Stop("2026-09-12 20:47", ShutdownKind.Bugcheck, sleep: 6, bugcheck: 0x133),
            Stop("2026-09-10 11:25", ShutdownKind.MidSleep, sleep: 6),
        };

        string text = StabilityHistory.Summarise(events, 60);

        Assert.Contains("1 with a bugcheck", text);
        Assert.DoesNotContain("No bugchecks at all", text);
    }

    // ---- log schema ---------------------------------------------------------

    /// <summary>
    /// Every log written before the soak term existed has nine columns, and those files are the
    /// entire history this machine has. A reader that rejected them would throw away the evidence
    /// at exactly the moment it became worth comparing a change against.
    /// </summary>
    [Fact]
    public void NineColumnLogsFromOlderBuildsStillParse()
    {
        string path = Path.Combine(Path.GetTempPath(), $"omni-old-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, new[]
        {
            "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor",
            "2026-09-13T10:00:00Z,72.5,-1,30,29,45,False,Auto,SmuDieTctl",
            "2026-09-13T10:00:02Z,73.1,-1,31,30,47,True,Auto,SmuDieTctl",
        });

        try
        {
            var rows = SessionAnalysis.ReadThermal(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal(72.5, rows[0].TempC, 3);
            Assert.Equal(45, rows[0].CommandedPercent);
            Assert.True(rows[1].Throttling);

            // Absent, not guessed. A build that never had the term contributed nothing.
            Assert.Equal(0, rows[0].SoakPercent, 3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TenColumnLogsCarryTheSoakContribution()
    {
        string path = Path.Combine(Path.GetTempPath(), $"omni-new-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, new[]
        {
            "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor,soak_pct",
            "2026-09-13T10:00:00Z,72.5,-1,30,29,45,False,Auto,SmuDieTctl,0",
            "2026-09-13T10:00:02Z,66.0,-1,31,30,48,False,Auto,SmuDieTctl,12.5",
        });

        try
        {
            var rows = SessionAnalysis.ReadThermal(path);

            Assert.Equal(2, rows.Count);
            Assert.Equal(0, rows[0].SoakPercent, 3);
            Assert.Equal(12.5, rows[1].SoakPercent, 3);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// A term that quietly adds airflow has to be attributable afterwards, or a raised fan level
    /// in the log is indistinguishable from the curve misbehaving.
    /// </summary>
    [Fact]
    public void SoakContributionIsReportedWhenItDidSomething()
    {
        var start = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 60)
            .Select(i => new ThermalRow(start.AddSeconds(i * 2), 64, null, 40, false, "Auto",
                "SmuDieTctl", SoakPercent: i < 40 ? 11.0 : 0))
            .ToList();

        var r = SessionAnalysis.Analyse(rows);

        Assert.InRange(r.SoakActiveFraction, 0.6, 0.7);
        Assert.Contains(r.Findings, f => f.Contains("Thermal soak added airflow"));
    }

    /// <summary>And a session where it never contributed must not mention it at all.</summary>
    [Fact]
    public void SoakIsNotMentionedWhenItContributedNothing()
    {
        var r = SessionAnalysis.Analyse(Session(count: 100, tempC: 58, fanPercent: 20));

        Assert.Equal(0, r.SoakActiveFraction, 3);
        Assert.DoesNotContain(r.Findings, f => f.Contains("Thermal soak"));
    }

    // ---- binding limit ------------------------------------------------------

    private static List<ThermalRow> Limited(string limit, int count, double percent = 99)
    {
        var start = new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count)
            .Select(i => new ThermalRow(start.AddSeconds(i * 2), 75, null, 50, false, "Auto",
                "SmuDieTctl", 0, limit, percent))
            .ToList();
    }

    /// <summary>
    /// The conclusion the whole column exists for: knowing WHICH limit binds decides whether
    /// tuning or cooling is worth the effort. A machine pinned at core current gains nothing
    /// from a higher power limit, and no wattage on its own conveys that.
    /// </summary>
    [Fact]
    public void TheBindingLimitIsReportedWithItsShare()
    {
        var rows = Limited("Core current (EDC)", 80).Concat(Limited("Temperature", 20)).ToList();

        var r = SessionAnalysis.Analyse(rows);

        Assert.Equal("Core current (EDC)", r.LimitBreakdown[0].Limit);
        Assert.Equal(0.8, r.LimitBreakdown[0].Fraction, 2);
        Assert.Contains(r.Findings, f => f.Contains("core current (edc)") && f.Contains("80%"));
    }

    /// <summary>
    /// Rows from before the column existed carry an empty name. Counting those as "nothing was
    /// binding" would dilute every fraction toward a reassuring zero and invent a conclusion out
    /// of missing data.
    /// </summary>
    [Fact]
    public void SamplesWithNoLimitReadAreExcludedRatherThanCountedAsUnconstrained()
    {
        var measured = Limited("Core current (EDC)", 20);
        var blank = Session(count: 180, tempC: 75, fanPercent: 50);   // no limit column

        var r = SessionAnalysis.Analyse(measured.Concat(blank).ToList());

        Assert.Single(r.LimitBreakdown);
        Assert.Equal(1.0, r.LimitBreakdown[0].Fraction, 2);
    }

    [Fact]
    public void NoLimitDataMeansNoClaimAboutLimits()
    {
        var r = SessionAnalysis.Analyse(Session(count: 50, tempC: 60, fanPercent: 20));

        Assert.Empty(r.LimitBreakdown);
        Assert.DoesNotContain(r.Findings, f => f.Contains("closest constraint"));
    }
}
