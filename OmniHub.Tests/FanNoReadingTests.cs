// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The one fan reply on this board that is not a reading.
///
/// Every case here is drawn from the counts in the real 192,791-row thermal trace, so a change
/// that widened or narrowed the rejection would be measurably wrong rather than merely different.
/// </summary>
public class FanNoReadingTests
{
    [Fact]
    public void TheSentinelPairIsNotAReading()
    {
        Assert.True(FanController.IsNoReading(27, 0));
    }

    [Theory]
    [InlineData(27, 24)]   // 4,426 rows -- the commonest partner of 27
    [InlineData(27, 27)]   // 4,071 rows
    [InlineData(27, 28)]
    [InlineData(27, 26)]
    public void TwentySevenIsAnOrdinaryValueBesideAnyOtherPartner(byte fan1, byte fan2)
    {
        // It is the pair that is the sentinel, not the number. 27 appears with a live second fan
        // in the majority of its 15,236 appearances, and rejecting the value would throw those
        // readings away.
        Assert.False(FanController.IsNoReading(fan1, fan2));
    }

    [Fact]
    public void BothFansGenuinelyStoppedIsStillAReading()
    {
        // 8,902 rows, and the state the whole application exists to notice when it happens while
        // the machine is hot. Suppressing it would hide the fault rather than the artefact.
        Assert.False(FanController.IsNoReading(0, 0));
    }

    [Theory]
    [InlineData(26, 0)]
    [InlineData(28, 0)]
    [InlineData(0, 27)]
    public void NeighbouringPairsAreNotAssumedToBeSentinelsToo(byte fan1, byte fan2)
    {
        // 26 and 28 beside a stopped fan 2 occur 70 and 63 times against 6,400 for 27, which is
        // the distribution of a real reading near a real value rather than a second sentinel.
        Assert.False(FanController.IsNoReading(fan1, fan2));
    }
}
