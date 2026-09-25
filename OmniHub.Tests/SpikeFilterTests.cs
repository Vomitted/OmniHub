// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Collections.Concurrent;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// The fan does not chase a temperature that has not persisted, and still answers one that has.
///
/// Measured before this existed: across 98 hours of this machine's thermal log the fan command
/// reversed direction by ten points or more 44 times an hour, a sawtooth driven by Tctl jumping for a
/// tick or two with each burst of boost. The filter's own comment carries the replay; these tests
/// hold its two promises, and the ones through the real loop are what fail if it is ever removed.
/// </summary>
public class SpikeFilterTests
{
    private const double FullSpeed = 88;

    private static List<double> Feed(SpikeFilter filter, params double[] readings) =>
        readings.Select(r => filter.Next(r, FullSpeed)).ToList();

    [Fact]
    public void ASingleTickSpikeNeverReachesTheCurve()
    {
        // The pattern straight from the log: a die sitting at 72, one tick at 81.1, back to 74.
        var output = Feed(new SpikeFilter(), 72, 72, 72, 72, 72, 81.1, 74.3, 72, 72);

        Assert.True(output.Skip(5).Max() < 75, $"the spike leaked through: {string.Join(", ", output)}");
    }

    [Fact]
    public void ATwoTickSpikeIsSetAsideToo()
    {
        var output = Feed(new SpikeFilter(), 72, 72, 72, 72, 72, 81, 82, 72, 72);

        Assert.True(output.Skip(5).Max() < 75, $"the spike leaked through: {string.Join(", ", output)}");
    }

    [Fact]
    public void ASustainedRiseArrivesWithinTwoTicks()
    {
        var output = Feed(new SpikeFilter(), 70, 70, 70, 70, 70, 84, 84, 84, 84, 84);

        // Rise at index 5; the median has it by the third reading, index 7.
        Assert.Equal(84.0, output[7], 9);
    }

    [Fact]
    public void TheTopOfTheCurveIsNeverDelayed()
    {
        var output = Feed(new SpikeFilter(), 70, 70, 70, 70, 70, 90);

        Assert.Equal(90.0, output[5], 9);
    }

    [Fact]
    public void NoHistoryIsInventedBeforeFiveReadings()
    {
        var output = Feed(new SpikeFilter(), 70, 85, 60, 75);

        Assert.Equal(new[] { 70.0, 85.0, 60.0, 75.0 }, output);
    }

    [Fact]
    public void AReadingThatIsNotANumberIsPassedOnAndNotKept()
    {
        var filter = new SpikeFilter();
        Feed(filter, 70, 70, 70, 70, 70);

        Assert.True(double.IsNaN(filter.Next(double.NaN, FullSpeed)));
        Assert.Equal(70.0, filter.Next(70, FullSpeed), 9);
    }

    [Fact]
    public void ResetForgetsTheHistory()
    {
        var filter = new SpikeFilter();
        Feed(filter, 80, 80, 80, 80, 80);
        filter.Reset();

        Assert.Equal(70.0, filter.Next(70, FullSpeed), 9);
    }

    [Fact]
    public void TheFullSpeedPointIsWhereTheCurveFirstAsksForAllOfIt()
    {
        Assert.Equal(88.0, FanCurve.CreateDefault().FullSpeedTempC, 9);

        var neverFull = new FanCurve(new[] { new CurvePoint(40, 0), new CurvePoint(90, 80) });
        Assert.Equal(double.PositiveInfinity, neverFull.FullSpeedTempC);
    }

    // ------------------------------------------------------------------ through the real loop

    /// <summary>Runs the real fan loop over a scripted temperature sequence and returns what it commanded.</summary>
    private static async Task<List<byte>> Commanded(params double[] temperatures)
    {
        int next = 0;
        TemperatureReading Read()
        {
            int i = Math.Min(Interlocked.Increment(ref next) - 1, temperatures.Length - 1);
            return new TemperatureReading(temperatures[i], TemperatureSource.SmuDieTctl);
        }

        var levels = new ConcurrentQueue<byte>();
        var fan = new ScriptedFanBackend();
        using var service = new FanService(fan, Read, FanCurve.CreateDefault(), TimeSpan.FromMilliseconds(5));
        service.OnTick += (_, level) => levels.Enqueue(level);

        service.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (levels.Count < temperatures.Length && DateTime.UtcNow < deadline) await Task.Delay(5);
        service.Stop();

        return levels.Take(temperatures.Length).ToList();
    }

    [Fact]
    public async Task TheFanLoopDoesNotAnswerASpike()
    {
        // 70 C asks the default curve for 40%; 84 C asks for 83%. Unfiltered, the one reading at
        // 84 took the fan to 83% and walked it back down over the next four ticks.
        byte atSpike = FanCurve.CreateDefault().Evaluate(84);
        var levels = await Commanded(70, 70, 70, 70, 70, 70, 84, 70, 70, 70, 70);

        Assert.True(levels.Count == 11, $"the loop only ticked {levels.Count} times");
        Assert.True(levels.Max() < atSpike,
                    $"the fan answered a single reading: {string.Join(", ", levels)} (the spike asks for {atSpike}%)");
    }

    [Fact]
    public async Task TheFanLoopStillAnswersSustainedHeat()
    {
        byte atHeat = FanCurve.CreateDefault().Evaluate(84);
        var levels = await Commanded(70, 70, 70, 70, 70, 70, 84, 84, 84, 84, 84, 84);

        int arrived = levels.FindIndex(l => l >= atHeat);
        Assert.True(arrived >= 0, $"sustained heat was never answered: {string.Join(", ", levels)}");
        Assert.True(arrived <= 6 + 2, $"sustained heat took {arrived - 6} ticks to answer: {string.Join(", ", levels)}");
    }

    [Fact]
    public async Task TheFanLoopAnswersTheTopOfTheCurveAtOnce()
    {
        var levels = await Commanded(70, 70, 70, 70, 70, 70, 92);

        Assert.Equal(100, levels[6]);
    }

    [Fact]
    public void TheTickSaysWhenAReadingWasSetAside()
    {
        var tick = new FanTick(81.1, 74.3, TemperatureSource.SmuDieTctl, false, true, 51, 0, null, FilteredC: 74.3);
        string text = tick.Describe();

        Assert.Contains("had not persisted", text);
        Assert.Contains("74.3 C", text);
        Assert.Equal(0.0, tick.LeadC, 9);   // the filter is not credited to the predictive lead
    }

    [Fact]
    public void TheTickSaysNothingAboutTheFilterWhenItChangedNothing()
    {
        var tick = new FanTick(72.4, 72.4, TemperatureSource.SmuDieTctl, false, true, 45, 0, null, FilteredC: 72.4);

        Assert.DoesNotContain("persisted", tick.Describe());
    }
}
