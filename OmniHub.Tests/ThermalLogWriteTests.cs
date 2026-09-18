// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// A fan level nobody reported must not reach the trace as a zero.
///
/// This is the contract the whole file exists for. A zero in fan1_raw means a fan is stopped,
/// and a stopped fan on a hot machine is the precise fault the fan curve was written to prevent
/// -- so it is the one value in the log that must never be manufactured. It was being
/// manufactured: the vendor call pads its reply to a fixed buffer size, a board answering with
/// fewer bytes left zeroes behind, and every layer above passed them along as readings.
///
/// The tests write into a temporary directory of their own. Checking this against the real trace
/// would mean writing invented rows into the user's telemetry, which is the same category of
/// mistake in a different costume.
/// </summary>
public class ThermalLogWriteTests
{
    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-log-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir) { try { Directory.Delete(dir, true); } catch { } }

    private static readonly DateTime At = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The headline: a null level writes an empty field, a real zero writes a zero.
    ///
    /// Both halves matter. Writing the null as 0 invents a stopped fan; suppressing a genuine 0
    /// hides one, which is the same failure pointed the other way.
    /// </summary>
    [Fact]
    public void AnUnreportedLevelIsAnEmptyFieldAndARealZeroIsAZero()
    {
        string dir = NewDir();
        try
        {
            using (var log = new ThermalLog(dir))
            {
                log.Append(At, 70.5, -1, fan1Raw: null, fan2Raw: null, 45, false, "Auto", "SmuDieTctl");
                log.Append(At.AddSeconds(2), 70.5, -1, fan1Raw: 27, fan2Raw: 0, 45, false, "Auto", "SmuDieTctl",
                           gpuTempC: 61, gpuWatts: 0,
                           packageWatts: 44.2, limit: "Temperature", limitPercent: 99.4);
            }

            string[] lines = File.ReadAllLines(Directory.GetFiles(dir, "thermal-*.csv").Single());

            // header, then the two rows
            // The GPU columns follow the same rule: a card that did not answer writes empty, and
            // a card genuinely drawing no measurable power writes a zero. Collapsing the two would
            // put an idle card and an absent one in the same bucket.
            Assert.Equal("2026-09-17T12:00:00Z,70.5,-1,,,45,False,Auto,SmuDieTctl,,,,,", lines[1]);
            Assert.Equal("2026-09-17T12:00:02Z,70.5,-1,27,0,45,False,Auto,SmuDieTctl,61,0,44.2,Temperature,99.4", lines[2]);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// One fan reported and the other not is the shape this machine actually produced.
    ///
    /// Fan 1 at 27 with fan 2 blank, for minutes at a time, while the die sat at 75 C. Written
    /// the old way that row read "27,0" -- a working processor fan beside a dead graphics fan,
    /// which is a far more alarming claim than the truth and was never a reading at all.
    /// </summary>
    [Fact]
    public void OneFanReportedAndTheOtherNotIsWrittenAsSuch()
    {
        string dir = NewDir();
        try
        {
            using (var log = new ThermalLog(dir))
                log.Append(At, 75, -1, fan1Raw: 27, fan2Raw: null, 47, false, "Auto", "SmuDieTctl");

            string row = File.ReadAllLines(Directory.GetFiles(dir, "thermal-*.csv").Single())[1];

            Assert.Contains(",27,,", row);
            Assert.DoesNotContain(",27,0,", row);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The round trip: what the writer leaves blank, the reader gives back as null.
    ///
    /// The two halves were written months apart and never checked against each other. The reader
    /// has always modelled these columns as nullable; the writer was the side that could not
    /// express it.
    /// </summary>
    [Fact]
    public async Task AnEmptyFieldReadsBackAsNullAndAZeroReadsBackAsZero()
    {
        string dir = NewDir();
        try
        {
            using (var log = new ThermalLog(dir))
            {
                log.Append(At, 70.5, -1, null, null, 45, false, "Auto", "SmuDieTctl");
                log.Append(At.AddSeconds(2), 70.5, -1, 27, 0, 45, false, "Auto", "SmuDieTctl");
            }

            var samples = await new TelemetryHistory(dir)
                .ReadThermalAsync(At.AddMinutes(-1), At.AddMinutes(1));

            Assert.Equal(2, samples.Count);

            Assert.Null(samples[0].Fan1Raw);
            Assert.Null(samples[0].Fan2Raw);

            Assert.Equal((byte)27, samples[1].Fan1Raw);
            Assert.Equal((byte)0, samples[1].Fan2Raw);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The display helpers say "unavailable", not "stopped".
    ///
    /// They exist so that three views cannot each invent their own <c>raw ?? 0</c>, which would
    /// reintroduce the same defect one screen at a time.
    /// </summary>
    [Fact]
    public void TheDisplayHelpersDistinguishUnavailableFromStopped()
    {
        Assert.Equal("--", FanCalibration.Default.RpmText(null));
        Assert.Equal("--", FanService.RawText(null));

        Assert.Equal("0", FanCalibration.Default.RpmText(0));
        Assert.Equal("0", FanService.RawText(0));

        Assert.Equal("2700", FanCalibration.Default.RpmText(27));
        Assert.Equal("27", FanService.RawText(27));
    }
}
