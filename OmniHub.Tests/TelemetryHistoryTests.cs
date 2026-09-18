// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.IO;
using System.Text;
using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Reading this machine's own logs back.
///
/// Every case here comes from something real on disk rather than from imagination, because the
/// interesting failures are all in the shape of the actual files: four thermal header layouts
/// coexist, one stream carries a BOM and the others do not, two columns use -1 as a sentinel,
/// and the file anyone actually wants to read is the one a writer currently holds open.
/// </summary>
public class TelemetryHistoryTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-history-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }

    private static void Write(string dir, string name, string contents, bool bom = false) =>
        File.WriteAllText(Path.Combine(dir, name), contents, new UTF8Encoding(bom));

    private const string NineColumn =
        "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor";

    // The layout before the sensor column was added. Files in this shape are still on disk.
    private const string EightColumn =
        "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode";

    // The layout from the change that began recording the discrete GPU.
    private const string ElevenColumn =
        "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor,gpu_c,gpu_w";

    // The layout from the change that began recording what the processor was up against.
    private const string FourteenColumn =
        "timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor,gpu_c,gpu_w,pkg_w,limit,limit_pct";

    private static readonly DateTime Sep1 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sep4 = new(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The load-bearing one. A positional reader would put "Auto" into Sensor on the older
    /// layout and look entirely plausible doing it, because both values are short strings.
    /// </summary>
    [Fact]
    public async Task ColumnsAreResolvedByNameNotPosition()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-01.csv",
                EightColumn + "\n2026-09-01T10:00:00Z,61.2,-1,12,12,28,False,Auto\n");
            Write(dir, "thermal-2026-09-02.csv",
                NineColumn + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl\n", bom: true);

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1.AddDays(-1), Sep4);

            Assert.Equal(2, samples.Count);

            // Older file: mode present, sensor genuinely absent -- not "Auto" shifted along.
            Assert.Equal("Auto", samples[0].Mode);
            Assert.Null(samples[0].Sensor);

            Assert.Equal("Auto", samples[1].Mode);
            Assert.Equal("SmuDieTctl", samples[1].Sensor);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// thermal-*.csv carries a UTF-8 BOM and the others do not, because ThermalLog builds its
    /// writer with Encoding.UTF8 while the rest wrap a FileStream. A stray U+FEFF surviving into
    /// the first column name would make every lookup of "timestamp" miss on exactly the largest
    /// and most useful stream.
    /// </summary>
    [Fact]
    public async Task AByteOrderMarkDoesNotHideTheFirstColumn()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-02.csv",
                NineColumn + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl\n", bom: true);

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Single(samples);
            Assert.Equal(61.2, samples[0].TempC);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// -1 means "prediction was off" and "the service had not commanded", not a temperature of
    /// minus one degree and a fan at minus one percent. There are 8,694 of them in one day's
    /// file on this machine, so getting this wrong would draw a solid band below zero.
    /// </summary>
    [Fact]
    public async Task TheMinusOneSentinelsReadAsAbsent()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-02.csv",
                NineColumn
                + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,-1,False,Auto,SmuDieTctl"
                + "\n2026-09-02T10:00:02Z,61.4,63.1,12,12,28,False,Auto,SmuDieTctl\n", bom: true);

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Null(samples[0].ForecastC);
            Assert.Null(samples[0].CommandedPercent);

            Assert.Equal(63.1, samples[1].ForecastC);
            Assert.Equal(28, samples[1].CommandedPercent);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>An empty field is unavailable, never zero. The writers rely on this.</summary>
    [Fact]
    public async Task AnEmptyFieldIsNullRatherThanZero()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "network-2026-09-02.csv",
                "timestamp,target,rtt_ms,lost,avg_ms,jitter_ms,loss_pct"
                + "\n2026-09-02T10:00:00Z,1.1.1.1,,1,,,\n");

            var samples = await new TelemetryHistory(dir).ReadNetworkAsync(Sep1, Sep4);

            Assert.Single(samples);
            Assert.Null(samples[0].RttMs);       // not 0
            Assert.True(samples[0].Lost);
            Assert.Null(samples[0].AvgMs);
            Assert.Null(samples[0].JitterMs);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// A plain DateTime.Parse returns Kind.Local and shifts the whole history by the machine's
    /// offset, which here is seven hours. A history quietly wrong by seven hours is worse than
    /// one that refuses to load, because nothing about it looks wrong.
    /// </summary>
    [Fact]
    public async Task TimestampsComeBackAsUtc()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-02.csv",
                NineColumn + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl\n", bom: true);

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Equal(DateTimeKind.Utc, samples[0].AtUtc.Kind);
            Assert.Equal(new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc), samples[0].AtUtc);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The writers flush on a ten-second interval, so the tail of today's file is routinely a
    /// partial line. That is the normal state of the file anyone actually wants to read, and it
    /// must not cost them the rows above it.
    /// </summary>
    [Fact]
    public async Task ATornLastLineDoesNotLoseTheRowsBeforeIt()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-02.csv",
                NineColumn
                + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl"
                + "\n2026-09-02T10:00:02Z,61.4,-1,12,12,28,False,Auto,SmuDieTctl"
                + "\n2026-09-02T10:00:04Z,61.9,-1,12");

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Equal(3, samples.Count);
            Assert.Equal(61.9, samples[2].TempC);
            Assert.Null(samples[2].Sensor);     // the columns that were never written
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The roll files of one day are not chronological among themselves: thermal-2026-09-13-2
    /// can hold rows earlier than thermal-2026-09-13, depending on when the header changed. So
    /// the result is sorted by timestamp rather than trusted in file order.
    /// </summary>
    [Fact]
    public async Task RollFilesAndDayBoundariesComeBackInTimeOrder()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-02-2.csv",
                NineColumn + "\n2026-09-02T08:00:00Z,50,-1,10,10,0,False,Auto,SmuDieTctl\n", bom: true);
            Write(dir, "thermal-2026-09-02.csv",
                NineColumn + "\n2026-09-02T23:59:00Z,55,-1,10,10,0,False,Auto,SmuDieTctl\n", bom: true);
            Write(dir, "thermal-2026-09-03.csv",
                NineColumn + "\n2026-09-03T00:01:00Z,56,-1,10,10,0,False,Auto,SmuDieTctl\n", bom: true);

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Equal(new double?[] { 50, 55, 56 }, samples.Select(s => s.TempC).ToArray());
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// ThermalLog holds its file FileAccess.Write, FileShare.Read. A reader asking for
    /// FileShare.Read fails against it -- which would make the current day, the one anyone
    /// wants, the single day that cannot be read.
    /// </summary>
    [Fact]
    public async Task TodaysFileIsReadableWhileTheWriterHoldsIt()
    {
        string dir = NewDir();
        try
        {
            string path = Path.Combine(dir, "thermal-2026-09-02.csv");

            using var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using (var writer = new StreamWriter(held, new UTF8Encoding(true), leaveOpen: true))
            {
                writer.WriteLine(NineColumn);
                writer.WriteLine("2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl");
                writer.Flush();
            }

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Single(samples);
            Assert.Equal(61.2, samples[0].TempC);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The logs are written with InvariantCulture, so they must be read with it. Under a culture
    /// using a decimal comma, a naive parse of "61.2" yields 612 -- a plausible-looking number
    /// an order of magnitude out, which is the worst kind of wrong.
    /// </summary>
    [Fact]
    public async Task ADecimalCommaCultureStillReadsTheNumbers()
    {
        var original = CultureInfo.CurrentCulture;
        string dir = NewDir();
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            Write(dir, "thermal-2026-09-02.csv",
                NineColumn + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl\n", bom: true);

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1, Sep4);

            Assert.Equal(61.2, samples[0].TempC);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            Cleanup(dir);
        }
    }

    [Fact]
    public async Task AMissingDirectoryReadsAsEmptyRatherThanThrowing()
    {
        var samples = await new TelemetryHistory(
                Path.Combine(Path.GetTempPath(), "omnihub-nope-" + Guid.NewGuid().ToString("N")))
            .ReadThermalAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        Assert.Empty(samples);
    }

    /// <summary>
    /// Read-only, against this machine's actual logs, in the same spirit as HardwareReadTests.
    /// Fourteen days of real trace with four header layouts and a live writer is a harsher test
    /// than anything constructed here, and it is the data the feature will actually face.
    /// </summary>
    [Fact]
    public async Task TheRealLogsOnThisMachineParse()
    {
        var history = new TelemetryHistory();

        var samples = await history.ReadThermalAsync(DateTime.UtcNow.AddDays(-14), DateTime.UtcNow);

        // Nothing to prove on a machine that has never logged; skip rather than fail.
        if (samples.Count == 0) return;

        Assert.All(samples, s => Assert.Equal(DateTimeKind.Utc, s.AtUtc.Kind));

        for (int i = 1; i < samples.Count; i++)
            Assert.True(samples[i].AtUtc >= samples[i - 1].AtUtc);

        // No sentinel leaked through as a value.
        Assert.DoesNotContain(samples, s => s.ForecastC is < 0);
        Assert.DoesNotContain(samples, s => s.CommandedPercent is < 0);

        // Temperatures that are present are physically possible.
        Assert.All(samples.Where(s => s.TempC is not null), s => Assert.InRange(s.TempC!.Value, 0, 120));
    }

    /// <summary>
    /// A file written before the GPU columns existed reads back with no GPU reading, not a zero.
    ///
    /// This is the fifth thermal header to coexist on disk, and the reason the reader has always
    /// resolved columns by name. Two weeks of trace predate these columns; reporting those rows as
    /// a GPU at 0 degrees drawing 0 watts would make an unrecorded card look like a cold one, and
    /// any later analysis of when the GPU was the constrained part would start from that.
    /// </summary>
    [Fact]
    public async Task FilesWrittenBeforeTheGpuColumnsReadBackAsNoReading()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-01.csv",
                NineColumn + "\n2026-09-01T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl\n");
            Write(dir, "thermal-2026-09-02.csv",
                ElevenColumn + "\n2026-09-02T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl,54.5,31.2\n");

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1.AddDays(-1), Sep4);

            Assert.Equal(2, samples.Count);

            Assert.Null(samples[0].GpuTempC);
            Assert.Null(samples[0].GpuWatts);

            Assert.Equal(54.5, samples[1].GpuTempC);
            Assert.Equal(31.2, samples[1].GpuWatts);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// An empty GPU field is an absent reading, and a written zero is a real one.
    ///
    /// The same distinction the fan columns carry, and it matters in the same way: an idle card
    /// genuinely drawing no measurable power is a reading, and a card that was asleep is not.
    /// </summary>
    [Fact]
    public async Task AnEmptyGpuFieldIsNotAZero()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-01.csv",
                ElevenColumn
                + "\n2026-09-01T10:00:00Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl,,"
                + "\n2026-09-01T10:00:02Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl,0,0\n");

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1.AddDays(-1), Sep4);

            Assert.Equal(2, samples.Count);

            Assert.Null(samples[0].GpuTempC);
            Assert.Null(samples[0].GpuWatts);

            Assert.Equal(0, samples[1].GpuTempC);
            Assert.Equal(0, samples[1].GpuWatts);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The binding limit reads back as the name the SMU gave it, and an unread one stays null.
    ///
    /// The name matters as much as the number: "Temperature at 99 per cent" and "Sustained power
    /// at 99 per cent" call for opposite responses, and a row that lost the name would be a
    /// percentage of something unidentified.
    /// </summary>
    [Fact]
    public async Task TheBindingLimitReadsBackWithItsName()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "thermal-2026-09-01.csv",
                FourteenColumn
                + "\n2026-09-01T10:00:00Z,85.4,-1,36,36,90,False,Auto,SmuDieTctl,45,1.8,44.2,Temperature,99.4"
                + "\n2026-09-01T10:00:02Z,61.2,-1,12,12,28,False,Auto,SmuDieTctl,,,,,\n");

            var samples = await new TelemetryHistory(dir).ReadThermalAsync(Sep1.AddDays(-1), Sep4);

            Assert.Equal(2, samples.Count);

            Assert.Equal(44.2, samples[0].PackageWatts);
            Assert.Equal("Temperature", samples[0].Limit);
            Assert.Equal(99.4, samples[0].LimitPercent);

            // An empty limit is no reading, not a limit named "".
            Assert.Null(samples[1].PackageWatts);
            Assert.Null(samples[1].Limit);
            Assert.Null(samples[1].LimitPercent);
        }
        finally { Cleanup(dir); }
    }
}
