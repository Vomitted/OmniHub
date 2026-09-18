// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Optimize;

/// <summary>Whether the remainder could be worked out, and if not, why not.</summary>
public enum PowerBudgetState
{
    /// <summary>Every input was present and the arithmetic holds.</summary>
    Available,

    /// <summary>
    /// On mains. The only instrument that measures the whole machine is the battery, and on AC
    /// it is not discharging, so there is no total to subtract from. This is not a missing
    /// sensor -- there is nothing to read.
    /// </summary>
    NotOnBattery,

    /// <summary>The battery is there but reported no discharge rate to divide up.</summary>
    DrawNotReported,

    /// <summary>The processor package power could not be read, so there is nothing to subtract.</summary>
    PackageUnavailable,

    /// <summary>
    /// The parts add up to more than the whole.
    ///
    /// Physically impossible, so one of the two instruments is wrong. Reported as a
    /// disagreement rather than clamped to zero, because a clamped zero would look like a
    /// measurement and this is the opposite: it is the moment the measurements stop agreeing.
    /// </summary>
    InputsDisagree,
}

/// <summary>
/// Where the other watts are going.
///
/// The machine measures its battery discharge, its processor package power and -- when the card
/// is awake -- its discrete GPU power. Nothing subtracts them. That is the whole of this file,
/// and it answers the question that started an entire evening of battery work: the battery
/// reporting 29.7 W when the expectation was 17 or 18, with no way to see which part of the
/// machine the difference belonged to.
///
/// It is a difference, not a sensor, and every part of this type is arranged to keep that
/// visible. The inputs are carried alongside the answer so the subtraction can be checked by
/// eye; a negative remainder is reported as a disagreement rather than hidden; and the label
/// says "everything else" rather than naming a component, because nothing here knows whether
/// the panel or the memory or a USB device is the one drawing it.
/// </summary>
/// <param name="State">Whether the remainder is available, and why not when it is not.</param>
/// <param name="TotalWatts">Measured at the battery: the whole machine, including its own losses.</param>
/// <param name="PackageWatts">The processor package, from the SMU. CPU and integrated graphics.</param>
/// <param name="GpuWatts">The discrete GPU, when it was awake enough to be asked. Usually null on battery.</param>
/// <param name="RemainderWatts">Total less the parts. Negative only when the inputs disagree.</param>
/// <param name="RemainderIncludesGpu">True when the discrete GPU was not measured and is inside the remainder.</param>
public sealed record PowerBudget(
    PowerBudgetState State,
    double? TotalWatts,
    double? PackageWatts,
    double? GpuWatts,
    double? RemainderWatts,
    bool RemainderIncludesGpu)
{
    /// <summary>The remainder as a share of the whole machine, or null when there is no whole.</summary>
    public double? RemainderShare =>
        RemainderWatts is { } rest && TotalWatts is { } total && total > 0
            ? rest / total * 100.0
            : null;

    /// <summary>
    /// Works out the remainder from readings the caller already has.
    ///
    /// Pure on purpose. Every input here is fetched somewhere else in the application on its own
    /// schedule -- the battery once a second, the SMU behind a five-second cache, the GPU behind
    /// a three-second one -- and a function that went and fetched them itself would both
    /// duplicate that work and take three readings from three different instants.
    /// </summary>
    /// <param name="draw">The battery reading, or null when the battery could not be read.</param>
    /// <param name="packageWatts">Processor package power in watts, or null when the SMU is unavailable.</param>
    /// <param name="gpuWatts">
    /// Discrete GPU power in watts, or null when it was not measured. Null is the normal case on
    /// battery and is treated as "inside the remainder", not as zero: this application
    /// deliberately does not run nvidia-smi on battery, because waking the card to ask what it is
    /// drawing changes what it is drawing.
    /// </param>
    public static PowerBudget Compute(BatteryDraw? draw, double? packageWatts, double? gpuWatts)
    {
        if (draw is null)
            return Unavailable(PowerBudgetState.DrawNotReported);

        if (draw.OnAc)
            return Unavailable(PowerBudgetState.NotOnBattery);

        if (draw.DischargeMilliwatts <= 0)
            return Unavailable(PowerBudgetState.DrawNotReported);

        double total = draw.DischargeMilliwatts / 1000.0;

        if (packageWatts is not { } package)
            return new PowerBudget(PowerBudgetState.PackageUnavailable, total, null, gpuWatts, null, gpuWatts is null);

        double remainder = total - package - (gpuWatts ?? 0);

        return new PowerBudget(
            remainder < 0 ? PowerBudgetState.InputsDisagree : PowerBudgetState.Available,
            total,
            package,
            gpuWatts,
            remainder,
            RemainderIncludesGpu: gpuWatts is null);
    }

    private static PowerBudget Unavailable(PowerBudgetState state) =>
        new(state, null, null, null, null, false);

    /// <summary>
    /// The budget in a sentence or two, including what the remainder does and does not contain.
    ///
    /// Longer than a number because a number alone would be read as a measurement of something.
    /// It is the arithmetic that is being reported, so the arithmetic is what it says.
    /// </summary>
    public string Describe() => State switch
    {
        PowerBudgetState.NotOnBattery =>
            "On mains. The battery is the only instrument that measures the whole machine, and it "
            + "is not discharging, so there is no total to divide up.",

        PowerBudgetState.DrawNotReported =>
            "The battery did not report a discharge rate, so the machine's total draw is unknown.",

        PowerBudgetState.PackageUnavailable =>
            $"Drawing {TotalWatts:0.0} W from the battery. The processor package power could not be "
            + "read, so there is nothing to subtract and no remainder to report.",

        PowerBudgetState.InputsDisagree =>
            $"The parts add up to more than the whole: {TotalWatts:0.0} W measured at the battery, "
            + $"but {Parts():0.0} W accounted for. One of those instruments is wrong by "
            + $"{-RemainderWatts:0.0} W and this does not say which.",

        _ =>
            $"Drawing {TotalWatts:0.0} W. The processor package accounts for {PackageWatts:0.0} W"
            + (GpuWatts is { } gpu ? $" and the discrete GPU for {gpu:0.0} W" : "")
            + $". The remaining {RemainderWatts:0.0} W ({RemainderShare:0}%) is everything else -- "
            + (RemainderIncludesGpu
                ? "the panel, memory, USB, the discrete GPU, and the battery's own losses. The "
                  + "discrete GPU is in there because asking it what it draws means waking it, "
                  + "which changes the answer."
                : "the panel, memory, USB, and the battery's own losses.")
            + " This is a subtraction, not a sensor.",
    };

    /// <summary>What was accounted for, for the disagreement sentence.</summary>
    private double Parts() => (PackageWatts ?? 0) + (GpuWatts ?? 0);
}
