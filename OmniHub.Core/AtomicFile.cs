// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Text;

namespace OmniHub.Core;

/// <summary>
/// Replaces a file's contents in one step, so a reader never sees a half-written one.
///
/// This exists because the obvious way to save a settings file -- File.WriteAllText -- opens it
/// with FileMode.Create, which truncates first and writes second. Between those two the file on
/// disk is empty, and anything that stops the process in that window leaves it that way. On a
/// laptop that records unexpected shutdowns most days, and with a settings file written from
/// forty-odd call sites, that window gets landed in.
///
/// The failure is quiet, which is what makes it worth a helper rather than a comment. A
/// truncated JSON file does not announce itself: it throws on the next parse, the loader catches
/// it and returns defaults, and the user's fan curve, tuning profiles, game rules, overlay
/// layout and theme are simply gone with nothing on screen to say so.
///
/// Three parts, all load-bearing:
///   - write to a temp file beside the target, never over it;
///   - flush THROUGH the OS cache to the disk, because a rename can otherwise reach the platter
///     before the bytes do and a crash in between leaves a file that is intact, current and
///     empty -- the one outcome worse than a torn write, since it looks valid;
///   - File.Replace, which NTFS performs atomically and which hands the previous contents to a
///     backup path for free.
/// </summary>
public static class AtomicFile
{
    /// <summary>Suffix of the in-progress file. Beside the target, so Replace stays on one volume.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>Suffix of the copy holding whatever the file said before this write.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/>, replacing it atomically.
    ///
    /// Throws on failure rather than swallowing: callers differ on whether a failed write is
    /// worth reporting, and a helper that decides for them is how a disk-full goes unnoticed.
    /// </summary>
    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string temp = path + TempSuffix;

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // Nothing to replace on a first write, and Replace requires the target to exist.
            // A move within a directory is itself atomic, so this path is no weaker.
            if (File.Exists(path)) File.Replace(temp, path, path + BackupSuffix, ignoreMetadataErrors: true);
            else File.Move(temp, path);
        }
        catch
        {
            // A temp file left behind would be written over by the next attempt anyway, but
            // leaving one in a user-visible folder invites the question of what it is.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}
