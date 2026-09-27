// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Theming;

/// <summary>
/// A 5 × 7 dot font for the Dot matrix look's figures: digits and the few marks a reading needs.
///
/// Drawn from a table rather than shipped as a typeface: the look's figures are the hardware's numbers
/// and nothing else, so a dozen glyphs cover them, and a table is a thing a test can read. Units stay
/// in the interface's ordinary face beside the dots.
/// </summary>
public static class DotFont
{
    public const int Rows = 7;

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
        ['.'] = new[] { "..", "..", "..", "..", "..", "##", "##" },
        [','] = new[] { "..", "..", "..", "..", "##", ".#", "#." },
        ['°'] = new[] { ".#.", "#.#", ".#.", "...", "...", "...", "..." },
        ['%'] = new[] { "##..#", "##..#", "...#.", "..#..", ".#...", "#..##", "#..##" },
        ['-'] = new[] { "...", "...", "...", "###", "...", "...", "..." },
        ['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" },
        [' '] = new[] { "..", "..", "..", "..", "..", "..", ".." },
    };

    /// <summary>Whether <paramref name="c"/> has a glyph.</summary>
    public static bool Has(char c) => Glyphs.ContainsKey(c);

    /// <summary>The glyph for <paramref name="c"/>, seven rows of '#' and '.', or null.</summary>
    public static IReadOnlyList<string>? Glyph(char c) => Glyphs.TryGetValue(c, out var g) ? g : null;

    /// <summary>
    /// The lit cells of <paramref name="text"/>, as column and row, with one dark column between
    /// glyphs. Characters without a glyph are skipped rather than drawn as something else.
    /// </summary>
    public static IEnumerable<(int Column, int Row)> Cells(string text)
    {
        int x = 0;
        foreach (char c in text)
        {
            if (Glyph(c) is not { } g) continue;
            for (int row = 0; row < Rows; row++)
                for (int col = 0; col < g[row].Length; col++)
                    if (g[row][col] == '#') yield return (x + col, row);
            x += g[0].Length + 1;
        }
    }

    /// <summary>How many columns <paramref name="text"/> takes, the gaps between glyphs included.</summary>
    public static int Columns(string text)
    {
        int width = 0, count = 0;
        foreach (char c in text)
            if (Glyph(c) is { } g) { width += g[0].Length; count++; }
        return width + Math.Max(0, count - 1);
    }
}
