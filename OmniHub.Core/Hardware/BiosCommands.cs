// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

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
            var data = _bios.SendAtLeast(BiosCmdGroup.Legacy, SysCmd.GetGpuMode, null, 4, needed: 1);
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

        // All four bytes are read by FromBytes, so all four have to be the board's.
        var data = _bios.SendAtLeast(BiosCmdGroup.Default, SysCmd.GetGpuPower, new byte[4], 4, needed: 4);
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
        Source == TemperatureSource.AcpiThermalZone && ThermalReader.IsAtSensorCeiling(Celsius);
}

public sealed class SystemController
{
    private readonly BiosInterop _bios;
    /// <summary>
    /// The vendor-neutral temperature machinery, which used to be written out inside this class.
    ///
    /// Exposed as well as delegated to, because a backend that is not HP has no SystemController
    /// to reach it through, and the ACPI thermal zone is the one sensor every laptop has.
    /// </summary>
    public ThermalReader Thermal { get; }

    /// <inheritdoc cref="ThermalReader.AttachSmu"/>
    public void AttachSmu(RyzenSmu smu) => Thermal.AttachSmu(smu);

    /// <param name="smu">
    /// Optional SMU access, handed straight to the thermal reader. Passing null is a supported
    /// configuration rather than a degraded one.
    /// </param>
    public SystemController(BiosInterop bios, RyzenSmu? smu = null)
    {
        _bios = bios;
        Thermal = new ThermalReader(smu);
    }

    /// <inheritdoc cref="ThermalReader.ReadTemperature"/>
    public TemperatureReading ReadTemperature() => Thermal.ReadTemperature();

    /// <inheritdoc cref="ThermalReader.GetTemperatureC"/>
    public byte GetTemperatureC() => Thermal.GetTemperatureC();

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
            // The Length check that used to sit here could never fire: Send always returns the
            // full requested size. A short reply now throws and is reported as no reading, which
            // matters because this one renders as "your charger is underpowered".
            var data = _bios.SendAtLeast(BiosCmdGroup.Legacy, SysCmd.GetAdapter, new byte[4], 4, needed: 1);
            return (HpAdapterStatus)data[0];
        }
        catch { return null; }
    }

    /// <summary>Keyboard layout, which also indicates whether per-key colour is possible.</summary>
    public HpKeyboardType? ReadKeyboardType()
    {
        try
        {
            var data = _bios.SendAtLeast(BiosCmdGroup.Default, SysCmd.GetKeyboardType, new byte[4], 4, needed: 1);
            return (HpKeyboardType)data[0];
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
            var data = _bios.SendAtLeast(BiosCmdGroup.Keyboard, SysCmd.HasBacklight, new byte[4], 4, needed: 1);
            return data[0] != 0x03;
        }
        catch { return null; }
    }

    public bool GetMaxFanActive()
    {
        var data = _bios.SendAtLeast(BiosCmdGroup.Default, SysCmd.GetMaxFan, new byte[4], 4, needed: 1);
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
    /// <summary>
    /// The capability byte the board returns for one selector, or null if it would not answer.
    ///
    /// Exists so the doubt above can be settled rather than restated. GetThrottling reads data[1]
    /// after asking with selector 4, and the suspicion is that data[1] is simply that 4 handed
    /// back -- which is untestable through GetThrottling itself, because it only ever asks one
    /// question. Asking several and comparing the answers is the whole experiment.
    ///
    /// Read-only: the second input byte chooses which capability is being queried, so a different
    /// value asks a different question rather than changing anything.
    /// </summary>
    public byte? ReadCapabilityByte(byte selector)
    {
        try
        {
            var data = _bios.SendAtLeast(
                BiosCmdGroup.Default, SysCmd.GetCapability, new byte[] { 0, selector, 0, 0 }, 128, needed: 2);

            return data[1];
        }
        catch { return null; }
    }

    public ThrottlingState GetThrottling()
    {
        try
        {
            // Two, not one: this reads data[1]. The off-by-one is the whole reason the length
            // requirement is stated by each caller rather than assumed to be one byte.
            var data = _bios.SendAtLeast(BiosCmdGroup.Default, SysCmd.GetCapability, new byte[] { 0, 4, 0, 0 }, 128, needed: 2);
            return (ThrottlingState)data[1];
        }
        catch { return ThrottlingState.Unknown; }
    }

    /// <summary>
    /// Whether the processor is being thermally throttled: true, false, or null when this machine
    /// did not say.
    ///
    /// The translation from HP's byte to a plain answer happens here, at the boundary, so that
    /// <see cref="ThrottlingState"/> stops travelling through the poll loop, the Reading record
    /// and every view that only ever asked "is it throttling?". A vendor byte is meaningful where
    /// the vendor's commands are; six screens away it is just an enum nobody can act on.
    ///
    /// <see cref="GetThrottling"/> stays for <see cref="ThrottlingProbe"/>, which reasons about
    /// the specific values -- including whether Default is real or an echo of the selector byte
    /// that was sent. That question is open, and answering it needs the byte, not a bool.
    ///
    /// Default maps to false, which is what every caller already did by testing for On. It is
    /// deliberately not mapped to null despite the echo suspicion: that would be a behaviour
    /// change dressed up as a refactor, and if the reading does turn out to be an echo the honest
    /// fix is to stop reporting it at all rather than to quietly widen its meaning here.
    /// </summary>
    public bool? IsThrottling() => ThrottlingFrom(GetThrottling());

    /// <summary>
    /// The mapping itself, static and pure so it can be tested without a laptop -- the same
    /// reason <see cref="Merge"/> is.
    ///
    /// Unknown becomes null rather than false, and that distinction is the point of returning a
    /// nullable at all. "Not throttling" and "this machine did not answer" are different claims,
    /// and a tool that collapses the second into the first is asserting something it was not
    /// told -- which is the one thing this project is built not to do.
    /// </summary>
    internal static bool? ThrottlingFrom(ThrottlingState state) => state switch
    {
        ThrottlingState.On => true,
        ThrottlingState.Unknown => null,
        _ => false,
    };
}
