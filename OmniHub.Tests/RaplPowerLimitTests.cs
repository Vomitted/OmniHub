// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Decoding an Intel package power limit.
///
/// Every value in MSR_PKG_POWER_LIMIT is a bit field in units the processor defines elsewhere, so
/// there is no such thing as reading it approximately right. A wrong offset, a wrong mask or a
/// missed unit register all produce a plausible wattage -- and a plausible wattage is precisely
/// the kind of number this project exists not to show.
///
/// None of this can be checked against silicon here; this machine is AMD. That is the argument for
/// pinning the arithmetic against values built by hand from the documented layout.
/// </summary>
public class RaplPowerLimitTests
{
    // Power in eighths of a watt, time in milliseconds: the usual client encoding.
    private static readonly RaplUnits Units = RaplUnits.Typical;

    /// <summary>
    /// A realistic laptop register: PL1 45 W enabled, PL2 60 W enabled, unlocked.
    ///
    /// Built field by field rather than pasted as one constant, so the test states the layout it
    /// believes in. 45 W is 360 eighths; 60 W is 480.
    /// </summary>
    private static ulong Realistic(bool locked = false)
    {
        ulong msr = 360UL                    // PL1, bits 14:0
                    | (1UL << 15)            // PL1 enabled
                    | (28UL << 17)           // PL1 window field
                    | (480UL << 32)          // PL2, bits 46:32
                    | (1UL << 47)            // PL2 enabled
                    | (2UL << 49);           // PL2 window field

        return locked ? msr | (1UL << 63) : msr;
    }

    [Fact]
    public void ARealisticRegisterDecodesToTheWattsItEncodes()
    {
        var limit = RaplPowerLimit.Decode(Realistic(), Units);

        Assert.Equal(45.0, limit.Sustained.Watts, 3);
        Assert.True(limit.Sustained.Enabled);

        Assert.Equal(60.0, limit.Burst.Watts, 3);
        Assert.True(limit.Burst.Enabled);

        Assert.False(limit.Locked);
    }

    [Fact]
    public void TheUnitRegisterChangesEveryFigure()
    {
        // The reason units are read rather than assumed. The same bits under a different power
        // unit are a different number of watts, and nothing about the value itself says which.
        var eighths = RaplPowerLimit.Decode(Realistic(), new RaplUnits(PowerRaw: 3, TimeRaw: 10));
        var quarters = RaplPowerLimit.Decode(Realistic(), new RaplUnits(PowerRaw: 2, TimeRaw: 10));

        Assert.Equal(45.0, eighths.Sustained.Watts, 3);
        Assert.Equal(90.0, quarters.Sustained.Watts, 3);
    }

    [Fact]
    public void TheUnitRegisterIsItselfDecodedFromItsOwnFields()
    {
        // Power in bits 3:0, time in bits 19:16. A typical client value is 0x000A0E03.
        var units = RaplUnits.FromRegister(0x000A0E03);

        Assert.Equal(3, units.PowerRaw);
        Assert.Equal(10, units.TimeRaw);
        Assert.Equal(0.125, units.WattsPerCount, 6);
    }

    [Fact]
    public void TheTimeWindowIsAnExponentAndAMantissaRatherThanANumber()
    {
        // Bits 21:17 are an exponent, 23:22 a two-bit mantissa: 2^y * (1 + z/4) time units.
        // Reading the seven bits as one integer gives an answer that looks reasonable and is wrong
        // by orders of magnitude -- field 28 would read as 28 units instead of 268 million.
        var limit = RaplPowerLimit.Decode(Realistic(), Units);

        // y = 28, z = 0, time unit 1/1024 s.
        Assert.Equal((1 << 28) / 1024.0, limit.Sustained.WindowSeconds, 3);
        Assert.NotEqual(28.0, limit.Sustained.WindowSeconds, 3);
    }

    [Fact]
    public void ALockedRegisterRefusesToBeWrittenAndSaysWhy()
    {
        // The case that matters most on the machines this release is aimed at. Laptop firmware
        // very commonly sets bit 63, and the register then reads perfectly and cannot be changed
        // until the machine resets.
        var locked = Realistic(locked: true);

        Assert.True(RaplPowerLimit.Decode(locked, Units).Locked);

        var refused = Assert.Throws<InvalidOperationException>(
            () => RaplPowerLimit.WithSustainedWatts(locked, 35, Units));

        Assert.Contains("locked", refused.Message);
        Assert.Contains("Nothing was written", refused.Message);
    }

    [Fact]
    public void WritingASustainedLimitLeavesEverythingElseAlone()
    {
        // Read-modify-write, because this register also holds PL2, both time windows and both
        // enable bits. Composing a fresh value from only what a caller cared about would zero the
        // rest -- including PL2, which on a laptop is what lets the processor respond at all.
        ulong updated = RaplPowerLimit.WithSustainedWatts(Realistic(), 35, Units);
        var limit = RaplPowerLimit.Decode(updated, Units);

        Assert.Equal(35.0, limit.Sustained.Watts, 3);

        Assert.Equal(60.0, limit.Burst.Watts, 3);
        Assert.True(limit.Burst.Enabled);
        Assert.True(limit.Sustained.Enabled);
        Assert.Equal(RaplPowerLimit.Decode(Realistic(), Units).Sustained.WindowSeconds,
                     limit.Sustained.WindowSeconds, 3);
    }

    [Fact]
    public void AnAbsurdlyLargeLimitSaturatesRatherThanWrapping()
    {
        // 15 bits hold up to 4095.875 W at eighth-watt units. A larger request that wrapped would
        // become a very small number and ask the processor for far LESS power than intended --
        // the opposite of what was meant, and the direction that presents as "my laptop got slower
        // and I do not know why".
        ulong updated = RaplPowerLimit.WithSustainedWatts(Realistic(), 100_000, Units);
        var limit = RaplPowerLimit.Decode(updated, Units);

        Assert.Equal(0x7FFF * Units.WattsPerCount, limit.Sustained.Watts, 3);
        Assert.True(limit.Sustained.Watts > 45);
    }

    [Fact]
    public void ANegativeLimitIsRefusedRatherThanCastIntoSomethingHuge()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RaplPowerLimit.WithSustainedWatts(Realistic(), -5, Units));
    }

    [Fact]
    public void ADisabledLimitIsReportedAsDisabledRatherThanAsZero()
    {
        // A limit that is present but not enforced is not a limit of zero watts. Reporting the
        // wattage without the enable bit would describe a processor as capped at 45 W when nothing
        // is capping it.
        ulong noEnable = Realistic() & ~(1UL << 15);
        var limit = RaplPowerLimit.Decode(noEnable, Units);

        Assert.False(limit.Sustained.Enabled);
        Assert.Equal(45.0, limit.Sustained.Watts, 3);
    }
}
