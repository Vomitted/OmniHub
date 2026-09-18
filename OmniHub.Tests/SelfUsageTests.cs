using System.Diagnostics;
using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

public class SelfUsageTests
{
    [Fact]
    public void TheFirstReadingIsNull()
    {
        // Processor time is cumulative since the process started, so one reading describes the
        // whole run rather than now.
        Assert.Null(new SelfUsage().CpuPercent());
    }

    [Fact]
    public void TwoSamplersEachKeepTheirOwnWindow()
    {
        // The mistake this project already made once with the system-wide CPU counter: a shared
        // previous sample means each call consumes the window since whichever caller asked last.
        var first = new SelfUsage();
        var second = new SelfUsage();

        Assert.Null(first.CpuPercent());
        Assert.Null(second.CpuPercent());

        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 30) { }

        Assert.NotNull(first.CpuPercent());
        Assert.NotNull(second.CpuPercent());
    }

    [Theory]
    [InlineData(50, 100, 50.0)]      // half a core
    [InlineData(100, 100, 100.0)]    // one core, fully
    [InlineData(250, 100, 250.0)]    // two and a half cores, which is a real answer
    [InlineData(0, 100, 0.0)]
    public void ItIsAShareOfOneCoreNotOfTheWholeProcessor(int cpuMs, int elapsedMs, double expected)
    {
        // Dividing by the core count would report a genuinely busy application as four per cent on
        // a twelve-thread part, which reads as nothing at all. Over a hundred is meaningful here:
        // it means more than one core's worth.
        Assert.Equal(expected, SelfUsage.Percent(TimeSpan.FromMilliseconds(cpuMs),
                                                 TimeSpan.FromMilliseconds(elapsedMs))!.Value, 6);
    }

    [Fact]
    public void NoElapsedTimeIsNoReadingRatherThanZero() =>
        Assert.Null(SelfUsage.Percent(TimeSpan.FromMilliseconds(5), TimeSpan.Zero));

    [Fact]
    public void ACounterThatWentBackwardsIsNoReading() =>
        Assert.Null(SelfUsage.Percent(TimeSpan.FromMilliseconds(-5), TimeSpan.FromMilliseconds(100)));

    [Fact]
    public void TheMemoryFigureIsPlausibleForThisProcess()
    {
        double mb = new SelfUsage().MemoryMegabytes();

        // A WPF test host is tens of megabytes; the bounds are loose enough to survive any runtime
        // and tight enough to catch a unit error, which is the failure worth catching.
        Assert.InRange(mb, 1, 8192);
    }
}
