// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

/// <summary>
/// The units RAPL reports power and time in, from MSR_RAPL_POWER_UNIT (0x606).
///
/// Nothing in the power-limit register is in watts or seconds. It is in multiples of whatever
/// this says, which differs between processor families -- so decoding a limit without first
/// reading this produces a number with no unit attached, which is worse than no number.
/// </summary>
/// <param name="PowerRaw">Bits 3:0. Watts per count is 1 / 2^PowerRaw.</param>
/// <param name="TimeRaw">Bits 19:16. Seconds per count is 1 / 2^TimeRaw.</param>
public readonly record struct RaplUnits(int PowerRaw, int TimeRaw)
{
    /// <summary>
    /// The common case on client parts: power in eighths of a watt, time in milliseconds.
    ///
    /// Present as a named default for tests and for a decode where the unit register could not be
    /// read -- but a caller in that position should say the limit is unknown rather than decode
    /// with an assumption. Getting this wrong scales every figure by a power of two.
    /// </summary>
    public static readonly RaplUnits Typical = new(PowerRaw: 3, TimeRaw: 10);

    public static RaplUnits FromRegister(ulong msr) =>
        new(PowerRaw: (int)(msr & 0xF), TimeRaw: (int)((msr >> 16) & 0xF));

    /// <summary>Watts per unit count.</summary>
    public double WattsPerCount => 1.0 / (1 << PowerRaw);

    /// <summary>Seconds per unit count.</summary>
    public double SecondsPerCount => 1.0 / (1 << TimeRaw);
}

/// <summary>One of the two package power limits, decoded.</summary>
/// <param name="Watts">The limit itself.</param>
/// <param name="Enabled">Whether the processor is enforcing it at all.</param>
/// <param name="Clamped">Whether it may drop below the guaranteed frequency to stay inside it.</param>
/// <param name="WindowSeconds">
/// How long the processor may average over. PL1's is the sustained envelope, usually tens of
/// seconds; PL2's is the burst, usually under a second.
/// </param>
public readonly record struct RaplLimit(double Watts, bool Enabled, bool Clamped, double WindowSeconds);

/// <summary>
/// MSR_PKG_POWER_LIMIT (0x610), which is how an Intel processor is told how much power it may use.
///
/// This is the Intel counterpart to the AMD SMU path this application already has, and until now
/// there was nothing at all: no MSR access, no RAPL, no Intel die temperature. Most Lenovo and
/// Dell laptops -- the machines this release is aimed at -- are Intel, so "OmniHub tunes AMD" has
/// been a much narrower claim than it sounded.
///
/// The decoding is pure and lives here on its own because it is the half that can be wrong
/// silently. A bit field read against the wrong layout produces a plausible wattage, and a
/// plausible wattage is exactly the kind of number this project refuses to show.
///
/// THE LOCK BIT IS THE HONEST PART. Bit 63, once set, makes this register read-only until the
/// next reset, and laptop firmware very commonly sets it. So on a great many machines this will
/// read perfectly and refuse to write, and the only decent thing to do is say so -- a greyed
/// control reading "the firmware locked this register" beats a slider that silently does nothing,
/// which is the same failure this project already documents on the SMU side, where HP accepts a
/// limit and then arbitrates it away.
///
/// Layout from the Intel Software Developer's Manual, volume 3, the package RAPL section:
/// bits 14:0 PL1, 15 enable, 16 clamp, 23:17 window; 46:32 PL2, 47 enable, 48 clamp, 55:49
/// window; 63 lock.
/// </summary>
public readonly record struct RaplPowerLimit(RaplLimit Sustained, RaplLimit Burst, bool Locked)
{
    /// <summary>MSR_PKG_POWER_LIMIT.</summary>
    public const uint Register = 0x610;

    /// <summary>MSR_RAPL_POWER_UNIT, which says what the numbers in it mean.</summary>
    public const uint UnitsRegister = 0x606;

    /// <summary>Reads the register into watts and seconds.</summary>
    public static RaplPowerLimit Decode(ulong msr, RaplUnits units) => new(
        Sustained: new RaplLimit(
            Watts: (msr & 0x7FFF) * units.WattsPerCount,
            Enabled: (msr & (1UL << 15)) != 0,
            Clamped: (msr & (1UL << 16)) != 0,
            WindowSeconds: DecodeWindow((int)((msr >> 17) & 0x7F), units)),
        Burst: new RaplLimit(
            Watts: ((msr >> 32) & 0x7FFF) * units.WattsPerCount,
            Enabled: (msr & (1UL << 47)) != 0,
            Clamped: (msr & (1UL << 48)) != 0,
            WindowSeconds: DecodeWindow((int)((msr >> 49) & 0x7F), units)),
        Locked: (msr & (1UL << 63)) != 0);

    /// <summary>
    /// Puts a new sustained limit into an existing register value, leaving everything else alone.
    ///
    /// Read-modify-write rather than composing a fresh value, because this register holds the
    /// burst limit, both time windows and both enable bits as well. Writing a value assembled
    /// from only what a caller cared about would silently zero the rest -- including PL2, which on
    /// a laptop is what lets the processor respond to anything at all.
    /// </summary>
    /// <exception cref="InvalidOperationException">The register is locked until the next reset.</exception>
    public static ulong WithSustainedWatts(ulong msr, double watts, RaplUnits units)
    {
        if ((msr & (1UL << 63)) != 0)
            throw new InvalidOperationException(
                "MSR_PKG_POWER_LIMIT is locked by firmware (bit 63) and cannot be changed until the "
                + "machine resets. Most laptop vendors lock it. Nothing was written.");

        if (watts < 0) throw new ArgumentOutOfRangeException(nameof(watts), "A power limit cannot be negative.");

        ulong counts = (ulong)Math.Round(watts / units.WattsPerCount);

        // Saturating rather than wrapping. A limit larger than the field can hold is a caller
        // mistake, and 15 bits of it silently becoming a very small number would ask the processor
        // for far LESS power than intended -- the opposite of what was meant, and the direction
        // that presents as "my laptop got slower and I do not know why".
        if (counts > 0x7FFF) counts = 0x7FFF;

        return (msr & ~0x7FFFUL) | counts;
    }

    /// <summary>
    /// The time window, which is not a plain integer.
    ///
    /// Bits 21:17 are an exponent and 23:22 a two-bit mantissa: the window is
    /// 2^y * (1 + z/4) time units. Reading the seven bits as one number gives an answer that looks
    /// reasonable and is wrong by orders of magnitude.
    /// </summary>
    private static double DecodeWindow(int field, RaplUnits units)
    {
        int y = field & 0x1F;
        int z = (field >> 5) & 0x3;
        return (1 << y) * (1.0 + z / 4.0) * units.SecondsPerCount;
    }
}
