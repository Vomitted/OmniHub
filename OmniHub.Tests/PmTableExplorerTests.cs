using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Finding out what the unmapped PM table words are, without inventing an answer.
///
/// The risk in a tool like this is not that it fails, it is that it succeeds too easily. Reading
/// arbitrary memory as IEEE floats always produces numbers; most of them are integers or padding
/// wearing a float's clothes, and a comparison that reported those as having changed by 0.3 would
/// bury the handful of words that genuinely responded to whatever was changed.
///
/// So the tests here are mostly about what must NOT be reported.
/// </summary>
public class PmTableExplorerTests
{
    private static readonly DateTime At = new(2026, 9, 17, 14, 0, 0, DateTimeKind.Utc);

    private static PmTableDump Dump(string label, params float[] values) =>
        new(At, label, 0x004C0009, values);

    /// <summary>A word that genuinely moved is reported.</summary>
    [Fact]
    public void AWordThatMovedIsReported()
    {
        var changes = PmTableExplorer.Diff(
            Dump("idle", 10f, 20f, 30f),
            Dump("load", 10f, 45f, 30f));

        var change = Assert.Single(changes);

        Assert.Equal(1, change.Index);
        Assert.Equal(20f, change.From);
        Assert.Equal(45f, change.To);
    }

    /// <summary>
    /// A measurement breathing is not a change.
    ///
    /// Live power figures jitter constantly. Without a threshold every floating-point word in the
    /// table reports as moved, and the output becomes the table.
    /// </summary>
    [Fact]
    public void JitterIsNotAChange() =>
        Assert.Empty(PmTableExplorer.Diff(
            Dump("a", 45.00f, 32.10f),
            Dump("b", 45.10f, 32.15f)));

    /// <summary>
    /// A tiny value doubling is not a big change.
    ///
    /// Relative alone would make 0.0001 to 0.0002 a hundred per cent move and rank it above a
    /// power limit that shifted by fifteen watts.
    /// </summary>
    [Fact]
    public void ATinyValueDoublingIsNotRankedAsHuge() =>
        Assert.Empty(PmTableExplorer.Diff(
            Dump("a", 0.0001f),
            Dump("b", 0.0002f)));

    /// <summary>
    /// Words that are not readings are excluded rather than described as having moved.
    ///
    /// An integer read as a float comes out around 1e-41, and NaN is not a value at all. Both
    /// appear throughout a table that packs flags and padding between its measurements.
    /// </summary>
    [Fact]
    public void NonReadingsAreExcluded()
    {
        var changes = PmTableExplorer.Diff(
            Dump("a", 1e-41f, float.NaN, float.PositiveInfinity, 20f),
            Dump("b", 5e-41f, 40f, 10f, 40f));

        var change = Assert.Single(changes);
        Assert.Equal(3, change.Index);
    }

    /// <summary>
    /// Only the confirmed head carries names, and nothing beyond it does.
    ///
    /// This is the line the whole feature rests on: a tool for discovering what words mean must
    /// not be the thing that invents meanings.
    /// </summary>
    [Fact]
    public void OnlyConfirmedIndicesAreNamed()
    {
        Assert.Equal("STAPM limit", PmTableExplorer.KnownName(0));
        Assert.Equal("GFX temperature", PmTableExplorer.KnownName(21));

        Assert.Null(PmTableExplorer.KnownName(22));
        Assert.Null(PmTableExplorer.KnownName(137));
        Assert.Null(PmTableExplorer.KnownName(-1));
    }

    /// <summary>Two captures of different layouts are not compared at all.</summary>
    [Fact]
    public void DifferentLayoutsAreNotCompared()
    {
        var before = new PmTableDump(At, "a", 0x004C0009, new[] { 10f });
        var after = new PmTableDump(At, "b", 0x00500000, new[] { 99f });

        string text = PmTableExplorer.Describe(before, after, PmTableExplorer.Diff(before, after));

        Assert.Contains("different table layouts", text);
        Assert.Contains("nothing can be compared", text);
    }

    /// <summary>
    /// Nothing moving is reported as a result, with both explanations offered.
    ///
    /// "The change did not reach the SMU" and "whatever it affected is not in this table" are
    /// different conclusions, and picking one would be a guess.
    /// </summary>
    [Fact]
    public void NothingMovingIsAResult()
    {
        var before = Dump("idle", 10f, 20f);
        var after = Dump("still idle", 10f, 20f);

        string text = PmTableExplorer.Describe(before, after, PmTableExplorer.Diff(before, after));

        Assert.Contains("Nothing moved", text);
        Assert.Contains("did not reach the SMU", text);
        Assert.Contains("not in this table", text);
    }

    /// <summary>The summary leads with the unknown words, which are the point of the exercise.</summary>
    [Fact]
    public void TheSummaryLeadsWithTheUnknownWords()
    {
        var values = Enumerable.Repeat(10f, 40).ToArray();
        var moved = values.ToArray();
        moved[0] = 50f;      // a known one
        moved[30] = 50f;     // an unmapped one

        var before = Dump("idle", values);
        var after = Dump("load", moved);

        string text = PmTableExplorer.Describe(before, after, PmTableExplorer.Diff(before, after));

        Assert.Contains("2 word(s) moved", text);
        Assert.Contains("1 of them at positions this project has never identified", text);
    }

    /// <summary>Captures of different lengths compare only the part they share.</summary>
    [Fact]
    public void DifferentLengthsCompareTheSharedPart()
    {
        var changes = PmTableExplorer.Diff(
            Dump("a", 10f, 20f, 30f, 40f),
            Dump("b", 10f, 99f));

        var change = Assert.Single(changes);
        Assert.Equal(1, change.Index);
    }

    /// <summary>What counts as a reading, at the edges.</summary>
    [Fact]
    public void TheReadingTestAcceptsRealValuesAndRejectsTheRest()
    {
        Assert.True(PmTableExplorer.LooksLikeAReading(0f));
        Assert.True(PmTableExplorer.LooksLikeAReading(45.5f));
        Assert.True(PmTableExplorer.LooksLikeAReading(-12f));

        Assert.False(PmTableExplorer.LooksLikeAReading(float.NaN));
        Assert.False(PmTableExplorer.LooksLikeAReading(float.PositiveInfinity));
        Assert.False(PmTableExplorer.LooksLikeAReading(1e-41f));
        Assert.False(PmTableExplorer.LooksLikeAReading(1e30f));
    }
}
