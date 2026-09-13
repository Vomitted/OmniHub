using OmniHub.Core.Optimize;
using Xunit;

namespace OmniHub.Tests;

public class ProcessPowerUseTests
{
    /// <summary>
    /// The real shape of a GPU engine counter instance name, taken from this machine.
    /// Everything downstream depends on pulling the pid out of it correctly.
    /// </summary>
    [Theory]
    [InlineData("pid_12912_luid_0x00000000_0x000132D1_phys_0_eng_0_engtype_3D", 12912)]
    [InlineData("pid_26216_luid_0x00000000_0x000132D1_phys_0_eng_4_engtype_Timer 0", 26216)]
    [InlineData("pid_4_luid_0x00000000_0x0000ABCD_phys_0_eng_1_engtype_Copy", 4)]
    public void PidIsParsedFromTheInstanceName(string instance, int expected)
    {
        Assert.True(ProcessPowerUse.TryParsePid(instance, out int pid));
        Assert.Equal(expected, pid);
    }

    /// <summary>
    /// Anything that is not a per-process engine instance is refused rather than parsed into a
    /// plausible-looking zero. The counter set contains aggregate rows too, and attributing those
    /// to pid 0 would invent a process that is using the GPU.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("_Total")]
    [InlineData("luid_0x00000000_0x000132D1_phys_0")]
    [InlineData("pid_")]
    [InlineData("pid_notanumber_luid_0x0")]
    public void NonProcessInstancesAreRejected(string instance)
    {
        Assert.False(ProcessPowerUse.TryParsePid(instance, out _));
    }

    /// <summary>
    /// The ordering key weighs real work only. Timer occupancy sits near 100% for processes doing
    /// almost no rendering, and letting it drive the sort would put a scheduling artefact at the
    /// top of a list whose entire purpose is naming the thing actually costing power.
    /// </summary>
    [Fact]
    public void TimerOccupancyDoesNotDominateTheRanking()
    {
        var timerHeavy = new ProcessDrain(1, "chat", GpuPercent: 3, TimerPercent: 100, CpuPercent: 2);
        var realWork = new ProcessDrain(2, "game", GpuPercent: 60, TimerPercent: 0, CpuPercent: 20);

        Assert.True(realWork.Weight > timerHeavy.Weight,
            $"a process at 60% 3D ranked below one at 3% 3D with a busy timer queue "
            + $"({realWork.Weight} against {timerHeavy.Weight})");
    }

    /// <summary>
    /// A live sample against the real machine. Asserts the shape rather than any particular
    /// process, because what is running is not this test's business -- but something is always
    /// using a sliver of CPU, and a sampler returning nothing at all is broken.
    /// </summary>
    [Fact]
    public async Task SamplingTheRealMachineReturnsPlausibleRows()
    {
        var rows = await ProcessPowerUse.SampleAsync(TimeSpan.FromMilliseconds(400));

        Assert.NotEmpty(rows);

        foreach (var r in rows)
        {
            Assert.InRange(r.GpuPercent, 0, 100);
            Assert.InRange(r.TimerPercent, 0, 100);
            Assert.True(r.CpuPercent >= 0, $"{r.Name} reported {r.CpuPercent}% CPU");
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
        }

        // Sorted heaviest first, so a caller can take the top few and be showing the right ones.
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i - 1].Weight >= rows[i].Weight, "rows came back out of order");
    }

    /// <summary>A cancelled sample returns nothing rather than a half-measured window.</summary>
    [Fact]
    public async Task CancellationYieldsNoRowsRatherThanPartialOnes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var rows = await ProcessPowerUse.SampleAsync(TimeSpan.FromSeconds(5), cts.Token);

        Assert.Empty(rows);
    }
}
