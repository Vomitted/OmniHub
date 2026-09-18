// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// A short BIOS reply must not be read as data.
///
/// The vendor call pads its answer out to whatever buffer size was asked for, so every reply is
/// the full length whether or not the board filled it. Every reader in this project then indexes
/// into that buffer, and the padding reads exactly like a measurement: a fan count of zero, a
/// charger reported as its enum's first member, a throttling state nobody read. Three readers
/// carried a "length greater than zero" check against precisely this, and not one of them could
/// ever have fired, because the length is always what was requested.
///
/// The guard itself needs a machine to exercise. The rule it enforces does not, and the rule is
/// where the realistic mistake lives -- a reader that wants data[1] needs two bytes, not one.
/// </summary>
public class BiosReplyLengthTests
{
    /// <summary>Exactly enough is enough. This is the boundary the off-by-one would move.</summary>
    [Fact]
    public void ExactlyEnoughBytesIsAccepted()
    {
        BiosInterop.RequireReported(reported: 1, needed: 1, commandId: 0x10);
        BiosInterop.RequireReported(reported: 2, needed: 2, commandId: 0x35);
        BiosInterop.RequireReported(reported: 4, needed: 4, commandId: 0x21);
    }

    /// <summary>More than enough is fine: the extra bytes simply go unread.</summary>
    [Fact]
    public void MoreThanEnoughIsAccepted() =>
        BiosInterop.RequireReported(reported: 128, needed: 2, commandId: 0x35);

    /// <summary>
    /// One byte short is refused.
    ///
    /// The case that matters is the throttling read, which indexes data[1] and so needs two
    /// bytes. A guard written as "at least one" would pass a one-byte reply and then report a
    /// throttling state assembled from padding -- and this application raises a tray
    /// notification off that value.
    /// </summary>
    [Fact]
    public void OneByteShortIsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => BiosInterop.RequireReported(reported: 1, needed: 2, commandId: 0x35));

    /// <summary>An empty reply is refused rather than read as a buffer of zeroes.</summary>
    [Fact]
    public void AnEmptyReplyIsRefused() =>
        Assert.Throws<InvalidOperationException>(
            () => BiosInterop.RequireReported(reported: 0, needed: 1, commandId: 0x10));

    /// <summary>
    /// The refusal says what happened, in the terms somebody debugging it would use.
    ///
    /// "Padding added by this layer, not data" is the sentence that would have saved the time
    /// spent reading a fan reported as stopped on a hot machine.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheCommandAndTheShortfall()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => BiosInterop.RequireReported(reported: 1, needed: 2, commandId: 0x2D));

        Assert.Contains("0x2D", error.Message);
        Assert.Contains("1 byte(s)", error.Message);
        Assert.Contains("needs 2", error.Message);
        Assert.Contains("padding", error.Message);
    }
}
