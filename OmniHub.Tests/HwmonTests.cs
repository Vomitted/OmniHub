// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System;
using System.IO;
using OmniHub.Core.Vendors;
using OmniHub.Daemon;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The Linux fan path, driven against a fake /sys tree.
///
/// This project has no Linux machine, which would ordinarily make a Linux backend the sort of code
/// that ships unverified and is found to be wrong by somebody else's laptop. It does not have to
/// be, because sysfs is not an API -- it is a directory of text files, and a directory of text
/// files can be built anywhere. Every path in the Linux code hangs off an injectable root for
/// exactly this reason.
///
/// What is asserted is the behaviour that differs from the HP path and so would be caught by no
/// existing test: that a PWM channel latches and must therefore be journalled, that the mode found
/// on arrival is the mode restored on exit, and that channel numbering is read rather than counted.
/// </summary>
public class HwmonTests
{
    [Fact]
    public void ChannelsAreEnumeratedRatherThanCounted()
    {
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798")
           .Temp(1, 42000).Temp(3, 51000)   // no temp2: ordinary, and fatal to anything counting
           .Fan(1, 2400).Fan(2, 2100)
           .Pwm(1, 128, enable: 2).Pwm(2, 120, enable: 2);

        var chip = Assert.Single(new Hwmon(sys.Root).Chips());

        Assert.Equal("nct6798", chip.Name);
        Assert.Equal(new[] { 1, 3 }, chip.Temps);
        Assert.Equal(new[] { 1, 2 }, chip.Fans);
        Assert.Equal(new[] { 1, 2 }, chip.Pwm);
    }

    [Fact]
    public void AnAttributeOfAChannelIsNotMistakenForAChannel()
    {
        // pwm1_enable and pwm1_mode describe pwm1. Reading them as channels of their own would
        // have this backend writing duty cycles into a mode register, which ends with a fan
        // controller in a state nobody asked for.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798").Pwm(1, 128, enable: 2);
        File.WriteAllText(Path.Combine(sys.Root, "hwmon0", "pwm1_mode"), "1");

        Assert.Equal(new[] { 1 }, new Hwmon(sys.Root).Chips()[0].Pwm);
    }

    [Fact]
    public void ASensorThatDidNotAnswerIsNotASensorReadingZero()
    {
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798");
        File.WriteAllText(Path.Combine(sys.Root, "hwmon0", "fan1_input"), "");

        var hwmon = new Hwmon(sys.Root);

        Assert.Null(hwmon.ReadNumber(Path.Combine(sys.Root, "hwmon0", "fan1_input")));
        Assert.Null(hwmon.ReadNumber(Path.Combine(sys.Root, "hwmon0", "does_not_exist")));
    }

    [Fact]
    public void ThePwmChannelIsTreatedAsLatchingSoTheTakeoverGetsWrittenDown()
    {
        // The most consequential difference from the HP board. HP's controller reverts when
        // nothing is commanding it, so a crash costs nothing. A hwmon channel in manual mode holds
        // its last duty indefinitely, and answering true here would make FanService skip the
        // journal entry that is the only record an unclean exit left the fan pinned.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798").Fan(1, 2400).Pwm(1, 128, enable: 2);

        Assert.False(Backend(sys, verified: true).RevertsWhenUncommanded);
    }

    [Fact]
    public void TheModeFoundOnArrivalIsTheModeRestoredOnExit()
    {
        // "Automatic" is not one number. Drivers use 2 for their own curve and some offer 3 and
        // above for other modes, so restoring a hard 2 would move a machine that started in mode 3
        // onto a different curve from the one its owner chose.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798").Fan(1, 2400).Pwm(1, 128, enable: 3);

        var backend = Backend(sys, verified: true);
        backend.TakeManualControl();
        Assert.Equal("1", sys.Read("hwmon0", "pwm1_enable"));

        backend.RestoreAutomatic();
        Assert.Equal("3", sys.Read("hwmon0", "pwm1_enable"));
    }

    [Fact]
    public void ReassertingControlDoesNotOverwriteWhatWasFoundOnArrival()
    {
        // Control is re-asserted on a timer, because firmware reclaims it on resume. If each pass
        // recaptured the "original" mode, the second would record our own 1 -- and the hand-back
        // would then put the machine into manual mode and call the debt settled.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798").Fan(1, 2400).Pwm(1, 128, enable: 2);

        var backend = Backend(sys, verified: true);
        backend.TakeManualControl();
        backend.TakeManualControl();
        backend.TakeManualControl();
        backend.RestoreAutomatic();

        Assert.Equal("2", sys.Read("hwmon0", "pwm1_enable"));
    }

    [Fact]
    public void NoModeIsWrittenToAMachineNothingWasTakenFrom()
    {
        // Two ways to arrive at a hand-back with no debt, and neither may touch the controller.
        //
        // An unverified board refuses, because the gate is the first thing in the method. A
        // verified board that simply never took control returns without writing, because the
        // fallback mode would otherwise be written to channels this process had not touched --
        // moving a machine onto its driver's automatic curve when it might have been sitting on
        // something else entirely.
        //
        // The assertion is on the controller rather than on which of those happened. What matters
        // is that the byte on the machine is the byte that was there before.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798").Fan(1, 2400).Pwm(1, 128, enable: 3);

        Assert.Throws<HardwareWriteRefusedException>(() => Backend(sys, verified: false).RestoreAutomatic());
        Assert.Equal("3", sys.Read("hwmon0", "pwm1_enable"));

        Backend(sys, verified: true).RestoreAutomatic();
        Assert.Equal("3", sys.Read("hwmon0", "pwm1_enable"));
    }

    [Fact]
    public void AnUnverifiedBoardIsNotCommanded()
    {
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798").Fan(1, 2400).Pwm(1, 128, enable: 2);

        var backend = Backend(sys, verified: false);

        Assert.Equal(VendorTier.Reading, backend.Tier);
        Assert.Throws<HardwareWriteRefusedException>(() => backend.TakeManualControl());
        Assert.Throws<HardwareWriteRefusedException>(() => backend.SetLevels(200, 200));
        Assert.Equal("128", sys.Read("hwmon0", "pwm1"));
    }

    [Fact]
    public void EveryChannelIsCommandedRatherThanOnlyTheFirstTwo()
    {
        // A chip with three fans is not a reason to leave the third uncommanded while the other two
        // are held at maximum. The second level applies to every channel past the first.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "nct6798")
           .Fan(1, 2400)
           .Pwm(1, 0, enable: 2).Pwm(2, 0, enable: 2).Pwm(3, 0, enable: 2);

        Backend(sys, verified: true).SetLevels(200, 150);

        Assert.Equal("200", sys.Read("hwmon0", "pwm1"));
        Assert.Equal("150", sys.Read("hwmon0", "pwm2"));
        Assert.Equal("150", sys.Read("hwmon0", "pwm3"));
    }

    [Fact]
    public void TheUnmodifiedJunctionTemperatureIsPreferredOverTheBiasedOne()
    {
        // Tctl carries a per-model offset whose entire purpose is to bias a fan curve. Where a part
        // publishes both, taking Tctl would mean this curve acting on a number AMD had already
        // adjusted for somebody else's curve.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "k10temp")
           .Temp(1, 61000, label: "Tctl")
           .Temp(2, 51000, label: "Tdie");

        var die = LinuxMachine.DieSensor(new Hwmon(sys.Root));

        Assert.NotNull(die);
        Assert.Equal(51.0, die!()!.Value, 3);
    }

    [Fact]
    public void AnUnlabelledChannelOnANamedDriverIsStillADieSensor()
    {
        // k10temp frequently publishes temp1_input with no label at all, and that channel is Tctl.
        // The driver's name stands in as the evidence the label did not provide.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "k10temp").Temp(1, 61000);

        Assert.Equal(61.0, LinuxMachine.DieSensor(new Hwmon(sys.Root))!()!.Value, 3);
    }

    [Fact]
    public void AnUnknownChipIsNotGuessedToBeADieSensor()
    {
        // Guessing that some unidentified chip's temp1 is a die sensor is how a fan curve ends up
        // tracking the battery or the chipset.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "BAT0").Temp(1, 31000);

        Assert.Null(LinuxMachine.DieSensor(new Hwmon(sys.Root)));
    }

    [Fact]
    public void AControllerWhoseEffectCanBeReadBackIsPreferred()
    {
        // Between two chips that both accept a duty cycle, the one with a tachometer is the one
        // whose control can ever be verified. The other can only be written to hopefully.
        using var sys = new FakeSys();
        sys.Chip("hwmon0", "blind_pwm").Pwm(1, 128, enable: 2);
        sys.Chip("hwmon1", "nct6798").Fan(1, 2400).Pwm(1, 128, enable: 2);

        Assert.Equal("nct6798", LinuxMachine.ControllableChip(new Hwmon(sys.Root))!.Name);
    }

    [Fact]
    public void AMachineWithNoHwmonAtAllIsAnAnswerRatherThanACrash()
    {
        // What this code does on Windows, in a container, and on a kernel built without hwmon.
        string absent = Path.Combine(Path.GetTempPath(), "omnihub-no-such-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(new Hwmon(absent).Chips());
    }

    [Fact]
    public void TheBoardIsNamedFromDmiSoAProfileHasSomethingToBeFiledUnder()
    {
        using var sys = new FakeSys();
        string dmi = Directory.CreateDirectory(Path.Combine(sys.Root, "dmi-id")).FullName;
        File.WriteAllText(Path.Combine(dmi, "sys_vendor"), "HP\n");
        File.WriteAllText(Path.Combine(dmi, "product_name"), "Victus by HP Laptop 15-fb2xxx\n");
        File.WriteAllText(Path.Combine(dmi, "board_name"), "8C2F\n");

        var model = LinuxMachine.Identify(dmi);

        Assert.Equal("HP", model.Manufacturer);
        Assert.Equal("Victus by HP Laptop 15-fb2xxx", model.Product);
        Assert.Equal("8C2F", model.BaseboardProduct);
    }

    [Fact]
    public void AMachineThatWillNotNameItselfReportsNothingRatherThanSomethingPlausible()
    {
        // An absent field must not become "unknown", or every laptop that cannot answer shares one
        // profile -- and a measurement proved on one of them is then applied to all of them.
        string absent = Path.Combine(Path.GetTempPath(), "omnihub-no-dmi-" + Guid.NewGuid().ToString("N"));

        var model = LinuxMachine.Identify(absent);

        Assert.Equal("", model.Manufacturer);
        Assert.Equal("", model.BaseboardProduct);
    }

    private static HwmonFanBackend Backend(FakeSys sys, bool verified)
    {
        var hwmon = new Hwmon(sys.Root);
        return new HwmonFanBackend(hwmon, LinuxMachine.ControllableChip(hwmon)!, "TEST-BOARD", verified);
    }

    /// <summary>A /sys/class/hwmon tree made of real directories and real text files.</summary>
    private sealed class FakeSys : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "omnihub-sys-" + Guid.NewGuid().ToString("N"))).FullName;

        private string _current = "";

        public FakeSys Chip(string dir, string name)
        {
            _current = Directory.CreateDirectory(Path.Combine(Root, dir)).FullName;
            File.WriteAllText(Path.Combine(_current, "name"), name + "\n");
            return this;
        }

        public FakeSys Temp(int n, int milliC, string? label = null)
        {
            File.WriteAllText(Path.Combine(_current, $"temp{n}_input"), milliC + "\n");
            if (label is not null) File.WriteAllText(Path.Combine(_current, $"temp{n}_label"), label + "\n");
            return this;
        }

        public FakeSys Fan(int n, int rpm)
        {
            File.WriteAllText(Path.Combine(_current, $"fan{n}_input"), rpm + "\n");
            return this;
        }

        public FakeSys Pwm(int n, int duty, int enable)
        {
            File.WriteAllText(Path.Combine(_current, $"pwm{n}"), duty.ToString());
            File.WriteAllText(Path.Combine(_current, $"pwm{n}_enable"), enable.ToString());
            return this;
        }

        public string Read(string chip, string attribute) =>
            File.ReadAllText(Path.Combine(Root, chip, attribute)).Trim();

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { /* a leftover temp folder is not a failure */ }
        }
    }
}
