// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Reading the machine's own memory and storage description, and refusing to improve on it.
///
/// The interesting failures here are all of the same kind: the firmware does not say something,
/// and the obvious code says it anyway. A board that labels its slots "BANK 0" and "BANK 1" has
/// said nothing about channels; a module whose part number field is the literal word "Unknown"
/// has no part number; an SMBIOS type this build has not heard of is not the nearest one it has.
/// </summary>
public class SystemInventoryTests
{
    private static MemoryModule Module(
        string slot = "Slot 1",
        string? channel = "A",
        double gb = 16,
        int rated = 5600,
        int configured = 5600,
        string? part = "SD5-5600",
        int type = 34) =>
        new(slot, channel, (ulong)(gb * 1024 * 1024 * 1024), rated, configured, part, type);

    private static MemoryConfig Config(params MemoryModule[] modules) =>
        new(modules, Slots: 2, MaxCapacityBytes: 34_359_738_368, Error: null);

    /// <summary>The ordinary case: two modules, two channels, at their rated speed.</summary>
    [Fact]
    public void TwoModulesInTwoChannelsAreDescribedAsSuch()
    {
        string text = Config(Module(channel: "A"), Module(slot: "Slot 2", channel: "B")).Describe();

        Assert.Contains("32 GB DDR5", text);
        Assert.Contains("2 of 2 slots populated", text);
        Assert.Contains("across 2 channels", text);
    }

    /// <summary>
    /// One channel is called out, with the reason it matters on this processor.
    ///
    /// The integrated GPU has no memory of its own, so a single channel is a graphics bandwidth
    /// figure as much as a memory one -- and it is the likeliest answer to "why is this slower
    /// than the same laptop somebody else has".
    /// </summary>
    [Fact]
    public void ASingleChannelIsCalledOut()
    {
        string text = Config(Module(channel: "A")).Describe();

        Assert.Contains("single channel", text);
        Assert.Contains("integrated GPU", text);
    }

    /// <summary>
    /// Unnamed channels report as unknown, not as one.
    ///
    /// This is the important one. Boards that label slots "BANK 0" and "BANK 1" are common, and
    /// counting those as a single channel would tell somebody their dual-channel machine was
    /// running in one -- a reading they might go and buy memory over.
    /// </summary>
    [Fact]
    public void UnnamedChannelsAreUnknownRatherThanOne()
    {
        var config = Config(Module(channel: null), Module(slot: "Slot 2", channel: null));

        Assert.Null(config.Channels);
        Assert.Contains("did not name any memory channels", config.Describe());
        Assert.DoesNotContain("single channel", config.Describe());
    }

    /// <summary>A bank label without the word "channel" in it yields no channel.</summary>
    [Theory]
    [InlineData("P0 CHANNEL A", "A")]
    [InlineData("p0 channel b", "b")]
    [InlineData("CHANNEL 1", "1")]
    [InlineData("BANK 0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyABankLabelThatNamesAChannelYieldsOne(string? label, string? expected) =>
        Assert.Equal(expected, SystemInventory.ChannelFrom(label));

    /// <summary>Memory running below its rating is reported, because that is correctable.</summary>
    [Fact]
    public void MemoryBelowItsRatingIsReported()
    {
        var config = Config(Module(rated: 5600, configured: 4800));

        Assert.Single(config.BelowRating);
        Assert.Contains("4800 MT/s against a rating of 5600 MT/s", config.Describe());
    }

    /// <summary>
    /// A module that does not report its rating is not "below" it.
    ///
    /// Zero is the firmware declining to answer, and treating it as a rating of nothing would
    /// have every such machine reported as down-clocked.
    /// </summary>
    [Fact]
    public void AnUnreportedRatingIsNotAShortfall() =>
        Assert.Empty(Config(Module(rated: 0, configured: 4800)).BelowRating);

    /// <summary>An SMBIOS type this build does not know is named by its number.</summary>
    [Fact]
    public void AnUnknownMemoryTypeIsReportedAsItself()
    {
        Assert.Equal("DDR5", Module(type: 34).TypeName);
        Assert.Equal("DDR4", Module(type: 26).TypeName);
        Assert.Equal("SMBIOS type 41", Module(type: 41).TypeName);
    }

    /// <summary>No modules is said plainly rather than described as 0 GB of something.</summary>
    [Fact]
    public void NoModulesIsSaidPlainly() =>
        Assert.Contains("reported no memory modules",
                        new MemoryConfig(Array.Empty<MemoryModule>(), 2, null, null).Describe());

    /// <summary>A failed read reports the reason instead of an empty machine.</summary>
    [Fact]
    public void AFailedReadReportsTheReason() =>
        Assert.Contains("WMI refused",
                        new MemoryConfig(Array.Empty<MemoryModule>(), null, null, "WMI refused").Describe());

    /// <summary>
    /// The drive health verdict is labelled as Windows' own, not as SMART.
    ///
    /// They are not the same claim. Windows reporting a drive healthy says nothing about its
    /// wear level, and a readout that let the two be confused would be the strongest statement
    /// in the panel resting on the weakest evidence.
    /// </summary>
    [Fact]
    public void TheDriveHealthVerdictIsNotPresentedAsSmart()
    {
        string text = SystemInventory.DescribeStorage(
            new[] { new StorageDevice("ADATA LEGEND 710", "SSD", "NVMe", 1_024_209_543_168, "healthy") },
            error: null);

        Assert.Contains("ADATA LEGEND 710", text);
        Assert.Contains("1024 GB NVMe SSD", text);
        Assert.Contains("rather than the drive's SMART data", text);
    }

    /// <summary>A storage namespace that refuses says so rather than reporting a machine with no drives.</summary>
    [Fact]
    public void AStorageFailureIsNotAnEmptyMachine() =>
        Assert.Contains("could not be read",
                        SystemInventory.DescribeStorage(Array.Empty<StorageDevice>(), "access denied"));

    /// <summary>
    /// Read-only, against this machine, in the same spirit as HardwareReadTests.
    ///
    /// Shape rather than content, so that a machine with soldered memory or a refused storage
    /// namespace passes too. The assertions catch the realistic failure, which is a unit error:
    /// reading MaxCapacityEx as bytes rather than kilobytes reports a board that takes 32 MB,
    /// and it looks entirely plausible until somebody does the arithmetic.
    /// </summary>
    [Fact]
    public void TheRealMachineReads()
    {
        var memory = SystemInventory.ReadMemory();

        if (memory.Error is null && memory.Modules.Count > 0)
        {
            Assert.All(memory.Modules, m => Assert.True(m.CapacityBytes > 0, "a module with no capacity"));

            // A populated board holds at least one module, so the ceiling cannot be below the
            // total. This is the unit error, caught.
            if (memory.MaxCapacityBytes is { } max)
                Assert.True(max >= memory.TotalBytes,
                            $"board maximum {max} is below the {memory.TotalBytes} installed");
        }

        var drives = SystemInventory.ReadStorage(out string? error);

        if (error is null)
            Assert.All(drives, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
    }
}
