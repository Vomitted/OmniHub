// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// Subtracting the parts from the whole, and refusing to when the subtraction would lie.
///
/// The arithmetic is one line. Everything worth testing here is the set of cases where the
/// arithmetic is available but wrong to perform: on mains there is no total, a missing GPU
/// reading is not a zero, and a remainder that comes out negative is two instruments disagreeing
/// rather than a component drawing negative power.
/// </summary>
public class PowerBudgetTests
{
    private static BatteryDraw OnBattery(int milliwatts) =>
        new(OnAc: false, Charging: false, DischargeMilliwatts: milliwatts,
            ChargeMilliwatts: 0, RemainingCapacityMWh: 40_000, MilliVolts: 11_500);

    private static readonly BatteryDraw OnMains =
        new(OnAc: true, Charging: false, DischargeMilliwatts: 0,
            ChargeMilliwatts: 0, RemainingCapacityMWh: 40_000, MilliVolts: 11_500);

    /// <summary>The case the feature exists for: 29.7 W measured, 14.2 W accounted for.</summary>
    [Fact]
    public void TheRemainderIsTheTotalLessTheParts()
    {
        var budget = PowerBudget.Compute(OnBattery(29_700), packageWatts: 14.2, gpuWatts: 3.5);

        Assert.Equal(PowerBudgetState.Available, budget.State);
        Assert.Equal(29.7, budget.TotalWatts!.Value, 3);
        Assert.Equal(12.0, budget.RemainderWatts!.Value, 3);
    }

    /// <summary>
    /// A GPU reading that was not taken is not a GPU drawing nothing.
    ///
    /// This is the normal case on battery, because the application will not run nvidia-smi there
    /// -- waking the card to ask what it draws changes what it draws. Treating the absence as
    /// zero would be silently correct in arithmetic and silently wrong in meaning: the discrete
    /// GPU's draw would be reported as part of "the panel and memory".
    /// </summary>
    [Fact]
    public void AnUnmeasuredGpuFallsIntoTheRemainderAndIsSaidSo()
    {
        var budget = PowerBudget.Compute(OnBattery(29_700), packageWatts: 14.2, gpuWatts: null);

        Assert.Equal(15.5, budget.RemainderWatts!.Value, 3);
        Assert.True(budget.RemainderIncludesGpu);
        Assert.Contains("discrete GPU", budget.Describe());
    }

    /// <summary>A measured GPU is subtracted, and the remainder stops claiming to contain it.</summary>
    [Fact]
    public void AMeasuredGpuLeavesTheRemainder()
    {
        var budget = PowerBudget.Compute(OnBattery(50_000), packageWatts: 20, gpuWatts: 15);

        Assert.False(budget.RemainderIncludesGpu);
        Assert.Equal(15.0, budget.RemainderWatts!.Value, 3);
    }

    /// <summary>
    /// On mains there is no total, so there is no budget.
    ///
    /// The battery is the only instrument that measures the whole machine. Reporting a remainder
    /// here would mean inventing the one number the whole calculation rests on.
    /// </summary>
    [Fact]
    public void OnMainsThereIsNothingToDivideUp()
    {
        var budget = PowerBudget.Compute(OnMains, packageWatts: 14.2, gpuWatts: 3.5);

        Assert.Equal(PowerBudgetState.NotOnBattery, budget.State);
        Assert.Null(budget.TotalWatts);
        Assert.Null(budget.RemainderWatts);
    }

    /// <summary>A battery that reports no rate is not a machine drawing nothing.</summary>
    [Fact]
    public void AZeroDischargeRateIsNotATotal()
    {
        var budget = PowerBudget.Compute(OnBattery(0), packageWatts: 14.2, gpuWatts: null);

        Assert.Equal(PowerBudgetState.DrawNotReported, budget.State);
        Assert.Null(budget.RemainderWatts);
    }

    /// <summary>An unreadable battery says so rather than producing a budget from two thirds of it.</summary>
    [Fact]
    public void NoBatteryReadingMeansNoBudget() =>
        Assert.Equal(PowerBudgetState.DrawNotReported,
                     PowerBudget.Compute(null, packageWatts: 14.2, gpuWatts: 3.5).State);

    /// <summary>
    /// Without the package figure the total is still real and is still reported.
    ///
    /// Losing the subtrahend loses the remainder, not the measurement. Blanking the whole card
    /// would throw away a number the machine did give.
    /// </summary>
    [Fact]
    public void AMissingPackageReadingKeepsTheTotal()
    {
        var budget = PowerBudget.Compute(OnBattery(29_700), packageWatts: null, gpuWatts: null);

        Assert.Equal(PowerBudgetState.PackageUnavailable, budget.State);
        Assert.Equal(29.7, budget.TotalWatts!.Value, 3);
        Assert.Null(budget.RemainderWatts);
    }

    /// <summary>
    /// The parts exceeding the whole is a disagreement, not a negative component.
    ///
    /// It happens for real: the SMU's package figure is a model rather than a shunt reading, and
    /// on a machine drawing 12 W it can report 14. Clamping that to zero would present a
    /// fabricated measurement; showing "-2.0 W of panel and memory" would present an impossible
    /// one. The honest third option is to say the two instruments do not agree and by how much.
    /// </summary>
    [Fact]
    public void PartsExceedingTheWholeIsReportedAsADisagreement()
    {
        var budget = PowerBudget.Compute(OnBattery(12_000), packageWatts: 14.0, gpuWatts: null);

        Assert.Equal(PowerBudgetState.InputsDisagree, budget.State);
        Assert.True(budget.RemainderWatts < 0);

        string text = budget.Describe();
        Assert.Contains("more than the whole", text);
        Assert.Contains("does not say which", text);
    }

    /// <summary>The share is of the whole machine, and is absent when there is no whole.</summary>
    [Fact]
    public void TheShareIsOfTheTotal()
    {
        Assert.Equal(50.0, PowerBudget.Compute(OnBattery(20_000), 10, null).RemainderShare!.Value, 3);
        Assert.Null(PowerBudget.Compute(OnMains, 10, null).RemainderShare);
    }

    /// <summary>
    /// The description never presents the remainder as a reading.
    ///
    /// It is the difference of three numbers from three instruments of differing accuracy, and a
    /// figure that arrives with no such warning gets treated as a sensor within a day.
    /// </summary>
    [Fact]
    public void TheDescriptionSaysItIsASubtraction() =>
        Assert.Contains("subtraction, not a sensor",
                        PowerBudget.Compute(OnBattery(29_700), 14.2, 3.5).Describe());
}
