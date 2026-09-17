using System.IO;
using System.IO.Compression;
using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// Collecting a machine's diagnostics into one archive.
///
/// The selection rule is the part worth testing. It has to window the rolling logs, because the
/// thermal trace alone is twelve megabytes and an archive nobody can attach anywhere helps no
/// one; it has to spare the deliberate artefacts whatever their age, because a load-test run is
/// somebody's saved measurement rather than accumulated exhaust; and it has to keep files that
/// carry no date at all, because crash.log is the single most useful file in the bundle on a
/// machine that has been crashing.
/// </summary>
public class SupportBundleTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan ThreeDays = TimeSpan.FromDays(3);

    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-bundle-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }

    [Theory]
    [InlineData("thermal-2026-09-17.csv", true)]      // today
    [InlineData("thermal-2026-09-14.csv", true)]      // exactly the window's edge
    [InlineData("thermal-2026-09-13.csv", false)]     // one day past it
    [InlineData("thermal-2026-09-15-2.csv", true)]    // a roll suffix must not confuse the date
    [InlineData("power-2026-09-16.csv", true)]
    [InlineData("polltiming-2026-09-03.csv", false)]
    public void RollingLogsAreWindowed(string name, bool expected) =>
        Assert.Equal(expected, SupportBundle.Includes(name, Now, ThreeDays));

    /// <summary>
    /// A load-test run or a probe report is a measurement somebody chose to make.
    ///
    /// The same rule the log retention already follows. Sweeping one out of a support bundle
    /// would discard the baseline half of a comparison at the moment it was being asked about.
    /// </summary>
    [Theory]
    [InlineData("loadtest-2026-09-09-124524.csv")]
    [InlineData("probe-2026-09-09-125015.txt")]
    public void DeliberateArtefactsAreKeptWhateverTheirAge(string name) =>
        Assert.True(SupportBundle.Includes(name, Now, ThreeDays));

    /// <summary>A file with no date in its name is kept. crash.log is the one that matters.</summary>
    [Fact]
    public void ADatelessFileIsKept() =>
        Assert.True(SupportBundle.Includes("crash.log", Now, ThreeDays));

    /// <summary>
    /// Something that merely looks like a date is not one.
    ///
    /// Parsed rather than pattern-matched, so a name carrying "2026-13-99" does not become a log
    /// from an impossible month and get kept or dropped on that basis.
    /// </summary>
    [Fact]
    public void OnlyARealDateCounts()
    {
        Assert.Equal(new DateTime(2026, 9, 17), SupportBundle.DateIn("thermal-2026-09-17.csv"));
        Assert.Null(SupportBundle.DateIn("thermal-2026-13-99.csv"));
        Assert.Null(SupportBundle.DateIn("crash.log"));
    }

    /// <summary>
    /// The manifest says what the archive holds about the person, not only about the machine.
    ///
    /// This is the part that stops the feature being a quiet collection of somebody's application
    /// list. It is a perfectly reasonable thing to include and an unreasonable thing to include
    /// silently.
    /// </summary>
    [Fact]
    public void TheManifestNamesWhatIsPersonal()
    {
        string text = SupportBundle.Manifest(
            new[] { new BundleEntry("logs/thermal-2026-09-17.csv", 1234) }, ThreeDays);

        Assert.Contains("ABOUT YOU", text);
        Assert.Contains("applications you have set a graphics preference for", text);
        Assert.Contains("Nothing here is sent anywhere", text);
        Assert.Contains("thermal-2026-09-17.csv", text);
    }

    /// <summary>
    /// A real archive is written, is openable, and contains the manifest and the notes.
    ///
    /// End to end rather than mocked, because the failure this guards against is a zip that
    /// cannot be opened -- which no amount of testing the selection rule would catch.
    /// </summary>
    [Fact]
    public void AnArchiveIsWrittenAndCanBeOpened()
    {
        string dir = NewDir();
        try
        {
            string zip = Path.Combine(dir, "bundle.zip");

            var result = SupportBundle.Create(
                zip,
                new Dictionary<string, string>
                {
                    ["capabilities.txt"] = "Fan band: raw 10-56",
                    ["system.txt"] = "32 GB DDR5 across 2 channels",
                });

            Assert.Null(result.Error);
            Assert.True(File.Exists(zip));

            using var archive = ZipFile.OpenRead(zip);

            Assert.NotNull(archive.GetEntry("MANIFEST.txt"));
            Assert.NotNull(archive.GetEntry("capabilities.txt"));
            Assert.NotNull(archive.GetEntry("system.txt"));

            using var reader = new StreamReader(archive.GetEntry("capabilities.txt")!.Open());
            Assert.Equal("Fan band: raw 10-56", reader.ReadToEnd());
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Writing where nothing can be written reports the reason rather than throwing.
    ///
    /// The button that calls this is pressed by somebody whose machine is already misbehaving,
    /// which is the worst possible moment for an unhandled exception out of the diagnostic tool.
    /// </summary>
    [Fact]
    public void AnUnwritableDestinationIsReportedNotThrown()
    {
        var result = SupportBundle.Create(
            Path.Combine("Z:", "no-such-volume", "bundle.zip"),
            new Dictionary<string, string>());

        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }
}
