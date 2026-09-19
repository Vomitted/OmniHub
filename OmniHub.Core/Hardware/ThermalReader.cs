// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
#if WINDOWS
using System.Management;
#endif

namespace OmniHub.Core.Hardware;

/// <summary>
/// The temperature the fan curve acts on, from whichever sensor this machine has.
///
/// Lifted out of SystemController, which was two classes sharing a name: six HP WMI commands on
/// one side, and on the other a standard Windows ACPI query, an AMD Tctl read and the arbitration
/// between them -- none of which has anything to do with HP. That only mattered once a second
/// vendor became the point: a Lenovo or a Dell has no hpqBIntM and every one of them has thermal
/// zones, so the half that works everywhere was locked inside the half that works on one brand.
///
/// Nothing here was rewritten in the move. The comments are the measurements that produced these
/// decisions and they are worth more than the code they sit above -- particularly the two that
/// were each a reported bug before they were a rule.
/// </summary>
public sealed class ThermalReader
{
    private Func<double?>? _die;

    /// <param name="dieTemperatureC">
    /// Optional access to a real die sensor, in Celsius, returning null when it has nothing
    /// trustworthy to say. When present its reading is preferred over the ACPI zone.
    ///
    /// A delegate rather than the SMU type it used to take. Three sensors now fill this slot and
    /// they have nothing in common but a number: AMD's Tctl through PawnIO on Windows, the same
    /// Tctl through the k10temp driver on Linux, and Intel's DTS. Naming one of them here made
    /// the whole cooling loop depend on a Windows kernel driver in order to compile, which is
    /// the coupling the vendor seam exists to break.
    ///
    /// Passing null is a supported configuration rather than a degraded one -- it is what
    /// happens with no driver at all, and the ACPI path below still works.
    /// </param>
    public ThermalReader(Func<double?>? dieTemperatureC = null) => _die = dieTemperatureC;

    /// <summary>
    /// Hands over an SMU that opened after construction.
    ///
    /// Exists because PawnIO's service ships as Manual start, so at boot OmniHub is running
    /// before the driver is, the one open attempt in HardwareContext's constructor fails, and
    /// there was no second chance -- the whole session then ran on the ACPI zone alone.
    /// Measured: a 3567-row thermal log with not one fractional temperature in it, meaning
    /// every reading for that entire session came from the zone, which pins at 85 C and so
    /// held both fans at 100% from launch to shutdown.
    /// </summary>
    public void AttachDieSensor(Func<double?> dieTemperatureC) => _die = dieTemperatureC;

    /// <summary>
    /// Temperature from the best sensor available, tagged with which one that was.
    ///
    /// Tctl is preferred, and the difference is not academic. Measured side by side on this
    /// machine while the fans ramped: Tctl fell smoothly from 69.75C to 60.38C in 0.125C
    /// steps, while the ACPI zone jumped 82 -> 77 and then sat at exactly 77.0 for the next
    /// ten seconds. The zone is quantised, lags badly, and pins at ~85C however much hotter
    /// the die gets -- which is precisely where a fan curve has to work, and is the direct
    /// cause of the reported "temp stopped at 82 degrees".
    ///
    /// The fallback is not a formality either. ReadDieTemperatureC returns null rather than a
    /// number whenever the decode looks implausible, so a machine without PawnIO, without the
    /// RyzenSMU module, or with a firmware this does not understand quietly keeps using the
    /// ACPI zone instead of reporting something invented.
    /// </summary>
    public TemperatureReading ReadTemperature()
    {
        double? die = _die?.Invoke();

        double zone;
        try
        {
            zone = CachedAcpiZoneC();
        }
        catch when (die is not null)
        {
            // No thermal zones, but the die sensor answered. That is still a real measurement,
            // so it is reported rather than thrown away.
            return new TemperatureReading(die.Value, TemperatureSource.SmuDieTctl, ZoneCeilingC);
        }

        return Merge(die, zone, ZoneCeilingC);
    }

    /// <summary>
    /// Picks which of the two sensors to report.
    ///
    /// Static and pure, separate from the hardware read, because this branch decides the
    /// temperature the fan curve acts on and it has already been wrong once in a way nothing
    /// could catch without a machine to boot.
    /// </summary>
    /// <param name="die">Tctl in C, or null when there is no SMU access.</param>
    /// <param name="zone">The ACPI thermal zone reading in C.</param>
    /// <param name="zoneCeilingC">Where this machine's zone saturates. See <see cref="ZoneCeilingC"/>.</param>
    public static TemperatureReading Merge(double? die, double zone, double zoneCeilingC = DefaultZoneCeilingC)
    {
        if (die is not double tctl)
            return new TemperatureReading(zone, TemperatureSource.AcpiThermalZone, zoneCeilingC);

        // A SATURATED zone reading cannot take part in the comparison below, because it is not
        // a number. The zone pins at its ceiling and reports that same value however much
        // hotter the machine gets, so 86.1 there means ">= 85" and nothing more. Letting it
        // outvote a real Tctl reading handed the control temperature to a sensor that had
        // stopped measuring, and marked the result ceiling-limited -- which forces 100% fan.
        //
        // Measured at every boot, from the thermal log: four consecutive ticks of exactly
        // 86.1 C with both fans commanded to maximum, until the cached zone value dropped back
        // into range and Tctl took over at 79.1 C. That was the "high temps and high fan right
        // after boot" report, and it was entirely this comparison.
        //
        // Tctl has no ceiling, so it is the better estimate precisely in the region where the
        // zone has stopped being one. With no SMU at all the branch above still returns the
        // zone reading with its ceiling flag intact, so the safety response survives exactly
        // where it is the only thing available.
        if (IsAtCeiling(zone, zoneCeilingC))
            return new TemperatureReading(tctl, TemperatureSource.SmuDieTctl, zoneCeilingC);

        // Otherwise the HIGHER of the two, deliberately, rather than simply preferring Tctl.
        //
        // These sensors do not measure the same thing. Tctl is the CPU die alone; the ACPI
        // reading is the maximum across every zone the platform exposes, which can include
        // parts of the machine the die sensor knows nothing about. Measured here: Tctl 73.25C
        // against an ACPI zone reading 82.0C at the same instant.
        //
        // Preferring Tctl outright would therefore have made the fan QUIETER at identical
        // conditions -- roughly 3500 RPM where the curve had been commanding 4200 -- because
        // it would have stopped seeing whatever the hotter zone was tracking. Taking the max
        // means the control temperature is never below what the old ACPI-only path would have
        // produced, while Tctl supplies the resolution and the headroom above 85C.
        return tctl >= zone
            ? new TemperatureReading(tctl, TemperatureSource.SmuDieTctl, zoneCeilingC)
            : new TemperatureReading(zone, TemperatureSource.AcpiThermalZone, zoneCeilingC);
    }

    private double _cachedZoneC;
    private DateTime _cachedZoneAtUtc = DateTime.MinValue;

    /// <summary>How long an ACPI zone reading is reused before the WMI query is repeated.</summary>
    private static readonly TimeSpan ZoneCacheLife = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The ACPI zone reading, refreshed at most every few seconds.
    ///
    /// A ManagementObjectSearcher round trip is one of the most expensive things this app does
    /// per tick, and once Tctl is available the zone is no longer the primary sensor -- it is
    /// there so the control temperature never drops below what the old ACPI-only path would
    /// have produced, and to catch a component the die sensor cannot see. Neither of those
    /// jobs needs half-second freshness from a sensor that only moves in 4-6C steps anyway.
    ///
    /// Tctl is still read on every call, so the fan curve keeps its full responsiveness on the
    /// sensor that actually resolves quickly.
    /// </summary>
    private double CachedAcpiZoneC()
    {
        if (DateTime.UtcNow - _cachedZoneAtUtc < ZoneCacheLife) return _cachedZoneC;

        double zone = ReadAcpiZoneC();
        _cachedZoneC = zone;
        _cachedZoneAtUtc = DateTime.UtcNow;
        return zone;
    }

    /// <summary>Whole-degree temperature from the best sensor available.</summary>
    public byte GetTemperatureC() =>
        (byte)Math.Clamp(Math.Round(ReadTemperature().Celsius), 0, 255);

    /// <summary>
    /// Highest reading across the standard Windows ACPI thermal zones (root\wmi
    /// MSAcpi_ThermalZoneTemperature, tenths of Kelvin). This replaces an earlier
    /// attempt to read temperature via hpqBIntM CommandType 0x23 -- that command
    /// exists on other Omen/Victus firmware revisions per community documentation,
    /// but on this exact machine it returns all-zero regardless of input (confirmed
    /// via direct sweep), so it's not wired up on this BIOS revision. ACPI thermal
    /// zones are a documented, non-reverse-engineered interface and read real values
    /// here (confirmed against this hardware: ~63C on the hot zone vs ~20C ambient
    /// on a second, presumably unrelated zone) -- using the max is the safe choice
    /// for fan-control purposes even if a given zone's relevance varies by model.
    ///
    /// It is also the one temperature source needing no vendor interface, no driver and no
    /// particular CPU, which is why it is the floor every machine gets.
    /// </summary>
    private double ReadAcpiZoneC()
    {
        double maxCelsius = 0;
        bool sawAnyZone = false;
#if WINDOWS
        using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
        foreach (ManagementObject mo in searcher.Get())
        {
            using var _ = mo;
            sawAnyZone = true;
            var raw = (uint)mo["CurrentTemperature"];
            double celsius = (raw / 10.0) - 273.15;
            if (celsius > maxCelsius) maxCelsius = celsius;
        }

#else
        // The same ACPI zones, through the kernel's thermal class instead of through WMI.
        //
        // /sys/class/thermal/thermal_zone*/temp is millidegrees Celsius rather than tenths of a
        // Kelvin, and underneath it is the same firmware object: Linux exposes _TMP directly
        // where Windows wraps it in MSAcpi_ThermalZoneTemperature. The maximum across zones is
        // taken for the reason given above, not because any one zone has been identified.
        //
        // A zone that will not answer is skipped rather than counted as cold. Some platforms
        // publish a zone whose read returns an error, and treating that as 0 C would be exactly
        // the fabricated low temperature the branch below exists to refuse.
        foreach (string dir in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*"))
        {
            try
            {
                if (!long.TryParse(File.ReadAllText(Path.Combine(dir, "temp")).Trim(),
                                   NumberStyles.Integer, CultureInfo.InvariantCulture, out long milli))
                    continue;

                sawAnyZone = true;
                double celsius = milli / 1000.0;
                if (celsius > maxCelsius) maxCelsius = celsius;
            }
            catch (IOException) { /* not a zone reading 0 C -- a zone that did not read */ }
            catch (UnauthorizedAccessException) { }
        }
#endif

        // A WMI query that returns zero rows is not "0C" -- it's a failed read. Reporting
        // 0C here would feed a false low temperature straight into the fan curve, which
        // would command a near-silent fan under a reading that was never actually taken:
        // the same class of bug (fan drops out while the real temperature is unknown/high)
        // that this whole app exists to fix on the stock BIOS side. Both callers of this
        // method (HardwareContext's poll loop and FanService's curve loop) already catch
        // and skip a failed tick rather than propagate bad data, so throwing here is safe.
        if (!sawAnyZone)
            throw new InvalidOperationException(
#if WINDOWS
                "MSAcpi_ThermalZoneTemperature returned no thermal zones -- refusing to report a fabricated 0C reading.");
#else
                "/sys/class/thermal exposed no readable thermal zone -- refusing to report a fabricated 0C reading.");
#endif

        // Returned at full precision. Rounding belongs to whoever is displaying it, not here:
        // the curve evaluates against a double, and throwing away the fraction at the source
        // is how a sensor that is already coarse gets coarser.
        return maxCelsius;
    }

    /// <summary>
    /// The highest value this platform's ACPI thermal zone will report.
    ///
    /// Measured on this machine, not assumed: under sustained 100% load the THRM_0 zone climbs
    /// 75.1 -> 81.1 -> 85.1C and then holds at exactly 85.05C (raw 3582) indefinitely. It is
    /// also coarsely quantised, stepping 4-6C at a time.
    ///
    /// This matters far more than a display glitch. The zone is blind above ~85C, which is
    /// precisely where fan control has to work, so a reading sitting on the ceiling means "at
    /// least this hot, possibly much hotter" and must never be treated as a measurement of
    /// 85C.
    ///
    /// This applies to the ACPI path ONLY, and is now the fallback rather than the norm: the
    /// true die temperature is read from whatever die sensor is attached -- Tctl through
    /// PawnIO on Windows, the same Tctl through k10temp on Linux -- and Tctl has no such
    /// ceiling. Check
    /// <see cref="TemperatureReading.IsCeilingLimited"/> rather than calling this directly,
    /// so a genuine 85C die reading is not mistaken for a blind sensor.
    ///
    /// This is the figure for THIS chassis, and it is only a default. Where a zone saturates is
    /// a property of the platform's firmware, not of thermal physics, so see
    /// <see cref="ZoneCeilingC"/> for the value actually in force.
    /// </summary>
    public const double DefaultZoneCeilingC = 85.0;

    /// <summary>
    /// Where this machine's ACPI zone stops measuring, in Celsius.
    ///
    /// It was a constant until support widened past one laptop, and a constant is the wrong
    /// shape for it in a way that is not cosmetic. This number drives the five-tick
    /// maximum-fan override: above it, a reading stops being a temperature and becomes "at
    /// least this hot", and the only safe answer to not knowing is full airflow.
    ///
    /// So a ceiling belonging to somebody else's laptop breaks that safety net in both
    /// directions. Set too low for a machine whose zone reports honestly past it, every
    /// ordinary 85 C gaming session pins both fans at maximum and never lets them down -- the
    /// noisy twin of the stopped-fan bug this application exists to fix. Set too high for a
    /// machine that saturates at 80, the override never fires at all and a blind sensor is
    /// treated as a measurement.
    ///
    /// <see cref="DefaultZoneCeilingC"/> is what stands until somebody measures their own
    /// board and records it in that board's profile, which is the same per-baseboard file the
    /// fan band already lives in. Measuring it needs the laptop, which is exactly why this is
    /// a value a stranger can supply rather than one this project has to guess for them.
    /// </summary>
    public double ZoneCeilingC { get; set; } = DefaultZoneCeilingC;

    /// <summary>
    /// True when the reading has hit the zone's ceiling, so the real temperature is unknown
    /// but at least this high. Callers must treat it as a worst case, not as a number.
    ///
    /// Takes the ceiling rather than reading a constant, so that no caller can accidentally
    /// ask the question about a machine other than the one in front of it.
    /// </summary>
    public static bool IsAtCeiling(double celsius, double zoneCeilingC) => celsius >= zoneCeilingC - 0.5;
}
