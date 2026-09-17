using System.Globalization;
using System.IO.Compression;
using System.Text;
using OmniHub.Core.Fan;

namespace OmniHub.Core.Diagnostics;

/// <summary>One file that went into the bundle.</summary>
public sealed record BundleEntry(string Name, long Bytes);

/// <summary>What was collected, so the manifest can be shown before anything is sent anywhere.</summary>
public sealed record BundleResult(string Path, IReadOnlyList<BundleEntry> Entries, long Bytes, string? Error);

/// <summary>
/// Everything needed to diagnose this machine, in one archive.
///
/// Assembling this by hand is a thing that has happened repeatedly: the thermal trace, the power
/// transitions, the poll timings, the crash log, the capability block, the settings, and a
/// handful of Windows event records, gathered one command at a time before anyone can even begin
/// looking at the problem. It is the same list every time, which is the definition of something
/// that should be a button.
///
/// Two rules it inherits from the log retention it sits beside. Deliberate artefacts -- load-test
/// runs and probe reports -- are somebody's saved measurement and go in whatever their age;
/// rolling logs are windowed, because the thermal trace alone is twelve megabytes and a support
/// bundle nobody can attach to anything is not useful.
///
/// And it tells the user what it collected. This archive contains their settings file, which
/// carries the paths of applications they have routed, and timestamps of when their machine was
/// running. That is entirely reasonable in a diagnostic bundle and entirely unreasonable to
/// include silently, so the manifest goes in the archive and comes back to the caller to show.
/// </summary>
public static class SupportBundle
{
    /// <summary>
    /// How much rolling history to include.
    ///
    /// Three days rather than the fourteen that are retained. The thermal trace runs to about a
    /// megabyte and a half a day, and the question a bundle is assembled to answer is nearly
    /// always about something recent.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(3);

    /// <summary>Kept whatever their age: these are measurements somebody chose to make.</summary>
    private static readonly string[] DeliberateArtefacts = { "loadtest-", "probe-" };

    /// <summary>
    /// Whether a log file belongs in a bundle covering this window.
    ///
    /// Separated out because it is the part that can be wrong quietly. A file whose name carries
    /// no date is kept -- crash.log is the one that matters, and it is the single most useful
    /// file in the archive on a machine that has been crashing.
    /// </summary>
    internal static bool Includes(string fileName, DateTime nowUtc, TimeSpan window)
    {
        foreach (string artefact in DeliberateArtefacts)
            if (fileName.StartsWith(artefact, StringComparison.OrdinalIgnoreCase))
                return true;

        return DateIn(fileName) is not { } date || nowUtc.Date - date <= window;
    }

    /// <summary>
    /// The first yyyy-MM-dd in a file name, or null when it carries none.
    ///
    /// Parsed rather than matched loosely, so a file named after something that merely looks like
    /// a date does not silently become a log from the year 20.
    /// </summary>
    internal static DateTime? DateIn(string fileName)
    {
        var parts = System.IO.Path.GetFileNameWithoutExtension(fileName).Split('-');

        for (int i = 0; i + 2 < parts.Length; i++)
            if (DateTime.TryParseExact(
                    $"{parts[i]}-{parts[i + 1]}-{parts[i + 2]}", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                return date;

        return null;
    }

    /// <summary>
    /// Collects everything into a zip at the given path.
    /// </summary>
    /// <param name="destinationZip">Where to write. Any existing file is replaced.</param>
    /// <param name="notes">
    /// Extra text files to include, by name -- the capability block, the machine summary, the
    /// readings the views already render. Supplied by the caller because that is where the
    /// hardware has already been asked, and asking it again to build an archive would make the
    /// archive a reason to talk to the firmware.
    /// </param>
    /// <param name="window">How much rolling history to include.</param>
    public static BundleResult Create(
        string destinationZip,
        IReadOnlyDictionary<string, string> notes,
        TimeSpan? window = null)
    {
        var entries = new List<BundleEntry>();
        TimeSpan span = window ?? DefaultWindow;
        DateTime now = DateTime.UtcNow;

        try
        {
            string? parent = System.IO.Path.GetDirectoryName(destinationZip);
            if (parent is { Length: > 0 }) Directory.CreateDirectory(parent);
            if (File.Exists(destinationZip)) File.Delete(destinationZip);

            using (var archive = ZipFile.Open(destinationZip, ZipArchiveMode.Create))
            {
                foreach (var (name, text) in notes)
                    entries.Add(WriteText(archive, name, text));

                string logs = ThermalLog.LogDirectory;
                if (Directory.Exists(logs))
                    foreach (string path in Directory.GetFiles(logs))
                    {
                        string name = System.IO.Path.GetFileName(path);
                        if (!Includes(name, now, span)) continue;

                        entries.Add(CopyIn(archive, path, $"logs/{name}"));
                    }

                // The settings file, and the backups beside it. Those backups are how a truncated
                // write was noticed in the first place, and their presence is itself a fact about
                // the machine worth carrying.
                string appData = System.IO.Path.GetDirectoryName(logs) ?? logs;
                if (Directory.Exists(appData))
                    foreach (string path in Directory.GetFiles(appData, "settings.json*"))
                        entries.Add(CopyIn(archive, path, $"settings/{System.IO.Path.GetFileName(path)}"));

                // Written last so it can count everything above it.
                entries.Add(WriteText(archive, "MANIFEST.txt", Manifest(entries, span)));
            }

            return new BundleResult(destinationZip, entries, entries.Sum(e => e.Bytes), null);
        }
        catch (Exception ex)
        {
            return new BundleResult(destinationZip, entries, entries.Sum(e => e.Bytes), ex.Message);
        }
    }

    /// <summary>
    /// What is in the archive, in the words somebody would want before sending it to a stranger.
    ///
    /// Leads with the parts that are about them rather than about the machine. A bundle that
    /// quietly carried an application list would be the sort of helpfulness nobody asked for.
    /// </summary>
    internal static string Manifest(IReadOnlyList<BundleEntry> entries, TimeSpan window)
    {
        var text = new StringBuilder();

        text.AppendLine("OmniHub support bundle");
        text.AppendLine($"Collected {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine($"Rolling logs covering the last {window.TotalDays:0.#} day(s).");
        text.AppendLine();
        text.AppendLine("WHAT THIS CONTAINS ABOUT YOU, NOT JUST ABOUT THE MACHINE");
        text.AppendLine("  settings/  your OmniHub settings, including the file paths of any");
        text.AppendLine("             applications you have set a graphics preference for.");
        text.AppendLine("  logs/      timestamps of when this machine was running and how hot it got.");
        text.AppendLine("  Anything supplied as a note may quote Windows event-log records.");
        text.AppendLine();
        text.AppendLine("Nothing here is sent anywhere by this application. The archive is written");
        text.AppendLine("to disk and what happens to it next is entirely your decision.");
        text.AppendLine();
        text.AppendLine("FILES");

        foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            text.AppendLine($"  {entry.Name,-48} {entry.Bytes,12:N0} bytes");

        return text.ToString();
    }

    private static BundleEntry WriteText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);

        return new BundleEntry(name, Encoding.UTF8.GetByteCount(text));
    }

    /// <summary>
    /// Copies a file in, tolerating one that a writer currently holds open.
    ///
    /// The thermal log is the single most useful file in the bundle and is being written to at
    /// the moment the button is pressed, so it is opened the way the history reader opens it --
    /// read access, sharing everything. A file that cannot be read is recorded as an entry of
    /// zero bytes rather than failing the whole bundle.
    /// </summary>
    private static BundleEntry CopyIn(ZipArchive archive, string path, string name)
    {
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var target = entry.Open();
            source.CopyTo(target);

            return new BundleEntry(name, source.Length);
        }
        catch
        {
            return new BundleEntry($"{name}  (could not be read)", 0);
        }
    }
}
