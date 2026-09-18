// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Text.Json;

namespace OmniHub.Core.Diagnostics;

/// <summary>
/// Remembers machine state this application changed and still owes back, across a crash.
///
/// Several things OmniHub changes are meant to be put back: the display refresh rate, the timer
/// resolution, DWM's scheduling class, the fan mode, the GPU power ceiling. Every one of those
/// restores lives in a field and runs on a clean exit. On a laptop that records unexpected
/// shutdowns most days, "on a clean exit" is a condition that does not always arrive.
///
/// The refresh rate is the one with evidence behind it and the only key wired up so far.
/// AutoEco captures the current rate into a field, drops the panel to its eco rate, and puts the
/// captured value back on release. If the machine hangs while eco is engaged, that field dies
/// with the process: the panel stays at 60 Hz and NOTHING will ever restore it, because the next
/// launch has no idea a previous run lowered it. The user notices a laggy pointer and a 60 Hz
/// desktop and has no reason to connect either to a fan utility.
///
/// The others are deliberately not journalled yet, because they do not need it and a journal
/// entry nobody honours is worse than none. Timer resolution and MMCSS are process-scoped and
/// die correctly with the process; the fan mode reverts to the BIOS curve on reset; the GPU
/// ceiling is re-asserted at startup. This exists for state that OUTLIVES the process and has
/// no other owner.
///
/// Written through AtomicFile, because a journal that can be found half-written after a crash
/// would be solving the problem it is made of.
/// </summary>
public sealed class RestoreJournal
{
    /// <summary>Refresh rate in Hz the display should be returned to. Value is the integer as text.</summary>
    public const string DisplayRefreshHz = "display.refreshHz";

    /// <summary>
    /// Fans were taken off the firmware's own curve and have not been handed back. The value
    /// names the backend and board, so a reconciliation can be reported in words.
    ///
    /// Recorded only by a backend that does NOT revert on its own. The HP board this application
    /// was built on does revert -- an EC with nobody commanding it returns to its own curve --
    /// which is why fan state was deliberately left out of this journal for as long as HP was the
    /// only backend, and the note above still says so.
    ///
    /// That reasoning does not survive contact with other people's hardware. A controller that
    /// latches keeps whatever level a crashed process last sent it, with nothing running to
    /// change it and no record that anything was ever taken over. The fan sits there until
    /// somebody reboots, and on a machine that hangs as often as this one that is not a
    /// hypothetical.
    /// </summary>
    public const string FanManualControl = "fan.manualControl";

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _pending = new(StringComparer.Ordinal);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "restore.json");

    /// <summary>
    /// The process-wide journal, on the default path.
    ///
    /// A static because the thing it describes is genuinely process-wide: there is one display
    /// and one machine, and two journals disagreeing about what is owed would be worse than the
    /// problem. Tests construct their own instance with an explicit path instead, which is why
    /// the constructor takes one.
    /// </summary>
    public static RestoreJournal Shared { get; } = new();

    public RestoreJournal(string? path = null)
    {
        _path = path ?? DefaultPath;
        Reload();
    }

    /// <summary>What a previous run changed and did not put back. Empty after a clean exit.</summary>
    public IReadOnlyDictionary<string, string> Pending
    {
        get { lock (_lock) return new Dictionary<string, string>(_pending, StringComparer.Ordinal); }
    }

    /// <summary>
    /// Notes that <paramref name="key"/> should be restored to <paramref name="value"/>.
    ///
    /// Call BEFORE making the change, never after. The window this protects is exactly the one
    /// between deciding to change something and having changed it.
    /// </summary>
    public void Record(string key, string value)
    {
        lock (_lock)
        {
            _pending[key] = value;
            Write();
        }
    }

    /// <summary>Notes that the debt is paid. Call after the restore has actually been applied.</summary>
    public void Clear(string key)
    {
        lock (_lock)
        {
            if (_pending.Remove(key)) Write();
        }
    }

    /// <summary>Re-reads the file. A missing or unreadable journal means nothing is owed.</summary>
    public void Reload()
    {
        lock (_lock)
        {
            try
            {
                _pending = File.Exists(_path)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                      ?? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch
            {
                // A corrupt journal must not stop the application starting. Nothing owed is the
                // safe reading: the alternative is refusing to launch over a bookkeeping file.
                _pending = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }

    private void Write()
    {
        try
        {
            if (_pending.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_pending, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort. Failing to write the journal costs a restore after a crash that may
            // not happen; throwing here would cost the feature that was about to be applied.
        }
    }
}
