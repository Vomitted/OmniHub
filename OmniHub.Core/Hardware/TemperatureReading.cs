// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

// Lifted out of BiosCommands.cs, which is six HP WMI commands and needs System.Management to
// compile at all. These two types have nothing to do with HP or with WMI -- they are a number
// and the name of the sensor that produced it -- and the fan curve, the thermal log and every
// consumer of a reading depend on them. Leaving them in that file meant the whole cooling loop
// could only be built on Windows, which is the same coupling the vendor seam was made to break,
// one layer further down.

/// <summary>Which sensor a temperature came from. Surfaced, because the two are not equivalent.</summary>
public enum TemperatureSource
{
    /// <summary>An ACPI thermal zone: coarse (measured 4-6C steps), laggy, and blind above ~85C.</summary>
    AcpiThermalZone,

    /// <summary>The processor's own Tctl sensor via the SMU: 0.125C resolution, no ceiling.</summary>
    SmuDieTctl,
}

/// <summary>A temperature together with the sensor that produced it.</summary>
/// <param name="Celsius">The reading.</param>
/// <param name="Source">Which sensor produced it.</param>
/// <param name="ZoneCeilingC">
/// Where the ACPI zone on the machine this was read from stops measuring.
///
/// Carried on the reading rather than looked up, because whether a number is a temperature or
/// a floor is a fact about the sensor that produced it, and a reading that travelled to a
/// dashboard should not have to find its way back to the reader to answer that. It defaults to
/// this chassis's measured figure so that every existing construction site keeps the behaviour
/// it had; <see cref="ThermalReader.ZoneCeilingC"/> supplies the real one.
/// </param>
public readonly record struct TemperatureReading(
    double Celsius,
    TemperatureSource Source,
    double ZoneCeilingC = ThermalReader.DefaultZoneCeilingC)
{
    /// <summary>
    /// True when this reading is sitting on the ACPI zone's ceiling, meaning the real
    /// temperature is unknown but at least this high.
    ///
    /// Never true for a Tctl reading. That sensor has no such ceiling, which is the entire
    /// reason for preferring it -- and treating a genuine 85C die reading as "blind" would
    /// pin the fan to maximum for no reason.
    /// </summary>
    public bool IsCeilingLimited =>
        Source == TemperatureSource.AcpiThermalZone && ThermalReader.IsAtCeiling(Celsius, ZoneCeilingC);
}
