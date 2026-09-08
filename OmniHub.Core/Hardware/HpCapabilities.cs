namespace OmniHub.Core.Hardware;

/// <summary>
/// What the firmware says the board supports. Byte #4 of the system design data.
/// </summary>
[Flags]
public enum HpSupportFlags : byte
{
    None = 0x00,

    /// <summary>
    /// Software fan control. The single most important bit in this application: without it,
    /// SetFanLevel is not something this board honours, and the safety floor cannot work.
    /// </summary>
    SoftwareFanControl = 0x01,

    /// <summary>Extreme performance mode is present in firmware.</summary>
    ExtremeMode = 0x02,

    /// <summary>Extreme mode is unlocked rather than merely present.</summary>
    ExtremeModeUnlocked = 0x04,
}

/// <summary>
/// Which generation of thermal-policy encoding the board speaks. Byte #3.
///
/// This is the real dividing line between HP families, and it is why a hard-coded list of
/// model numbers is the wrong way to decide what to support. Older boards -- Pavilion Gaming
/// and early Omen among them -- report V0 and take the legacy 0x00-0x03 performance modes;
/// current Omen and Victus report V1 and take the 0x30-0x50 encoding.
/// </summary>
public enum ThermalPolicyVersion : byte
{
    /// <summary>Legacy encoding: Default 0x00, Performance 0x01, Cool 0x02, Quiet 0x03.</summary>
    Legacy = 0x00,

    /// <summary>Current encoding: Default 0x30, Performance 0x31, Cool 0x50.</summary>
    Current = 0x01,
}

/// <summary>Smart power adapter state, from the legacy adapter query.</summary>
public enum HpAdapterStatus : byte
{
    NotSupported = 0x00,
    MeetsRequirement = 0x01,
    BelowRequirement = 0x02,
    BatteryPower = 0x03,
    NotFunctioning = 0x04,
    Error = 0xFF,
}

/// <summary>Keyboard layout, which also says whether per-key colour is possible.</summary>
public enum HpKeyboardType : byte
{
    Standard = 0x00,
    WithNumPad = 0x01,
    TenKeyLess = 0x02,
    PerKeyRgb = 0x03,
}

/// <summary>
/// The board's own account of itself, decoded from HP's system design data.
///
/// Every field here is something the firmware reports rather than something inferred from a
/// model string. That matters for the question this application keeps having to answer --
/// "will this work on my laptop?" -- because the honest answer comes from the board, not from
/// a list of names somebody maintained by hand and got wrong.
/// </summary>
public readonly record struct HpSystemData(
    ushort StatusFlags,
    ThermalPolicyVersion ThermalPolicy,
    HpSupportFlags SupportFlags,
    byte DefaultCpuPowerLimit4W,
    bool BiosOverclockSupported,
    byte GpuModeSwitchFlags,
    byte DefaultCpuPowerLimitWithGpuW)
{
    /// <summary>True when the board says software fan control is available at all.</summary>
    public bool SoftwareFanControl => SupportFlags.HasFlag(HpSupportFlags.SoftwareFanControl);

    /// <summary>
    /// True when the reply carries no capability information at all, however well-formed it
    /// looked.
    ///
    /// This exists because of a real failure. Asked the wrong way, the BIOS returned a full
    /// 128-byte buffer with every capability byte clear, and that decoded into a confident
    /// report that the machine supported no fan control -- on the very laptop whose fans this
    /// application was driving at the time. A zeroed reply is a failed read, not a featureless
    /// board, and the difference has to reach whatever displays it.
    ///
    /// The default CPU PL4 is the giveaway: a board that answers this command at all reports a
    /// real wattage there, so a zero means nothing was populated.
    /// </summary>
    public bool LooksUnreported =>
        DefaultCpuPowerLimit4W == 0 && SupportFlags == HpSupportFlags.None && GpuModeSwitchFlags == 0;

    /// <summary>
    /// Whether the board can switch graphics mode. Bits #2 and #3 are the observed "supported"
    /// bits; the remainder have only ever been seen clear, so they are reported raw rather than
    /// given names this project cannot stand behind.
    /// </summary>
    public bool GpuModeSwitchSupported => (GpuModeSwitchFlags & 0x0C) != 0;

    /// <summary>
    /// Whether the firmware DENIED a capability, as distinct from never having answered about it.
    ///
    /// Three states, not two, and collapsing them is a mistake this project has already made
    /// once: a capability counts as denied only when there is a credible block in hand AND the
    /// bit in it is clear. Null (the command failed, or is not implemented) and
    /// <see cref="LooksUnreported"/> (a well-formed reply carrying nothing) both mean unknown,
    /// and unknown must not disable anything.
    ///
    /// The asymmetry is deliberate. Offering a control that turns out to do nothing is a small
    /// harm, and self-evident the moment it is tried; switching off working hardware because the
    /// firmware would not describe itself is a silent one, and it is what produced a printed
    /// "this machine supports no fan control" on a laptop whose fans were being driven at that
    /// moment.
    /// </summary>
    public static bool Denies(HpSystemData? data, Func<HpSystemData, bool> capability) =>
        data is { LooksUnreported: false } d && !capability(d);

    /// <summary>
    /// Decodes the 128-byte system design payload.
    ///
    /// Layout follows OmenMon's BiosData.SystemData, which was derived from HP's own Omen
    /// Gaming Hub: bytes #0-1 are status flags little-endian, #3 the thermal policy version,
    /// #4 the support flags, #5 the default CPU PL4 in watts, #6 BIOS overclock support, #7
    /// graphics-switch support, #8 the default concurrent CPU limit shared with the GPU.
    ///
    /// Returns null for a short buffer rather than reading past it, because a truncated reply
    /// from a board that does not implement this command must not be decoded into confident
    /// nonsense about what the machine can do.
    /// </summary>
    public static HpSystemData? Parse(byte[]? data)
    {
        if (data is null || data.Length < 9) return null;

        return new HpSystemData(
            StatusFlags: (ushort)(data[1] << 8 | data[0]),
            ThermalPolicy: (ThermalPolicyVersion)data[3],
            SupportFlags: (HpSupportFlags)data[4],
            DefaultCpuPowerLimit4W: data[5],
            BiosOverclockSupported: data[6] == 0x01,
            GpuModeSwitchFlags: data[7],
            DefaultCpuPowerLimitWithGpuW: data[8]);
    }

    /// <summary>A one-line summary for the probe output and the readiness panel.</summary>
    public override string ToString() =>
        $"policy={ThermalPolicy} fanctl={(SoftwareFanControl ? "yes" : "no")} " +
        $"flags=0x{(byte)SupportFlags:X2} status=0x{StatusFlags:X4} " +
        $"pl4default={DefaultCpuPowerLimit4W}W gpuswitch={(GpuModeSwitchSupported ? "yes" : "no")}" +
        (DefaultCpuPowerLimitWithGpuW > 0 ? $" concurrent={DefaultCpuPowerLimitWithGpuW}W" : "");
}
