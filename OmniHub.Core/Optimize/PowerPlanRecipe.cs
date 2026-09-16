using System.Runtime.InteropServices;
using System.Text;

namespace OmniHub.Core.Optimize;

/// <summary>One selectable value of an enumerated power setting, as Windows names it.</summary>
public sealed record PowerSettingOption(uint Index, string Name);

/// <summary>
/// One knob the plan builder can turn, described by Windows rather than by this application.
///
/// The indices are read rather than assumed for a reason that already bit this project: boost
/// mode was written as 3 under a comment saying "Aggressive", when on this hardware 3 is
/// Efficient Enabled and Aggressive is 2. Hard-coded indices are guesses about someone else's
/// machine, and they fail silently -- the plan applies, reports success, and does the wrong
/// thing.
/// </summary>
public sealed record PowerKnob(
    Guid Sub,
    Guid Id,
    string Key,
    string Label,
    string Units,
    uint Min,
    uint Max,
    IReadOnlyList<PowerSettingOption> Options)
{
    /// <summary>True when the value is picked from a list rather than typed as a number.</summary>
    public bool IsEnumerated => Options.Count > 0;

    /// <summary>Renders a raw index the way the machine would name it.</summary>
    public string Describe(uint value)
    {
        foreach (var o in Options)
            if (o.Index == value) return o.Name;

        if (Units.Length == 0) return value.ToString();

        // A zero timeout means "never", not "immediately", and showing "0 seconds" invites
        // exactly the wrong reading.
        return value == 0 && Units.StartsWith("second", StringComparison.OrdinalIgnoreCase)
            ? "never"
            : $"{value} {Units}";
    }
}

/// <summary>
/// A power plan expressed as a single intent -- battery life at one end, performance at the
/// other -- plus any values the user pinned by hand.
///
/// The bias is the whole point. Most people do not want to reason about PCI Express link state
/// management; they want "make this last" or "make this fast", or something between. Every knob
/// is derived from that one number, and anything set explicitly overrides the derivation and
/// stays put when the slider moves.
/// </summary>
public sealed record PowerPlanRecipe(string Name, int Bias)
{
    /// <summary>Explicit values, keyed by <see cref="PowerKnob.Key"/>, as (ac, dc) pairs.</summary>
    public Dictionary<string, (uint Ac, uint Dc)> Overrides { get; init; } = new();

    /// <summary>0 = maximum battery life, 100 = maximum performance.</summary>
    public int Bias { get; init; } = Math.Clamp(Bias, 0, 100);

    public static PowerPlanRecipe MaximumBattery(string name = "OmniHub Battery") => new(name, 0);
    public static PowerPlanRecipe Balanced(string name = "OmniHub Balanced") => new(name, 50);
    public static PowerPlanRecipe MaximumPerformance(string name = "OmniHub Performance") => new(name, 100);

    /// <summary>
    /// Linear interpolation between the battery-end and performance-end value of a knob.
    ///
    /// Rounded rather than truncated so the midpoint of a range lands on the midpoint of the
    /// slider instead of one below it.
    /// </summary>
    private static uint Lerp(uint atZero, uint atHundred, int bias)
    {
        double t = Math.Clamp(bias, 0, 100) / 100.0;
        return (uint)Math.Round(atZero + (atHundred - (double)atZero) * t);
    }

    /// <summary>
    /// Resolves the recipe into concrete values for the knobs this machine actually exposes.
    ///
    /// The DC column is never simply the AC column. A plan is a pair of policies, and a machine
    /// unplugged has different priorities regardless of what the plan is called: even at bias
    /// 100 the battery rail keeps boost off and a ceiling, so a performance plan left active on
    /// battery degrades rather than emptying the cell.
    /// </summary>
    public IReadOnlyList<(PowerKnob Knob, uint Ac, uint Dc)> Resolve(IReadOnlyList<PowerKnob> available)
    {
        var result = new List<(PowerKnob, uint, uint)>();
        int b = Bias;

        foreach (var k in available)
        {
            if (Overrides.TryGetValue(k.Key, out var pinned))
            {
                result.Add((k, Clamp(k, pinned.Ac), Clamp(k, pinned.Dc)));
                continue;
            }

            (uint ac, uint dc) = k.Key switch
            {
                "video" => (Lerp(120, 1800, b), Lerp(60, 600, b)),
                "disk"  => (Lerp(300, 0, b), Lerp(120, 600, b)),   // 0 = never spin down
                "sleep" => (Lerp(900, 0, b), Lerp(300, 1800, b)),  // 0 = never sleep
                "aspm"  => (AspmFor(k, b), MaxIndex(k)),           // battery always saves most
                _ => (Lerp(k.Min, k.Max, b), Lerp(k.Min, k.Max, b)),
            };

            result.Add((k, Clamp(k, ac), Clamp(k, dc)));
        }

        return result;
    }

    private static uint Clamp(PowerKnob k, uint value)
    {
        if (k.IsEnumerated)
        {
            foreach (var o in k.Options)
                if (o.Index == value) return value;
            return k.Options[0].Index;
        }
        return k.Max >= k.Min ? Math.Clamp(value, k.Min, k.Max) : value;
    }

    private static uint AspmFor(PowerKnob k, int bias) =>
        bias >= 66 ? MinIndex(k) : bias >= 34 ? MidIndex(k) : MaxIndex(k);

    private static uint MinIndex(PowerKnob k) => k.IsEnumerated ? k.Options[0].Index : k.Min;
    private static uint MaxIndex(PowerKnob k) => k.IsEnumerated ? k.Options[^1].Index : k.Max;
    private static uint MidIndex(PowerKnob k) =>
        k.IsEnumerated ? k.Options[k.Options.Count / 2].Index : (k.Min + k.Max) / 2;
}

/// <summary>
/// Reads what a power setting supports from Windows.
///
/// Everything the builder offers comes from here rather than from a table in this repository,
/// so a machine exposing a different set of boost modes, or no PCI Express control at all, gets
/// a builder that matches it instead of one that writes values it will silently ignore.
/// </summary>
public static class PowerKnobReader
{
    private static Guid SubVideo      = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static Guid SubDisk       = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static Guid SubSleep      = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");

    // The processor knobs -- minimum state, maximum state and boost mode -- are deliberately
    // not here. The builder used to offer them, which meant a plan built from it wrote the
    // owner's processor configuration out from under them. PowerPlanSetup.NeverWrite is the
    // backstop; leaving them out of this table is the actual fix, because a knob that is never
    // offered cannot be resolved, clamped or written.
    private static Guid Video   = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static Guid Disk    = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    private static Guid Standby = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static Guid Aspm    = new("ee12f906-d277-404b-b6da-e5fa1a576df5");

    private const uint ErrorSuccess = 0;

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadPossibleValue(IntPtr rootPowerKey, ref Guid subGroup,
        ref Guid setting, out uint type, uint possibleSettingIndex, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadPossibleFriendlyName(IntPtr rootPowerKey, ref Guid subGroup,
        ref Guid setting, uint possibleSettingIndex, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadValueMin(IntPtr rootPowerKey, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadValueMax(IntPtr rootPowerKey, ref Guid subGroup, ref Guid setting, out uint value);

    /// <summary>
    /// The knobs the builder offers, in the order they are shown.
    ///
    /// A setting the machine does not expose is left out entirely rather than shown disabled:
    /// an inert row invites clicking to find out what it does, and there is nothing to find.
    /// </summary>
    public static IReadOnlyList<PowerKnob> Read()
    {
        var wanted = new (Guid Sub, Guid Id, string Key, string Label, string Units)[]
        {
            (SubVideo,      Video,   "video",   "Turn off display after",   "seconds"),
            (SubDisk,       Disk,    "disk",    "Turn off disk after",      "seconds"),
            (SubSleep,      Standby, "sleep",   "Sleep after",              "seconds"),
            (SubPciExpress, Aspm,    "aspm",    "PCI Express power saving", ""),
        };

        var knobs = new List<PowerKnob>();
        foreach (var w in wanted)
        {
            try
            {
                var sub = w.Sub;
                var id = w.Id;

                var options = ReadOptions(ref sub, ref id);

                bool hasMin = PowerReadValueMin(IntPtr.Zero, ref sub, ref id, out uint min) == ErrorSuccess;
                bool hasMax = PowerReadValueMax(IntPtr.Zero, ref sub, ref id, out uint max) == ErrorSuccess;

                // Neither a range nor a list means the setting is not present on this machine.
                if (options.Count == 0 && !(hasMin && hasMax)) continue;

                knobs.Add(new PowerKnob(w.Sub, w.Id, w.Key, w.Label, w.Units, min, max, options));
            }
            catch
            {
                // One unreadable setting must not cost the whole builder.
            }
        }

        return knobs;
    }

    private static IReadOnlyList<PowerSettingOption> ReadOptions(ref Guid sub, ref Guid id)
    {
        var found = new List<PowerSettingOption>();

        // Enumerated settings are short lists; the cap only stops a malformed reply spinning.
        for (uint i = 0; i < 32; i++)
        {
            uint size = 0;
            if (PowerReadPossibleValue(IntPtr.Zero, ref sub, ref id, out _, i, null, ref size) != ErrorSuccess)
                break;

            uint nameSize = 0;
            PowerReadPossibleFriendlyName(IntPtr.Zero, ref sub, ref id, i, null, ref nameSize);
            if (nameSize == 0) break;

            var buffer = new byte[nameSize];
            if (PowerReadPossibleFriendlyName(IntPtr.Zero, ref sub, ref id, i, buffer, ref nameSize) != ErrorSuccess)
                break;

            found.Add(new PowerSettingOption(i, Encoding.Unicode.GetString(buffer).TrimEnd('\0')));
        }

        return found;
    }
}
