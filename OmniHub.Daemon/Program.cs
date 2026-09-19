// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OmniHub.Core.Diagnostics;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;

namespace OmniHub.Daemon;

/// <summary>
/// OmniHub for Linux: the cooling loop, and the evidence needed to be allowed to run it.
///
/// Four commands, and their order is the argument. Probe and watch read and never write, so they
/// work the moment this is installed on any machine. Verify is the only thing that earns a board
/// the right to be commanded, and it does so by watching a tachometer rather than by asking
/// somebody to agree to a warning. Run refuses until that has happened.
///
/// That sequence is the whole difference between this and a fan script. Plenty of tools will
/// write a duty cycle to pwm1 on request; the question this one insists on answering first is
/// whether writing it does anything, on this laptop, to this fan.
/// </summary>
public static class Program
{
    private const string CurvePath = "/etc/omnihub/curve.json";
    private const string ProfileDir = "/etc/omnihub/profiles";
    private const string StateDir = "/var/lib/omnihub";

    public static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        try
        {
            return command switch
            {
                "probe" => Probe(),
                "watch" => Watch(),
                "verify" => Verify(),
                "run" => Run(),
                "version" or "--version" or "-v" => Version(),
                _ => Help(),
            };
        }
        catch (HardwareWriteRefusedException ex)
        {
            // Not an error. It is the gate doing its job, and its message already explains what
            // would unlock it, so wrapping that in a stack trace would only bury it.
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"omnihub: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            omnihub - fan and thermal control for Linux laptops

              omnihub probe     what this machine exposes; reads only, writes nothing
              omnihub watch     live temperatures and fan speeds; reads only
              omnihub verify    prove a commanded change moves this board's fans (needs root)
              omnihub run       run the fan curve (needs root, and a verified board)
              omnihub version

            Reading works on any machine. Writing stays refused until 'verify' has shown, on this
            exact board, that commanding a fan actually changes its speed. That is not caution for
            its own sake: a PWM file existing only means the kernel has a driver for the chip, not
            that the driver is wired to the fan in this chassis, nor that firmware will honour it.

            Curve:    /etc/omnihub/curve.json     (optional; a measured default is used otherwise)
            Profiles: /etc/omnihub/profiles/<baseboard>.json
            """);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"omnihub {typeof(Program).Assembly.GetName().Version} (GPL-3.0-or-later)");
        return 0;
    }

    // ---------------------------------------------------------------- probe

    /// <summary>
    /// Everything this machine will say about itself, in a form safe to attach to an issue.
    ///
    /// The counterpart of the Windows build's -Probe, and the mechanism by which a laptop nobody
    /// on this project owns becomes a laptop this project supports. Driver names, channel numbers
    /// and readings only: no serial numbers, no UUIDs, nothing identifying a person.
    /// </summary>
    private static int Probe()
    {
        var hwmon = new Hwmon();
        var model = LinuxMachine.Identify();
        var report = new StringBuilder();

        void Say(string line) { Console.WriteLine(line); report.AppendLine(line); }

        Say("=== OmniHub Linux probe ===");
        Say($"Manufacturer : {Blank(model.Manufacturer)}");
        Say($"Product      : {Blank(model.Product)}");
        Say($"Baseboard    : {Blank(model.BaseboardProduct)}");
        Say($"Kernel       : {Environment.OSVersion.VersionString}");
        Say($"Privilege    : {(LibC.IsRoot ? "root" : "unprivileged; the kernel would refuse any write")}");
        Say("");

        var chips = hwmon.Chips();
        if (chips.Count == 0)
        {
            Say($"No hardware monitoring chips under {hwmon.Root}.");
            Say("That is not the same as a machine with no fans. It usually means no kernel driver");
            Say("has claimed this laptop's controller; check lsmod and the dmesg lines for your");
            Say("superio or platform driver.");
        }
        else
        {
            Say($"--- hardware monitoring: {chips.Count} chip{(chips.Count == 1 ? "" : "s")} ---");
            foreach (HwmonChip chip in chips)
            {
                Say($"  {chip}");

                foreach (int n in chip.Fans)
                {
                    long? rpm = hwmon.ReadNumber(Path.Combine(chip.Path, $"fan{n}_input"));
                    Say($"      fan{n}   : {(rpm is null ? "no reading" : rpm + " rpm")}");
                }

                foreach (int n in chip.Temps)
                {
                    string? label = hwmon.ReadText(Path.Combine(chip.Path, $"temp{n}_label"));
                    long? milli = hwmon.ReadNumber(Path.Combine(chip.Path, $"temp{n}_input"));
                    Say($"      temp{n}  : {(milli is null ? "no reading" : (milli.Value / 1000.0).ToString("0.0") + " C")}"
                        + (label is null ? "" : $"  [{label}]"));
                }

                foreach (int n in chip.Pwm)
                {
                    long? duty = hwmon.ReadNumber(Path.Combine(chip.Path, $"pwm{n}"));
                    long? mode = hwmon.ReadNumber(Path.Combine(chip.Path, $"pwm{n}_enable"));
                    Say($"      pwm{n}   : duty {(duty?.ToString() ?? "unreadable")}, mode {DescribeMode(mode)}");
                }
            }
        }

        Say("");
        Say("--- what OmniHub would do here ---");

        var die = LinuxMachine.DieSensor(hwmon);
        Say($"Die sensor   : {(die is null
            ? "none found; the ACPI thermal zone would be the only source"
            : (die()?.ToString("0.0") ?? "present but unreadable") + " C")}");

        Say($"ACPI zone    : {(TryZone() is { } z ? z.ToString("0.0") + " C" : "no readable thermal zone")}");

        if (LinuxMachine.ControllableChip(hwmon) is not { } target)
        {
            Say("Fan control  : unavailable. No chip here exposes a PWM channel, so there is");
            Say("               nothing to command. Reading still works.");
        }
        else
        {
            bool verified = FanProfiles.LoadFanControlVerified(model, ProfileDir);
            var backend = new HwmonFanBackend(hwmon, target, model.BaseboardProduct, verified);
            Say($"Fan control  : {target.Name}, {target.Pwm.Count} channel{(target.Pwm.Count == 1 ? "" : "s")}");
            Say($"Tier         : {backend.Tier} -- {DescribeTier(backend.Tier)}");
        }

        Say($"Zone ceiling : {(FanProfiles.LoadZoneCeilingC(model, ProfileDir) is { } c
            ? $"{c:0.#} C [profile for board {model.BaseboardProduct}]"
            : $"{ThermalReader.DefaultZoneCeilingC:0.#} C [built-in default, measured on an HP board 8C2F; unverified here]")}");

        string path = SaveReport(report.ToString());
        Console.WriteLine();
        Console.WriteLine($"Saved to {path} -- safe to attach to an issue; it names drivers and channels, never serials.");
        return 0;
    }

    // ---------------------------------------------------------------- watch

    private static int Watch()
    {
        var hwmon = new Hwmon();
        var model = LinuxMachine.Identify();
        var thermal = new ThermalReader(LinuxMachine.DieSensor(hwmon));

        if (FanProfiles.LoadZoneCeilingC(model, ProfileDir) is { } ceiling)
            thermal.ZoneCeilingC = ceiling;

        HwmonChip? chip = LinuxMachine.ControllableChip(hwmon)
                          ?? hwmon.Chips().FirstOrDefault(c => c.Fans.Count > 0);

        Console.WriteLine("Reading only. Nothing here writes to the machine. Ctrl-C to stop.");
        Console.WriteLine();

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        while (!stop.IsCancellationRequested)
        {
            string temp;
            try
            {
                var reading = thermal.ReadTemperature();
                temp = reading.IsCeilingLimited
                    ? $"at least {reading.Celsius:0.0} C (sensor saturated)"
                    : $"{reading.Celsius:0.0} C from {reading.Source}";
            }
            catch (Exception ex)
            {
                // A failed read is reported as a failed read. Printing the last good number, or a
                // zero, is the fabricated telemetry this project exists to not produce.
                temp = $"unavailable ({ex.Message})";
            }

            string fans = chip is null || chip.Fans.Count == 0
                ? "no tachometer"
                : string.Join(", ", chip.Fans.Select(n =>
                    hwmon.ReadNumber(Path.Combine(chip.Path, $"fan{n}_input")) is { } rpm
                        ? $"fan{n} {rpm} rpm"
                        : $"fan{n} no reading"));

            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {temp}  |  {fans}");

            if (stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))) break;
        }

        return 0;
    }

    // --------------------------------------------------------------- verify

    /// <summary>
    /// Proves, on this board, that commanding a fan changes its speed.
    ///
    /// The only route from Reading to Verified, and deliberately not replaceable by a prompt.
    /// "Are you sure?" collects consent; it does not collect evidence, and consent from somebody
    /// with no way to know the answer is not worth having.
    ///
    /// The test is small and self-restoring: take control, hold a low duty, read the tachometer,
    /// hold a high duty, read it again, and put the controller back the way it was found whatever
    /// happened in between. A fan that does not move between those two points is a control path
    /// that does not work, however present its files are.
    /// </summary>
    private static int Verify()
    {
        if (!LibC.IsRoot)
        {
            Console.Error.WriteLine("verify writes to the fan controller, which needs root. Try: sudo omnihub verify");
            return 2;
        }

        var hwmon = new Hwmon();
        var model = LinuxMachine.Identify();

        if (string.IsNullOrWhiteSpace(model.BaseboardProduct))
        {
            Console.Error.WriteLine("This machine does not report a baseboard name, so there is no key to file the result under.");
            Console.Error.WriteLine("Without one, a result proved here would be applied to every machine that cannot answer.");
            return 2;
        }

        if (LinuxMachine.ControllableChip(hwmon) is not { } chip)
        {
            Console.Error.WriteLine("No PWM channel on this machine, so there is nothing to verify.");
            return 2;
        }

        if (chip.Fans.Count == 0)
        {
            Console.Error.WriteLine($"{chip.Name} offers PWM but no tachometer, so a commanded change cannot be");
            Console.Error.WriteLine("observed. Verification needs to see the fan move, not merely to write to it.");
            return 2;
        }

        // Constructed as verified, because the point of this command IS the write. The gate keeps
        // the fan curve off an unproven board; it cannot also stand between a board and the only
        // test that would prove it.
        var backend = new HwmonFanBackend(hwmon, chip, model.BaseboardProduct, verified: true);
        string fanPath = Path.Combine(chip.Path, $"fan{chip.Fans[0]}_input");

        Console.WriteLine($"Board {model.BaseboardProduct}, chip {chip.Name}.");
        Console.WriteLine("Taking manual control, then holding two duty cycles for six seconds each.");
        Console.WriteLine("The fans are handed back whatever the result.");
        Console.WriteLine();

        try
        {
            backend.TakeManualControl();

            long? low = HoldAndRead(backend, hwmon, fanPath, duty: 60, "low");
            long? high = HoldAndRead(backend, hwmon, fanPath, duty: 200, "high");

            if (low is null || high is null)
            {
                Console.Error.WriteLine("The tachometer did not answer, so nothing was proved. Not recorded.");
                return 2;
            }

            // A threshold rather than "any change": fan speed wanders by tens of rpm on its own,
            // and a controller whose writes are being swallowed will still show that wander. What
            // is tested is whether the fan followed the command, not whether the number moved.
            const long Meaningful = 200;
            long moved = high.Value - low.Value;

            if (moved < Meaningful)
            {
                Console.Error.WriteLine($"Commanding 60 then 200 moved the fan from {low} to {high} rpm, a change of {moved}.");
                Console.Error.WriteLine($"That is below the {Meaningful} rpm needed to call it a response rather than drift,");
                Console.Error.WriteLine("so this board is NOT recorded as verified. The write may be reaching something");
                Console.Error.WriteLine("this fan is not attached to.");
                return 2;
            }

            var band = FanProfiles.Load(model, ProfileDir) ?? new FanCalibration(0, 255, 255);
            var (saved, detail) = FanProfiles.Save(
                model, band,
                note: $"Fan control verified on Linux against {chip.Name}: duty 60 gave {low} rpm, duty 200 gave {high} rpm.",
                directory: ProfileDir,
                fanControlVerified: true);

            Console.WriteLine();
            Console.WriteLine($"Verified: {low} rpm at duty 60, {high} rpm at duty 200, a change of {moved} rpm.");
            Console.WriteLine(saved ? detail : $"Could not record it: {detail}");
            return saved ? 0 : 1;
        }
        finally
        {
            backend.RestoreAutomatic();
            Console.WriteLine("Controller handed back.");
        }
    }

    private static long? HoldAndRead(HwmonFanBackend backend, Hwmon hwmon, string fanPath, byte duty, string which)
    {
        backend.SetLevels(duty, duty);
        Console.WriteLine($"  duty {duty} ({which}) ... waiting six seconds for the fan to settle");

        // Six seconds because a laptop fan's spin-up is mechanical, not electrical. Reading
        // straight after the write measures how fast the file system is.
        Thread.Sleep(TimeSpan.FromSeconds(6));

        long? rpm = hwmon.ReadNumber(fanPath);
        Console.WriteLine($"  duty {duty} ({which}) gave {(rpm is null ? "no reading" : rpm + " rpm")}");
        return rpm;
    }

    // ------------------------------------------------------------------ run

    private static int Run()
    {
        if (!LibC.IsRoot)
        {
            Console.Error.WriteLine("run writes to the fan controller, which needs root. Try: sudo omnihub run");
            return 2;
        }

        var hwmon = new Hwmon();
        var model = LinuxMachine.Identify();

        if (LinuxMachine.ControllableChip(hwmon) is not { } chip)
        {
            Console.Error.WriteLine("No PWM channel on this machine. 'omnihub watch' still works.");
            return 2;
        }

        bool verified = FanProfiles.LoadFanControlVerified(model, ProfileDir);
        var backend = new HwmonFanBackend(hwmon, chip, model.BaseboardProduct, verified,
                                          FanProfiles.Load(model, ProfileDir));

        Console.WriteLine($"Board {Blank(model.BaseboardProduct)}, chip {chip.Name}, tier {backend.Tier}.");

        if (!verified)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("This board has not been verified, so every fan command would be refused.");
            Console.Error.WriteLine("Run 'sudo omnihub verify' first. It takes about fifteen seconds and hands the");
            Console.Error.WriteLine("fans back when it is done.");
            return 3;
        }

        Directory.CreateDirectory(StateDir);
        var journal = new RestoreJournal(Path.Combine(StateDir, "restore.json"));

        // Settle anything a previous run left owing BEFORE taking control again. This controller
        // latches, so an unclean exit really can have left the fans pinned, and the journal is the
        // only record that it did.
        foreach (var done in RestoreReconciler.Run(journal, backend))
            Console.WriteLine($"Startup: {done.Detail}");

        var thermal = new ThermalReader(LinuxMachine.DieSensor(hwmon));
        if (FanProfiles.LoadZoneCeilingC(model, ProfileDir) is { } ceiling)
            thermal.ZoneCeilingC = ceiling;

        var service = new FanService(backend, thermal.ReadTemperature, LoadCurve()) { Journal = journal };

        using var stop = new CancellationTokenSource();

        // SIGTERM is how systemd stops this, and it is the shutdown that actually matters: a
        // person pressing Ctrl-C is watching, whereas "systemctl stop omnihub" and a reboot are
        // not. Both signals are intercepted rather than observed -- ctx.Cancel suppresses the
        // default termination -- because the default is to end the process immediately, and this
        // controller latches. Ending immediately would leave the fan at whatever duty the curve
        // last commanded, with nothing running to release it.
        //
        // PosixSignalRegistration rather than ProcessExit for the same reason. ProcessExit runs
        // during shutdown under a time limit, racing the cleanup it is supposed to be starting.
        void Handle(PosixSignalContext ctx) { ctx.Cancel = true; stop.Cancel(); }

        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle);
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle);
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Handle);

        service.Start();
        Console.WriteLine("Fan curve running. Ctrl-C or SIGTERM hands the fans back.");

        stop.Token.WaitHandle.WaitOne();

        // Stop calls RestoreAutomatic. This is the one line whose failure leaves a hot laptop
        // under the control of a process that has stopped running.
        service.Stop();
        Console.WriteLine("Fans handed back.");
        return 0;
    }

    // -------------------------------------------------------------- helpers

    /// <summary>
    /// The curve from /etc/omnihub/curve.json, or the measured default.
    ///
    /// A malformed file falls back rather than refusing to start, and says so. A laptop that will
    /// not run its fan curve because of a misplaced comma is a worse outcome than one running the
    /// built-in curve loudly.
    /// </summary>
    private static FanCurve LoadCurve()
    {
        if (!File.Exists(CurvePath)) return FanCurve.CreateDefault();

        try
        {
            var file = JsonSerializer.Deserialize<CurveFile>(
                File.ReadAllText(CurvePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (file?.Points is { Length: > 0 } points)
                return new FanCurve(points.Select(p => new CurvePoint(p.TempC, p.LevelPercent)));

            Console.Error.WriteLine($"{CurvePath} lists no points; using the built-in curve.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{CurvePath} could not be read ({ex.Message}); using the built-in curve.");
        }

        return FanCurve.CreateDefault();
    }

    private sealed class CurveFile
    {
        public CurvePointFile[]? Points { get; set; }
    }

    private sealed class CurvePointFile
    {
        public double TempC { get; set; }
        public byte LevelPercent { get; set; }
    }

    private static double? TryZone()
    {
        try { return new ThermalReader().ReadTemperature().Celsius; }
        catch (Exception) { return null; }
    }

    private static string SaveReport(string text)
    {
        string dir = LibC.IsRoot && Directory.Exists(StateDir) ? StateDir : Path.GetTempPath();
        string path = Path.Combine(dir, $"omnihub-probe-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    private static string Blank(string s) => string.IsNullOrWhiteSpace(s) ? "(not reported)" : s;

    private static string DescribeMode(long? mode) => mode switch
    {
        null => "unreadable",
        0 => "0 (no control: full speed)",
        1 => "1 (manual)",
        2 => "2 (automatic)",
        _ => $"{mode} (driver-specific)",
    };

    private static string DescribeTier(VendorTier tier) => tier switch
    {
        VendorTier.Verified => "fan commands are allowed on this board",
        VendorTier.Reading => "speeds and temperatures can be read; fan commands are refused until 'omnihub verify' passes",
        _ => "nothing is read from or written to the controller",
    };
}
