// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// The rule that "at the sensor ceiling" is a property of the SENSOR and not of the number.
///
/// This distinction is the whole point of reading Tctl. The ACPI thermal zone pins at ~85C
/// and reports that same value however much hotter the die actually gets, so a reading there
/// is a floor rather than a measurement and the only safe response is maximum airflow. Tctl
/// has no such ceiling, so an 85C reading from it is simply 85C.
///
/// Getting this backwards is expensive in both directions: treating a blind ACPI reading as
/// real under-cools a machine that could be at 95C, and treating a genuine 85C die reading as
/// blind pins the fans to 100% for no reason at all.
/// </summary>
public class SmuTemperatureTests
{
    [Fact]
    public void AcpiReadingAtTheCeiling_IsFlaggedAsCeilingLimited()
    {
        var reading = new TemperatureReading(ThermalReader.SensorCeilingC, TemperatureSource.AcpiThermalZone);
        Assert.True(reading.IsCeilingLimited);
    }

    /// <summary>
    /// The boot case, taken straight from the thermal log: the zone sitting on its ceiling at
    /// 86.1 C while Tctl read 79.1 C. Reporting the zone there made the reading ceiling-limited
    /// and forced both fans to 100% for the first eight seconds of every boot.
    /// </summary>
    [Fact]
    public void SaturatedZone_DoesNotOutvoteTctl()
    {
        var reading = ThermalReader.Merge(die: 79.1, zone: 86.1);

        Assert.Equal(TemperatureSource.SmuDieTctl, reading.Source);
        Assert.Equal(79.1, reading.Celsius, 3);
        Assert.False(reading.IsCeilingLimited);
    }

    /// <summary>
    /// The zone keeps its vote while it is still measuring. Measured on this machine: Tctl
    /// 73.25 C against a zone reading 82.0 C at the same instant, which is real coverage of
    /// something the die sensor cannot see.
    /// </summary>
    [Fact]
    public void InRangeZone_StillWinsWhenItIsHotter()
    {
        var reading = ThermalReader.Merge(die: 73.25, zone: 82.0);

        Assert.Equal(TemperatureSource.AcpiThermalZone, reading.Source);
        Assert.Equal(82.0, reading.Celsius, 3);
    }

    /// <summary>
    /// Without SMU access the zone is all there is, so a saturated reading must still come
    /// back flagged -- that is the one situation where forcing maximum airflow is the only
    /// honest response.
    /// </summary>
    [Fact]
    public void SaturatedZone_WithNoTctl_StaysCeilingLimited()
    {
        var reading = ThermalReader.Merge(die: null, zone: 86.1);

        Assert.Equal(TemperatureSource.AcpiThermalZone, reading.Source);
        Assert.True(reading.IsCeilingLimited);
    }

    /// <summary>
    /// The case that motivated the change. Same number, different sensor, opposite meaning.
    /// </summary>
    [Fact]
    public void DieReadingAtTheSameTemperature_IsNotCeilingLimited()
    {
        var reading = new TemperatureReading(ThermalReader.SensorCeilingC, TemperatureSource.SmuDieTctl);
        Assert.False(reading.IsCeilingLimited);
    }

    [Fact]
    public void DieReadingWellAboveTheAcpiCeiling_IsStillNotCeilingLimited()
    {
        // Tctl can report values the ACPI zone simply cannot express. That is the headroom
        // this whole change exists to gain, so it must not be mistaken for a failed sensor.
        var reading = new TemperatureReading(97.5, TemperatureSource.SmuDieTctl);
        Assert.False(reading.IsCeilingLimited);
    }

    [Fact]
    public void AcpiReadingBelowTheCeiling_IsNotFlagged()
    {
        var reading = new TemperatureReading(72.0, TemperatureSource.AcpiThermalZone);
        Assert.False(reading.IsCeilingLimited);
    }

    /// <summary>
    /// The ceiling measured by driving the fans directly: the readback tracks the command up
    /// to 54 and pins at 56 for anything above. Not 55 (borrowed from OmenMon) and not 54
    /// (which was HP's max-fan policy, not the hardware limit).
    /// </summary>
    [Fact]
    public void MeasuredFanCeiling_MapsToFullScale()
    {
        Assert.Equal(100, FanCalibration.Default.RawToPercent(56));
    }

    /// <summary>
    /// The regression this guards: 54 was mistaken for the ceiling, which capped full speed
    /// about 200 RPM short. It must now read as below full scale, not at it.
    /// </summary>
    [Fact]
    public void OldMistakenCeiling_IsNoLongerFullScale()
    {
        Assert.True(FanCalibration.Default.RawToPercent(54) < 100);
    }

    [Fact]
    public void RawToRpm_IsTheDocumentedHundredFoldTarget()
    {
        // Raw is an RPM/100 target, not a 0-255 PWM duty cycle.
        Assert.Equal(5600, FanCalibration.Default.RawToRpm(56));
        Assert.Equal(0, FanCalibration.Default.RawToRpm(0));
    }
}
