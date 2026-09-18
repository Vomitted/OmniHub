// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniHub.Core.Hardware;

/// <summary>
/// The raw fan-level band a particular chassis actually uses.
///
/// The EC's fan byte is an RPM/100 target, not a 0-255 duty cycle, and the usable range is a
/// property of the chassis rather than of the interface. The values here were measured on one
/// machine: readback tracks the command up to 54 and then pins at 56, and a floor of 10 was
/// confirmed by commanding 0, 10, 15, 20, 25 and 30 in turn.
///
/// They are the right numbers for that machine and an assumption everywhere else, which is why
/// they are overridable. Anyone can measure their own with the Manual Calibration tool and put
/// the result in a profile rather than editing a constant and rebuilding.
/// </summary>
public sealed record FanCalibration(
    byte MinRawLevel,
    byte MaxRawLevelFan1,
    byte MaxRawLevelFan2)
{
    /// <summary>
    /// Measured on the HP Victus 15 fb2xxx, board 8C2F. Also the fallback for every machine with
    /// no profile, because it is the only band anyone has actually measured -- a wider guess
    /// would command speeds nobody has confirmed the fan reaches.
    /// </summary>
    public static readonly FanCalibration Default = new(MinRawLevel: 10, MaxRawLevelFan1: 56, MaxRawLevelFan2: 56);

    /// <summary>
    /// True when the numbers form a usable scale. A ceiling at or below the floor would collapse
    /// PercentToRaw's range to a single value, so every percentage would command the same speed
    /// and the curve would silently stop working.
    /// </summary>
    public bool IsUsable => MaxRawLevelFan1 > MinRawLevel && MaxRawLevelFan2 > MinRawLevel;

    // The conversions below used to be statics on FanService, reading a static band that startup
    // assigned once. They live here now because a band and the arithmetic that interprets it are
    // one thing: a percentage means nothing without the scale it is a percentage of, and holding
    // the two apart is what let a caller convert last week's log rows with this week's scale.

    /// <summary>
    /// The raw level a curve percentage maps to, against a given ceiling.
    ///
    /// percent == 0 maps to raw 0 rather than to the floor, and that is deliberate: raw is a real
    /// speed target, so 0 means "stop", and the curve's flat 0% segment through true idle exists
    /// to be silent. Without the special case every "silent" tick was sent as the floor -- audibly
    /// running, never silent. The floor remains correct for every percentage above zero.
    /// </summary>
    public byte PercentToRaw(byte percent, byte maxRaw) => percent == 0
        ? (byte)0
        : (byte)Math.Round(MinRawLevel + percent / 100.0 * (maxRaw - MinRawLevel));

    /// <summary>The raw level for fan 1, which is the ceiling the UI's percentages refer to.</summary>
    public byte PercentToRawFan1(byte percent) => PercentToRaw(percent, MaxRawLevelFan1);

    /// <summary>The raw level for fan 2. The two fans do not share a ceiling on every chassis.</summary>
    public byte PercentToRawFan2(byte percent) => PercentToRaw(percent, MaxRawLevelFan2);

    /// <summary>
    /// Inverse of <see cref="PercentToRawFan1"/>, for showing a level read back from the hardware
    /// as a percentage.
    ///
    /// Shares this record's one definition of the band so both directions cannot drift apart. The
    /// UI once did its own raw/255*100 conversion -- the 0-255 duty-cycle assumption this scale is
    /// not -- and under-reported every fan figure on screen by roughly half.
    /// </summary>
    public byte RawToPercent(byte raw) => raw == 0
        ? (byte)0
        : (byte)Math.Clamp(Math.Round((raw - MinRawLevel) * 100.0 / (MaxRawLevelFan1 - MinRawLevel)), 0, 100);

    /// <summary>
    /// Approximate RPM for a raw level, for display.
    ///
    /// A unit conversion rather than an estimate, but only on hardware whose raw byte is an
    /// RPM/100 target -- which is HP's encoding and not a universal one. A board taking a PWM duty
    /// cycle has no RPM to report here at all, which is why this hangs off the calibration rather
    /// than standing as a free function: the scale that defines the band is what knows what its
    /// units mean.
    ///
    /// It is the target the controller was given, not a tachometer reading, and the two differ
    /// while a fan is still spinning up.
    /// </summary>
    public int RawToRpm(byte raw) => raw * 100;

    /// <summary>
    /// An RPM figure for display, or the two dashes this application uses for "no reading".
    ///
    /// Here rather than in each view because several of them render it, and the alternative is
    /// several chances to write <c>RawToRpm(raw ?? 0)</c> -- which turns a level the board
    /// declined to report into a fan reading zero, the exact failure this application exists to
    /// detect. Unavailable is not stopped.
    /// </summary>
    public string RpmText(byte? raw) => raw is { } v ? RawToRpm(v).ToString() : "--";

    /// <summary>Lowest RPM this chassis will actually run the fans at, measured.</summary>
    public int MinRpm => RawToRpm(MinRawLevel);

    /// <summary>Highest RPM this chassis will actually run fan 1 at, measured.</summary>
    public int MaxRpm => RawToRpm(MaxRawLevelFan1);
}

/// <summary>
/// Per-model overrides, loaded from JSON beside the executable.
///
/// This is the mechanism ModelProfile has pointed at since it was written: "per-model overrides
/// go in /profiles/*.json as they're discovered via -Probe". Until now nothing read one, so a
/// machine whose fans run a different band had no way to say so short of editing a constant and
/// rebuilding from source.
///
/// Keyed on the baseboard product rather than the marketing name. Two laptops sold under the
/// same product string can carry different boards, and the board is what the EC belongs to.
/// </summary>
public static class FanProfiles
{
    /// <summary>Where profiles live: a folder beside the executable, so one can be dropped in.</summary>
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "profiles");

    private sealed class ProfileFile
    {
        [JsonPropertyName("baseboard")] public string? Baseboard { get; set; }
        [JsonPropertyName("minRawLevel")] public byte? MinRawLevel { get; set; }
        [JsonPropertyName("maxRawLevelFan1")] public byte? MaxRawLevelFan1 { get; set; }
        [JsonPropertyName("maxRawLevelFan2")] public byte? MaxRawLevelFan2 { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    /// <summary>Where this board's profile lives, whether or not it exists yet.</summary>
    public static string? PathFor(ModelInfo model, string? directory = null)
    {
        string baseboard = model.BaseboardProduct?.Trim() ?? "";
        if (baseboard.Length == 0) return null;
        if (baseboard.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        return Path.Combine(directory ?? DirectoryPath, baseboard + ".json");
    }

    /// <summary>
    /// Writes a measured band for this board, and returns where it went.
    ///
    /// Until now nothing wrote one of these. The loader has existed since per-model overrides
    /// were introduced, ModelProfile's own comment points at it, and the README tells people
    /// their measurements go here -- but the Manual Calibration tool could measure a chassis and
    /// then had nowhere to put the answer. So the feature ended at the interesting part: you
    /// could find out your fan band and then had to edit a constant and rebuild to use it.
    ///
    /// Refuses to write an unusable band for the same reason Load refuses to return one. A
    /// ceiling at or below the floor collapses the whole percentage scale onto one speed, which
    /// presents as fan control having silently stopped working -- and a file is a much worse
    /// place to discover that than a dialog.
    ///
    /// Written atomically: this file is read at startup, and a half-written one would be
    /// indistinguishable from a malformed one, which Load treats as "no profile" and falls back
    /// from without saying why.
    /// </summary>
    public static (bool Saved, string Detail) Save(
        ModelInfo model, FanCalibration calibration, string? note = null, string? directory = null)
    {
        if (!calibration.IsUsable)
            return (false, $"A ceiling of {calibration.MaxRawLevelFan1}/{calibration.MaxRawLevelFan2} "
                         + $"is not above the floor of {calibration.MinRawLevel}, so every percentage "
                         + "would command the same speed. Not saved.");

        if (PathFor(model, directory) is not { } path)
            return (false, "This machine does not report a baseboard product, so there is no name to file the profile under.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var file = new ProfileFile
            {
                Baseboard = model.BaseboardProduct?.Trim(),
                MinRawLevel = calibration.MinRawLevel,
                MaxRawLevelFan1 = calibration.MaxRawLevelFan1,
                MaxRawLevelFan2 = calibration.MaxRawLevelFan2,
                Note = string.IsNullOrWhiteSpace(note)
                    ? $"Measured on this machine {DateTime.UtcNow:yyyy-MM-dd}."
                    : note.Trim(),
            };

            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            return (true, $"Saved to {path}. It is applied at the next launch.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not write the profile: {ex.Message}");
        }
    }

    /// <summary>
    /// The calibration for this board, or null when there is no usable profile for it.
    ///
    /// Null rather than a partly-applied default: a profile naming two of the three values is one
    /// somebody got wrong, and quietly filling the gap from another machine's measurements would
    /// produce a fan scale belonging to neither.
    /// </summary>
    public static FanCalibration? Load(ModelInfo model, string? directory = null)
    {
        string baseboard = model.BaseboardProduct?.Trim() ?? "";
        if (baseboard.Length == 0) return null;

        // Reject anything that could climb out of the profiles folder. The name comes from WMI
        // rather than from a user, but a path built by concatenation deserves the check anyway.
        if (baseboard.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

        string path = Path.Combine(directory ?? DirectoryPath, baseboard + ".json");

        try
        {
            if (!File.Exists(path)) return null;

            var parsed = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path));
            if (parsed?.MinRawLevel is not byte min
                || parsed.MaxRawLevelFan1 is not byte max1
                || parsed.MaxRawLevelFan2 is not byte max2)
                return null;

            var calibration = new FanCalibration(min, max1, max2);

            // An unusable band is worse than no profile: it would command one speed for every
            // percentage on the curve, which looks like fan control having stopped working.
            return calibration.IsUsable ? calibration : null;
        }
        catch
        {
            // Malformed, unreadable, or locked. A bad profile must never stop the application
            // starting, and the measured default is a safe place to land.
            return null;
        }
    }
}
