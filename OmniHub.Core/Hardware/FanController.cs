namespace OmniHub.Core.Hardware;

public sealed class FanController
{
    private readonly BiosInterop _bios;

    public FanController(BiosInterop bios) => _bios = bios;

    public byte GetFanCount() =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanCount, null, 4)[0];

    /// <summary>Per-fan type, one nibble/byte per fan slot.</summary>
    public byte[] GetFanType() =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanType, null, 128);

    /// <summary>Current fan speed level per fan (raw BIOS units, not RPM).</summary>
    public byte[] GetFanLevel() =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanLevel, null, 128);

    /// <summary>
    /// Directly commands fan 1 and fan 2 levels. Bypasses BIOS auto-control. The raw byte
    /// is NOT a 0-255 PWM duty cycle -- it's an RPM/100 target, usable range ~20-55 on
    /// this hardware family (see FanService.PercentToRaw for the sourcing). Passing a raw
    /// 0-255-scale value here would be wrong on both ends: too low to move the fan at all,
    /// or (well above the real ceiling) potentially treated as the "release to BIOS"
    /// sentinel some values in that range map to.
    /// </summary>
    public void SetFanLevel(byte fan1, byte fan2) =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.SetFanLevel, new byte[] { fan1, fan2, 0, 0 }, 4);

    /// <summary>
    /// Which performance-mode encoding this board speaks.
    ///
    /// Defaults to Current, and only ever moves to Legacy on a firmware report that positively
    /// says so -- set by HardwareContext once the capability block has been read, in the same
    /// "handed over after construction" style as SystemController.AttachSmu.
    ///
    /// The default matters. A board that will not describe itself keeps the encoding this
    /// application was developed and measured against, rather than being switched onto an
    /// untested path on the strength of a failed read. That is not hypothetical: a failed read
    /// returns a zeroed block, and byte #3 of zero decodes as Legacy.
    /// </summary>
    public ThermalPolicyVersion Encoding { get; set; } = ThermalPolicyVersion.Current;

    /// <summary>
    /// The legacy policy value corresponding to a fan mode.
    ///
    /// Pure and static so the mapping can be tested without a machine -- no hardware here speaks
    /// the legacy encoding, so this is code that cannot be exercised by running the application
    /// on the laptop it was written on.
    /// </summary>
    public static HpThermalPolicy ToLegacyPolicy(FanMode mode) => mode switch
    {
        FanMode.Performance => HpThermalPolicy.Performance,
        FanMode.Cool => HpThermalPolicy.Quiet,
        // Default, LegacyQuiet and anything unmapped fall back to the BIOS's own default rather
        // than to a guess -- handing cooling back is always a safe answer.
        _ => HpThermalPolicy.Default,
    };

    /// <summary>
    /// Switches BIOS fan operating mode (Default/Performance/Cool/etc), in whichever encoding
    /// this board speaks.
    ///
    /// Current boards (Omen, Victus) take 0x30-0x50 in byte #1 with byte #2 clear. Legacy boards
    /// -- Pavilion Gaming and early Omen -- take a small 0/1/2 policy with byte #2 set to 0x01.
    /// Sending 0x31 to a legacy board is out of range for its firmware, which is what every
    /// version of this application did before the encoding was selected here.
    /// </summary>
    public void SetFanMode(FanMode mode)
    {
        if (Encoding == ThermalPolicyVersion.Legacy)
        {
            SetThermalPolicy(ToLegacyPolicy(mode));
            return;
        }

        _bios.Send(BiosCmdGroup.Default, FanCmd.SetFanMode, new byte[] { 0xFF, (byte)mode, 0, 0 }, 4);
    }

    /// <summary>
    /// Same BIOS command as <see cref="SetFanMode"/>, sent with OmniControlSuite's payload
    /// encoding instead of ours: a small 0/1/2 policy byte, and byte[2] set to 0x01 rather
    /// than 0x00. See <see cref="HpThermalPolicy"/> for why both encodings exist.
    ///
    /// Kept as a separate method rather than silently replacing SetFanMode: which encoding
    /// this machine actually honours is still an open question, and quietly switching the
    /// one the fan-curve loop depends on would be changing cooling behaviour on a guess.
    /// Send() throws on a non-zero BIOS return code, so a rejected policy surfaces as an
    /// exception rather than as a command that appeared to work.
    /// </summary>
    public void SetThermalPolicy(HpThermalPolicy policy) =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.SetFanMode, new byte[] { 0xFF, (byte)policy, 0x01, 0x00 }, 4);

    public byte[] GetFanTable() =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanTable, null, 128);

    public void SetFanTable(byte[] table128) =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.SetFanTable, table128, 4);

    /// <summary>
    /// Hands control back to the BIOS automatic fan management.
    /// Always call this on clean shutdown -- never exit while fans are pinned
    /// to a manual level, or they can stay stuck at that level.
    /// </summary>
    public void RestoreAutomaticControl() => SetFanMode(FanMode.Default);
}
