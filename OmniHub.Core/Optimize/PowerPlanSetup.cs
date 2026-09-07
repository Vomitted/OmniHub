using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

/// <summary>
/// Creates and configures the two power schemes OmniHub switches between.
///
/// It builds NEW schemes rather than editing the one you already use, and that is the whole
/// design. A tool that reaches into the active plan and rewrites processor values is a tool
/// that silently undoes a deliberate configuration -- someone who set boost off because their
/// laptop runs hot does not expect a fan utility to turn it back on. Duplicating a scheme
/// leaves the original untouched and reversible: delete the OmniHub plans and nothing about the
/// machine's own settings has moved.
///
/// Every setting written here is a documented Microsoft power-setting GUID. Nothing is guessed.
/// </summary>
public static class PowerPlanSetup
{
    // Subgroups
    private static Guid SubProcessor  = new("54533251-82be-4824-96c1-47b60b740d00");
    private static Guid SubVideo      = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static Guid SubDisk       = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");

    // Settings
    private static Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static Guid PerfBoostMode   = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static Guid VideoIdle       = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static Guid DiskIdle        = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    private static Guid PciExpressAspm  = new("ee12f906-d277-404b-b6da-e5fa1a576df5");

    /// <summary>Balanced, the stock scheme both OmniHub plans are duplicated from.</summary>
    private static Guid SchemeBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public const string BatterySaverName = "OmniHub Battery Saver";
    public const string PerformanceName  = "OmniHub Performance";

    private const uint ErrorSuccess = 0;

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid,
        ref IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerWriteFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid, IntPtr powerSettingGuid, byte[] buffer, uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid, uint dcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>
    /// One setting, both rails. Written as a pair because a plan that is correct on the rail it
    /// was designed for and wrong on the other is a trap: the automation can be turned off, or
    /// Windows can switch plans on its own, and a "performance" plan left boosting on battery
    /// would then run flat out at a picnic table.
    /// </summary>
    private readonly record struct Setting(Guid Sub, Guid Id, uint Ac, uint Dc);

    /// <summary>
    /// Values for the battery plan. Conservative but not crippling: the processor may still
    /// reach 60%, which keeps the desktop responsive, while boost stays off and the peripherals
    /// power down quickly.
    /// </summary>
    private static Setting[] BatterySaverValues() => new[]
    {
        new Setting(SubProcessor,  ProcThrottleMin, 5,   5),
        new Setting(SubProcessor,  ProcThrottleMax, 100, 60),
        new Setting(SubProcessor,  PerfBoostMode,   0,   0),   // no turbo on the battery plan, either rail
        new Setting(SubVideo,      VideoIdle,       120, 60),  // seconds
        new Setting(SubDisk,       DiskIdle,        600, 120), // seconds
        new Setting(SubPciExpress, PciExpressAspm,  2,   2),   // 2 = maximum power savings
    };

    /// <summary>
    /// Values for the mains plan. The boost setting is passed in rather than hard-coded because
    /// it is the one value here with a real thermal cost, and it is a decision for whoever owns
    /// the laptop rather than for this file.
    ///
    /// Note the DC column: even on the performance plan, battery keeps boost off and a 60%
    /// ceiling. If this plan is ever active while unplugged -- automation disabled, or Windows
    /// switching on its own -- it degrades to something sane instead of draining flat out.
    /// </summary>
    private static Setting[] PerformanceValues(uint boostAc) => new[]
    {
        new Setting(SubProcessor,  ProcThrottleMin, 5,   5),
        new Setting(SubProcessor,  ProcThrottleMax, 100, 60),
        new Setting(SubProcessor,  PerfBoostMode,   boostAc, 0),
        new Setting(SubVideo,      VideoIdle,       900, 300),
        new Setting(SubDisk,       DiskIdle,        0,   600), // 0 = never spin down on mains
        new Setting(SubPciExpress, PciExpressAspm,  0,   2),   // 0 = ASPM off on mains
    };

    /// <summary>
    /// Finds the OmniHub plans, creating them if absent, and writes their settings.
    ///
    /// Idempotent: an existing plan is reconfigured rather than duplicated again, so this can
    /// run at every launch without breeding copies in the power menu -- which is exactly what
    /// tools that call PowerDuplicateScheme unconditionally end up doing.
    /// </summary>
    public static (Guid? BatterySaver, Guid? Performance, string Detail) EnsurePlans(uint acBoostMode)
    {
        try
        {
            var existing = PowerPlan.List();

            Guid? saver = Find(existing, BatterySaverName) ?? Duplicate(BatterySaverName);
            Guid? perf  = Find(existing, PerformanceName)  ?? Duplicate(PerformanceName);

            if (saver is null || perf is null)
                return (saver, perf, "Windows would not create the power schemes. Administrator rights are required.");

            int written = Apply(saver.Value, BatterySaverValues())
                        + Apply(perf.Value, PerformanceValues(acBoostMode));

            return (saver, perf, $"Power plans ready ({written} settings written).");
        }
        catch (Exception ex)
        {
            return (null, null, $"Could not prepare the power plans: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds a plan from a recipe: duplicates Balanced, names it, and writes the resolved
    /// values for every knob this machine exposes.
    ///
    /// Reuses an existing scheme of the same name rather than making a second one with the same
    /// label, so re-creating a plan after moving the slider updates it instead of littering the
    /// power menu with near-identical entries.
    ///
    /// As everywhere in this file, it only ever writes to a scheme it created.
    /// </summary>
    public static (Guid? Id, string Detail) CreateFromRecipe(
        PowerPlanRecipe recipe, IReadOnlyList<PowerKnob> knobs)
    {
        if (string.IsNullOrWhiteSpace(recipe.Name))
            return (null, "The plan needs a name.");

        try
        {
            Guid? id = Find(PowerPlan.List(), recipe.Name) ?? Duplicate(recipe.Name);
            if (id is not { } scheme)
                return (null, "Windows would not create the scheme. Administrator rights are required.");

            int written = 0, attempted = 0;
            foreach (var (knob, ac, dc) in recipe.Resolve(knobs))
            {
                var sub = knob.Sub;
                var setting = knob.Id;
                attempted += 2;
                if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, ac) == ErrorSuccess) written++;
                if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, dc) == ErrorSuccess) written++;
            }

            // Reported as a fraction rather than a bare success. A scheme can be created while
            // individual settings are refused by policy, and "created" alone would hide that.
            return (scheme, written == attempted
                ? $"\"{recipe.Name}\" created with {written / 2} settings."
                : $"\"{recipe.Name}\" created, but only {written} of {attempted} values were accepted.");
        }
        catch (Exception ex)
        {
            return (null, $"Could not create the plan: {ex.Message}");
        }
    }

    /// <summary>Deletes one scheme by name, refusing while it is the active one.</summary>
    public static string RemoveByName(string name)
    {
        var active = PowerPlan.GetActiveSchemeId();
        foreach (var s in PowerPlan.List())
        {
            if (!string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (s.Id == active) return "That plan is currently active. Switch to another plan first.";

            var id = s.Id;
            return PowerDeleteScheme(IntPtr.Zero, ref id) == ErrorSuccess
                ? $"Deleted \"{name}\"."
                : $"Windows would not delete \"{name}\".";
        }
        return $"No plan named \"{name}\".";
    }

    private static Guid? Find(IReadOnlyList<PowerScheme> schemes, string name)
    {
        foreach (var s in schemes)
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                return s.Id;
        return null;
    }

    private static Guid? Duplicate(string name)
    {
        IntPtr dst = IntPtr.Zero;
        try
        {
            if (PowerDuplicateScheme(IntPtr.Zero, ref SchemeBalanced, ref dst) != ErrorSuccess || dst == IntPtr.Zero)
                return null;

            var id = Marshal.PtrToStructure<Guid>(dst);

            // Null-terminated Unicode, and the length must include the terminator.
            byte[] buffer = System.Text.Encoding.Unicode.GetBytes(name + "\0");
            PowerWriteFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, (uint)buffer.Length);

            return id;
        }
        catch { return null; }
        finally
        {
            // PowerDuplicateScheme allocates with LocalAlloc; the caller owns the buffer.
            if (dst != IntPtr.Zero) LocalFree(dst);
        }
    }

    private static int Apply(Guid scheme, Setting[] settings)
    {
        int ok = 0;
        foreach (var s in settings)
        {
            var sub = s.Sub;
            var id = s.Id;
            // Counted per rail: a setting the board does not expose fails quietly on one side
            // rather than aborting the whole plan.
            if (PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, s.Ac) == ErrorSuccess) ok++;
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref id, s.Dc) == ErrorSuccess) ok++;
        }
        return ok;
    }

    /// <summary>
    /// Removes both OmniHub schemes, so the feature is fully reversible: turning the automation
    /// off should be able to leave no trace in the Windows power menu.
    ///
    /// Refuses to delete the active scheme -- Windows rejects that anyway, and switching the
    /// user to something else behind their back to force it through would be worse than leaving
    /// the plan in place.
    /// </summary>
    public static string Remove()
    {
        int removed = 0;
        var active = PowerPlan.GetActiveSchemeId();

        foreach (var s in PowerPlan.List())
        {
            if (s.Name != BatterySaverName && s.Name != PerformanceName) continue;
            if (s.Id == active) return "That plan is currently active. Switch to another plan first.";

            var id = s.Id;
            if (PowerDeleteScheme(IntPtr.Zero, ref id) == ErrorSuccess) removed++;
        }

        return removed > 0 ? $"Removed {removed} OmniHub power plan(s)." : "No OmniHub power plans were present.";
    }
}
