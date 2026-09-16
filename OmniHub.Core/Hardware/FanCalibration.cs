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
