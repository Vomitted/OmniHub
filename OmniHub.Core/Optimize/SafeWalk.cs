// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Optimize;

/// <summary>
/// File enumeration for the deletion services, with two guarantees that plain
/// Directory.EnumerateFiles(..., SearchOption.AllDirectories) does not give you.
///
/// 1. REPARSE POINTS ARE NOT FOLLOWED. .NET's recursive enumeration walks straight through
///    directory junctions and symlinks. Under a cleanup root that is a real escape hatch: a
///    junction inside a temp directory takes the walk somewhere else entirely, and a tool
///    that deletes what it found would then delete files outside the tree it was told about.
///    Windows genuinely ships junctions inside user profiles -- the legacy "Application Data"
///    compatibility links are the classic example -- so this is not hypothetical. Junctions
///    are skipped rather than traversed.
///
/// 2. EVERY RETURNED PATH IS VERIFIED TO BE INSIDE THE ROOT after resolution. That is the
///    backstop for anything the first rule misses: a path that does not resolve to somewhere
///    under the requested root is dropped, so the blast radius can never exceed the allowlist
///    entry that produced it.
///
/// Both rules cost some speed on large trees. For code whose failure mode is deleting the
/// wrong file, that is not a close call.
/// </summary>
internal static class SafeWalk
{
    /// <summary>
    /// Files beneath <paramref name="root"/>: junctions skipped, each verified contained by
    /// the root. Materialised into a list because callers delete while iterating, and
    /// enumerating lazily over a tree being mutated is undefined behaviour.
    /// </summary>
    public static List<string> Files(string root)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return results;

        string canonicalRoot;
        try { canonicalRoot = NormaliseDirectory(root); }
        catch { return results; }

        // Explicit stack rather than recursion: these trees get deep, and an access-denied
        // subtree must skip only that branch, not abandon the whole walk the way a single
        // try/catch around a recursive enumeration does.
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (IsContained(canonicalRoot, file)) results.Add(file);
                }
            }
            catch { /* unreadable directory; skip just this level */ }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (IsReparsePoint(sub)) continue;              // rule 1
                    if (!IsContained(canonicalRoot, sub)) continue;  // rule 2
                    pending.Push(sub);
                }
            }
            catch { /* unreadable directory; skip just this level */ }
        }

        return results;
    }

    /// <summary>
    /// Directories beneath <paramref name="root"/>, deepest first, junctions skipped.
    /// Deepest-first is what lets a caller delete a directory emptied by removing its
    /// children in the same pass.
    /// </summary>
    public static List<string> DirectoriesDeepestFirst(string root)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return results;

        string canonicalRoot;
        try { canonicalRoot = NormaliseDirectory(root); }
        catch { return results; }

        var pending = new Stack<string>();
        pending.Push(canonicalRoot);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (IsReparsePoint(sub)) continue;
                    if (!IsContained(canonicalRoot, sub)) continue;
                    results.Add(sub);
                    pending.Push(sub);
                }
            }
            catch { }
        }

        // Sorted by actual separator count, not string length. Length is only a proxy for
        // depth and gets it wrong whenever a shallow directory has a long name.
        results.Sort((a, b) => Depth(b).CompareTo(Depth(a)));
        return results;
    }

    private static int Depth(string path) =>
        path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; } // unreadable attributes: treat as unsafe and skip
    }

    private static string NormaliseDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool IsContained(string canonicalRootWithSlash, string candidate)
    {
        try
        {
            var full = Path.GetFullPath(candidate);
            return full.StartsWith(canonicalRootWithSlash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
