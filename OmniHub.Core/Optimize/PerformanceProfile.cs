using OmniHub.Core.Hardware;

namespace OmniHub.Core.Optimize;

public enum PerformanceMode { Eco, Balanced, Performance }

/// <summary>
/// Applies a whole machine state in one action, the way Omen Gaming Hub's performance modes
/// do: GPU power level, CPU wattage caps and the Windows power scheme all move together.
///
/// This exists as a Core service rather than staying inline in the dashboard because a
/// "mode" is only meaningful if every lever moves consistently. Setting the GPU to Eco while
/// Windows stays on a high-performance scheme -- which is what the old inline preset code
/// did -- produces a machine that is neither quiet nor fast, and leaves the user unable to
/// tell which setting won.
///
/// FAN behaviour is deliberately NOT part of this. The fan is governed by the curve service,
/// the one subsystem here with a safety obligation, and a profile that could quietly slow
/// the fan would be able to undo the protection this whole app exists to provide. Callers
/// set the fan mode explicitly and visibly instead.
/// </summary>
public static class PerformanceProfile
{
    // Windows' own well-known scheme GUIDs. Present on essentially every install; where an
    // OEM has replaced them, Activate reports the failure rather than pretending.
    private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    private static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    // CPU WATTAGE IS DELIBERATELY NOT SET HERE.
    //
    // An earlier version of this file wrote absolute caps per mode -- 25/35 W for Eco, up to
    // 65/95 W for Performance -- under a comment claiming they were "conservative". They were
    // not. They were invented. This machine's CPU is a 35-54 W cTDP part, so the Performance
    // figures sat above its rated sustained envelope, and the app would have been raising the
    // power ceiling on a laptop whose entire reason for existing is that it runs too hot.
    //
    // Why it cannot be done safely: the HP interface exposes SetCpuPower (0x29) but no
    // corresponding Get. There is no way to read what this CPU is actually rated for, so any
    // absolute wattage written here is a guess dressed up as a setting. Clamping to
    // PowerController's 10-140 W range does not rescue it; that range is a sanity bound, not
    // a statement about this silicon.
    //
    // The sliders that once set them explicitly were removed for the same reason, and the code
    // that wrote the wattages went with them: a setter for a value nobody can read back is a
    // control that cannot be checked, and this project does not keep those.
    //
    // What replaced it is the tuning screen, which writes SMU limits the SMU reports back -- so
    // every one of those is verified by read-back rather than assumed.

    private static GpuPowerLevel GpuFor(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => GpuPowerLevel.Eco,
        PerformanceMode.Balanced => GpuPowerLevel.Balanced,
        _ => GpuPowerLevel.Performance,
    };

    /// <summary>
    /// Resolves the scheme against what this machine actually has, rather than assuming the
    /// well-known GUID exists. Windows 11 hides High Performance by default, and OEMs ship
    /// their own schemes, so a hardcoded GUID fails on a lot of real machines. Falls back to
    /// whatever is currently active, which is a no-op rather than a wrong-plan surprise.
    /// </summary>
    private static Guid? PlanFor(PerformanceMode mode)
    {
        var available = PowerPlan.List();
        if (available.Count == 0) return null;

        Guid preferred = mode switch
        {
            PerformanceMode.Eco => PowerSaver,
            PerformanceMode.Balanced => BalancedPlan,
            _ => HighPerformance,
        };

        if (available.Any(s => s.Id == preferred)) return preferred;

        // Not present. Balanced is the safe substitute in every direction: it exists on
        // essentially every install, and it is never the wrong answer badly.
        if (available.Any(s => s.Id == BalancedPlan)) return BalancedPlan;
        return null;
    }

    /// <summary>
    /// Applies the mode. Every step is attempted independently and its outcome recorded, so
    /// a machine where the BIOS rejects a wattage change still gets the GPU and power plan
    /// applied, and the caller is told exactly which part did not take.
    /// </summary>
    public static TuningResult Apply(PerformanceMode mode, GpuController gpu)
    {
        var applied = new List<string>();
        var failed = new List<string>();

        try { gpu.SetPowerPreset(GpuFor(mode)); applied.Add("GPU"); }
        catch { failed.Add("GPU"); }

        var plan = PlanFor(mode);
        if (plan is null) failed.Add("power plan (none available)");
        else
        {
            var planResult = PowerPlan.Activate(plan.Value);
            if (planResult.Applied) applied.Add("power plan"); else failed.Add("power plan");
        }

        // Eco is the only mode that also releases the high-resolution timer: its entire
        // purpose is battery life, and holding a finer timer works directly against that.
        if (mode == PerformanceMode.Eco)
        {
            var timer = SystemTuning.ReleaseHighResolutionTimer();
            if (timer.Applied) applied.Add("timer released");
        }

        string name = mode.ToString();
        if (failed.Count == 0)
            return new TuningResult(true, $"{name}: {string.Join(", ", applied)} applied.");

        return applied.Count == 0
            ? new TuningResult(false, $"{name} could not be applied ({string.Join(", ", failed)} all failed).")
            : new TuningResult(true, $"{name}: {string.Join(", ", applied)} applied; {string.Join(", ", failed)} refused.");
    }
}
