// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

/// <summary>Where the machine is currently getting its power.</summary>
public enum PowerSource
{
    /// <summary>Windows could not say. Treated as neither, so nothing is applied on a guess.</summary>
    Unknown,
    Battery,
    Mains,
}

/// <summary>
/// Watches for the plug going in or out and applies the matching tuning profile.
///
/// Polled rather than event-driven. The event route (SystemEvents.PowerModeChanged) lives in
/// WinForms, which this assembly deliberately does not reference, and a five-second poll of a
/// single kernel32 call costs nothing measurable against a transition a person makes by hand.
///
/// Unknown is a real third state, not a synonym for battery. Windows reports 255 when it
/// cannot tell, and applying the battery profile on that basis would quietly detune a machine
/// that is plugged in.
/// </summary>
public sealed class PowerSourceWatcher : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    /// <summary>The source seen at the last poll.</summary>
    public PowerSource Current { get; private set; } = PowerSource.Unknown;

    /// <summary>Raised when the source changes, including the first observation.</summary>
    public event Action<PowerSource>? OnChanged;

    public PowerSourceWatcher(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(5);

    private Task? _loop;

    /// <summary>True only while the loop is genuinely alive -- see FanService.IsRunning for
    /// why testing the cancellation token alone was not enough.</summary>
    public bool IsRunning => _cts is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    /// <summary>Reads the current power source without starting the watcher.</summary>
    public static PowerSource Read() =>
        GetSystemPowerStatus(out var status)
            ? status.ACLineStatus switch { 0 => PowerSource.Battery, 1 => PowerSource.Mains, _ => PowerSource.Unknown }
            : PowerSource.Unknown;

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // The whole body is guarded. Read() is a P/Invoke into GetSystemPowerStatus and it
            // sat outside every handler, so one failed call ended this loop for good --
            // silently, because Task.Run's result is discarded. Automatic profile switching
            // then stopped for the rest of the session while IsRunning still reported true.
            try
            {
                var seen = Read();

                // Only a real transition fires. Re-applying the same profile every five
                // seconds would fight anything the user changed by hand in between.
                if (seen != PowerSource.Unknown && seen != Current)
                {
                    bool firstObservation = Current == PowerSource.Unknown;
                    Current = seen;
                    if (!firstObservation)
                    {
                        try { OnChanged?.Invoke(seen); }
                        catch { /* a handler that throws must not kill the watcher */ }
                    }
                }
            }
            catch
            {
                // A failed read must not end the loop; the next tick re-reads.
            }

            try { await Task.Delay(_interval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => Stop();
}
