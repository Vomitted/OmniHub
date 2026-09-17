using System.IO;
using System.Text;
using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Getting a measurement out of the window intact.
///
/// The export is only worth having if what comes out means the same as what went in. Two things
/// decide that, and both have a plausible wrong answer: a null must stay empty rather than
/// becoming a zero, because in the fan columns those are opposite claims; and a verdict is a whole
/// sentence full of commas, which written raw into a comma-separated file does not merely look
/// untidy -- it shifts every column after it.
/// </summary>
public class CsvExportTests
{
    private static readonly DateTime At = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-export-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }

    /// <summary>The export carries the log's own header, so both open in the same tool.</summary>
    [Fact]
    public void TheHeaderMatchesTheLog() =>
        Assert.StartsWith(
            "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor",
            CsvExport.Thermal(Array.Empty<ThermalSample>()));

    /// <summary>
    /// A missing reading exports as an empty field, and a real zero as a zero.
    ///
    /// The same distinction the writer and reader were taught this week. An export that collapsed
    /// them would reintroduce the fabricated stopped fan at the moment the data leaves the
    /// application, which is the worst possible place to lose it.
    /// </summary>
    [Fact]
    public void AMissingReadingStaysEmptyAndARealZeroStaysZero()
    {
        string csv = CsvExport.Thermal(new[]
        {
            new ThermalSample(At, 70.5, null, null, null, null, null, "Auto", "SmuDieTctl"),
            new ThermalSample(At.AddSeconds(2), 70.5, null, 27, 0, 45, false, "Auto", "SmuDieTctl"),
        });

        string[] lines = csv.Split('\n');

        Assert.Equal("2026-09-17T12:00:00Z,70.5,,,,,,Auto,SmuDieTctl", lines[1]);
        Assert.Equal("2026-09-17T12:00:02Z,70.5,,27,0,45,False,Auto,SmuDieTctl", lines[2]);
    }

    /// <summary>
    /// The round trip: what is exported reads back as what was exported.
    ///
    /// Through the application's own reader, because that is the tool most likely to open one of
    /// these files next -- and because a header that merely looks right is not the same as one the
    /// parser accepts.
    /// </summary>
    [Fact]
    public async Task AnExportReadsBackThroughTheApplicationsOwnReader()
    {
        string dir = NewDir();
        try
        {
            var samples = new[]
            {
                new ThermalSample(At, 70.5, null, null, null, null, null, "Auto", "SmuDieTctl"),
                new ThermalSample(At.AddSeconds(2), 66.25, null, 27, 0, 45, false, "Auto", "SmuDieTctl"),
            };

            File.WriteAllText(
                Path.Combine(dir, "thermal-2026-09-17.csv"),
                CsvExport.Thermal(samples),
                new UTF8Encoding(false));

            var read = await new TelemetryHistory(dir)
                .ReadThermalAsync(At.AddMinutes(-1), At.AddMinutes(1));

            Assert.Equal(2, read.Count);

            Assert.Null(read[0].Fan1Raw);
            Assert.Null(read[0].Fan2Raw);
            Assert.Null(read[0].CommandedPercent);

            Assert.Equal((byte)27, read[1].Fan1Raw);
            Assert.Equal((byte)0, read[1].Fan2Raw);
            Assert.Equal(45, read[1].CommandedPercent);
            Assert.Equal("SmuDieTctl", read[1].Sensor);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A verdict full of commas is quoted, so it stays in one cell.
    ///
    /// Unquoted it would not merely wrap oddly: every column after it would shift, and a reader
    /// would take the second half of a sentence for a number.
    /// </summary>
    [Fact]
    public void TheVerdictIsQuotedBecauseItIsASentence()
    {
        var metrics = new RunMetrics(600, TimeSpan.FromMinutes(20), 75, 78, 85, 30);

        string csv = CsvExport.Comparison(new RunComparisonResult(
            metrics, metrics, new Interval(-0.4, 0.6), null,
            "Indistinguishable. The difference lies between -0.4 and +0.6 C, which includes zero."));

        Assert.Contains("verdict,\"Indistinguishable. The difference lies between -0.4 and +0.6 C, "
                        + "which includes zero.\"", csv);
    }

    /// <summary>An absent interval exports as empty bounds, never as zeros.</summary>
    [Fact]
    public void AnAbsentIntervalIsEmptyNotZero()
    {
        var metrics = new RunMetrics(600, TimeSpan.FromMinutes(20), 75, 78, 85, 30);

        string csv = CsvExport.Comparison(new RunComparisonResult(
            metrics, metrics, null, null, "Not enough data to compare."));

        // The bounds line under each interval header is two empty fields.
        Assert.Contains("temperature_difference_c_low,temperature_difference_c_high\n,\n", csv);
    }

    /// <summary>A quote inside a field is doubled, which is how CSV escapes one.</summary>
    [Fact]
    public void AQuoteInsideAFieldIsDoubled() =>
        Assert.Equal("\"he said \"\"hot\"\", twice\"", CsvExport.Escape("he said \"hot\", twice"));

    /// <summary>An ordinary field is not quoted, because quoting everything is its own noise.</summary>
    [Fact]
    public void AnOrdinaryFieldIsLeftAlone()
    {
        Assert.Equal("Auto", CsvExport.Escape("Auto"));
        Assert.Equal("", CsvExport.Escape(null));
    }
}
