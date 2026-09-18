// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

public sealed class FanController
{
    private readonly BiosInterop _bios;

    public FanController(BiosInterop bios) => _bios = bios;

    // SendAtLeast, because a board that answers with nothing would otherwise report a fan count
    // of zero -- a confident claim about the hardware, assembled out of this layer's own padding.
    public byte GetFanCount() =>
        _bios.SendAtLeast(BiosCmdGroup.Default, FanCmd.GetFanCount, null, 4, needed: 1)[0];

    /// <summary>Per-fan type, one nibble/byte per fan slot.</summary>
    public byte[] GetFanType() =>
        _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanType, null, 128);

    /// <summary>
    /// Which response-buffer size this board answers fan level with. Null until measured.
    /// </summary>
    private int? _fanLevelOutSize;

    /// <summary>
    /// Current fan speed level per fan (raw BIOS units, not RPM).
    ///
    /// Asked with a four-byte reply where the board allows it, because hpqBIntM exposes one
    /// method per response size and they are not equally priced. Measured on the Victus 15
    /// fb2xxx with the poll instrumentation: this call was 306ms of a 324ms tick, 94% of it,
    /// while the max-fan and throttling reads beside it cost about 8ms each. The difference
    /// between them is that those ask hpqBIOSInt4 and this asked hpqBIOSInt128 for two bytes of
    /// answer.
    ///
    /// Since the poll re-arms after each tick rather than on a fixed period, that single call
    /// was setting the whole application's cadence: a nominal 2s loop measured at 2.31s.
    ///
    /// The small size is not assumed. The first call asks BOTH ways and keeps the cheap one only
    /// if it agrees with the one already known to work: a fan level read out of the wrong buffer
    /// would drive the curve from a wrong number, which is the one failure this application
    /// cannot have. A board that disagrees, or that refuses the small method, keeps the 128-byte
    /// call for the rest of the session and behaves exactly as it did before.
    /// </summary>
    public byte[] GetFanLevel() => GetFanLevel(out _);

    /// <summary>
    /// The levels, plus how many bytes the board actually supplied.
    ///
    /// Everything past <paramref name="reported"/> is padding this layer added, not a reading.
    /// </summary>
    /// <summary>
    /// Set when the cheap read was caught reporting a stopped fan the expensive one contradicted.
    ///
    /// Null while the cheap method is still trusted, or while it was never chosen.
    /// </summary>
    public string? CheapReadRejected { get; private set; }

    /// <summary>
    /// Whether a cheap reply's zero is contradicted by the reply that is known to work.
    ///
    /// Pure, so the decision can be tested without a machine. A zero is the only answer worth
    /// re-reading: it is both the most consequential value this call can return -- a stopped fan
    /// on a hot machine is the fault the whole application exists to catch -- and the one a
    /// mis-sized buffer produces most readily.
    /// </summary>
    internal static bool CheapReplyIsWrong(byte[] cheap, byte[] expensive, int expensiveReported)
    {
        if (cheap.Length < 2 || expensiveReported < 2) return false;

        // Only where the cheap read claims a stop. Two readings disagreeing by a unit or two
        // while a fan ramps is ordinary and is not what this is looking for.
        for (int fan = 0; fan < 2; fan++)
            if (cheap[fan] == 0 && expensive[fan] > 0)
                return true;

        return false;
    }

    public byte[] GetFanLevel(out int reported)
    {
        if (_fanLevelOutSize is int size)
        {
            var data = _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanLevel, null, size, out reported);

            // A zero from the cheap method gets checked against the method known to work.
            //
            // This was built to explain the zeros seen on this machine, and it did not: both
            // methods agree on them. The cause turned out to be the sentinel pair documented at
            // NoReadingFan1 -- the board answering "no measurement" rather than either method
            // mis-reading one. The check is kept anyway, because a mis-sized buffer producing a
            // zero is a real failure mode that simply is not the one that was happening here.
            //
            // Costs nothing in the ordinary case: fans at rest genuinely read zero and the two
            // methods agree, so this fires on disagreement rather than on every zero.
            if (size != 128 && (data.Length > 1 && (data[0] == 0 || data[1] == 0)))
            {
                try
                {
                    var checkedAgainst = _bios.Send(
                        BiosCmdGroup.Default, FanCmd.GetFanLevel, null, 128, out int checkedReported);

                    if (CheapReplyIsWrong(data, checkedAgainst, checkedReported))
                    {
                        _fanLevelOutSize = 128;
                        CheapReadRejected =
                            $"The 4-byte fan read reported {data[0]}/{data[1]} while the 128-byte read "
                            + $"reported {checkedAgainst[0]}/{checkedAgainst[1]} at the same moment. The "
                            + "cheap method is not trusted for the rest of this session.";

                        reported = checkedReported;
                        return checkedAgainst;
                    }
                }
                catch
                {
                    // The expensive method refused. The cheap reading stands rather than being
                    // discarded on the strength of a call that did not happen.
                }
            }

            return data;
        }

        var large = _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanLevel, null, 128, out reported);

        try
        {
            var small = _bios.Send(BiosCmdGroup.Default, FanCmd.GetFanLevel, null, 4);

            // Compared with a tolerance rather than for equality: the two reads are consecutive,
            // not simultaneous, and a fan that is ramping can genuinely move a unit or two
            // between them. A real mismatch is not off by one, it is a different buffer.
            _fanLevelOutSize =
                small.Length >= 2 && large.Length >= 2
                && Math.Abs(small[0] - large[0]) <= 4
                && Math.Abs(small[1] - large[1]) <= 4
                    ? 4
                    : 128;
        }
        catch
        {
            // The small method is not available here. Pay for finding that out once.
            _fanLevelOutSize = 128;
        }

        return large;
    }

    /// <summary>
    /// The two fan levels, with "the board did not say" kept distinct from "the fan is stopped".
    ///
    /// Those two have rendered identically for the whole life of this application, as a raw
    /// level of zero. That is the worst possible collision to have: a stopped fan on a hot
    /// machine is the precise fault the fan curve exists to prevent, and the thermal log has
    /// been recording it out of bytes the board never sent. Fourteen days of that trace contain
    /// thousands of such rows.
    ///
    /// Zero is still returned when the board genuinely reports zero -- that reading is real and
    /// suppressing it would be the opposite mistake, hiding a stall to tidy up a display.
    /// </summary>
    /// <summary>
    /// The pair this board returns when it has no fan measurement to give.
    ///
    /// Not a guess. Fourteen days of thermal log -- 192,791 readings -- were searched for it after
    /// the user reported hearing no difference at the times the log claimed a stopped fan, and the
    /// evidence that it is a sentinel rather than a reading is four-fold:
    ///
    ///   * The commanded level during those 6,400 rows is spread flat across the whole range --
    ///     3, 11, 26, 40 and 52 per cent each account for two to four per cent of them. A fan
    ///     reading that is identical whether three or fifty-two per cent was commanded is not
    ///     tracking anything.
    ///   * The die was above 70 C for 2,401 of them and reached 98.6 C. A genuinely stopped fan
    ///     at 98.6 C is not a reading, it is an emergency that did not happen.
    ///   * Fan 1 reads exactly 27 in 15,236 rows against roughly 3,000 each for 26 and 28. A real
    ///     fan's readings do not spike five-fold at one value.
    ///   * It holds unchanged for up to 28 minutes at a stretch, 268 separate times.
    ///
    /// The value 27 on its own is ordinary -- it pairs with 24 and with 27 in the majority of its
    /// appearances -- so it is the pair that is rejected, not the number.
    ///
    /// This is board-specific (8C2F) and is deliberately not generalised into a profile: one
    /// board's measured sentinel is evidence, and a table of them would be an invitation to add
    /// unmeasured entries.
    /// </summary>
    internal const byte NoReadingFan1 = 27;
    internal const byte NoReadingFan2 = 0;

    /// <summary>Whether a reply is the sentinel above rather than a measurement.</summary>
    internal static bool IsNoReading(byte fan1, byte fan2) => fan1 == NoReadingFan1 && fan2 == NoReadingFan2;

    /// <summary>
    /// How many times the sentinel has been seen this session.
    ///
    /// Counted rather than merely suppressed, because a reading the hardware declined to give is
    /// worth knowing about -- silently returning nulls would replace a wrong number with an
    /// unexplained blank, which is better but still not an explanation.
    /// </summary>
    public int NoReadingCount { get; private set; }

    public (byte? Fan1, byte? Fan2) ReadLevels()
    {
        byte[] data = GetFanLevel(out int reported);

        if (reported > 1 && IsNoReading(data[0], data[1]))
        {
            NoReadingCount++;
            return (null, null);
        }

        return (reported > 0 ? data[0] : null,
                reported > 1 ? data[1] : null);
    }

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
