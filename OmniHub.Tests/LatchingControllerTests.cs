// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// What happens to the fans when this application stops existing without being asked to.
///
/// OmniHub currently rests on an assumption it has never had to state: that a controller left
/// under manual control reverts to its own curve once nothing is commanding it. That is true of
/// the HP board this was built on, and it is why the fan mode is deliberately NOT journalled --
/// RestoreJournal says so in as many words.
///
/// It is an assumption about one embedded controller, being carried into a release whose whole
/// purpose is to talk to other people's. A controller that latches instead would hold whatever
/// level a crashed process last sent it, with nothing left running to change it, and on a machine
/// that hangs as often as this one that is not a hypothetical.
///
/// These tests do not fix that. They establish what is true today on each kind of controller, so
/// the journal work has something to flip rather than a claim to re-derive. The second one
/// asserts the hazard deliberately: it will need changing when the fix lands, and that is the
/// point of it.
/// </summary>
public class LatchingControllerTests
{
    private static FanService Loop(ScriptedFanBackend fan) =>
        new(fan,
            () => new TemperatureReading(70, TemperatureSource.SmuDieTctl),
            FanCurve.CreateDefault(),
            TimeSpan.FromMilliseconds(5));

    private static async Task<bool> Commanded(ScriptedFanBackend fan)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (fan.Levels.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);
        return fan.Levels.Count > 0;
    }

    [Fact]
    public async Task OnAControllerThatRevertsACrashCostsNothing()
    {
        // The HP case, and the reason nothing has gone wrong so far. The process disappears, the
        // EC notices nobody is commanding it, and its own curve takes over -- including the
        // 0%-while-hot entry this application exists to prevent, but the fans are at least under
        // some control rather than pinned.
        var fan = new ScriptedFanBackend { Latches = false };
        using var service = Loop(fan);

        service.Start();
        Assert.True(await Commanded(fan), "the loop never commanded a level");
        Assert.NotNull(fan.Held);

        fan.SimulateUncleanExit();

        Assert.Null(fan.Held);
    }

    [Fact]
    public async Task OnAControllerThatLatchesACrashLeavesTheFansPinned()
    {
        // The hazard, asserted as the current state of affairs rather than as a desired one.
        //
        // Nothing in this application writes down what it commanded before it commanded it, so
        // after an unclean exit there is no record that the fans were ever taken off the
        // firmware's curve, and the next launch has no idea there is anything to undo. The fan
        // sits at whatever the dead process last asked for until somebody reboots.
        //
        // When the restore journal covers fan state, this test should stop being able to observe
        // a pinned fan across a restart, and it is written so the fix breaks it visibly.
        var fan = new ScriptedFanBackend { Latches = true };
        using var service = Loop(fan);

        service.Start();
        Assert.True(await Commanded(fan));
        var commanded = fan.Held;

        fan.SimulateUncleanExit();

        Assert.Equal(commanded, fan.Held);
        Assert.NotNull(fan.Held);
    }

    [Fact]
    public async Task AnOrderlyStopIsSafeOnEitherKind()
    {
        // Which is exactly why the crash case is the one that matters: the clean path already
        // hands control back, on a latching controller and a reverting one alike, so nothing
        // about the shutdown sequence needs to change. Only the path that never reaches it does.
        foreach (bool latches in new[] { false, true })
        {
            var fan = new ScriptedFanBackend { Latches = latches };
            using var service = Loop(fan);

            service.Start();
            Assert.True(await Commanded(fan));
            service.Stop();

            Assert.Null(fan.Held);
            Assert.False(fan.UnderManualControl);
        }
    }
}
