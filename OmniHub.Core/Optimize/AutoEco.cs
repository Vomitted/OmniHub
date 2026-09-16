using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

/// <summary>When eco should engage itself.</summary>
public sealed record AutoEcoSettings(
    bool OnBattery,
    bool OnIdle,
    int IdleMinutes,
    int EcoRefreshHz,
    string? ProfileName);

/// <summary>
/// OMEN Gaming Hub's Eco mode, with the "auto" part it never had.
///
/// Eco there is a manual switch that caps the panel to 60 Hz and biases the machine towards
/// battery life. This does the same work and adds triggers: on battery, or after a stretch of
/// no input. Both can be on; either one alone is enough to engage.
///
/// The refresh-rate drop is the piece that matters most on this laptop. Power-limit commands
/// are accepted and then ignored by HP's firmware, but a 144 Hz panel dropped to 60 Hz is a
/// real reduction in both panel and GPU work that nothing can quietly undo.
///
/// Everything it changes is captured before it changes it, and put back on exit. An eco mode
/// that cannot restore what it found is just a settings change with extra steps.
/// </summary>
public sealed class AutoEco : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private readonly Func<AutoEcoSettings> _settings;
    private readonly Func<string, TuningResult> _applyProfile;
    private readonly Action _restoreProfile;
    private readonly TimeSpan _interval;

    private CancellationTokenSource? _cts;
    private int? _refreshBeforeEco;

    /// <summary>True while eco is engaged.</summary>
    public bool IsEcoActive { get; private set; }

    /// <summary>Why eco last engaged or disengaged, for display.</summary>
    public string? Status { get; private set; }

    /// <summary>Raised when eco engages (true) or releases (false).</summary>
    public event Action<bool, string>? OnEcoChanged;

    /// <summary>
    /// <paramref name="journal"/> is what makes the refresh-rate restore survive a crash. The
    /// captured rate otherwise lives only in a field, so a hang while eco is engaged strands the
    /// panel at its eco rate permanently -- the next launch has no idea a previous run lowered
    /// it. Optional, because the controller is still correct without one; it just cannot clean
    /// up after a death.
    /// </summary>
    public AutoEco(
        Func<AutoEcoSettings> settings,
        Func<string, TuningResult> applyProfile,
        Action restoreProfile,
        TimeSpan? interval = null,
        Diagnostics.RestoreJournal? journal = null)
    {
        _settings = settings;
        _applyProfile = applyProfile;
        _restoreProfile = restoreProfile;
        _interval = interval ?? TimeSpan.FromSeconds(10);
        _journal = journal;
    }

    private readonly Diagnostics.RestoreJournal? _journal;

    private Task? _loop;

    /// <summary>True only while the loop is genuinely alive -- see FanService.IsRunning for
    /// why testing the cancellation token alone was not enough.</summary>
    public bool IsRunning => _cts is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    /// <summary>How long since the last keyboard or mouse input.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;

        // dwTime is a 32-bit tick count and wraps roughly every 49 days. Comparing it against
        // the low 32 bits of TickCount64 keeps the subtraction correct across that wrap
        // instead of reporting an idle time of several weeks once every couple of months.
        uint now = unchecked((uint)Environment.TickCount64);
        return TimeSpan.FromMilliseconds(unchecked(now - info.dwTime));
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Stops watching, and releases eco if it is currently engaged.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        if (IsEcoActive) Release("Auto eco switched off");
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var s = _settings();
                string? reason = ShouldEngage(s);

                if (reason is not null && !IsEcoActive) Engage(s, reason);
                else if (reason is null && IsEcoActive) Release("back on mains power and active");
            }
            catch
            {
                // A failed tick must not end the loop; the next one re-evaluates.
            }

            try { await Task.Delay(_interval, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>The reason eco should be on right now, or null if it should not.</summary>
    private static string? ShouldEngage(AutoEcoSettings s)
    {
        if (s.OnBattery && PowerSourceWatcher.Read() == PowerSource.Battery)
            return "on battery";

        if (s.OnIdle && IdleTime() >= TimeSpan.FromMinutes(Math.Max(1, s.IdleMinutes)))
            return $"idle for {s.IdleMinutes} min";

        return null;
    }

    private void Engage(AutoEcoSettings s, string reason)
    {
        // Captured before anything changes, so Release has something true to put back rather
        // than a hardcoded guess at what the panel was running at.
        _refreshBeforeEco = DisplayControl.CurrentRefreshHz();

        // Written down BEFORE the display is touched, not after. The window worth protecting is
        // the one between deciding to lower the rate and having lowered it, and a note written
        // afterwards does not cover it.
        if (_refreshBeforeEco is int before) _journal?.Record(Diagnostics.RestoreJournal.DisplayRefreshHz, before.ToString());

        var parts = new List<string>();

        if (s.EcoRefreshHz > 0 && _refreshBeforeEco != s.EcoRefreshHz)
        {
            var display = DisplayControl.SetRefreshHz(s.EcoRefreshHz);
            parts.Add(display.Applied ? $"{s.EcoRefreshHz} Hz" : $"display refused ({display.Detail})");
        }

        if (s.ProfileName is { } profile)
        {
            var applied = _applyProfile(profile);
            parts.Add(applied.Applied ? profile : $"{profile} not applied");
        }

        IsEcoActive = true;
        Status = $"Eco on — {reason}" + (parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "");
        OnEcoChanged?.Invoke(true, Status);
    }

    private void Release(string reason)
    {
        // Restore the refresh rate first. It is the change a person actually sees, and leaving
        // a 60 Hz panel behind because a later step threw would be the most visible failure.
        if (_refreshBeforeEco is int hz) DisplayControl.SetRefreshHz(hz);
        _refreshBeforeEco = null;

        // Cleared only after the restore has actually been issued, so a crash between the two
        // still leaves the debt recorded.
        _journal?.Clear(Diagnostics.RestoreJournal.DisplayRefreshHz);

        try { _restoreProfile(); } catch { /* the caller decides what "normal" means */ }

        IsEcoActive = false;
        Status = $"Eco off — {reason}";
        OnEcoChanged?.Invoke(false, Status);
    }

    public void Dispose() => Stop();
}
