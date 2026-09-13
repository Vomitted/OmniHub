using OmniHub.Core.Diagnostics;
using Xunit;

namespace OmniHub.Tests;

public class CoolingHealthTests
{
    private static readonly DateTime Day = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// A day of logging: some idle, then a stretch of load at a given temperature and power.
    /// The idle portion is what the ambient proxy is taken from.
    /// </summary>
    private static List<ThermalRow> Day_(double idleC, double loadC, double watts, int loadSamples = 200)
    {
        var rows = new List<ThermalRow>();
        for (int i = 0; i < 200; i++)
            rows.Add(new ThermalRow(Day.AddSeconds(i * 2), idleC, null, 10, false, "Auto",
                "SmuDieTctl", 0, "", 0, 4.0));

        for (int i = 0; i < loadSamples; i++)
            rows.Add(new ThermalRow(Day.AddSeconds(400 + i * 2), loadC, null, 60, false, "Auto",
                "SmuDieTctl", 0, "Core current (EDC)", 98, watts));

        return rows;
    }

    [Fact]
    public void ADayWithNoRealLoadIsNotMeasured()
    {
        // Browsing all day. Deriving a thermal resistance from idle would be inventing a trend.
        var rows = Day_(idleC: 48, loadC: 52, watts: 6.0);

        Assert.Null(CoolingHealth.Measure(Day, rows));
    }

    [Fact]
    public void ALoadedDayYieldsDegreesPerWatt()
    {
        // 45C idle, 80C under 35W: a 35C rise over 35W is 1.0 degrees per watt.
        var sample = CoolingHealth.Measure(Day, Day_(idleC: 45, loadC: 80, watts: 35));

        Assert.NotNull(sample);
        Assert.Equal(1.0, sample!.DegreesPerWatt, 1);
        Assert.Equal(35, sample.MedianWatts, 1);
        Assert.Equal(60, sample.MedianFanPercent, 1);
    }

    /// <summary>
    /// The measurement that matters: the same temperature for LESS power means worse cooling.
    /// Temperature alone cannot see this, which is the entire reason the watts column exists.
    /// </summary>
    [Fact]
    public void SameTemperatureAtLowerPowerReadsAsWorseCooling()
    {
        var healthy = CoolingHealth.Measure(Day, Day_(idleC: 45, loadC: 80, watts: 45))!;
        var clogged = CoolingHealth.Measure(Day, Day_(idleC: 45, loadC: 80, watts: 25))!;

        Assert.True(clogged.DegreesPerWatt > healthy.DegreesPerWatt,
            $"a machine reaching 80C on 25W ({clogged.DegreesPerWatt:0.00} C/W) should read worse "
            + $"than one reaching 80C on 45W ({healthy.DegreesPerWatt:0.00} C/W)");
    }

    [Fact]
    public void TooFewDaysRefusesToCallATrend()
    {
        var history = Enumerable.Range(0, 3)
            .Select(i => new CoolingSample(Day.AddDays(i), 1.0, 200, 35, 60))
            .ToList();

        string text = CoolingHealth.Summarise(history);

        Assert.Contains("needs about a week", text);
    }

    /// <summary>
    /// Degradation is called out, and the ambient caveat travels with it. The room getting hotter
    /// produces the same shape as a dusty heatsink, and a reader who is not told that will clean a
    /// heatsink that was fine.
    /// </summary>
    [Fact]
    public void ClearDegradationIsReportedWithItsAmbientCaveat()
    {
        var history = new List<CoolingSample>();
        for (int i = 0; i < 9; i++)
            history.Add(new CoolingSample(Day.AddDays(i), i < 3 ? 1.00 : i < 6 ? 1.15 : 1.40, 200, 35, 60));

        string text = CoolingHealth.Summarise(history);

        Assert.Contains("worse than it was", text);
        Assert.Contains("cleaning the heatsink", text);
        Assert.Contains("room got hotter", text);
    }

    [Fact]
    public void SteadyCoolingIsReportedAsSteady()
    {
        var history = Enumerable.Range(0, 9)
            .Select(i => new CoolingSample(Day.AddDays(i), 1.0 + (i % 2) * 0.02, 200, 35, 60))
            .ToList();

        string text = CoolingHealth.Summarise(history);

        Assert.Contains("holding steady", text);
    }

    [Fact]
    public void NoMeasurableDaysSaysSoRatherThanReportingZero()
    {
        string text = CoolingHealth.Summarise(Array.Empty<CoolingSample>());

        Assert.Contains("No day has enough sustained load", text);
        Assert.DoesNotContain("0.00", text);
    }
}
