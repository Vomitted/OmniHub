// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Management;

namespace OmniHub.Core.Hardware;

/// <summary>One memory module, as the firmware describes it.</summary>
/// <param name="Slot">The physical slot, e.g. "Bottom - Slot 1 (left)".</param>
/// <param name="Channel">The memory channel, when the firmware named one. Null when it did not.</param>
/// <param name="CapacityBytes">Size of this module.</param>
/// <param name="RatedMts">What the module is rated for, in MT/s.</param>
/// <param name="ConfiguredMts">What it is actually running at. Lower than rated means it was down-clocked.</param>
/// <param name="PartNumber">The module's part number, trimmed. Null when the firmware left it blank.</param>
/// <param name="SmbiosType">The raw SMBIOS memory type byte, kept so an unrecognised one can be reported as itself.</param>
public sealed record MemoryModule(
    string Slot,
    string? Channel,
    ulong CapacityBytes,
    int RatedMts,
    int ConfiguredMts,
    string? PartNumber,
    int SmbiosType)
{
    public double CapacityGb => CapacityBytes / (1024.0 * 1024 * 1024);

    /// <summary>
    /// The generation, from the SMBIOS memory-type table.
    ///
    /// An unrecognised value is reported as the number it is. The table gains entries with each
    /// SMBIOS revision, and a build from before the next one should say "SMBIOS type 41" rather
    /// than round down to the nearest generation it happens to know.
    /// </summary>
    public string TypeName => SmbiosType switch
    {
        18 => "DDR",
        19 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => $"SMBIOS type {SmbiosType}",
    };
}

/// <summary>
/// What memory is installed, how fast it is running, and whether it is running in more than one
/// channel.
/// </summary>
/// <param name="Modules">Every populated slot.</param>
/// <param name="Slots">Total slots on the board, populated or not. Null when the firmware did not say.</param>
/// <param name="MaxCapacityBytes">The most the board will take. Null when the firmware did not say.</param>
/// <param name="Error">Why the read failed, or null.</param>
public sealed record MemoryConfig(
    IReadOnlyList<MemoryModule> Modules,
    int? Slots,
    ulong? MaxCapacityBytes,
    string? Error)
{
    public ulong TotalBytes => Modules.Aggregate(0UL, (sum, m) => sum + m.CapacityBytes);

    public double TotalGb => TotalBytes / (1024.0 * 1024 * 1024);

    /// <summary>
    /// How many distinct channels the firmware named, or null when it named none.
    ///
    /// Null is not "one". Plenty of boards label their slots "BANK 0" and "BANK 1" and say
    /// nothing about channels at all, and a machine that is in fact dual-channel would then be
    /// reported as single -- which is the reading somebody would go and buy memory over.
    /// </summary>
    public int? Channels
    {
        get
        {
            int named = Modules.Select(m => m.Channel)
                               .Where(c => c is { Length: > 0 })
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .Count();

            return named == 0 ? null : named;
        }
    }

    /// <summary>Modules running slower than they are rated for, which is a fixable condition.</summary>
    public IReadOnlyList<MemoryModule> BelowRating =>
        Modules.Where(m => m.RatedMts > 0 && m.ConfiguredMts > 0 && m.ConfiguredMts < m.RatedMts).ToList();

    /// <summary>
    /// The configuration in a sentence, including the two facts that are worth acting on.
    ///
    /// Running in one channel and running below the module's rating are both correctable, and
    /// both are invisible in Task Manager's memory page. Everything else here is identification.
    /// </summary>
    public string Describe()
    {
        if (Error is { Length: > 0 }) return $"Memory configuration could not be read: {Error}";
        if (Modules.Count == 0) return "The firmware reported no memory modules.";

        var first = Modules[0];
        string text = $"{TotalGb:0.#} GB {first.TypeName} across {Modules.Count} module(s) at "
                    + $"{first.ConfiguredMts} MT/s";

        if (Slots is { } slots)
            text += $", {Modules.Count} of {slots} slots populated";

        text += ".";

        // Channel count first: on this processor the integrated GPU has no memory of its own, so
        // the channel count is a graphics bandwidth figure as much as a memory one.
        text += Channels switch
        {
            null => " The firmware did not name any memory channels, so this cannot say whether it "
                    + "is running single or dual channel.",
            1 => " Running in a single channel. The integrated GPU has no memory of its own, so on "
                 + "this processor that halves the bandwidth available to it as well as to the CPU.",
            var n => $" Running across {n} channels.",
        };

        if (BelowRating.Count > 0)
        {
            var slow = BelowRating[0];
            text += $" {BelowRating.Count} module(s) are running at {slow.ConfiguredMts} MT/s "
                  + $"against a rating of {slow.RatedMts} MT/s.";
        }

        if (first.PartNumber is { Length: > 0 } part)
            text += $" Part {part}.";

        return text;
    }
}

/// <summary>One physical drive, as Windows' storage stack describes it.</summary>
public sealed record StorageDevice(string Name, string Media, string Bus, long SizeBytes, string Health)
{
    public double SizeGb => SizeBytes / (1000.0 * 1000 * 1000);
}

/// <summary>
/// The static facts about this machine's memory and storage that nothing has ever reported.
///
/// "Why is this one slower than the other machine of the same model" is the first question
/// anybody asks, and on a laptop with an APU the answer is often the memory: one module instead
/// of two, or two running below their rating. Both are cheap to read, both are correctable, and
/// neither is visible anywhere in Windows without going looking.
///
/// The reads are WMI and the descriptions are pure functions over the records, so the half that
/// decides what to say can be tested without a machine to say it about.
/// </summary>
public static class SystemInventory
{
    /// <summary>Reads the installed memory. Never throws; failures come back in the record.</summary>
    public static MemoryConfig ReadMemory()
    {
        var modules = new List<MemoryModule>();
        int? slots = null;
        ulong? max = null;

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory"))
                foreach (ManagementObject mo in searcher.Get())
                {
                    using var _ = mo;
                    modules.Add(new MemoryModule(
                        Slot: Text(mo["DeviceLocator"]) ?? "unnamed slot",
                        Channel: ChannelFrom(Text(mo["BankLabel"])),
                        CapacityBytes: Convert.ToUInt64(mo["Capacity"] ?? 0UL),
                        RatedMts: Convert.ToInt32(mo["Speed"] ?? 0),
                        ConfiguredMts: Convert.ToInt32(mo["ConfiguredClockSpeed"] ?? 0),
                        PartNumber: Text(mo["PartNumber"]),
                        SmbiosType: Convert.ToInt32(mo["SMBIOSMemoryType"] ?? 0)));
                }

            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemoryArray"))
                foreach (ManagementObject mo in searcher.Get())
                {
                    using var _ = mo;
                    slots = Convert.ToInt32(mo["MemoryDevices"] ?? 0);

                    // MaxCapacityEx is in kilobytes, not bytes. Reading it as bytes reports a
                    // board that takes 32 MB.
                    max = Convert.ToUInt64(mo["MaxCapacityEx"] ?? 0UL) * 1024UL;
                    break;
                }
        }
        catch (Exception ex)
        {
            return new MemoryConfig(modules, slots, max, ex.Message);
        }

        return new MemoryConfig(modules, slots, max, null);
    }

    /// <summary>Reads the physical drives. Empty with a reason when the storage namespace refuses.</summary>
    public static IReadOnlyList<StorageDevice> ReadStorage(out string? error)
    {
        var devices = new List<StorageDevice>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\microsoft\windows\storage", "SELECT * FROM MSFT_PhysicalDisk");

            foreach (ManagementObject mo in searcher.Get())
            {
                using var _ = mo;
                devices.Add(new StorageDevice(
                    Name: Text(mo["FriendlyName"]) ?? "unnamed drive",
                    Media: MediaName(Convert.ToInt32(mo["MediaType"] ?? 0)),
                    Bus: BusName(Convert.ToInt32(mo["BusType"] ?? 0)),
                    SizeBytes: Convert.ToInt64(mo["Size"] ?? 0L),
                    Health: HealthName(Convert.ToInt32(mo["HealthStatus"] ?? 0))));
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return devices;
        }

        error = null;
        return devices;
    }

    /// <summary>
    /// The drives in a sentence.
    ///
    /// The health figure is Windows' own summary, and it is named as such: it is not SMART, it
    /// does not know the drive's wear level, and a healthy verdict here is a good deal weaker
    /// than one from the drive's own reliability counters.
    /// </summary>
    public static string DescribeStorage(IReadOnlyList<StorageDevice> devices, string? error)
    {
        if (error is { Length: > 0 }) return $"The drives could not be read: {error}";
        if (devices.Count == 0) return "Windows reported no physical drives.";

        var lines = devices.Select(d =>
            $"{d.Name}, {d.SizeGb:0} GB {d.Bus} {d.Media}, Windows reports it {d.Health}");

        return string.Join(". ", lines)
             + ". That health verdict is Windows' own summary rather than the drive's SMART data, "
             + "so it knows nothing about wear level.";
    }

    /// <summary>
    /// Pulls the channel out of a bank label like "P0 CHANNEL A".
    ///
    /// Only where the firmware actually uses the word. "BANK 0" says nothing about channels, and
    /// reading a channel into it would turn an unknown into a confident wrong answer.
    /// </summary>
    internal static string? ChannelFrom(string? bankLabel)
    {
        if (bankLabel is not { Length: > 0 }) return null;

        int at = bankLabel.IndexOf("CHANNEL", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        string rest = bankLabel[(at + "CHANNEL".Length)..].Trim();
        return rest.Length > 0 ? rest : null;
    }

    /// <summary>WMI hands back blanks, padding and the literal word "Unknown". None of those is a value.</summary>
    private static string? Text(object? value)
    {
        string? text = value?.ToString()?.Trim();

        return text is { Length: > 0 } && !text.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? text
            : null;
    }

    private static string MediaName(int value) => value switch
    {
        3 => "hard disk",
        4 => "SSD",
        5 => "storage-class memory",
        _ => "drive of unstated type",
    };

    private static string BusName(int value) => value switch
    {
        7 => "USB",
        8 => "RAID",
        11 => "SATA",
        17 => "NVMe",
        _ => $"bus type {value}",
    };

    private static string HealthName(int value) => value switch
    {
        0 => "healthy",
        1 => "in a warning state",
        2 => "unhealthy",
        _ => "of unstated health",
    };
}
