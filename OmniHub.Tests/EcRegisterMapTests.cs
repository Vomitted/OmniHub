// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The rule that nothing reaches an embedded controller unless somebody established it was safe.
///
/// This is the most consequential test class in the project, and it is worth saying why plainly.
/// Everything else here protects a number on a screen or a decision about a fan curve. This
/// protects the machine: an EC register does whatever that board's firmware says it does, and the
/// published Pavilion fan register turns a value above 0x5A into an immediate power-off.
///
/// None of it can be verified on hardware by anybody here, which is exactly the argument for
/// testing the reasoning as hard as possible where it CAN be tested.
/// </summary>
public class EcRegisterMapTests
{
    private const string Board = "PAVILION-EXAMPLE";

    private static EcRegisterMap Map() => EcRegisterMap.PavilionExample(Board);

    [Fact]
    public void TheDocumentedShutdownValueIsRefused()
    {
        // The case the class exists for. 0x5B is one past the published ceiling, and on a real
        // Pavilion it is the difference between a fan command and the machine powering itself off.
        var refused = Assert.Throws<EcWriteRefusedException>(() => Map().CheckWrite(0x58, 0x5B));

        Assert.Equal(0x58, refused.Address);
        Assert.Equal(0x5B, refused.Value);
        Assert.Contains("5A", refused.Message);
    }

    [Theory]
    [InlineData((byte)0x5C)]
    [InlineData((byte)0x64)]
    [InlineData((byte)0x80)]
    [InlineData((byte)0xFF)]
    public void NothingAboveTheCeilingGetsThroughByAnyRoute(byte value)
    {
        // Not just the boundary. A bug producing 0xFF is far likelier than one producing exactly
        // 0x5B, and 0xFF is the value an uninitialised buffer carries.
        Assert.Throws<EcWriteRefusedException>(() => Map().CheckWrite(0x58, value));
        Assert.False(Map().Allows(0x58, value));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x2D)]
    [InlineData((byte)0x5A)]
    public void EverythingInsideTheEstablishedRangeIsAllowed(byte value)
    {
        // The converse, and it has to be here: a guard that refuses everything would pass the
        // tests above and control no fan at all.
        Map().CheckWrite(0x58, value);
        Assert.True(Map().Allows(0x58, value));
    }

    [Fact]
    public void ARegisterNobodyDescribedIsNeverWritten()
    {
        // The default answer for an address not in the map is no. An EC has 256 of these and this
        // map knows about one; the other 255 do things nobody here has established, and some of
        // them are battery, charging and thermal-trip configuration.
        var refused = Assert.Throws<EcWriteRefusedException>(() => Map().CheckWrite(0x59, 0x20));

        Assert.Contains("not described", refused.Message);
        Assert.Contains(Board, refused.Message);
    }

    [Fact]
    public void ARegisterMarkedReadOnlyIsNotWritableEvenInRange()
    {
        // Most useful registers are read-only to this application: a tachometer, a temperature, a
        // battery figure. Reading them is how support widens. Writing them is not, and a value
        // that happens to sit inside a range does not make it so.
        var map = new EcRegisterMap(Board, new[]
        {
            new EcRegister("fan tachometer", 0x2A, Min: 0x00, Max: 0xFF),
        });

        var refused = Assert.Throws<EcWriteRefusedException>(() => map.CheckWrite(0x2A, 0x10));

        Assert.Contains("read-only", refused.Message);
        Assert.False(map.Allows(0x2A, 0x10));

        // But it is still describable, which is the point of having it in the map at all.
        Assert.NotNull(map.At(0x2A));
    }

    [Fact]
    public void ARefusalNamesTheRegisterTheBoardAndWhatWouldBeAccepted()
    {
        // A refusal nobody can act on is a slightly politer silent failure. Somebody reading this
        // in a log should be able to tell whether they hit a bug or a boundary.
        var refused = Assert.Throws<EcWriteRefusedException>(() => Map().CheckWrite(0x58, 0x90));

        Assert.Contains("0x58", refused.Message);
        Assert.Contains(Board, refused.Message);
        Assert.Contains("fan speed", refused.Message);
        Assert.Contains("0x00", refused.Message);
        Assert.Contains("0x5A", refused.Message);
    }

    [Fact]
    public void AskingWhetherAWriteIsAllowedAgreesWithTrying()
    {
        // Two answers to one question is how a control gets offered over a write that will be
        // refused, or withheld over one that would have worked.
        var map = Map();

        for (int value = 0; value <= 0xFF; value++)
        {
            bool allowed = map.Allows(0x58, (byte)value);
            bool threw = Record.Exception(() => map.CheckWrite(0x58, (byte)value)) is not null;

            Assert.Equal(allowed, !threw);
        }
    }

    [Fact]
    public void TheTransportRefusesToOpenWithNothingToBeSafeAbout()
    {
        // A board with no established register map has nothing this could safely read, so opening
        // is declined before the driver is even asked. The failure has to be an explanation rather
        // than a silence -- on the great majority of machines EC access will legitimately be
        // unavailable, and that is an ordinary state, not a fault.
        var empty = new EcRegisterMap("UNKNOWN-BOARD", Array.Empty<EcRegister>());

        var access = EcAccess.TryOpen(empty, out string? reason);

        Assert.Null(access);
        Assert.NotNull(reason);
        Assert.Contains("UNKNOWN-BOARD", reason);
    }

    [Fact]
    public void WithoutTheModuleThereIsNoEcAccessAtAll()
    {
        // The module is not shipped and nothing downloads it, so this is the state every machine
        // is in today -- including the one this was written on. The test exists to keep it a
        // clean, explained refusal rather than a throw, because it is the common path.
        var access = EcAccess.TryOpen(Map(), out string? reason);

        if (access is null)
        {
            Assert.NotNull(reason);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        else
        {
            // Somebody has put the module in place. Then the map still governs everything, which
            // is the property worth holding either way.
            Assert.Equal(Board, access.Map.Board);
            access.Dispose();
        }
    }

    [Fact]
    public void AMapBelongsToOneBoardAndSaysWhich()
    {
        // Maps do not transfer. Two laptops sold under the same marketing name can carry
        // different boards, and the board is what the EC belongs to -- which is why this project
        // already keys fan calibration on the baseboard rather than the product string.
        Assert.Equal(Board, Map().Board);
        Assert.Contains(Board, Assert.Throws<EcWriteRefusedException>(
            () => Map().CheckWrite(0x58, 0xFF)).Message);
    }
}
