// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Catching the cheap fan read when it reports a fan that has not stopped.
///
/// The fan level can be asked for two ways. The expensive one was measured at 306 ms of a 324 ms
/// poll tick, so the cheap one is preferred where the board agrees -- and the choice between them
/// is made once, from a single comparison, then trusted for the whole session.
///
/// On this machine that produced 290 rows in one afternoon where fan 2 read zero while fan 1 kept
/// reporting, the die sat between 46 and 82 C, and the readings recovered only on restart, when
/// the choice is made afresh. A stopped fan on a hot machine is the precise fault this
/// application exists to catch, so a zero is the one answer worth paying to check.
/// </summary>
public class FanLevelCrossCheckTests
{
    /// <summary>The observed fault: one fan zero on the cheap read, spinning on the other.</summary>
    [Fact]
    public void AZeroContradictedByTheExpensiveReadIsWrong() =>
        Assert.True(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 27, 0, 0, 0 },
            expensive: new byte[] { 27, 26, 0, 0 },
            expensiveReported: 4));

    /// <summary>Either fan, not just the second one.</summary>
    [Fact]
    public void TheFirstFanCountsToo() =>
        Assert.True(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 0, 26, 0, 0 },
            expensive: new byte[] { 25, 26, 0, 0 },
            expensiveReported: 4));

    /// <summary>
    /// Fans genuinely at rest agree, and must not demote the cheap method.
    ///
    /// This is the ordinary case on an idle machine handed back to the BIOS. Treating it as a
    /// disagreement would put every session onto the 306 ms read for no reason at all.
    /// </summary>
    [Fact]
    public void BothStoppedIsAgreementNotAFault() =>
        Assert.False(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 0, 0, 0, 0 },
            expensive: new byte[] { 0, 0, 0, 0 },
            expensiveReported: 4));

    /// <summary>
    /// Two spinning fans differing by a unit or two is a ramp, not a fault.
    ///
    /// The reads are consecutive rather than simultaneous, and a fan takes about six seconds to
    /// complete a step, so small disagreements are expected and are not what this looks for.
    /// </summary>
    [Fact]
    public void ARampingDisagreementIsNotAFault() =>
        Assert.False(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 24, 25, 0, 0 },
            expensive: new byte[] { 27, 26, 0, 0 },
            expensiveReported: 4));

    /// <summary>
    /// A cheap zero that the expensive read also calls zero stands.
    ///
    /// The point is not to disbelieve zeros. It is to disbelieve zeros the method known to work
    /// disagrees with -- a real stall has to survive this check, or the cross-check would hide
    /// the fault it was written to expose.
    /// </summary>
    [Fact]
    public void ARealStallSurvivesTheCheck() =>
        Assert.False(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 0, 0, 0, 0 },
            expensive: new byte[] { 0, 0, 0, 0 },
            expensiveReported: 128));

    /// <summary>
    /// An expensive reply too short to read cannot contradict anything.
    ///
    /// Otherwise a board that answered with nothing would demote the cheap method on the strength
    /// of padding -- the same mistake, one layer up.
    /// </summary>
    [Fact]
    public void AnUnusableSecondOpinionIsNotEvidence() =>
        Assert.False(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 27, 0, 0, 0 },
            expensive: new byte[] { 27, 26, 0, 0 },
            expensiveReported: 1));

    /// <summary>A truncated cheap reply is not evidence either.</summary>
    [Fact]
    public void ATruncatedCheapReplyIsNotEvidence() =>
        Assert.False(FanController.CheapReplyIsWrong(
            cheap: new byte[] { 0 },
            expensive: new byte[] { 25, 26, 0, 0 },
            expensiveReported: 4));
}
