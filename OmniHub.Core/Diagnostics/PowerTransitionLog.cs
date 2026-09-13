using System.Globalization;

namespace OmniHub.Core.Diagnostics;

/// <summary>
/// Records every power-transition signal this machine delivers, and what the application did
/// about it.
///
/// This exists because a fix was shipped on an assumption that was never checked. Three of this
/// machine's hard hangs carry SleepInProgress in their Kernel-Power 41 event, so OmniHub was
/// taught to stand down on SystemEvents.PowerModeChanged. That wraps PBT_APMSUSPEND -- and this
/// laptop has no S3 at all, only Modern Standby, where entering S0ix does not necessarily
/// broadcast a suspend to desktop applications; they are throttled by the Desktop Activity
/// Moderator instead. If that is what happens here, the stand-down handler has never once run and
/// the fix is dead code that looks like a fix.
///
/// Guessing again would be the same mistake twice. This writes down which signals actually
/// arrive, in what order, and how long before the machine goes quiet, so the next change is made
/// against evidence. It is deliberately a separate file from the thermal log: the thermal log
/// stops during a transition, which is exactly when this needs to still be writing.
/// </summary>
public sealed class PowerTransitionLog : IDisposable
{
    // action records what OmniHub DID, not only what Windows said. A row showing a suspend
    // arriving with no stand-down beside it is the difference between "the signal never came"
    // and "the signal came and we ignored it", and those need completely different fixes.
    private const string Header = "timestamp,source,event,detail,action";

    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _openedForDate;
    private bool _failed;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "logs");

    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Appends one transition and flushes IMMEDIATELY.
    ///
    /// The other logs flush on an interval to avoid a syscall every couple of seconds. This one
    /// cannot: these rows are written at the exact moment the machine is about to stop executing,
    /// and a buffered row describing the transition that killed it is a row that never reaches the
    /// disk. Transitions are rare enough that the cost does not matter, and the last row before a
    /// freeze is the entire reason this file exists.
    /// </summary>
    public void Append(DateTime utcNow, string source, string evt, string detail, string action)
    {
        if (_failed) return;

        lock (_lock)
        {
            try
            {
                EnsureWriterFor(utcNow);
                if (_writer is null) return;

                _writer.WriteLine(string.Join(',',
                    utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    Clean(source), Clean(evt), Clean(detail), Clean(action)));
                _writer.Flush();
            }
            catch
            {
                _failed = true;
                CurrentPath = null;
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    /// <summary>Commas and newlines would break the column layout; these fields are short labels.</summary>
    private static string Clean(string s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');

    private void EnsureWriterFor(DateTime utcNow)
    {
        if (_writer is not null && _openedForDate == utcNow.Date) return;

        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;

        Directory.CreateDirectory(LogDirectory);
        string path = Path.Combine(LogDirectory, $"power-{utcNow:yyyy-MM-dd}.csv");
        bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        if (fresh) _writer.WriteLine(Header);

        CurrentPath = path;
        _openedForDate = utcNow.Date;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
            CurrentPath = null;
        }
    }
}
