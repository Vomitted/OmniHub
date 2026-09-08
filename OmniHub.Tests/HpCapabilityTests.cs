using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Decoding HP's system design data.
///
/// This is the block that decides what OmniHub tells someone their laptop can do, so an offset
/// error here does not produce an obvious failure -- it produces a confident, wrong answer
/// about whether fan control works on their machine. Byte positions follow OmenMon's
/// BiosData.SystemData, which took them from HP's own Omen Gaming Hub.
/// </summary>
public class HpCapabilityTests
{
    /// <summary>Builds a 128-byte reply with the documented fields at their real offsets.</summary>
    private static byte[] Payload(
        ushort status = 0x00E6, byte policy = 0x01, byte support = 0x01,
        byte pl4 = 0xD7, byte biosOc = 0x00, byte gpuSwitch = 0x06, byte concurrent = 0x00)
    {
        var d = new byte[128];
        d[0] = (byte)(status & 0xFF);
        d[1] = (byte)(status >> 8);
        d[2] = 0x35;          // unknown, observed constant
        d[3] = policy;
        d[4] = support;
        d[5] = pl4;
        d[6] = biosOc;
        d[7] = gpuSwitch;
        d[8] = concurrent;
        return d;
    }

    [Fact]
    public void DecodesEveryDocumentedField()
    {
        var s = HpSystemData.Parse(Payload())!.Value;

        Assert.Equal(0x00E6, s.StatusFlags);
        Assert.Equal(ThermalPolicyVersion.Current, s.ThermalPolicy);
        Assert.Equal(HpSupportFlags.SoftwareFanControl, s.SupportFlags);
        Assert.Equal(215, s.DefaultCpuPowerLimit4W);   // 0xD7
        Assert.False(s.BiosOverclockSupported);
        Assert.True(s.GpuModeSwitchSupported);         // 0x06 has bit #2 set
        Assert.Equal(0, s.DefaultCpuPowerLimitWithGpuW);
    }

    /// <summary>Status flags span bytes #0 and #1 little-endian, not big-endian.</summary>
    [Fact]
    public void StatusFlagsAreLittleEndian()
    {
        Assert.Equal(0x0118, HpSystemData.Parse(Payload(status: 0x0118))!.Value.StatusFlags);
    }

    /// <summary>
    /// The three-state rule that decides whether a capability actually gates a feature.
    ///
    /// Denied and unknown are different answers, and the asymmetry is deliberate: only a
    /// credible block with the bit clear counts as a denial. A machine that will not describe
    /// itself keeps its features, because switching off working hardware on the strength of a
    /// failed read is the silent failure -- and it is the one that already happened here.
    /// </summary>
    [Fact]
    public void UnknownCapabilitiesDenyNothing()
    {
        // The command failed outright.
        Assert.False(HpSystemData.Denies(null, c => c.SoftwareFanControl));

        // A well-formed reply carrying nothing -- the all-zero regression.
        var blank = HpSystemData.Parse(Payload(support: 0x00, pl4: 0x00, gpuSwitch: 0x00))!.Value;
        Assert.True(blank.LooksUnreported);
        Assert.False(HpSystemData.Denies(blank, c => c.SoftwareFanControl));
        Assert.False(HpSystemData.Denies(blank, c => c.GpuModeSwitchSupported));
    }

    [Fact]
    public void ACredibleBlockWithTheBitClearIsADenial()
    {
        // Real board, real PL4 reported, fan control bit clear: this machine genuinely says no.
        var s = HpSystemData.Parse(Payload(support: 0x00, pl4: 0xD7, gpuSwitch: 0x06))!.Value;

        Assert.False(s.LooksUnreported);
        Assert.True(HpSystemData.Denies(s, c => c.SoftwareFanControl));
        Assert.False(HpSystemData.Denies(s, c => c.GpuModeSwitchSupported));
    }

    [Fact]
    public void ASupportedCapabilityIsNeverDenied()
    {
        var s = HpSystemData.Parse(Payload())!.Value;   // the machine this was developed on

        Assert.False(HpSystemData.Denies(s, c => c.SoftwareFanControl));
        Assert.False(HpSystemData.Denies(s, c => c.GpuModeSwitchSupported));
    }

    /// <summary>
    /// The legacy fan-mode encoding, which no hardware here speaks -- so these assertions are
    /// the only thing standing between the mapping and a board nobody can test it on.
    /// </summary>
    [Theory]
    [InlineData(FanMode.Performance, HpThermalPolicy.Performance)]
    [InlineData(FanMode.Cool, HpThermalPolicy.Quiet)]
    [InlineData(FanMode.Default, HpThermalPolicy.Default)]
    [InlineData(FanMode.LegacyQuiet, HpThermalPolicy.Default)]
    public void FanModesMapOntoTheLegacyPolicySet(FanMode mode, HpThermalPolicy expected)
    {
        Assert.Equal(expected, FanController.ToLegacyPolicy(mode));
    }

    /// <summary>
    /// A new FanController must speak the current encoding until something credible says
    /// otherwise. Defaulting the other way would put every board that fails to describe itself
    /// onto the untested path -- and a failed read decodes as Legacy, so this is the exact case
    /// that would go wrong.
    /// </summary>
    [Fact]
    public void FanEncodingDefaultsToCurrent()
    {
        Assert.Equal(ThermalPolicyVersion.Current, new FanController(null!).Encoding);
    }

    /// <summary>
    /// The zeroed reply decodes as Legacy, which is precisely why the encoding is gated on
    /// LooksUnreported rather than read straight out of the block.
    /// </summary>
    [Fact]
    public void AZeroedBlockLooksLikeALegacyBoardAndMustNotBeTrusted()
    {
        var blank = HpSystemData.Parse(Payload(policy: 0x00, support: 0x00, pl4: 0x00, gpuSwitch: 0x00))!.Value;

        Assert.Equal(ThermalPolicyVersion.Legacy, blank.ThermalPolicy);
        Assert.True(blank.LooksUnreported);
    }

    /// <summary>
    /// The real reply from the Victus 15 fb2xxx (board 8C2F), which is not all zeroes and was
    /// wrongly judged credible.
    ///
    /// Measured via -Probe: status=0x00C8, policy byte 0, support flags 0, PL4 0, and a non-zero
    /// byte #7 whose switchable bits are clear. Read as credible, that block says the board has
    /// no software fan control and no graphics switching, and reports the legacy thermal policy
    /// -- on a laptop whose fans this application was driving at the moment the probe ran, with
    /// fan count, types, levels and the 32-byte fan table all reading correctly in the same
    /// output. Gating on it would have moved fan commands onto the legacy encoding.
    ///
    /// The exact value of byte #7 is not recoverable from the probe text, which prints the
    /// decoded bit test rather than the raw byte; 0x03 stands in for "some bits set, none of
    /// them the switchable ones", which is what the output proves.
    /// </summary>
    [Fact]
    public void TheVictusBlockIsTreatedAsUnreportedDespiteNotBeingAllZeroes()
    {
        var s = HpSystemData.Parse(Payload(
            status: 0x00C8, policy: 0x00, support: 0x00, pl4: 0x00, gpuSwitch: 0x03))!.Value;

        Assert.True(s.LooksUnreported, "a block with no PL4 and no support flags carries nothing");

        // And therefore denies nothing, and cannot move the fan encoding.
        Assert.False(HpSystemData.Denies(s, c => c.SoftwareFanControl));
        Assert.False(HpSystemData.Denies(s, c => c.GpuModeSwitchSupported));
    }

    /// <summary>
    /// The bit the whole safety floor depends on. A board that does not set it will not honour
    /// SetFanLevel, and saying otherwise would promise a fix that cannot work there.
    /// </summary>
    [Theory]
    [InlineData(0x00, false)]
    [InlineData(0x01, true)]
    [InlineData(0x03, true)]   // fan control + extreme mode
    [InlineData(0x02, false)]  // extreme mode only, no fan control
    public void SoftwareFanControlBitIsReadCorrectly(byte flags, bool expected)
    {
        Assert.Equal(expected, HpSystemData.Parse(Payload(support: flags))!.Value.SoftwareFanControl);
    }

    /// <summary>
    /// The family discriminator. Older boards -- Pavilion Gaming, early Omen -- report V0 and
    /// take the legacy performance-mode encoding; current Omen and Victus report V1.
    /// </summary>
    [Theory]
    [InlineData(0x00, ThermalPolicyVersion.Legacy)]
    [InlineData(0x01, ThermalPolicyVersion.Current)]
    public void ThermalPolicyVersionSelectsTheEncoding(byte raw, ThermalPolicyVersion expected)
    {
        Assert.Equal(expected, HpSystemData.Parse(Payload(policy: raw))!.Value.ThermalPolicy);
    }

    /// <summary>Bits #2 and #3 are the observed "supported" bits; anything else is not.</summary>
    [Theory]
    [InlineData(0x06, true)]   // observed on board 88F7
    [InlineData(0x0C, true)]   // observed on board 8A14
    [InlineData(0x00, false)]
    [InlineData(0x03, false)]  // only the two bits never observed set
    public void GpuModeSwitchSupportIsReadFromTheObservedBits(byte raw, bool expected)
    {
        Assert.Equal(expected, HpSystemData.Parse(Payload(gpuSwitch: raw))!.Value.GpuModeSwitchSupported);
    }

    /// <summary>
    /// The regression that prompted this property.
    ///
    /// Asked with a four-byte payload instead of none, the BIOS returned a well-formed 128-byte
    /// buffer with every capability byte clear. That decoded to "no software fan control" and
    /// was printed as such -- on the machine whose fans the application was driving at that
    /// moment. A zeroed block has to be distinguishable from a real negative answer.
    /// </summary>
    [Fact]
    public void AllZeroCapabilityBlockIsFlaggedAsUnreported()
    {
        // The exact reply observed on an HP Victus 15-fb2xxx before the transport was fixed.
        var s = HpSystemData.Parse(Payload(
            status: 0x00C8, policy: 0x00, support: 0x00, pl4: 0x00, gpuSwitch: 0x00))!.Value;

        Assert.True(s.LooksUnreported);
        Assert.False(s.SoftwareFanControl);  // still false, but callers must not trust it
    }

    /// <summary>A populated reply is never mistaken for an empty one.</summary>
    [Fact]
    public void PopulatedBlockIsNotFlaggedAsUnreported()
    {
        Assert.False(HpSystemData.Parse(Payload())!.Value.LooksUnreported);
    }

    /// <summary>
    /// A board that genuinely lacks fan control still reports its other design data, so a real
    /// negative answer stays distinguishable from a blank one.
    /// </summary>
    [Fact]
    public void RealNegativeAnswerIsNotFlaggedAsUnreported()
    {
        var s = HpSystemData.Parse(Payload(support: 0x00, pl4: 0xD7, gpuSwitch: 0x06))!.Value;

        Assert.False(s.LooksUnreported);
        Assert.False(s.SoftwareFanControl);
    }

    /// <summary>
    /// A board that does not implement the command replies short or not at all. That must come
    /// back as "unknown" rather than being decoded out of whatever bytes happen to be there.
    /// </summary>
    [Fact]
    public void ShortOrMissingReplyDecodesToNull()
    {
        Assert.Null(HpSystemData.Parse(null));
        Assert.Null(HpSystemData.Parse(Array.Empty<byte>()));
        Assert.Null(HpSystemData.Parse(new byte[8]));    // one byte short of the last field read
        Assert.NotNull(HpSystemData.Parse(new byte[9])); // exactly enough
    }
}
