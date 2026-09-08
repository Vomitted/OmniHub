using System.Management;

namespace OmniHub.Core.Hardware;

public sealed class GpuController
{
    private readonly BiosInterop _bios;
    public GpuController(BiosInterop bios) => _bios = bios;

    /// <summary>
    /// How long a GPU read is reused.
    ///
    /// Short on purpose. The point is only to collapse the readers that land in the same poll
    /// tick -- the dashboard's mode panel and the re-assertion loop both hang off OnReading and
    /// arrive milliseconds apart, and each was a separate BIOS round trip queued behind the same
    /// send lock. It must stay well under the five-second re-assertion interval, because that
    /// loop exists to notice the firmware quietly dropping the GPU power ceiling, and a cache as
    /// long as its period could hand it back the value from before the drop.
    /// </summary>
    private static readonly TimeSpan ReadCacheLife = TimeSpan.FromSeconds(1);

    private readonly object _cacheLock = new();
    private GpuMode? _cachedMode;
    private DateTime _cachedModeAtUtc = DateTime.MinValue;
    private GpuPowerData? _cachedPower;
    private DateTime _cachedPowerAtUtc = DateTime.MinValue;

    /// <summary>
    /// Drops both cached reads. Called after every write here.
    ///
    /// Not optional, and not defensive. Writing a value and reading it straight back is how this
    /// application tells a setting the firmware accepted from one it merely acknowledged, and a
    /// cache that survives the write inverts that check -- the same failure that made the
    /// adaptive controller stop itself reporting locked power limits on hardware which had
    /// accepted every command it was sent.
    /// </summary>
    private void InvalidateReads()
    {
        lock (_cacheLock)
        {
            _cachedModeAtUtc = DateTime.MinValue;
            _cachedPowerAtUtc = DateTime.MinValue;
        }
    }

    /// <summary>Current hybrid/discrete/Optimus mode. Read is safe on all devices (errors reported as Hybrid).</summary>
    public GpuMode GetMode()
    {
        lock (_cacheLock)
        {
            if (_cachedMode is { } cached && DateTime.UtcNow - _cachedModeAtUtc < ReadCacheLife)
                return cached;
        }

        GpuMode mode;
        try
        {
            var data = _bios.Send(BiosCmdGroup.Legacy, SysCmd.GetGpuMode, null, 4);
            mode = (GpuMode)data[0];
        }
        catch { return GpuMode.Hybrid; }   // not cached: a failed read should be retried, not remembered

        lock (_cacheLock)
        {
            _cachedMode = mode;
            _cachedModeAtUtc = DateTime.UtcNow;
        }
        return mode;
    }

    /// <summary>
    /// Changes graphics mode. DANGEROUS: not Advanced Optimus -- takes effect only after a
    /// reboot, and switching to Discrete-only on a system without a wired-up dGPU display
    /// output can leave you without video until you boot into safe mode and revert. Confirm
    /// with the user before calling this from UI.
    /// </summary>
    public void SetMode(GpuMode mode)
    {
        _bios.Send(BiosCmdGroup.GpuMode, SysCmd.SetGpuMode, new byte[] { (byte)mode, 0, 0, 0 }, 4);
        InvalidateReads();
    }

    public GpuPowerData GetPower()
    {
        lock (_cacheLock)
        {
            if (_cachedPower is { } cached && DateTime.UtcNow - _cachedPowerAtUtc < ReadCacheLife)
                return cached;
        }

        var data = _bios.Send(BiosCmdGroup.Default, SysCmd.GetGpuPower, new byte[4], 4);
        var power = GpuPowerData.FromBytes(data);

        lock (_cacheLock)
        {
            _cachedPower = power;
            _cachedPowerAtUtc = DateTime.UtcNow;
        }
        return power;
    }

    /// <summary>
    /// While set, <see cref="SetPower"/> may not lower the discrete GPU's power ceiling.
    ///
    /// Three separate places write this register -- the dashboard's mode buttons, the Optimize
    /// tab's performance profiles, and the Tuning tab's explicit unlock -- and only the last of
    /// them knows the user asked for maximum GPU power. The other two build their payload from
    /// a GpuPowerLevel, where Balanced means Ppab off and Eco means both off, so pressing SMART
    /// BALANCED silently undid the unlock and put the card back under its 60 W stock cap.
    ///
    /// The latch lives here because this is the one function all three route through, which
    /// makes it the only place a fourth caller cannot forget to check.
    /// </summary>
    public bool ForceMaxPower { get; set; }

    public void SetPower(GpuPowerData data)
    {
        if (ForceMaxPower)
            data = new GpuPowerData(GpuCustomTgp.On, GpuPpab.On, data.DState, data.PeakTemperatureC);

        _bios.Send(BiosCmdGroup.Default, SysCmd.SetGpuPower, data.ToBytes(), 4);
        InvalidateReads();
    }

    public void SetPowerPreset(GpuPowerLevel level) => SetPower(new GpuPowerData(level));
}

public sealed class PowerController
{
    // Matches PowerView's Slider Minimum/Maximum -- kept here too as defense-in-depth so
    // this Core-layer method can't be made to send a nonsensical wattage (a raw byte is
    // 0-255) to the BIOS regardless of which caller invokes it, not just the one UI path
    // that currently happens to constrain it.
    private const byte MinWatts = 10;
    private const byte MaxWatts = 140;

    private readonly BiosInterop _bios;
    public PowerController(BiosInterop bios) => _bios = bios;

    /// <summary>Sets sustained (PL1) and matched PL2 limit, in watts, leaving PL4/concurrent untouched.</summary>
    public void SetCpuSustainedWatts(byte watts)
    {
        watts = Math.Clamp(watts, MinWatts, MaxWatts);
        var data = new CpuPowerData(watts, watts, 0, 0);
        _bios.Send(BiosCmdGroup.Default, SysCmd.SetCpuPower, data.ToBytes(), 4);
    }

    /// <summary>Sets peak/boost (PL4) limit, in watts, leaving the sustained limit untouched.</summary>
    public void SetCpuBoostWatts(byte watts)
    {
        watts = Math.Clamp(watts, MinWatts, MaxWatts);
        var data = new CpuPowerData(0, 0, watts, 0);
        _bios.Send(BiosCmdGroup.Default, SysCmd.SetCpuPower, data.ToBytes(), 4);
    }

    public void SetIdle(bool enabled) =>
        _bios.Send(BiosCmdGroup.Default, SysCmd.SetIdle,
            new byte[] { enabled ? (byte)1 : (byte)0, 0, 0, 0 }, 4);
}

/// <summary>Which sensor a temperature came from. Surfaced, because the two are not equivalent.</summary>
public enum TemperatureSource
{
    /// <summary>An ACPI thermal zone: coarse (measured 4-6C steps), laggy, and blind above ~85C.</summary>
    AcpiThermalZone,

    /// <summary>The processor's own Tctl sensor via the SMU: 0.125C resolution, no ceiling.</summary>
    SmuDieTctl,
}

/// <summary>A temperature together with the sensor that produced it.</summary>
public readonly record struct TemperatureReading(double Celsius, TemperatureSource Source)
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
        Source == TemperatureSource.AcpiThermalZone && SystemController.IsAtSensorCeiling(Celsius);
}

public sealed class SystemController
{
    private readonly BiosInterop _bios;
    private RyzenSmu? _smu;

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
    public void AttachSmu(RyzenSmu smu) => _smu = smu;

    /// <param name="smu">
    /// Optional SMU access. When present, its Tctl reading is preferred over the ACPI zone.
    /// Passing null is a supported configuration rather than a degraded one -- it is simply
    /// what happens without the PawnIO driver, and the ACPI path below still works.
    /// </param>
    public SystemController(BiosInterop bios, RyzenSmu? smu = null)
    {
        _bios = bios;
        _smu = smu;
    }

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
        double? die = _smu?.ReadDieTemperatureC();

        double zone;
        try
        {
            zone = CachedAcpiZoneC();
        }
        catch when (die is not null)
        {
            // No thermal zones, but the die sensor answered. That is still a real measurement,
            // so it is reported rather than thrown away.
            return new TemperatureReading(die.Value, TemperatureSource.SmuDieTctl);
        }

        return Merge(die, zone);
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
    public static TemperatureReading Merge(double? die, double zone)
    {
        if (die is not double tctl)
            return new TemperatureReading(zone, TemperatureSource.AcpiThermalZone);

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
        if (IsAtSensorCeiling(zone))
            return new TemperatureReading(tctl, TemperatureSource.SmuDieTctl);

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
            ? new TemperatureReading(tctl, TemperatureSource.SmuDieTctl)
            : new TemperatureReading(zone, TemperatureSource.AcpiThermalZone);
    }

    /// <summary>
    /// CPU die temperature (Tctl) alone, at full precision, or null when SMU access is
    /// unavailable. Separate from <see cref="ReadTemperature"/> because that method returns
    /// the temperature the fan should act on, which may be a different, hotter component.
    /// </summary>
    public double? ReadDieTemperatureC() => _smu?.ReadDieTemperatureC();

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
    /// </summary>
    private double ReadAcpiZoneC()
    {
        double maxCelsius = 0;
        bool sawAnyZone = false;
        using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
        foreach (ManagementObject mo in searcher.Get())
        {
            using var _ = mo;
            sawAnyZone = true;
            var raw = (uint)mo["CurrentTemperature"];
            double celsius = (raw / 10.0) - 273.15;
            if (celsius > maxCelsius) maxCelsius = celsius;
        }

        // A WMI query that returns zero rows is not "0C" -- it's a failed read. Reporting
        // 0C here would feed a false low temperature straight into the fan curve, which
        // would command a near-silent fan under a reading that was never actually taken:
        // the same class of bug (fan drops out while the real temperature is unknown/high)
        // that this whole app exists to fix on the stock BIOS side. Both callers of this
        // method (HardwareContext's poll loop and FanService's curve loop) already catch
        // and skip a failed tick rather than propagate bad data, so throwing here is safe.
        if (!sawAnyZone)
            throw new InvalidOperationException(
                "MSAcpi_ThermalZoneTemperature returned no thermal zones -- refusing to report a fabricated 0C reading.");

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
    /// true die temperature is read through the SMU when PawnIO is available (see
    /// <see cref="RyzenSmu.ReadDieTemperatureC"/>), and Tctl has no such ceiling. Check
    /// <see cref="TemperatureReading.IsCeilingLimited"/> rather than calling this directly,
    /// so a genuine 85C die reading is not mistaken for a blind sensor.
    /// </summary>
    public const double SensorCeilingC = 85.0;

    /// <summary>
    /// True when the reading has hit the zone's ceiling, so the real temperature is unknown
    /// but at least this high. Callers must treat it as a worst case, not as a number.
    /// </summary>
    public static bool IsAtSensorCeiling(double celsius) => celsius >= SensorCeilingC - 0.5;

    /// <summary>
    /// Asks the board what it supports, rather than inferring it from the model name.
    ///
    /// This is the answer to "does OmniHub work on a Pavilion / an older Omen / a Victus S".
    /// Maintaining a list of model numbers by hand is how that question gets answered wrongly:
    /// HP ships the same interface across families with different capability bits set, and the
    /// firmware will simply say which. In particular the software-fan-control bit decides
    /// whether the safety floor can work at all on a given board.
    ///
    /// Null when the command is not implemented or returns a short reply -- which is itself
    /// information, and is reported as "not available" rather than defaulted to something
    /// optimistic.
    /// </summary>
    public HpSystemData? ReadSystemData()
    {
        // No payload, not a four-byte zero buffer. Asked with a payload this command replies
        // with every capability byte clear, which decodes into "this machine supports nothing"
        // -- on a machine whose fans this application is demonstrably driving. HP's own
        // software omits the payload here; see BiosInterop.SendWithoutPayload.
        try { return HpSystemData.Parse(_bios.SendWithoutPayload(BiosCmdGroup.Default, SysCmd.GetSystemData, 128)); }
        catch { return null; }
    }

    /// <summary>
    /// Smart power adapter state. Worth surfacing because "below requirement" is a real and
    /// commonly misdiagnosed cause of a laptop refusing to boost: the machine is not throttling
    /// for heat, it is being fed by an underpowered charger.
    /// </summary>
    public HpAdapterStatus? ReadAdapterStatus()
    {
        try
        {
            var data = _bios.Send(BiosCmdGroup.Legacy, SysCmd.GetAdapter, new byte[4], 4);
            return data is { Length: > 0 } ? (HpAdapterStatus)data[0] : null;
        }
        catch { return null; }
    }

    /// <summary>Keyboard layout, which also indicates whether per-key colour is possible.</summary>
    public HpKeyboardType? ReadKeyboardType()
    {
        try
        {
            var data = _bios.Send(BiosCmdGroup.Default, SysCmd.GetKeyboardType, new byte[4], 4);
            return data is { Length: > 0 } ? (HpKeyboardType)data[0] : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether this board has a controllable keyboard backlight.
    ///
    /// Checked before any backlight feature is offered. A value of 0x03 is the documented
    /// "no support" reply; null means the query itself failed, which is a different thing and
    /// is not treated as a no.
    /// </summary>
    public bool? HasKeyboardBacklight()
    {
        try
        {
            var data = _bios.Send(BiosCmdGroup.Keyboard, SysCmd.HasBacklight, new byte[4], 4);
            return data is { Length: > 0 } ? data[0] != 0x03 : null;
        }
        catch { return null; }
    }

    public bool GetMaxFanActive()
    {
        var data = _bios.Send(BiosCmdGroup.Default, SysCmd.GetMaxFan, new byte[4], 4);
        return (data[0] & 0x01) != 0;
    }

    public void SetMaxFan(bool enabled) =>
        _bios.Send(BiosCmdGroup.Default, SysCmd.SetMaxFan, new byte[] { enabled ? (byte)1 : (byte)0, 0, 0, 0 }, 4);

    /// <summary>
    /// UNVERIFIED on the Victus 15 fb2xxx: a diagnostic sweep of GetCapability's second
    /// input byte showed the response simply echoing that byte back at data[1] for every
    /// value tried, which is exactly the position this method reads. That means the
    /// "Default" throttling state seen so far may just be an artifact of sending selector
    /// byte 4 (which happens to equal ThrottlingState.Default's numeric value) rather than
    /// real hardware state -- treat this reading with real skepticism until cross-checked
    /// (e.g. against actual observed clock-speed drops) on real hardware.
    /// </summary>
    public ThrottlingState GetThrottling()
    {
        try
        {
            var data = _bios.Send(BiosCmdGroup.Default, SysCmd.GetCapability, new byte[] { 0, 4, 0, 0 }, 128);
            return (ThrottlingState)data[1];
        }
        catch { return ThrottlingState.Unknown; }
    }
}
