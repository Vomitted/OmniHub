using System.IO;
using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// This log is only worth having if its last row survives the event that wrote it.
///
/// Every row here is written at the moment the machine is about to stop executing, and the row
/// that matters most is the final one before a hard hang -- the transition nobody saw. A buffered
/// row describing that transition is a row that never reaches the disk, so the flush-per-row
/// behaviour is not an implementation detail, it is the entire premise of the file. Switching it
/// to the interval flush the thermal log uses would look like a harmless consistency tidy-up in a
/// diff, and would silently destroy exactly the evidence this was built to capture.
/// </summary>
public class PowerTransitionLogTests
{
    /// <summary>
    /// Reads the file back through a second handle WHILE the writer is still open, which is the
    /// only way to distinguish "flushed" from "will be flushed when Dispose runs". A hang never
    /// runs Dispose.
    /// </summary>
    [Fact]
    public void EveryRowReachesDiskBeforeTheNextCall()
    {
        string dir = Path.Combine(Path.GetTempPath(), "omnihub-powerlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new PowerTransitionLog(dir);
            var when = new DateTime(2026, 9, 16, 3, 0, 0, DateTimeKind.Utc);

            log.Append(when, "WM_POWERBROADCAST", "PBT_POWERSETTINGCHANGE", "display off", "observed only");

            Assert.NotNull(log.CurrentPath);
            var lines = ReadWhileOpen(log.CurrentPath!);

            Assert.Equal("timestamp,source,event,detail,action", lines[0]);
            Assert.Equal("2026-09-16T03:00:00Z,WM_POWERBROADCAST,PBT_POWERSETTINGCHANGE,display off,observed only", lines[1]);

            // A second row must also be readable without the first being rewritten, which is what
            // catches an append that reopens the file in Create mode.
            log.Append(when.AddSeconds(4), "SystemEvents", "Suspend", "", "stood down");
            Assert.Equal(3, ReadWhileOpen(log.CurrentPath!).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// A comma in any field would shift every column to its right, and these rows get read back
    /// as CSV by whoever is reconstructing a freeze. The payload detail is the field most likely
    /// to carry one, since it can be a raw GUID or an unrecognised state.
    /// </summary>
    [Fact]
    public void AFieldContainingACommaCannotShiftTheColumns()
    {
        string dir = Path.Combine(Path.GetTempPath(), "omnihub-powerlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new PowerTransitionLog(dir);
            log.Append(DateTime.UtcNow, "WM_POWERBROADCAST", "0x9", "unknown, maybe DRIPS", "observed only");

            var row = ReadWhileOpen(log.CurrentPath!)[1];
            Assert.Equal(4, row.Count(c => c == ','));
            Assert.Contains("unknown; maybe DRIPS", row);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>FileShare.ReadWrite, so this mirrors how the file is actually read after a hang.</summary>
    private static string[] ReadWhileOpen(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.TrimEnd('\r')).ToArray();
    }
}
