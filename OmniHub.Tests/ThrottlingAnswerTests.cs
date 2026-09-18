// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Turning HP's throttling byte into a plain answer.
///
/// The mapping is three lines and could not be tested at all until it was pulled out of the
/// method that reads the hardware. It is worth testing because it is the boundary where a vendor
/// value stops being a vendor value: everything past it -- the poll loop, the Reading record, six
/// views, the tray flyout and the thermal log's throttling column -- sees only what this decides.
/// </summary>
public class ThrottlingAnswerTests
{
    [Fact]
    public void ThrottlingIsReportedAsThrottling()
    {
        Assert.True(SystemController.ThrottlingFrom(ThrottlingState.On));
    }

    [Fact]
    public void NoAnswerIsNotTheSameAsNotThrottling()
    {
        // The distinction the nullable exists for. GetThrottling returns Unknown when the WMI
        // call failed outright, and collapsing that to false would have the application state
        // that the processor is fine on the strength of a question nobody answered.
        Assert.Null(SystemController.ThrottlingFrom(ThrottlingState.Unknown));

        Assert.NotEqual(SystemController.ThrottlingFrom(ThrottlingState.Unknown),
                        SystemController.ThrottlingFrom(ThrottlingState.Default));
    }

    [Fact]
    public void TheDefaultByteStillMeansNotThrottling()
    {
        // Deliberately false rather than null, even though this byte is under suspicion of being
        // an echo of the selector that was sent rather than a hardware state -- see the doc on
        // GetThrottling and ThrottlingProbe.
        //
        // Every caller previously tested for On and treated everything else as not throttling, so
        // false is exactly what this machine has always displayed. Changing it here would be a
        // behaviour change wearing a refactor's clothes; if the echo suspicion is ever confirmed,
        // the honest answer is to stop reporting the reading, not to quietly widen its meaning.
        Assert.False(SystemController.ThrottlingFrom(ThrottlingState.Default));
    }

    [Theory]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x7F)]
    [InlineData((byte)0xFF)]
    public void AByteNobodyHasSeenIsNotReadAsThrottling(byte raw)
    {
        // GetThrottling casts whatever arrives straight to the enum, so a board answering
        // something outside the three known values lands here. Not throttling is the safe
        // reading: the alarming interpretation of an unrecognised byte would show a throttling
        // warning on hardware that never said anything of the kind.
        Assert.False(SystemController.ThrottlingFrom((ThrottlingState)raw));
    }
}
