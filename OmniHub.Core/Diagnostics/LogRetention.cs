namespace OmniHub.Core.Diagnostics;

/// <summary>
/// Deletes log files older than a retention window.
///
/// This was ThermalLog's private business, and it was the only writer of the six that had it.
/// The network log, the power-transition log, the poll-timing trace and the crash log all grew
/// without limit -- the thermal trace alone runs to about a megabyte and a half on a busy day,
/// so "unbounded" is not a theoretical tidiness point.
///
/// Two rules are deliberate and worth keeping when this is called from somewhere new.
///
/// The file currently being written is never a candidate. Deleting the open file would leave a
/// writer appending to a handle nothing can find.
///
/// Deliberate artefacts are never swept. A load-test run and a probe report are things somebody
/// created in order to compare against later; a retention policy that quietly eats the baseline
/// half of a measurement is worse than no retention policy. That is why this takes an explicit
/// pattern rather than cleaning a directory.
/// </summary>
public static class LogRetention
{
    /// <summary>
    /// How long a trace is kept. Long enough to compare a machine against itself a fortnight
    /// ago, which is the span these files actually get used over.
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromDays(14);

    /// <summary>
    /// Deletes files in <paramref name="directory"/> matching <paramref name="pattern"/> whose
    /// last write is older than <paramref name="retention"/>. Returns how many went.
    ///
    /// Never throws. Housekeeping that can stop logging is worse than no housekeeping, and
    /// every caller is on a path that matters more than this does.
    /// </summary>
    /// <param name="keepSince">
    /// A moment nothing older than which may be deleted, whatever the retention window says.
    ///
    /// This is how a saved session protects the trace behind it. A session is a name and a range
    /// over logs that already exist, so a fortnight-old one would silently resolve to nothing once
    /// the retention swept the files -- an A/B comparison that has forgotten A, which is worse than
    /// one that refuses to run, because it still produces a verdict.
    /// </param>
    public static int Prune(string directory, string pattern, TimeSpan? retention = null,
                            string? keepPath = null, DateTime? keepSince = null)
    {
        int removed = 0;
        try
        {
            var cutoff = DateTime.UtcNow - (retention ?? Default);

            // Whichever is earlier wins. A session reaching further back than the retention window
            // extends it; one inside the window changes nothing.
            if (keepSince is { } since && since < cutoff) cutoff = since;

            foreach (string file in Directory.EnumerateFiles(directory, pattern))
            {
                if (keepPath is not null && string.Equals(file, keepPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) { File.Delete(file); removed++; }
                }
                catch { /* locked, or vanished under us: leave it and try again another day */ }
            }
        }
        catch { /* missing directory, permissions: logging matters more than pruning */ }

        return removed;
    }

    /// <summary>
    /// Keeps a single appended file under a byte ceiling by starting it again, preserving the
    /// tail rather than the head.
    ///
    /// For crash.log, which is one growing file rather than one file a day. Three handlers
    /// append to it -- dispatcher, AppDomain and unobserved-task -- so a background failure
    /// that repeats writes without limit. The tail is the part worth having: it is the most
    /// recent failure, and the first entries of an endlessly repeating one say nothing the
    /// last entries do not.
    /// </summary>
    public static bool Trim(string path, long maxBytes, long keepBytes)
    {
        try
        {
            if (!File.Exists(path)) return false;

            var info = new FileInfo(path);
            if (info.Length <= maxBytes) return false;

            string tail;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(Math.Max(0, stream.Length - keepBytes), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);

                // The seek lands mid-line; drop the partial one rather than leave a fragment
                // that reads as a real entry.
                reader.ReadLine();
                tail = reader.ReadToEnd();
            }

            AtomicFile.WriteAllText(path,
                $"[trimmed {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss'Z'}: earlier entries removed, file exceeded {maxBytes:N0} bytes]"
                + Environment.NewLine + tail);
            return true;
        }
        catch { return false; }
    }
}
