// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

/// <summary>One capture of the PM table, decoded as floats and labelled by nothing else.</summary>
/// <param name="AtUtc">When it was taken.</param>
/// <param name="Label">What the machine was doing, in the user's own words.</param>
/// <param name="Version">The table layout version the SMU reported.</param>
/// <param name="Values">Every 32-bit word, in order, decoded as a float.</param>
public sealed record PmTableDump(DateTime AtUtc, string Label, uint Version, IReadOnlyList<float> Values);

/// <summary>One word that moved between two captures.</summary>
public sealed record PmTableChange(int Index, float From, float To)
{
    public float Delta => To - From;

    /// <summary>The confirmed name for this index, or null where nothing has been established.</summary>
    public string? Known => PmTableExplorer.KnownName(Index);
}

/// <summary>
/// The unmapped part of the PM table, made discoverable instead of guessed at.
///
/// This application reads 22 fields out of a table with hundreds of words in it, and the source
/// says plainly that the rest "stay unmapped rather than guessed". That is the right call and it
/// leaves the obvious question unanswered: what are the others?
///
/// Guessing is not the way to find out, and neither is copying somebody else's table for a
/// different silicon revision. The way to find out is how the confirmed 22 were found -- change
/// one thing about the machine, look at what moved, repeat. This makes that a two-click operation
/// rather than an afternoon.
///
/// Everything here is labelled as raw. An index is an index, a float is a float, and no word gets
/// a name it has not earned: the handful that are named were confirmed against readings the
/// hardware reports elsewhere, and everything else is a number at a position. A tool for
/// discovering meanings must not be the thing that invents them.
///
/// Not every word is a float. The table packs integers, flags and padding alongside them, and
/// reading those as IEEE floats yields values like 1e-41 or NaN. Those are excluded from the
/// comparison rather than shown as data -- a word that is not a float cannot meaningfully be said
/// to have changed by 0.3.
/// </summary>
public static class PmTableExplorer
{
    /// <summary>
    /// How many 64-bit words to ask for.
    ///
    /// Two hundred and fifty-six words is 512 floats, comfortably past the confirmed head and
    /// bounded. The call reports how many it actually wrote, so asking for more than the table
    /// holds costs nothing -- but is not a reason to ask for an unbounded amount.
    /// </summary>
    public const int Words = 256;

    /// <summary>
    /// The smallest change worth reporting, relative to the larger of the two values.
    ///
    /// A live power figure jitters continuously, so an exact comparison would report every
    /// floating-point word as having moved and the output would be the whole table. One per cent
    /// separates "this responded to what I changed" from "this is a measurement breathing".
    /// </summary>
    public const double RelativeThreshold = 0.01;

    /// <summary>
    /// Below this, a change is noise whatever its ratio.
    ///
    /// Without it, a word drifting from 0.0001 to 0.0002 reports as a 100% change and buries the
    /// ones that matter.
    /// </summary>
    public const double AbsoluteFloor = 0.05;

    /// <summary>
    /// The confirmed head, in the order the table packs it.
    ///
    /// These are named because they were checked against values the hardware reports through
    /// other routes, not because the position looked right. Nothing outside this list is named.
    /// </summary>
    private static readonly string[] Confirmed =
    {
        "STAPM limit", "STAPM value",
        "Fast limit", "Fast value",
        "Slow limit", "Slow value",
        "APU slow limit", "APU slow value",
        "TDC VDD limit", "TDC VDD value",
        "TDC SoC limit", "TDC SoC value",
        "EDC VDD limit", "EDC VDD value",
        "EDC SoC limit", "EDC SoC value",
        "Thermal limit", "Core temperature",
        "SoC thermal limit", "SoC temperature",
        "GFX thermal limit", "GFX temperature",
    };

    /// <summary>The confirmed name for an index, or null where this project has not earned one.</summary>
    public static string? KnownName(int index) =>
        index >= 0 && index < Confirmed.Length ? Confirmed[index] : null;

    /// <summary>
    /// Whether a decoded word is plausibly a reading rather than an integer or padding read as one.
    ///
    /// Everything the confirmed head holds is a watt, an amp or a degree, so real values sit in a
    /// narrow band. A word decoding to 1e-41 is an integer wearing a float's clothes, and one
    /// decoding to NaN is not a reading at all.
    /// </summary>
    internal static bool LooksLikeAReading(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value)
        && (value == 0f || (Math.Abs(value) >= 0.001f && Math.Abs(value) < 1e6f));

    /// <summary>
    /// Takes one capture. Null when the SMU will not answer.
    /// </summary>
    public static PmTableDump? Capture(RyzenSmu smu, string label)
    {
        try
        {
            var (version, _) = smu.ResolvePmTable();

            smu.UpdatePmTable();

            var words = new ulong[Words];
            int written = smu.ReadPmTable(words);
            if (written <= 0) return null;

            var values = new float[written * 2];
            for (int i = 0; i < written; i++)
            {
                values[i * 2] = BitConverter.Int32BitsToSingle(unchecked((int)(uint)(words[i] & 0xFFFFFFFF)));
                values[i * 2 + 1] = BitConverter.Int32BitsToSingle(unchecked((int)(uint)(words[i] >> 32)));
            }

            return new PmTableDump(DateTime.UtcNow, label, version, values);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Every word that moved between two captures, largest relative change first.
    ///
    /// Pure, so the part that decides what counts as a change can be checked without a machine to
    /// change.
    /// </summary>
    public static IReadOnlyList<PmTableChange> Diff(PmTableDump before, PmTableDump after)
    {
        var changes = new List<PmTableChange>();

        int count = Math.Min(before.Values.Count, after.Values.Count);

        for (int i = 0; i < count; i++)
        {
            float from = before.Values[i], to = after.Values[i];

            // A word that is not a reading in either capture cannot be said to have changed.
            if (!LooksLikeAReading(from) || !LooksLikeAReading(to)) continue;

            double delta = Math.Abs(to - from);
            if (delta < AbsoluteFloor) continue;

            double scale = Math.Max(Math.Abs(from), Math.Abs(to));
            if (scale > 0 && delta / scale < RelativeThreshold) continue;

            changes.Add(new PmTableChange(i, from, to));
        }

        return changes
            .OrderByDescending(c => Math.Abs(c.Delta) / Math.Max(Math.Max(Math.Abs(c.From), Math.Abs(c.To)), 1e-6))
            .ToList();
    }

    /// <summary>
    /// What moved, in a form somebody can act on.
    ///
    /// Leads with the unknown words, because the known ones moving is expected and uninformative
    /// -- the point of the exercise is the other ones.
    /// </summary>
    public static string Describe(PmTableDump before, PmTableDump after, IReadOnlyList<PmTableChange> changes)
    {
        if (before.Version != after.Version)
            return $"These two captures are of different table layouts (0x{before.Version:X} and "
                 + $"0x{after.Version:X}), so their positions do not refer to the same things and "
                 + "nothing can be compared.";

        if (changes.Count == 0)
            return $"Nothing moved by more than {RelativeThreshold:P0} between \"{before.Label}\" and "
                 + $"\"{after.Label}\". Either the change did not reach the SMU, or whatever it "
                 + "affected is not in this table.";

        int unknown = changes.Count(c => c.Known is null);

        return $"{changes.Count} word(s) moved between \"{before.Label}\" and \"{after.Label}\", "
             + $"{unknown} of them at positions this project has never identified. Those are the "
             + "interesting ones: a word that responds to a change nobody has mapped is a field "
             + "waiting to be named, and naming it takes repeating this with one thing different.";
    }
}
