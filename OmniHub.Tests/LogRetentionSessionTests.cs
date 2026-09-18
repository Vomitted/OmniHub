using System.IO;
using OmniHub.Core.Diagnostics;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// A saved session holds the trace behind it open.
///
/// Worth testing against real files rather than reasoning about, because the failure is silent:
/// the session still exists, still names a range, and resolves to nothing. An A/B comparison that
/// has forgotten A is worse than one that refuses to run, because it still produces a verdict.
/// </summary>
public class LogRetentionSessionTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-retain-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }

    private static string Aged(string dir, string name, int daysOld)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "timestamp,temp_c\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void WithoutASessionAnOldTraceIsSwept()
    {
        string dir = NewDir();
        try
        {
            string old = Aged(dir, "thermal-old.csv", 30);

            LogRetention.Prune(dir, "thermal-*.csv");

            Assert.False(File.Exists(old));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ASessionReachingBackFurtherThanTheWindowKeepsTheTrace()
    {
        string dir = NewDir();
        try
        {
            string old = Aged(dir, "thermal-old.csv", 30);

            LogRetention.Prune(dir, "thermal-*.csv", keepSince: DateTime.UtcNow.AddDays(-45));

            Assert.True(File.Exists(old), "a session pointing at it should have kept it");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ASessionInsideTheWindowChangesNothing()
    {
        // The session extends the window; it does not replace it. One from this morning must not
        // start protecting a trace from a month ago, and must not start deleting one from
        // yesterday either.
        string dir = NewDir();
        try
        {
            string old = Aged(dir, "thermal-old.csv", 30);
            string recent = Aged(dir, "thermal-recent.csv", 2);

            LogRetention.Prune(dir, "thermal-*.csv", keepSince: DateTime.UtcNow.AddHours(-3));

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void TheFileBeingWrittenIsNeverSweptEvenWhenAncient()
    {
        // Deleting the open file leaves a writer appending to a handle nothing can find.
        string dir = NewDir();
        try
        {
            string current = Aged(dir, "thermal-current.csv", 90);

            LogRetention.Prune(dir, "thermal-*.csv", keepPath: current);

            Assert.True(File.Exists(current));
        }
        finally { Cleanup(dir); }
    }
}
