using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Per-logical-processor clocks, against this machine.
///
/// Read-only, like HardwareReadTests. The application has shown one CPU frequency from WMI,
/// whose own source comment warns it "is not guaranteed to track the CPU's real-time dynamic
/// (Turbo Boost) frequency" -- so the single number on screen was both an average across twelve
/// logical processors and of uncertain freshness. This replaces it with what Windows actually
/// knows, and these assertions are the ones that would catch a struct laid out wrongly, which is
/// the realistic way a P/Invoke like this goes wrong.
/// </summary>
public class ProcessorClocksTests
{
    [Fact]
    public void EveryLogicalProcessorReportsAPlausibleClock()
    {
        var clocks = ProcessorClocks.Read();

        // A machine where the call is unavailable returns nothing rather than zeroes, and that
        // is a pass: the point is that it never invents a row.
        if (clocks.Count == 0) return;

        Assert.Equal(Environment.ProcessorCount, clocks.Count);

        Assert.All(clocks, c =>
        {
            // A mis-laid-out struct shows up here first: fields would land in each other's
            // slots and produce megahertz figures in the hundreds of thousands, or zero.
            Assert.InRange(c.MaxMhz, 300, 12000);
            Assert.InRange(c.CurrentMhz, 0, 12000);
            Assert.InRange(c.LimitMhz, 0, 12000);
        });

        // Numbers are the logical processor indices, so they are distinct and cover the range.
        Assert.Equal(clocks.Count, clocks.Select(c => c.Number).Distinct().Count());
    }

    /// <summary>
    /// The peak is the maximum, not the mean.
    ///
    /// A lightly threaded load leaves most cores parked, so averaging across twelve reports a
    /// low number for a processor that is boosting hard on one -- the opposite of what somebody
    /// watching a clock wants to know.
    /// </summary>
    [Fact]
    public void ThePeakIsTheFastestCoreNotTheAverage()
    {
        var clocks = new[]
        {
            new CoreClock(0, 4300, 4300, 4300),
            new CoreClock(1, 400, 4300, 4300),
            new CoreClock(2, 400, 4300, 4300),
            new CoreClock(3, 400, 4300, 4300),
        };

        Assert.Equal(4300, ProcessorClocks.PeakMhz(clocks));
    }

    [Fact]
    public void NoClocksMeansNoPeakRatherThanZero() =>
        Assert.Null(ProcessorClocks.PeakMhz(Array.Empty<CoreClock>()));

    /// <summary>
    /// A ceiling below the nominal maximum is a constraint the platform is reporting, not one
    /// inferred from a clock that happens to look low.
    /// </summary>
    [Fact]
    public void ACeilingBelowMaximumIsReportedAsCapped()
    {
        Assert.True(ProcessorClocks.IsCapped(new[] { new CoreClock(0, 2000, 4300, 2150) }));
        Assert.False(ProcessorClocks.IsCapped(new[] { new CoreClock(0, 2000, 4300, 4300) }));

        // An unreported limit is not a cap. Zero here means "not stated", and treating it as a
        // ceiling of zero would report every machine as throttled to a standstill.
        Assert.False(ProcessorClocks.IsCapped(new[] { new CoreClock(0, 2000, 4300, 0) }));
    }

    [Fact]
    public void NothingReadIsNotCapped() =>
        Assert.False(ProcessorClocks.IsCapped(Array.Empty<CoreClock>()));
}
