// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using OmniHub.Core.Diagnostics;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// What happens to the fans when this application stops existing without being asked to.
///
/// OmniHub rested on an assumption it never had to state: that a controller left under manual
/// control reverts to its own curve once nothing is commanding it. That is true of the HP board
/// this was built on, and it is why fan state was deliberately left out of the restore journal.
///
/// It is an assumption about one embedded controller, carried into a release whose whole purpose
/// is to talk to other people's. A controller that latches instead holds whatever level a crashed
/// process last sent it, with nothing left running to change it, and on a machine that hangs as
/// often as this one that is not a hypothetical.
///
/// These tests were first written to assert the hazard, deliberately, so the fix would break them
/// visibly rather than having to re-derive the claim. It did, and this is the other side of it:
/// the takeover is now written down BEFORE it happens, a controller that reverts is not journalled
/// at all, and the next launch after a crash hands a latched fan back.
///
/// The ordering is the whole property. A note made after taking control is missing in exactly the
/// case it exists for -- the process dying between the two.
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
    public async Task OnAControllerThatLatchesTheTakeoverIsWrittenDownBeforeItHappens()
    {
        // This test used to assert the hazard: a latching controller kept whatever a crashed
        // process last sent it, because nothing wrote down that the fans had been taken. It was
        // written so the fix would break it visibly, and this is the fix.
        //
        // The ordering is the entire property. A note made AFTER taking control is missing in
        // exactly the case it exists for -- the process dying between the two.
        var journal = new RestoreJournal(TempJournal());
        var fan = new ScriptedFanBackend { Latches = true };

        // Captured AT the moment control is taken, not after. Once both have happened the order is
        // invisible, and the order is the whole point -- so the observation has to be made from
        // inside the hardware call itself.
        bool? journalledFirst = null;
        fan.OnTakeControl = () =>
            journalledFirst ??= journal.Pending.ContainsKey(RestoreJournal.FanManualControl);

        using var service = Loop(fan);
        service.Journal = journal;

        service.Start();
        Assert.True(await Commanded(fan));

        Assert.True(journalledFirst, "the fans were taken before the takeover was written down");
        Assert.True(journal.Pending.ContainsKey(RestoreJournal.FanManualControl));
    }

    [Fact]
    public async Task AControllerThatRevertsIsNotJournalledAtAll()
    {
        // The HP case. There is no debt, so recording one would be noise -- and the journal's own
        // documentation is explicit that an entry nobody needs to honour is worse than none.
        var journal = new RestoreJournal(TempJournal());
        var fan = new ScriptedFanBackend { Latches = false };
        using var service = Loop(fan);
        service.Journal = journal;

        service.Start();
        Assert.True(await Commanded(fan));

        Assert.Empty(journal.Pending);
    }

    [Fact]
    public async Task AfterACrashTheNextLaunchHandsALatchedFanBack()
    {
        // The whole point, end to end: a session takes the fans on a latching controller and dies
        // without cleaning up, and the next launch finds the note and settles it.
        string path = TempJournal();
        var journal = new RestoreJournal(path);
        var crashed = new ScriptedFanBackend { Latches = true };

        var service = Loop(crashed);
        service.Journal = journal;
        service.Start();
        Assert.True(await Commanded(crashed));

        crashed.SimulateUncleanExit();
        Assert.NotNull(crashed.Held);   // still pinned, as a latching controller would be

        // Next launch: same machine, fresh objects, and the journal read back off disk.
        var relaunched = new RestoreJournal(path);
        var fan = new ScriptedFanBackend { Latches = true };

        var done = RestoreReconciler.Run(relaunched, fan);

        Assert.Contains(done, r => r.Key == RestoreJournal.FanManualControl && r.Applied);
        Assert.Contains("restore", fan.Calls);
        Assert.Empty(relaunched.Pending);
    }

    [Fact]
    public async Task AnOrderlyStopSettlesTheDebtRatherThanLeavingIt()
    {
        // The clean path has to clear the note, or every subsequent launch would hand back fans
        // nobody was holding -- and a journal that cries wolf is one people learn to ignore.
        var journal = new RestoreJournal(TempJournal());
        var fan = new ScriptedFanBackend { Latches = true };
        using var service = Loop(fan);
        service.Journal = journal;

        service.Start();
        Assert.True(await Commanded(fan));
        Assert.NotEmpty(journal.Pending);

        service.Stop();

        Assert.Empty(journal.Pending);
    }

    private static string TempJournal() =>
        Path.Combine(Path.GetTempPath(), $"omnihub-journal-{Guid.NewGuid():N}.json");

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
