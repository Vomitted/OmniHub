// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Where a thermal zone stops measuring is a fact about one machine's firmware.
///
/// It was a constant -- 85.0, measured on the board this project was written on -- and it decides
/// whether a reading is a temperature or a floor. Above it the fan loop stops believing the
/// sensor and commands maximum airflow, which is the right answer to not knowing and the wrong
/// answer to knowing perfectly well that the die is at 85 C.
///
/// So the constant is a safety net aimed at one chassis, and pointing it at a different one
/// breaks it in whichever direction the guess was wrong. Too low, and every ordinary hot
/// afternoon pins both fans at maximum with no way down -- the noisy twin of the stopped-fan
/// fault this application exists to fix. Too high, and a genuinely blind sensor is treated as a
/// measurement and the override never fires.
///
/// These tests are the reason the value is now per-board rather than per-project.
/// </summary>
public class ZoneCeilingTests
{
    [Fact]
    public void TheSameReadingIsBlindOnOneMachineAndFineOnAnother()
    {
        // 85 C from the zone on this chassis means "at least 85, and the sensor has stopped
        // counting". The identical number on a machine whose zone reports honestly to 100 is an
        // ordinary hot reading, and treating it as blind would hold that laptop's fans at
        // maximum for the rest of the session.
        var here = new TemperatureReading(85.0, TemperatureSource.AcpiThermalZone);
        var elsewhere = new TemperatureReading(85.0, TemperatureSource.AcpiThermalZone, 100.0);

        Assert.True(here.IsCeilingLimited);
        Assert.False(elsewhere.IsCeilingLimited, "another machine's ceiling was applied to this reading");

        // And the machine with the higher ceiling still has one.
        Assert.True(new TemperatureReading(100.0, TemperatureSource.AcpiThermalZone, 100.0).IsCeilingLimited);
    }

    [Fact]
    public void TheCeilingDecidesWhichSensorTheFanCurveActsOn()
    {
        // Merge's saturated-zone branch is the one place the ceiling changes which sensor wins,
        // rather than only how a reading is labelled. Same two numbers, two machines:
        //
        //   ceiling 85  -- the zone at 86 has stopped measuring, so Tctl's 79.1 is the estimate
        //   ceiling 100 -- the zone at 86 is a real reading of something Tctl cannot see, and the
        //                  higher of the two is taken, as it is everywhere else
        var saturated = ThermalReader.Merge(die: 79.1, zone: 86.0, zoneCeilingC: 85.0);
        var honest = ThermalReader.Merge(die: 79.1, zone: 86.0, zoneCeilingC: 100.0);

        Assert.Equal(TemperatureSource.SmuDieTctl, saturated.Source);
        Assert.Equal(79.1, saturated.Celsius, 3);

        Assert.Equal(TemperatureSource.AcpiThermalZone, honest.Source);
        Assert.Equal(86.0, honest.Celsius, 3);
    }

    [Fact]
    public async Task TheMaximumFanOverrideFollowsTheMachineItIsRunningOn()
    {
        // The end of the chain, and the only part a user feels. A zone reading of 86 forces both
        // fans to maximum on a board that saturates at 85, and must not on one that does not.
        var blindHere = new ScriptedFanBackend();
        using var here = Loop(blindHere, () => new TemperatureReading(86.0, TemperatureSource.AcpiThermalZone));

        here.Start();
        Assert.True(await Settle(() => here.SensorCeilingReached), "a blind sensor never forced the fans up");
        here.Stop();

        var fineElsewhere = new ScriptedFanBackend();
        using var elsewhere = Loop(fineElsewhere, () => new TemperatureReading(86.0, TemperatureSource.AcpiThermalZone, 100.0));

        elsewhere.Start();
        Assert.True(await Settle(() => elsewhere.HasCommanded));
        await Task.Delay(150);
        elsewhere.Stop();

        Assert.False(elsewhere.SensorCeilingReached,
                     "a machine whose zone reads honestly past 86 C had its fans pinned by another board's ceiling");
    }

    [Fact]
    public void AMeasuredCeilingIsReadFromTheBoardsOwnProfile()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "TEST-BOARD", "{ \"baseboard\": \"TEST-BOARD\", \"zoneCeilingC\": 98.5 }");
            Assert.Equal(98.5, FanProfiles.LoadZoneCeilingC(Model("TEST-BOARD"), dir));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AProfileSilentAboutTheCeilingIsNotAClaimThatItIsTheDefault()
    {
        // Absent and default-valued have to stay distinguishable: one means nobody has measured
        // this board, the other would mean somebody measured it and got this chassis's number.
        string dir = NewDir();
        try
        {
            Write(dir, "TEST-BOARD",
                  "{ \"baseboard\": \"TEST-BOARD\", \"minRawLevel\": 10, \"maxRawLevelFan1\": 56, \"maxRawLevelFan2\": 56 }");
            Assert.Null(FanProfiles.LoadZoneCeilingC(Model("TEST-BOARD"), dir));
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData(20.0)]   // a room temperature, or somebody's idea of a safe limit
    [InlineData(358.2)]  // tenths of a Kelvin, the unit the zone itself reports in
    public void AnImplausibleCeilingIsRefusedRatherThanPulledIntoRange(double claimed)
    {
        // Refused, not clamped -- the same rule the embedded-controller register map follows.
        // Clamping 20 up to 60 would leave a file saying one thing and the machine doing another,
        // and a number nobody chose is a worse place to land than the measured default.
        string dir = NewDir();
        try
        {
            Write(dir, "TEST-BOARD",
                  "{ \"baseboard\": \"TEST-BOARD\", \"zoneCeilingC\": "
                  + claimed.ToString(CultureInfo.InvariantCulture) + " }");
            Assert.Null(FanProfiles.LoadZoneCeilingC(Model("TEST-BOARD"), dir));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void AnUnusableFanBandDoesNotThrowAwayTheCeilingBesideIt()
    {
        // The reason these are read separately rather than folded into one call. A fan scale that
        // would collapse every percentage onto one speed is rejected, and rightly -- but the
        // sensor in the same laptop is still saturating where it always did, and falling back to
        // another board's figure because the fan numbers were wrong would be a second fault
        // caused by the first.
        string dir = NewDir();
        try
        {
            Write(dir, "TEST-BOARD",
                  "{ \"baseboard\": \"TEST-BOARD\", \"minRawLevel\": 90, \"maxRawLevelFan1\": 10, "
                  + "\"maxRawLevelFan2\": 10, \"zoneCeilingC\": 92.0 }");

            Assert.Null(FanProfiles.Load(Model("TEST-BOARD"), dir));
            Assert.Equal(92.0, FanProfiles.LoadZoneCeilingC(Model("TEST-BOARD"), dir));
        }
        finally { Cleanup(dir); }
    }

    private static FanService Loop(ScriptedFanBackend fan, Func<TemperatureReading> read) =>
        new(fan, read, FanCurve.CreateDefault(), TimeSpan.FromMilliseconds(5));

    private static ModelInfo Model(string baseboard) => new("Test", "Test Laptop", baseboard);

    private static void Write(string dir, string baseboard, string json) =>
        File.WriteAllText(Path.Combine(dir, baseboard + ".json"), json);

    private static string NewDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-ceiling-" + Guid.NewGuid().ToString("N"))).FullName;

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* a leftover temp folder is not a test failure */ }
    }

    private static async Task<bool> Settle(Func<bool> until, int millis = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return true;
            await Task.Delay(5);
        }
        return until();
    }
}
