using System.IO;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Per-model fan bands, loaded from a profile.
///
/// This decides how every percentage on the curve becomes a speed command, so the cases that
/// matter are the ones where a profile is wrong: the loader has to refuse it and leave the
/// measured default standing, rather than half-apply it and produce a scale belonging to no
/// machine at all.
/// </summary>
public class FanCalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omnihub-profiles-" + Guid.NewGuid().ToString("N"));

    public FanCalibrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ModelInfo Board(string baseboard) => new("HP", "Victus by HP Gaming Laptop 15-fb2xxx", baseboard);

    private void WriteProfile(string baseboard, string json) =>
        File.WriteAllText(Path.Combine(_dir, baseboard + ".json"), json);

    [Fact]
    public void LoadsAProfileNamedForTheBoard()
    {
        WriteProfile("8C2F", """
            { "baseboard": "8C2F", "minRawLevel": 12, "maxRawLevelFan1": 60, "maxRawLevelFan2": 58 }
            """);

        var c = FanProfiles.Load(Board("8C2F"), _dir);

        Assert.NotNull(c);
        Assert.Equal(12, c!.MinRawLevel);
        Assert.Equal(60, c.MaxRawLevelFan1);
        Assert.Equal(58, c.MaxRawLevelFan2);
    }

    [Fact]
    public void NoProfileMeansNoOverride()
    {
        Assert.Null(FanProfiles.Load(Board("8C2F"), _dir));
    }

    [Fact]
    public void AProfileMissingAValueIsRefusedRatherThanPartlyApplied()
    {
        // Filling the gap from another machine's measurements would produce a band belonging to
        // neither board, which is worse than having no profile at all.
        WriteProfile("8C2F", """
            { "baseboard": "8C2F", "minRawLevel": 12, "maxRawLevelFan1": 60 }
            """);

        Assert.Null(FanProfiles.Load(Board("8C2F"), _dir));
    }

    [Fact]
    public void AnUnusableBandIsRefused()
    {
        // A ceiling at or below the floor collapses the whole percentage range onto one speed,
        // which on screen looks like fan control having stopped working.
        WriteProfile("8C2F", """
            { "baseboard": "8C2F", "minRawLevel": 40, "maxRawLevelFan1": 40, "maxRawLevelFan2": 40 }
            """);

        Assert.Null(FanProfiles.Load(Board("8C2F"), _dir));
        Assert.False(new FanCalibration(40, 40, 40).IsUsable);
        Assert.True(FanCalibration.Default.IsUsable);
    }

    [Fact]
    public void MalformedJsonFallsBackRatherThanThrowing()
    {
        WriteProfile("8C2F", "{ this is not json");
        Assert.Null(FanProfiles.Load(Board("8C2F"), _dir));
    }

    [Fact]
    public void ABlankOrUnsafeBoardNameLoadsNothing()
    {
        Assert.Null(FanProfiles.Load(Board(""), _dir));
        Assert.Null(FanProfiles.Load(Board("   "), _dir));

        // The name comes from WMI rather than from a user, but a path built by concatenation
        // still must not be able to climb out of the profiles folder.
        Assert.Null(FanProfiles.Load(Board(".." + Path.DirectorySeparatorChar + "escaped"), _dir));
    }

    [Fact]
    public void TheDefaultIsTheBandThatWasActuallyMeasured()
    {
        Assert.Equal(10, FanCalibration.Default.MinRawLevel);
        Assert.Equal(56, FanCalibration.Default.MaxRawLevelFan1);
        Assert.Equal(56, FanCalibration.Default.MaxRawLevelFan2);
    }

    // ---------------------------------------------------------------- saving

    /// <summary>
    /// The loader has existed since per-model overrides were introduced, ModelProfile's comment
    /// points at it, and the README tells people their measurements go there -- but nothing ever
    /// wrote one. So Manual Calibration could measure a chassis's real fan band and then had
    /// nowhere to put the answer, which ended the feature at the interesting part.
    /// </summary>
    [Fact]
    public void ASavedBandIsLoadedBackForTheSameBoard()
    {
        string dir = NewDir();
        try
        {
            var model = new ModelInfo("HP", "Victus by HP Laptop 15-fb2xxx", "8C2F");

            var (saved, detail) = FanProfiles.Save(model, new FanCalibration(12, 58, 57), directory: dir);
            Assert.True(saved, detail);

            var loaded = FanProfiles.Load(model, dir);

            Assert.NotNull(loaded);
            Assert.Equal(new FanCalibration(12, 58, 57), loaded);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A ceiling at or below the floor collapses the percentage scale onto one speed, so every
    /// point on the curve commands the same thing. Load already refuses to return such a band;
    /// refusing to WRITE it means the user finds out in a dialog rather than by noticing, days
    /// later, that fan control appears to have stopped working.
    /// </summary>
    [Theory]
    [InlineData(40, 40, 56)]
    [InlineData(40, 30, 56)]
    [InlineData(40, 56, 40)]
    public void AnUnusableBandIsRefusedRatherThanWritten(byte min, byte max1, byte max2)
    {
        string dir = NewDir();
        try
        {
            var model = new ModelInfo("HP", "Victus", "8C2F");

            var (saved, detail) = FanProfiles.Save(model, new FanCalibration(min, max1, max2), directory: dir);

            Assert.False(saved);
            Assert.Contains("same speed", detail);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Cleanup(dir); }
    }

    /// <summary>A board with no name has nowhere to be filed, and says so rather than guessing one.</summary>
    [Fact]
    public void ABoardWithNoNameCannotBeSaved()
    {
        string dir = NewDir();
        try
        {
            var (saved, _) = FanProfiles.Save(new ModelInfo("HP", "Victus", ""), FanCalibration.Default, directory: dir);
            Assert.False(saved);
            Assert.Null(FanProfiles.PathFor(new ModelInfo("HP", "Victus", "")));
        }
        finally { Cleanup(dir); }
    }

    /// <summary>Saving twice updates the profile rather than leaving the first values in place.</summary>
    [Fact]
    public void SavingAgainReplacesTheEarlierBand()
    {
        string dir = NewDir();
        try
        {
            var model = new ModelInfo("HP", "Victus", "8C2F");

            FanProfiles.Save(model, new FanCalibration(10, 56, 56), directory: dir);
            FanProfiles.Save(model, new FanCalibration(11, 54, 54), directory: dir);

            Assert.Equal(new FanCalibration(11, 54, 54), FanProfiles.Load(model, dir));
        }
        finally { Cleanup(dir); }
    }

    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-profiles-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }
}
