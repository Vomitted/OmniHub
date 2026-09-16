using System.IO;
using System.Text;
using OmniHub.Core;

namespace OmniHub.Tests;

/// <summary>
/// The guarantee is that a reader never sees a half-written file.
///
/// Settings are written from more than forty call sites and this machine records unexpected
/// shutdowns most days, so the window between "truncate" and "write" in a plain
/// File.WriteAllText is one that gets landed in. What comes back afterwards is not an error but
/// an empty file, a parse failure the loader catches, and a silent reset to defaults.
/// </summary>
public class AtomicFileTests
{
    private static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-atomic-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void WritesThenReadsBackExactly()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            AtomicFile.WriteAllText(path, "{\"a\":1}");
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// The previous contents survive as a backup. This is the observable evidence that the file
    /// was REPLACED rather than truncated and rewritten -- a truncating write has nothing to
    /// hand to a backup, so this assertion fails the moment someone puts File.WriteAllText back.
    /// </summary>
    [Fact]
    public void ThePreviousContentsAreKeptAsABackup()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            AtomicFile.WriteAllText(path, "first");
            AtomicFile.WriteAllText(path, "second");

            Assert.Equal("second", File.ReadAllText(path));
            Assert.Equal("first", File.ReadAllText(path + AtomicFile.BackupSuffix));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A first write has nothing to back up and must still succeed.</summary>
    [Fact]
    public void AFirstWriteNeedsNoExistingFile()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "nested", "settings.json");
            AtomicFile.WriteAllText(path, "hello");

            Assert.Equal("hello", File.ReadAllText(path));
            Assert.False(File.Exists(path + AtomicFile.BackupSuffix));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// No .tmp is left in a folder the user opens. The logs directory is one click from the
    /// Settings tab, and a stray half-file there is a question nobody should have to answer.
    /// </summary>
    [Fact]
    public void NoTemporaryFileIsLeftBehind()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            AtomicFile.WriteAllText(path, "one");
            AtomicFile.WriteAllText(path, "two");

            Assert.False(File.Exists(path + AtomicFile.TempSuffix));
            Assert.Equal(new[] { "settings.json", "settings.json.bak" },
                         Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>
    /// A reader holding the file open across the write still sees a whole document.
    ///
    /// This is the actual promise, stated the way it is consumed: open the old file, write a
    /// new one underneath, and the open handle keeps reading complete content rather than
    /// finding itself pointed at a truncated file mid-read.
    /// </summary>
    [Fact]
    public void AReaderOpenAcrossTheWriteStillSeesAWholeFile()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            string first = new string('a', 4096);
            AtomicFile.WriteAllText(path, first);

            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            AtomicFile.WriteAllText(path, new string('b', 4096));

            using var reader = new StreamReader(held);
            Assert.Equal(first, reader.ReadToEnd());
            Assert.Equal(new string('b', 4096), File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>No BOM. The logs and settings are read by tooling that does not expect one.</summary>
    [Fact]
    public void WritesUtf8WithoutAByteOrderMark()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            AtomicFile.WriteAllText(path, "t");

            Assert.Equal(new byte[] { (byte)'t' }, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
