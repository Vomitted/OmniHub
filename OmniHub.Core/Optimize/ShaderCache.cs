// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Optimize;

public sealed record CacheLocation(string Label, string Path, long Bytes, int Files);

/// <summary>
/// Clears GPU shader caches.
///
/// Safe to delete: these are derived artefacts. Drivers and DirectX rebuild them on demand,
/// which costs a one-off compile pause the next time a title launches. Worth doing after a
/// driver update, where a stale cache is a genuine cause of stutter and crashes -- and not
/// otherwise, since deleting them routinely just means paying the recompile over and over.
///
/// The paths are enumerated explicitly rather than pattern-matched under the user profile.
/// A cleanup tool that goes hunting for things that "look like" caches is how people lose
/// data; nothing here deletes a directory it was not told about by name.
/// </summary>
public static class ShaderCache
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string LocalLow => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    /// <summary>
    /// The exact directories considered. Each is a documented vendor cache location whose
    /// contents regenerate automatically. Missing directories are skipped, so this is safe
    /// on a machine with no NVIDIA, AMD or Intel graphics.
    /// </summary>
    private static IEnumerable<(string Label, string Path)> Candidates()
    {
        yield return ("DirectX shader cache", System.IO.Path.Combine(Local, "D3DSCache"));
        yield return ("NVIDIA DXCache", System.IO.Path.Combine(Local, "NVIDIA", "DXCache"));
        yield return ("NVIDIA GLCache", System.IO.Path.Combine(Local, "NVIDIA", "GLCache"));
        yield return ("NVIDIA OptixCache", System.IO.Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "OptixCache"));
        yield return ("AMD DxCache", System.IO.Path.Combine(Local, "AMD", "DxCache"));
        yield return ("AMD DxcCache", System.IO.Path.Combine(Local, "AMD", "DxcCache"));
        yield return ("Intel ShaderCache", System.IO.Path.Combine(Local, "Intel", "ShaderCache"));
    }

    /// <summary>Measures what is there without deleting anything. Drives the UI preview.</summary>
    public static IReadOnlyList<CacheLocation> Scan()
    {
        var results = new List<CacheLocation>();
        foreach (var (label, path) in Candidates())
        {
            if (!Directory.Exists(path)) continue;

            long bytes = 0;
            int files = 0;
            foreach (var file in SafeWalk.Files(path))
            {
                try { bytes += new FileInfo(file).Length; files++; }
                catch { /* vanished or locked mid-enumeration; skip */ }
            }

            if (files > 0) results.Add(new CacheLocation(label, path, bytes, files));
        }
        return results;
    }

    /// <summary>
    /// Deletes cache contents, leaving the directories themselves in place because drivers
    /// expect them to exist. Locked files are skipped rather than treated as failures: a
    /// file held open by a running game is normal, not an error.
    /// </summary>
    public static TuningResult Clear()
    {
        long freed = 0;
        int deleted = 0, skipped = 0;

        foreach (var location in Scan())
        {
            foreach (var file in SafeWalk.Files(location.Path))
            {
                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += size;
                    deleted++;
                }
                catch { skipped++; }
            }
        }

        if (deleted == 0)
            return new TuningResult(true, skipped > 0
                ? $"Nothing removed; {skipped} file(s) were in use."
                : "Shader caches were already empty.");

        double freedMb = freed / 1024.0 / 1024.0;
        string detail = $"Cleared {freedMb:0.#} MB across {deleted} file(s).";
        if (skipped > 0) detail += $" {skipped} in use and skipped.";
        return new TuningResult(true, detail);
    }

    public static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
        : bytes >= 1024L * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB"
        : $"{bytes / 1024.0:0} KB";
}
