// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// Five of the six log writers in this application kept everything forever.
///
/// Only the thermal trace pruned, and it is the one that grows fastest -- about a megabyte and
/// a half on a busy day -- so the writer with a policy was the writer that needed one least
/// urgently in absolute terms and most obviously in relative ones. The network log, the
/// power-transition log, the poll-timing trace and crash.log had none at all.
///
/// The two rules worth protecting are not "delete old files". They are which files are spared.
/// </summary>
public class LogRetentionTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-retention-" + Guid.NewGuid().ToString("N"))).FullName;

    private static string Aged(string dir, string name, int daysOld)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void DeletesOnlyWhatIsOlderThanTheWindow()
    {
        string dir = TempDir();
        try
        {
            Aged(dir, "thermal-old.csv", 20);
            Aged(dir, "thermal-recent.csv", 3);

            Assert.Equal(1, LogRetention.Prune(dir, "thermal-*.csv"));
            Assert.False(File.Exists(Path.Combine(dir, "thermal-old.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "thermal-recent.csv")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The file being written is never a candidate, however old its timestamp looks. Deleting
    /// it would leave a writer appending to a handle nothing can find.
    /// </summary>
    [Fact]
    public void TheFileCurrentlyOpenIsNeverDeleted()
    {
        string dir = TempDir();
        try
        {
            string current = Aged(dir, "thermal-today.csv", 99);

            Assert.Equal(0, LogRetention.Prune(dir, "thermal-*.csv", keepPath: current));
            Assert.True(File.Exists(current));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// Load-test runs and probe reports are deliberate artefacts, created so somebody could
    /// compare against them later. A sweep that eats the baseline half of a measurement is
    /// worse than no sweep, which is why the pattern is explicit rather than "*.csv".
    /// </summary>
    [Fact]
    public void DeliberateArtefactsAreNotSweptByAPatternedPrune()
    {
        string dir = TempDir();
        try
        {
            Aged(dir, "thermal-old.csv", 40);
            Aged(dir, "loadtest-2026-01-01-120000.csv", 40);
            Aged(dir, "probe-2026-01-01-120000.txt", 40);

            LogRetention.Prune(dir, "thermal-*.csv");

            Assert.True(File.Exists(Path.Combine(dir, "loadtest-2026-01-01-120000.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "probe-2026-01-01-120000.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AMissingDirectoryIsNotAnError() =>
        Assert.Equal(0, LogRetention.Prune(Path.Combine(Path.GetTempPath(), "omnihub-nope-" + Guid.NewGuid().ToString("N")), "*.csv"));

    /// <summary>
    /// crash.log is one growing file, appended by three handlers including the unobserved-task
    /// one, so a repeating background failure writes without limit. It keeps the TAIL: the most
    /// recent failure is the one worth having.
    /// </summary>
    [Fact]
    public void TrimKeepsTheEndOfTheFileNotTheStart()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "crash.log");
            var lines = Enumerable.Range(0, 5000).Select(i => $"line {i}");
            File.WriteAllText(path, string.Join(Environment.NewLine, lines));

            Assert.True(LogRetention.Trim(path, maxBytes: 8 * 1024, keepBytes: 2 * 1024));

            string after = File.ReadAllText(path);
            Assert.True(new FileInfo(path).Length < 8 * 1024);
            Assert.Contains("line 4999", after);
            Assert.DoesNotContain("line 0" + Environment.NewLine, after);
            Assert.Contains("earlier entries removed", after);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void TrimLeavesAFileUnderTheCeilingAlone()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "crash.log");
            File.WriteAllText(path, "short");

            Assert.False(LogRetention.Trim(path, maxBytes: 1024, keepBytes: 512));
            Assert.Equal("short", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
