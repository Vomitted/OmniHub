// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

public class HardwareNamesTests
{
    [Theory]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "Ryzen 7 7840HS")]
    [InlineData("AMD Ryzen 5 5600H with Radeon Graphics", "Ryzen 5 5600H")]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", "Ryzen 9 5900X")]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700H", "Core i7-12700H")]
    [InlineData("Intel(R) Core(TM) i7-8750H CPU @ 2.20GHz", "Core i7-8750H")]
    public void AProcessorKeepsItsModelAndLosesTheSpecSheet(string reported, string expected) =>
        Assert.Equal(expected, HardwareNames.ShortCpu(reported));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4050 Laptop GPU", "RTX 4050 Laptop")]
    [InlineData("AMD Radeon RX 7600S", "Radeon RX 7600S")]
    public void AGraphicsCardKeepsItsModel(string reported, string expected) =>
        Assert.Equal(expected, HardwareNames.ShortGpu(reported));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void NothingReportedIsNothing(string? reported)
    {
        Assert.Null(HardwareNames.ShortCpu(reported));
        Assert.Null(HardwareNames.ShortGpu(reported));
    }
}
