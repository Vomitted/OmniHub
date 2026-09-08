namespace OmniHub.Core.Optimize;

/// <summary>
/// What the discrete GPU is doing, as far as anything can tell. Every field is nullable because
/// on a machine without nvidia-smi the vendor-neutral path reports load and nothing else, and on
/// a machine with no discrete GPU at all it reports none of it.
/// </summary>
public readonly record struct GpuDemand(double? LoadPercent, double? TempC, double? PowerWatts);

/// <summary>
/// Arbitrates one thermal budget between two chips that share it.
///
/// The CPU and the discrete GPU in this chassis sit under the same heatpipe assembly and are
/// cooled by the same two fans, but the application tunes them as though they were independent:
/// a CPU power controller steering by die temperature, and a GPU ceiling set by a preset. Under
/// a GPU-bound load that spends watts badly. The CPU holds a sustained limit it does not need,
/// the package sits at its thermal target because of it, and the shared heatpipe is already
/// saturated -- so the GPU, which is the part actually doing the work, has nothing left to take.
///
/// WHAT THIS DELIBERATELY DOES NOT DO
///
/// It does not lower the GPU. That looks like the symmetric move and it is the wrong one here.
/// GPU power on this platform is not a continuous knob: it is four discrete states formed by two
/// flags, measured at 60, 65, 70 and 75 W. More to the point, GpuController.ForceMaxPower is a
/// deliberate latch meaning "never reduce the GPU ceiling", it is set from the user's own
/// setting, and SetPower already enforces it against three separate callers. A budget arbiter
/// quietly undoing that would be the fourth caller to forget, which is the exact failure that
/// latch was introduced to end.
///
/// So the arbitration is one-directional: when the budget is genuinely contended and the GPU is
/// the one under load, the CPU yields. When it is not contended, nothing changes and the
/// ordinary adaptive rule applies.
///
/// ponytail: one threshold and one branch, wrapping the existing rule rather than replacing it.
/// A real joint optimiser would need a model of how watts on each side convert into temperature
/// on the shared pipe, and nobody here has measured that.
/// </summary>
public static class ThermalBudget
{
    /// <summary>
    /// GPU utilisation at or above which the discrete GPU counts as "the one doing the work".
    ///
    /// A calibration knob rather than a constant: what counts as busy depends on the workload
    /// mix, and 70% is a starting point chosen to sit clear of desktop compositing and video
    /// decode without demanding a fully pegged GPU.
    /// </summary>
    public const double DefaultGpuBusyPercent = 70.0;

    /// <summary>
    /// Which way to move the CPU's sustained power limit: -1 down, 0 hold, +1 up.
    ///
    /// Defers to <see cref="AdaptiveTuning.Direction"/> for everything except the contended case,
    /// so the demand term and the windup fix it encodes still apply unchanged.
    /// </summary>
    /// <param name="gpu">What the GPU is doing. All-null means no GPU term, and the plain rule applies.</param>
    public static int CpuDirection(
        double tempC, int watts, double? drawWatts, GpuDemand gpu,
        int targetC, double deadbandC, double demandThreshold,
        double gpuBusyPercent = DefaultGpuBusyPercent)
    {
        // Too hot outranks every other consideration, exactly as it does without a GPU term.
        if (tempC > targetC + deadbandC) return -1;

        // The contended case, and the only thing this function adds.
        //
        // Both conditions are required. A busy GPU on a cool machine is not competing for
        // anything: there is headroom for both, and taking watts off the CPU would slow it down
        // to buy cooling nobody needs. Only once the package has reached its target is the
        // budget finite and the question of who gets it a real one.
        //
        // This deliberately blocks the climb as well as forcing a descent. Without it, a
        // GPU-bound game with an idle-ish CPU sits just under target, reads as "cool and not
        // power-limited", and the ordinary rule keeps handing the CPU headroom it cannot use.
        if (IsBusy(gpu, gpuBusyPercent) && tempC >= targetC - deadbandC) return -1;

        return AdaptiveTuning.Direction(tempC, watts, drawWatts, targetC, deadbandC, demandThreshold);
    }

    /// <summary>
    /// Whether the GPU is under enough load to be worth yielding to.
    ///
    /// An unreadable load is not busy. Absence of evidence must not move the CPU limit, for the
    /// same reason a blank capability block must not disable a feature: the failure worth
    /// avoiding is acting confidently on a reading nobody took.
    /// </summary>
    public static bool IsBusy(GpuDemand gpu, double gpuBusyPercent = DefaultGpuBusyPercent) =>
        gpu.LoadPercent is double load && load >= gpuBusyPercent;
}
