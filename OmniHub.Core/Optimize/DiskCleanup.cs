// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Optimize;

public sealed record CleanupTarget(string Label, string Path, long Bytes, int Files, string Note);

/// <summary>
/// Removes disposable files from a fixed allowlist of known-safe locations.
///
/// Two rules make this safe enough to ship, and neither is negotiable:
///
///   1. The locations are hardcoded below. Nothing is discovered, pattern-matched or
///      inferred, and no user document directory is ever touched. A cleaner that goes
///      looking for things that "look like" junk is how people lose work.
///
///   2. Only files older than <see cref="MinimumAge"/> are removed, so anything a running
///      process created moments ago is left alone. That avoids the classic cleaner bug of
///      deleting the temp file an installer is actively writing.
///
/// Scan() measures without touching anything, so the UI can show what would go before the
/// user commits to it.
/// </summary>
public static class DiskCleanup
{
    /// <summary>Files newer than this are skipped: they are likely still in use.</summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(24);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static IEnumerable<(string Label, string Path, string Note)> Candidates()
    {
        yield return ("User temp files", Path.GetTempPath(),
            "Scratch files left behind by applications and installers.");

        yield return ("System temp files", Path.Combine(WindowsDir, "Temp"),
            "The machine-wide equivalent. Needs Administrator.");

        yield return ("Crash dumps", Path.Combine(Local, "CrashDumps"),
            "Memory dumps from applications that crashed. Only useful while debugging one.");

        // Windows regenerates this on the next update check. Safe, and frequently the
        // largest single item on a machine that has been updating for a year.
        yield return ("Windows Update cache", Path.Combine(WindowsDir, "SoftwareDistribution", "Download"),
            "Installers for updates that have already been applied.");

        // Prefetch is a launch-time optimisation, so clearing it is a real trade: the next
        // launch of each app is marginally slower until Windows rebuilds the data.
        yield return ("Prefetch data", Path.Combine(WindowsDir, "Prefetch"),
            "Launch-timing hints. Windows rebuilds these; the next launch of each app is slightly slower.");

        yield return ("Delivery Optimization cache", Path.Combine(WindowsDir, "SoftwareDistribution", "DeliveryOptimization"),
            "Update payloads cached for peer-to-peer sharing.");
    }

    public static IReadOnlyList<CleanupTarget> Scan()
    {
        var results = new List<CleanupTarget>();
        DateTime cutoff = DateTime.UtcNow - MinimumAge;

        foreach (var (label, path, note) in Candidates())
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;

            long bytes = 0;
            int files = 0;
            foreach (var file in SafeWalk.Files(path))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff) continue;
                    bytes += info.Length;
                    files++;
                }
                catch { /* locked or vanished; it would be skipped on delete too */ }
            }

            if (files > 0) results.Add(new CleanupTarget(label, path, bytes, files, note));
        }
        return results;
    }

    /// <summary>
    /// Deletes the aged files in the given targets. Locked files are skipped rather than
    /// treated as errors: a temp file held open by a running process is normal.
    /// </summary>
    public static TuningResult Clean(IEnumerable<CleanupTarget> targets)
    {
        DateTime cutoff = DateTime.UtcNow - MinimumAge;
        long freed = 0;
        int deleted = 0, skipped = 0;

        foreach (var target in targets)
        {
            foreach (var file in SafeWalk.Files(target.Path))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoff) continue;
                    long size = info.Length;
                    info.Delete();
                    freed += size;
                    deleted++;
                }
                catch { skipped++; }
            }

            // Prune directories left empty, but never the target root itself: Windows
            // expects these to exist.
            TryRemoveEmptyDirectories(target.Path);
        }

        if (deleted == 0)
            return new TuningResult(true, skipped > 0
                ? $"Nothing removed; {skipped} file(s) were in use."
                : "Nothing old enough to remove.");

        string detail = $"Freed {ShaderCache.FormatBytes(freed)} across {deleted} file(s).";
        if (skipped > 0) detail += $" {skipped} in use and skipped.";
        return new TuningResult(true, detail);
    }

    private static void TryRemoveEmptyDirectories(string root)
    {
        // Deepest first (by real path depth, not string length), so a directory emptied by
        // removing its children is reconsidered afterwards. Junctions are excluded by
        // SafeWalk: deleting one would remove the link, and a caller cannot tell from the
        // path alone whether the target still matters.
        foreach (var dir in SafeWalk.DirectoriesDeepestFirst(root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }
}
