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
