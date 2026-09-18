using System.IO;
using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class SessionTests
{
    private static readonly DateTime Noon = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    private static string NewFile() =>
        Path.Combine(
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-sessions-" + Guid.NewGuid().ToString("N"))).FullName,
            "sessions.json");

    private static void Cleanup(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { }
    }

    [Fact]
    public void AStartedSessionIsOpenUntilItIsStopped()
    {
        var log = SessionLog.Empty.Start(new Session("before undervolt", Noon));

        Assert.NotNull(log.Open);
        Assert.False(log.Sessions[0].IsClosed);

        log = log.Stop(Noon.AddMinutes(20));

        Assert.Null(log.Open);
        Assert.Equal(TimeSpan.FromMinutes(20), log.Sessions[0].Duration());
    }

    [Fact]
    public void StartingASecondClosesTheFirstRatherThanRefusing()
    {
        // The common way one is left open is a hang or a hard exit. Refusing to start a new
        // measurement because the machine crashed during the last one would punish somebody for
        // the thing this application is trying to diagnose.
        var log = SessionLog.Empty
            .Start(new Session("first", Noon))
            .Start(new Session("second", Noon.AddHours(1)));

        Assert.Equal(2, log.Sessions.Count);
        Assert.Equal(Noon.AddHours(1), log.Sessions[0].EndedUtc);
        Assert.Equal("second", log.Open!.Name);
    }

    [Fact]
    public void StoppingWithNothingOpenChangesNothing()
    {
        var log = SessionLog.Empty.Start(new Session("done", Noon)).Stop(Noon.AddMinutes(5));

        Assert.Same(log, log.Stop(Noon.AddMinutes(9)));
    }

    [Fact]
    public void ASessionKnowsWhichMomentsItCovers()
    {
        var session = new Session("run", Noon, Noon.AddMinutes(30));

        Assert.False(session.Covers(Noon.AddMinutes(-1)));
        Assert.True(session.Covers(Noon));
        Assert.True(session.Covers(Noon.AddMinutes(15)));
        Assert.True(session.Covers(Noon.AddMinutes(30)));
        Assert.False(session.Covers(Noon.AddMinutes(31)));
    }

    [Fact]
    public void TheEarliestReferencedMomentIsWhatTheLogRetentionNeeds()
    {
        var log = SessionLog.Empty
            .Start(new Session("old", Noon.AddDays(-30))).Stop(Noon.AddDays(-30).AddHours(1))
            .Start(new Session("recent", Noon));

        Assert.Equal(Noon.AddDays(-30), log.EarliestReferenced);
    }

    [Fact]
    public void WithNoSessionsNothingIsReferenced() => Assert.Null(SessionLog.Empty.EarliestReferenced);

    [Fact]
    public void ASessionSurvivesTheRoundTrip()
    {
        string file = NewFile();
        try
        {
            SessionLog.Empty
                .Start(new Session("before undervolt", Noon, null, "Mains", "Balanced", "1.3.3"))
                .Stop(Noon.AddMinutes(20))
                .Save(file);

            var read = SessionLog.Load(file);
            var session = Assert.Single(read.Sessions);

            Assert.Equal("before undervolt", session.Name);
            Assert.Equal(Noon, session.StartedUtc);
            Assert.Equal(Noon.AddMinutes(20), session.EndedUtc);
            Assert.Equal("Mains", session.PowerSource);
            Assert.Equal("Balanced", session.Profile);
            Assert.Equal("1.3.3", session.AppVersion);
        }
        finally { Cleanup(file); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("""{ "sessions": null }""")]
    public void AnUnreadableFileIsNoSessionsRatherThanAThrow(string contents)
    {
        string file = NewFile();
        try
        {
            File.WriteAllText(file, contents);

            // Notably including the null case: a record with a null list would make every caller
            // guard it, so Load normalises instead.
            Assert.NotNull(SessionLog.Load(file).Sessions);
            Assert.Empty(SessionLog.Load(file).Sessions);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void AMissingFileIsNoSessions()
    {
        string file = NewFile();
        try { Assert.Empty(SessionLog.Load(file).Sessions); }
        finally { Cleanup(file); }
    }

    [Fact]
    public void TheOldestAreDroppedOnceTheFileIsFull()
    {
        var log = SessionLog.Empty;

        for (int i = 0; i < SessionLog.Capacity + 25; i++)
            log = log.Start(new Session($"run {i}", Noon.AddMinutes(i)));

        Assert.Equal(SessionLog.Capacity, log.Sessions.Count);

        // The newest survive, and the order stays oldest-first so the list reads as a history.
        Assert.Equal($"run {SessionLog.Capacity + 24}", log.Sessions[^1].Name);
        Assert.True(log.Sessions[0].StartedUtc < log.Sessions[^1].StartedUtc);
    }

    [Fact]
    public void AnOpenSessionHasADurationThatGrows()
    {
        var session = new Session("running", Noon);

        Assert.Equal(TimeSpan.FromMinutes(5), session.Duration(Noon.AddMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(9), session.Duration(Noon.AddMinutes(9)));
    }
}
