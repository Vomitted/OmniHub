// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// The Curve Optimizer encoding, which is the one piece of arithmetic here that can hurt the
/// machine rather than merely fail.
///
/// A refused power limit is a no-op. A mis-signed voltage offset is not: get it backwards and
/// a requested undervolt becomes a large positive offset, or a modest one becomes enormous.
/// The SMU will not complain either way, so this maths is the only thing standing between the
/// intent and the silicon.
/// </summary>
public class CurveOptimizerEncodingTests
{
    /// <summary>
    /// The exact case documented in RyzenAdj issue #296: -21 must go out as 0xFFFEB
    /// (1048555). Passing it as an ordinary negative int yields 0xFFFFFFEB (4294967275),
    /// which is a completely different value to the mailbox.
    /// </summary>
    [Fact]
    public void NegativeOffset_UsesTwentyBitTwosComplement()
    {
        Assert.Equal(0xFFFEBu, AmdTuning.EncodeCurveOptimizer(-21));
        Assert.Equal(1048555u, AmdTuning.EncodeCurveOptimizer(-21));
    }

    [Fact]
    public void NegativeOffset_IsNotThirtyTwoBitTwosComplement()
    {
        // The mistake this guards against, stated as its own assertion.
        Assert.NotEqual(unchecked((uint)-21), AmdTuning.EncodeCurveOptimizer(-21));
    }

    [Fact]
    public void PositiveOffset_IsSentAsIs()
    {
        Assert.Equal(0u, AmdTuning.EncodeCurveOptimizer(0));
        Assert.Equal(21u, AmdTuning.EncodeCurveOptimizer(21));
    }

    [Fact]
    public void EncodingStaysInsideTwentyBits()
    {
        // Anything wider would collide with the core index the per-core command packs above it.
        foreach (int counts in new[] { -30, -21, -1, 0, 1, 21, 30 })
            Assert.True(AmdTuning.EncodeCurveOptimizer(counts) <= 0xFFFFF,
                $"{counts} encoded outside 20 bits");
    }

    [Fact]
    public void AdjacentOffsets_DifferByOne()
    {
        // Two's complement is contiguous: -10 and -11 must be one apart, not wrap oddly.
        Assert.Equal(1u, AmdTuning.EncodeCurveOptimizer(-10) - AmdTuning.EncodeCurveOptimizer(-11));
    }
}

/// <summary>The premade profiles, checked for the properties that make them safe to click.</summary>
public class TuningProfileTests
{
    [Fact]
    public void NoPremadeProfile_SetsCurveOptimizer()
    {
        // An undervolt stable on one chip crashes the next, so it must never arrive as a
        // side effect of choosing a preset called "Performance".
        Assert.All(AmdTuning.Profiles, p => Assert.Null(p.CurveOptimizerAllCore));
    }

    [Fact]
    public void ProfilesAreOrderedByAscendingPower()
    {
        var watts = AmdTuning.Profiles.Select(p => p.StapmWatts ?? 0).ToList();
        Assert.Equal(watts.OrderBy(w => w), watts);
    }

    [Fact]
    public void EveryProfileStaysInsideTheSupportedBands()
    {
        Assert.All(AmdTuning.Profiles, p =>
        {
            Assert.InRange(p.StapmWatts!.Value, AmdTuning.MinWatts, AmdTuning.MaxWatts);
            Assert.InRange(p.FastWatts!.Value, AmdTuning.MinWatts, AmdTuning.MaxWatts);
            Assert.InRange(p.TctlTempC!.Value, AmdTuning.MinTempC, AmdTuning.MaxTempC);
        });
    }

    [Fact]
    public void BoostLimitIsNeverBelowSustained()
    {
        // A fast limit under the sustained one is incoherent: the burst ceiling would sit
        // below the long-run average it is supposed to sit above.
        Assert.All(AmdTuning.Profiles, p => Assert.True(p.FastWatts >= p.StapmWatts,
            $"{p.Name}: boost {p.FastWatts}W is below sustained {p.StapmWatts}W"));
    }
}
