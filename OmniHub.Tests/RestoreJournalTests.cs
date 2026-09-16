using System.IO;
using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// The debt this application owes the machine, written down so a crash cannot cancel it.
///
/// Auto Eco lowers the display refresh rate on battery and raises it again on release, and the
/// value it captured to raise it back to lived in a field. On a laptop that records unexpected
/// shutdowns most days, that field is one hang away from being lost -- and then the panel stays
/// at 60 Hz permanently, because the next launch has no idea a previous run lowered it. The
/// user sees a laggy pointer and a 60 Hz desktop, and nothing about either points at a fan
/// utility.
/// </summary>
public class RestoreJournalTests
{
    private static string TempFile() =>
        Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-journal-" + Guid.NewGuid().ToString("N"))).FullName, "restore.json");

    private static void Cleanup(string path) { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }

    /// <summary>
    /// The whole point, stated as a test: what one process recorded, the next process reads.
    /// A journal that only worked within a single run would be the field it replaces.
    /// </summary>
    [Fact]
    public void ADebtRecordedByOneInstanceIsVisibleToTheNext()
    {
        string path = TempFile();
        try
        {
            new RestoreJournal(path).Record(RestoreJournal.DisplayRefreshHz, "144");

            var afterCrash = new RestoreJournal(path);
            Assert.Equal("144", afterCrash.Pending[RestoreJournal.DisplayRefreshHz]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ClearingRemovesTheDebtForTheNextInstanceToo()
    {
        string path = TempFile();
        try
        {
            var journal = new RestoreJournal(path);
            journal.Record(RestoreJournal.DisplayRefreshHz, "144");
            journal.Clear(RestoreJournal.DisplayRefreshHz);

            Assert.Empty(new RestoreJournal(path).Pending);
        }
        finally { Cleanup(path); }
    }

    /// <summary>An empty journal leaves no file, so a clean run leaves nothing behind.</summary>
    [Fact]
    public void ClearingTheLastEntryRemovesTheFile()
    {
        string path = TempFile();
        try
        {
            var journal = new RestoreJournal(path);
            journal.Record(RestoreJournal.DisplayRefreshHz, "144");
            Assert.True(File.Exists(path));

            journal.Clear(RestoreJournal.DisplayRefreshHz);
            Assert.False(File.Exists(path));
        }
        finally { Cleanup(path); }
    }

    /// <summary>
    /// A corrupt journal reads as "nothing owed" rather than throwing.
    ///
    /// Refusing to launch over a bookkeeping file would be a worse failure than the one this
    /// guards against, and this application holds fan control while it runs.
    /// </summary>
    [Fact]
    public void ACorruptJournalIsTreatedAsNothingOwed()
    {
        string path = TempFile();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Empty(new RestoreJournal(path).Pending);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void AMissingJournalIsNothingOwed()
    {
        string path = TempFile();
        try { Assert.Empty(new RestoreJournal(path).Pending); }
        finally { Cleanup(path); }
    }

    /// <summary>Recording the same key twice is an update, not a second debt.</summary>
    [Fact]
    public void RecordingTheSameKeyAgainReplacesIt()
    {
        string path = TempFile();
        try
        {
            var journal = new RestoreJournal(path);
            journal.Record(RestoreJournal.DisplayRefreshHz, "60");
            journal.Record(RestoreJournal.DisplayRefreshHz, "144");

            Assert.Equal("144", new RestoreJournal(path).Pending[RestoreJournal.DisplayRefreshHz]);
            Assert.Single(new RestoreJournal(path).Pending);
        }
        finally { Cleanup(path); }
    }

    /// <summary>
    /// The journal is written through AtomicFile, so it cannot itself be found half-written
    /// after the crash it exists to survive. Evidenced by the backup Replace leaves behind.
    /// </summary>
    [Fact]
    public void TheJournalIsWrittenAtomically()
    {
        string path = TempFile();
        try
        {
            var journal = new RestoreJournal(path);
            journal.Record(RestoreJournal.DisplayRefreshHz, "60");
            journal.Record(RestoreJournal.DisplayRefreshHz, "144");

            Assert.True(File.Exists(path + OmniHub.Core.AtomicFile.BackupSuffix));
            Assert.False(File.Exists(path + OmniHub.Core.AtomicFile.TempSuffix));
        }
        finally { Cleanup(path); }
    }

    /// <summary>
    /// An entry no build understands is dropped rather than carried forever. It can only come
    /// from a version that knew how to honour it, and this one has no way to guess.
    /// </summary>
    [Fact]
    public void TheReconcilerDropsAnUnrecognisedEntry()
    {
        string path = TempFile();
        try
        {
            var journal = new RestoreJournal(path);
            journal.Record("something.fromTheFuture", "1");

            var done = RestoreReconciler.Run(journal);

            Assert.Single(done);
            Assert.False(done[0].Applied);
            Assert.Empty(journal.Pending);
        }
        finally { Cleanup(path); }
    }

    /// <summary>An unreadable refresh rate is dropped, not retried forever against the display.</summary>
    [Fact]
    public void TheReconcilerDropsAnUnreadableRefreshRate()
    {
        string path = TempFile();
        try
        {
            var journal = new RestoreJournal(path);
            journal.Record(RestoreJournal.DisplayRefreshHz, "not a number");

            var done = RestoreReconciler.Run(journal);

            Assert.False(done[0].Applied);
            Assert.Empty(journal.Pending);
        }
        finally { Cleanup(path); }
    }
}
