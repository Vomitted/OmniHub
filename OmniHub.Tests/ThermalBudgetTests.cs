using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// Splitting one thermal budget between two chips under the same heatpipe.
///
/// The cases worth pinning are the two that distinguish this from the CPU-only rule: a busy GPU
/// on a machine with headroom must change nothing, and a busy GPU on a machine already at its
/// target must stop the CPU climbing, even though the CPU looks entitled to more by every
/// CPU-only measure.
/// </summary>
public class ThermalBudgetTests
{
    private const int Target = 85;
    private const double Deadband = 3.0;
    private const double Threshold = 0.85;

    private static readonly GpuDemand Idle = new(LoadPercent: 3, TempC: 45, PowerWatts: 8);
    private static readonly GpuDemand Gaming = new(LoadPercent: 97, TempC: 72, PowerWatts: 74);
    private static readonly GpuDemand Unknown = new(null, null, null);

    private static int Dir(double tempC, int watts, double? draw, GpuDemand gpu) =>
        ThermalBudget.CpuDirection(tempC, watts, draw, gpu, Target, Deadband, Threshold);

    [Fact]
    public void BusyGpuWithThermalHeadroomChangesNothing()
    {
        // 62 C is far below target: both chips can have what they want, and taking watts off the
        // CPU here would slow it down to buy cooling nobody is short of.
        Assert.Equal(
            AdaptiveTuning.Direction(62, 40, 38, Target, Deadband, Threshold),
            Dir(62, 40, 38, Gaming));
    }

    [Fact]
    public void BusyGpuAtTargetStopsTheCpuClimbing()
    {
        // The case this exists for. A GPU-bound game with a lightly loaded CPU: the package has
        // reached its target so the budget is finite, and the CPU is not the chip doing the
        // work. The CPU-only rule would hold here; this yields instead.
        Assert.Equal(-1, Dir(tempC: Target, watts: 40, draw: 38, gpu: Gaming));
        Assert.Equal(-1, Dir(tempC: Target - 2, watts: 40, draw: 38, gpu: Gaming));
    }

    [Fact]
    public void IdleGpuLeavesTheOrdinaryRuleAlone()
    {
        // Nothing to yield to, at any temperature: the answer must match the CPU-only rule
        // exactly, or this class would be quietly changing behaviour on single-GPU workloads.
        foreach (double t in new double[] { 40, 70, Target - 2, Target, Target + 2, 95 })
            Assert.Equal(
                AdaptiveTuning.Direction(t, 45, 44, Target, Deadband, Threshold),
                Dir(t, 45, 44, Idle));
    }

    [Fact]
    public void UnreadableGpuLoadIsNotTreatedAsBusy()
    {
        // Absence of evidence must not move the limit. A machine without nvidia-smi reports no
        // load at all, and must behave exactly as it did before this term existed.
        Assert.False(ThermalBudget.IsBusy(Unknown));
        Assert.Equal(
            AdaptiveTuning.Direction(Target, 45, 44, Target, Deadband, Threshold),
            Dir(Target, 45, 44, Unknown));
    }

    [Fact]
    public void OverTargetStillBacksOffWhateverTheGpuIsDoing()
    {
        Assert.Equal(-1, Dir(95, 45, 45, Gaming));
        Assert.Equal(-1, Dir(95, 45, 45, Idle));
        Assert.Equal(-1, Dir(95, 45, 45, Unknown));
    }

    [Theory]
    [InlineData(69.9, false)]
    [InlineData(70.0, true)]
    [InlineData(100.0, true)]
    [InlineData(0.0, false)]
    public void BusyIsInclusiveOfTheThreshold(double load, bool expected)
    {
        Assert.Equal(expected, ThermalBudget.IsBusy(new GpuDemand(load, null, null)));
    }

    [Fact]
    public void ContendedGpuBoundLoadWalksTheCpuDownToItsFloor()
    {
        // The sequence, so the intent is pinned rather than just the single step: a sustained
        // GPU-bound game should end with the CPU at its floor rather than holding watts it
        // cannot spend.
        const int step = 3, min = 25, max = 60;
        int watts = max;

        for (int tick = 0; tick < 100; tick++)
            watts = Math.Clamp(watts + step * Dir(Target, watts, watts * 0.9, Gaming), min, max);

        Assert.Equal(min, watts);
    }
}
