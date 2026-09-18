// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Diagnostics;

namespace OmniHub.Core.Apps;

public sealed record DetectedApp(string ExecutablePath, string ProcessName, string WindowTitle);

/// <summary>Lists currently running processes that look like real user-facing apps
/// (have a visible top-level window), so the user can pick one to route instead of
/// browsing the filesystem for its .exe. Deliberately does not guess which apps are
/// "games" or GPU-heavy -- the user still picks the GPU preference explicitly.</summary>
public static class RunningAppDetector
{
    public static List<DetectedApp> GetVisibleApps()
    {
        var results = new List<DetectedApp>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(proc.MainWindowTitle)) continue;

                string? path = proc.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                if (!seenPaths.Add(path)) continue;

                results.Add(new DetectedApp(path, proc.ProcessName, proc.MainWindowTitle));
            }
            catch
            {
                // Protected/system processes deny module access even to an elevated
                // caller -- skip rather than fail the whole scan.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return results.OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
