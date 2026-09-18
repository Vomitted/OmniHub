// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Every figure here is from this machine: a 75 W ceiling, a 60 W default, real draws around 15 W
/// under no load, and the impossible 312.13 W the card reports in one in four samples.
/// </summary>
public class GpuPowerPlausibilityTests
{
    private const double Ceiling = 75;

    [Theory]
    [InlineData(12.4)]
    [InlineData(15.02)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(100)]    // above the ceiling but within the tolerance a real boost can reach
    public void ARealDrawIsBelieved(double watts) =>
        Assert.True(GpuPowerPlausibility.IsMeasurement(watts, Ceiling));

    [Fact]
    public void TheImpossibleReadingIsRefused()
    {
        // The one that started this: four times a limit the board enforces.
        Assert.False(GpuPowerPlausibility.IsMeasurement(312.13, Ceiling));
        Assert.Null(GpuPowerPlausibility.Filter(312.13, Ceiling));
    }

    [Fact]
    public void ACardWhoseCeilingIsUnknownIsBelieved()
    {
        // This refuses readings it can prove impossible. Without a limit it has proved nothing,
        // and discarding the reading anyway would turn one driver quirk into missing data on
        // every card whose limit cannot be read.
        Assert.True(GpuPowerPlausibility.IsMeasurement(312.13, null));
        Assert.Equal(312.13, GpuPowerPlausibility.Filter(312.13, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonsenseCeilingIsTreatedAsNoCeiling(double limit) =>
        Assert.True(GpuPowerPlausibility.IsMeasurement(300, limit));

    [Fact]
    public void AnIdleCardDrawingNothingIsStillAReading()
    {
        // Zero watts is a measurement, unlike the absent reading it must not be confused with.
        Assert.True(GpuPowerPlausibility.IsMeasurement(0, Ceiling));
        Assert.Equal(0, GpuPowerPlausibility.Filter(0, Ceiling));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5)]
    public void AValueThatIsNotAPowerIsRefusedEvenWithNoCeiling(double watts) =>
        Assert.False(GpuPowerPlausibility.IsMeasurement(watts, null));

    [Fact]
    public void AMissingReadingStaysMissing() =>
        Assert.Null(GpuPowerPlausibility.Filter(null, Ceiling));
}
