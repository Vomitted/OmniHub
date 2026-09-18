// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Diagnostics;

namespace OmniHub.Core.Optimize;

/// <summary>
/// Watches for named processes appearing and disappearing, so a profile can follow whatever
/// is actually running. UXTU calls this its Game Library; the mechanism is the same.
///
/// Polled at a few seconds rather than event-driven. The event route is a WMI
/// __InstanceCreationEvent query against Win32_Process, which keeps a permanent WMI consumer
/// alive and wakes on every process start on the machine -- a heavy way to notice that a game
/// launched, on an app that has already been through one round of WMI cost problems. Reading
/// process names is a cheap enumeration that opens no handles of its own.
///
/// ponytail: O(processes x rules) scan every few seconds. Fine for a handful of rules; hash
/// the rule names first if anyone ever has hundreds.
/// </summary>
public sealed class ProcessWatcher : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<IReadOnlyCollection<string>> _watchedNames;
    private CancellationTokenSource? _cts;

    /// <summary>The process currently matched, or null when none of the watched names is running.</summary>
    public string? Active { get; private set; }

    /// <summary>
    /// Raised when the matched process changes: the name when one starts, null when the last
    /// one exits. Only fires on a real transition.
    /// </summary>
    public event Action<string?>? OnChanged;

    /// <param name="watchedNames">
    /// Read fresh each tick rather than captured, so edits to the rule list take effect without
    /// restarting the watcher.
    /// </param>
    public ProcessWatcher(Func<IReadOnlyCollection<string>> watchedNames, TimeSpan? interval = null)
    {
        _watchedNames = watchedNames;
        _interval = interval ?? TimeSpan.FromSeconds(4);
    }

    private Task? _loop;

    /// <summary>True only while the loop is genuinely alive -- see FanService.IsRunning for
    /// why testing the cancellation token alone was not enough.</summary>
    public bool IsRunning => _cts is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        Active = null;
    }

    /// <summary>The first watched name that is currently running, or null.</summary>
    public static string? FindRunning(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return null;

        // Process objects hold OS handles; enumerate, read the name, dispose. Leaking these
        // from a loop that runs all day is how a monitoring app exhausts handles.
        foreach (var process in Process.GetProcesses())
        {
            string name;
            try { name = process.ProcessName; }
            catch { continue; }
            finally { process.Dispose(); }

            foreach (var watched in names)
                if (string.Equals(name, watched, StringComparison.OrdinalIgnoreCase))
                    return watched;
        }
        return null;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                string? seen = FindRunning(_watchedNames());
                if (!string.Equals(seen, Active, StringComparison.OrdinalIgnoreCase))
                {
                    Active = seen;
                    try { OnChanged?.Invoke(seen); }
                    catch { /* a handler that throws must not kill the watcher */ }
                }
            }
            catch
            {
                // Enumerating processes can transiently fail; the next tick will do.
            }

            try { await Task.Delay(_interval, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    public void Dispose() => Stop();
}
