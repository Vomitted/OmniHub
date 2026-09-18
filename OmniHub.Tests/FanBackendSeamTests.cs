// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The cooling loop, driven against a fake machine.
///
/// None of this could be tested before. FanService took a concrete FanController, which needs
/// HP WMI, which needs an HP laptop -- so the one loop in this application whose failure is a
/// hot machine with stopped fans was also the one part of it with no tests at all. The seam was
/// introduced to let a second vendor in; being able to run the loop on a desk is the part that
/// pays for itself immediately.
///
/// What is asserted here is ordering and restraint rather than arithmetic. The curve's numbers
/// are covered by FanCurveTests; what was uncovered is whether the loop takes control before it
/// commands, stops writing when nothing changed, and gives the fans back when it is told to.
/// </summary>
public class FanBackendSeamTests
{
    private static TemperatureReading Die(double c) => new(c, TemperatureSource.SmuDieTctl);

    /// <summary>A zone reading on its ceiling: the sensor has stopped measuring.</summary>
    private static TemperatureReading BlindZone() => new(86.0, TemperatureSource.AcpiThermalZone);

    private static FanService Loop(ScriptedFanBackend fan, Func<TemperatureReading> read) =>
        new(fan, read, FanCurve.CreateDefault(), TimeSpan.FromMilliseconds(5));

    /// <summary>Runs until <paramref name="until"/> holds, or gives up and lets the assert speak.</summary>
    private static async Task<bool> Settle(Func<bool> until, int millis = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return true;
            await Task.Delay(5);
        }
        return until();
    }

    [Fact]
    public async Task ControlIsTakenBeforeTheFirstLevelIsWritten()
    {
        // The ordering is the whole safety property, not a nicety. A level written before
        // control is taken is accepted by the firmware and quietly discarded, so the reverse
        // order looks identical from here and cools nothing.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, () => Die(70));

        service.Start();
        Assert.True(await Settle(() => fan.Levels.Count > 0), "the loop never commanded a level");
        service.Stop();

        Assert.Equal("control", fan.Calls[0]);
        Assert.StartsWith("level", fan.Calls[1]);
    }

    [Fact]
    public async Task AnUnchangedLevelIsNotRewrittenEveryTick()
    {
        // Re-sending an identical level cost one firmware round trip every two seconds and
        // bought nothing -- measured, and the reason the write became conditional. At a 5ms
        // interval this test would see hundreds of writes if the condition regressed.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, () => Die(70));

        service.Start();
        Assert.True(await Settle(() => fan.Levels.Count > 0));
        await Task.Delay(200);
        service.Stop();

        Assert.Single(fan.Levels);
    }

    [Fact]
    public async Task AChangedTemperatureCommandsANewLevel()
    {
        // The converse of the test above, and it has to be here: "never writes twice" is also
        // what a completely broken loop looks like.
        var fan = new ScriptedFanBackend();
        double temp = 45;
        using var service = Loop(fan, () => Die(temp));

        service.Start();
        Assert.True(await Settle(() => fan.Levels.Count > 0));
        var first = fan.Levels[0];

        temp = 85;
        Assert.True(await Settle(() => fan.Levels.Count > 1), "a large temperature rise commanded nothing");
        service.Stop();

        Assert.True(fan.Levels[^1].Fan1 > first.Fan1,
                    $"fan went {first.Fan1} -> {fan.Levels[^1].Fan1} as the die went 45C -> 85C");
    }

    [Fact]
    public async Task StoppingHandsTheFansBack()
    {
        // The most important line in the class. Exiting without this leaves the fans latched at
        // whatever was last commanded, with nothing running to change it.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, () => Die(70));

        service.Start();
        Assert.True(await Settle(() => fan.Levels.Count > 0));
        service.Stop();

        Assert.Equal("restore", fan.Calls[^1]);
    }

    [Fact]
    public void StoppingHandsTheFansBackEvenIfTheLoopNeverRan()
    {
        // Stop() is reached from shutdown paths that do not know whether Start() ever succeeded,
        // and a service that took control and then failed its first tick is exactly the case
        // where handing back matters most.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, () => Die(70));

        service.Stop();

        Assert.Contains("restore", fan.Calls);
    }

    [Fact]
    public async Task AThrowingBackendDoesNotKillTheLoop()
    {
        // A dead loop is silent and the machine keeps getting hotter, so no single failed tick
        // may end it. The failure is recorded instead, because "the fans did nothing" should be
        // diagnosable rather than guessed at.
        var fan = new ScriptedFanBackend { FailWith = new InvalidOperationException("firmware said no") };
        using var service = Loop(fan, () => Die(70));

        service.Start();
        Assert.True(await Settle(() => service.LastError is not null), "the failure was never recorded");

        int seen = fan.Calls.Count;
        Assert.True(await Settle(() => fan.Calls.Count > seen), "the loop stopped trying after one failure");

        Assert.Contains("firmware said no", service.LastError);
        Assert.True(service.IsRunning);
        service.Stop();
    }

    [Fact]
    public async Task AReadingThatThrowsIsSurvivedToo()
    {
        // The temperature delegate reaches WMI and the SMU on a real machine, and both fail
        // transiently. This used to sit outside the handler.
        var fan = new ScriptedFanBackend();
        bool fail = true;
        using var service = Loop(fan, () => fail ? throw new TimeoutException("sensor busy") : Die(70));

        service.Start();
        Assert.True(await Settle(() => service.LastError is not null));

        fail = false;
        Assert.True(await Settle(() => fan.Levels.Count > 0), "the loop never recovered once the sensor came back");
        service.Stop();

        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task ABlindSensorForcesMaximumOnlyAfterItPersists()
    {
        // A saturated zone reading means the real temperature is unknown but at least that high,
        // so the only honest answer is maximum airflow. But one saturated sample is not evidence
        // of an emergency -- at startup it is close to guaranteed, and answering the first one
        // with 5600 rpm is the "jet engine every time I boot" behaviour. Five consecutive
        // samples is the agreed threshold.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, BlindZone);

        service.Start();
        Assert.True(await Settle(() => service.SensorCeilingReached), "a blind sensor never forced the fans up");
        service.Stop();

        Assert.Equal(100, service.LastCommandedLevelPercent);
    }

    [Fact]
    public async Task ATrustworthySensorAtTheSameTemperatureIsNotTreatedAsBlind()
    {
        // 86C from Tctl is a real 86C and should be cooled as such, not treated as a sensor that
        // has run out of range. Testing the bare number rather than the reading would force
        // maximum fan on every genuine hot die reading.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, () => Die(86));

        service.Start();
        Assert.True(await Settle(() => service.HasCommanded));
        await Task.Delay(150);
        service.Stop();

        Assert.False(service.SensorCeilingReached);
    }

    [Fact]
    public async Task NothingIsReportedAsCommandedBeforeATickHasRun()
    {
        // LastCommandedLevelPercent defaults to 0, and 0 is a real fan level rather than a
        // sentinel. Anything reading it before the first tick records a commanded 0% that never
        // happened -- and in a fan log specifically, a spurious 0% while hot looks exactly like
        // the failure this application exists to detect.
        var fan = new ScriptedFanBackend();
        using var service = Loop(fan, () => Die(70));

        Assert.False(service.HasCommanded);

        service.Start();
        Assert.True(await Settle(() => service.HasCommanded));
        service.Stop();

        // And cleared again on stop, so a restarted service cannot report a stale figure.
        Assert.False(service.HasCommanded);
    }
}
