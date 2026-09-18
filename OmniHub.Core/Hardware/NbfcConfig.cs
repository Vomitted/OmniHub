// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniHub.Core.Hardware;

/// <summary>One fan as a NoteBook FanControl configuration describes it.</summary>
public sealed record NbfcFan
{
    public int ReadRegister { get; init; }
    public int WriteRegister { get; init; }

    /// <summary>
    /// The value that produces the SLOWEST fan, which is not necessarily the smaller number.
    ///
    /// Several controllers run inverted -- a higher value means less fan -- and NBFC expresses
    /// that by simply putting the larger number here. Reading these as an ordered pair would
    /// produce an empty range on those boards, and an empty range means either every write is
    /// refused or, far worse, the bounds get swapped by something that assumed they were sorted.
    /// </summary>
    public int MinSpeedValue { get; init; }

    /// <summary>The value that produces the FASTEST fan. May be lower than <see cref="MinSpeedValue"/>.</summary>
    public int MaxSpeedValue { get; init; }

    public bool IndependentReadMinMaxValues { get; init; }
    public int MinSpeedValueRead { get; init; }
    public int MaxSpeedValueRead { get; init; }

    public bool ResetRequired { get; init; }
    public int FanSpeedResetValue { get; init; }

    public string? FanDisplayName { get; init; }
}

/// <summary>
/// A NoteBook FanControl model configuration, read as it is actually written.
///
/// NBFC and its Linux port between them hold configurations for over three hundred laptops, each
/// naming the EC registers that board's fans live at. That corpus is the single largest body of
/// measured knowledge about laptop embedded controllers in existence, and it is GPL-3.0, which
/// this project now is too.
///
/// The schema here was taken from a real file rather than from documentation, and doing so
/// corrected two things worth having corrected. Registers are plain decimal integers, not hex, so
/// the HP Pavilion fan register appears as 88 rather than 0x58. And MinSpeedValue is allowed to be
/// GREATER than MaxSpeedValue, because some controllers run inverted -- assuming an ordered pair
/// would have produced an empty range on those boards.
///
/// Importing one of these is not verification and does not grant permission to write. The map it
/// produces describes what the board accepts; whether this application may act on that is the
/// tier's business, and a backend built from an imported config starts at Reading.
/// </summary>
public sealed record NbfcConfig
{
    public string? NotebookModel { get; init; }
    public string? Author { get; init; }

    /// <summary>
    /// How often the controller must be rewritten, in milliseconds.
    ///
    /// Not a preference. On several boards -- the documented Pavilion among them -- the firmware
    /// takes fan control back if the value is not refreshed, so this is the interval at which a
    /// command stays a command. A loop slower than this does not control the fan; it nudges it.
    /// </summary>
    public int EcPollInterval { get; init; }

    public bool ReadWriteWords { get; init; }
    public int CriticalTemperature { get; init; }

    public IReadOnlyList<NbfcFan> FanConfigurations { get; init; } = Array.Empty<NbfcFan>();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Parses a configuration, or explains why it could not be used.
    ///
    /// Never throws. A configuration is a file somebody else wrote for a machine nobody here has,
    /// which is precisely the input that should not be able to take an application down.
    /// </summary>
    public static NbfcConfig? Parse(string json, out string? problem)
    {
        try
        {
            var config = JsonSerializer.Deserialize<NbfcConfig>(json, Options);

            if (config is null)
            {
                problem = "The file did not contain a configuration.";
                return null;
            }

            if (config.FanConfigurations.Count == 0)
            {
                problem = $"'{config.NotebookModel ?? "this configuration"}' describes no fans, "
                          + "so there is nothing in it to use.";
                return null;
            }

            problem = null;
            return config;
        }
        catch (JsonException ex)
        {
            problem = $"The file is not valid JSON ({ex.Message}).";
            return null;
        }
    }

    /// <summary>Reads a configuration from disk. Returns null with a reason for any failure.</summary>
    public static NbfcConfig? Load(string path, out string? problem)
    {
        try
        {
            return Parse(File.ReadAllText(path), out problem);
        }
        catch (Exception ex)
        {
            problem = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// The registers this configuration describes, as something that can refuse a write.
    ///
    /// The translation is deliberately narrow in three ways.
    ///
    /// The accepted range is exactly what the configuration states, normalised for direction and
    /// not widened by a single value. The person who wrote it measured that band on the machine;
    /// this has not, and a range extended for tidiness would be this application inventing
    /// headroom on hardware it has never seen.
    ///
    /// A register outside a byte is dropped rather than truncated. The schema stores plain
    /// integers and nothing stops a malformed file carrying 300, which would silently become 44.
    ///
    /// A read register is added as read-only when it differs from the write register, so a fan's
    /// speed can be watched on a board whose control has not been verified -- which is the whole
    /// shape of this release.
    /// </summary>
    public EcRegisterMap ToRegisterMap(string board)
    {
        var registers = new List<EcRegister>();
        var seen = new HashSet<byte>();

        foreach (var fan in FanConfigurations)
        {
            string name = string.IsNullOrWhiteSpace(fan.FanDisplayName) ? "fan" : fan.FanDisplayName!;

            if (AsByte(fan.WriteRegister) is { } write && seen.Add(write))
            {
                // Direction is the board's business, not ours: taking the lower of the pair as the
                // floor keeps an inverted controller's range intact instead of emptying it.
                // Direction is the board's business, not ours: taking the lower of the pair as the
                // floor keeps an inverted controller's range intact instead of emptying it.
                byte low = (byte)Math.Min(fan.MinSpeedValue, fan.MaxSpeedValue);
                byte high = (byte)Math.Max(fan.MinSpeedValue, fan.MaxSpeedValue);

                registers.Add(new EcRegister($"{name} speed", write, low, high, Writable: true));
            }

            if (AsByte(fan.ReadRegister) is { } read && seen.Add(read))
            {
                byte low = (byte)Math.Min(fan.MinSpeedValueRead, fan.MaxSpeedValueRead);
                byte high = (byte)Math.Max(fan.MinSpeedValueRead, fan.MaxSpeedValueRead);

                registers.Add(new EcRegister($"{name} speed readback", read, low, high));
            }
        }

        return new EcRegisterMap(board, registers);

        static byte? AsByte(int value) => value is >= 0 and <= 255 ? (byte)value : null;
    }

    /// <summary>
    /// Whatever about this configuration should be said out loud before anybody relies on it.
    ///
    /// Not a validity check -- Parse already refused the unusable ones. These are things that are
    /// legal in the format and still worth a person knowing, because the file was written for a
    /// machine and the person reading this may not have that machine.
    /// </summary>
    public IReadOnlyList<string> Cautions()
    {
        var cautions = new List<string>();

        if (ReadWriteWords)
            cautions.Add("This configuration uses 16-bit register access, which OmniHub's "
                         + "byte-wide EC path does not perform. Its fan registers cannot be used here.");

        foreach (var fan in FanConfigurations)
        {
            string name = string.IsNullOrWhiteSpace(fan.FanDisplayName) ? "a fan" : fan.FanDisplayName!;

            if (fan.MinSpeedValue > fan.MaxSpeedValue)
                cautions.Add($"{name} runs inverted on this board: {fan.MinSpeedValue} is its "
                             + $"slowest and {fan.MaxSpeedValue} its fastest.");

            if (fan.ResetRequired)
                cautions.Add($"{name} needs {fan.FanSpeedResetValue} written to hand control back "
                             + "to the firmware; stopping without it leaves the fan where it was.");

            if (fan.WriteRegister is < 0 or > 255 || fan.ReadRegister is < 0 or > 255)
                cautions.Add($"{name} names a register outside a byte, which has been ignored.");
        }

        return cautions;
    }
}
