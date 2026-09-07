using System.IO;
using System.Runtime.InteropServices;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.App;

internal static class Program
{
    // This is a WPF/WinForms (Windows-subsystem) executable, so it has no console
    // by default -- Console.WriteLine in the CLI branches below would otherwise
    // write to nothing. AllocConsole() alone isn't enough: .NET's Console class
    // latches onto the process's original (invalid) std handles at startup and
    // doesn't notice a console created afterward, so Console.Out/In must be
    // rebound to the new console's real handles explicitly.
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private static void AttachVisibleConsole()
    {
        AllocConsole();
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }

    public static void RunProbeCli()
    {
        AttachVisibleConsole();
        try
        {
            RunProbe();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Probe crashed: {ex}");
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }
    }

    public static void RunCalibrateCli()
    {
        AttachVisibleConsole();
        try
        {
            RunCalibrate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Calibration crashed: {ex}");
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }
    }

    public static void RunHeadlessCli()
    {
        AttachVisibleConsole();
        RunHeadlessFanService();
    }

    // Dumps everything we can read from the BIOS interface, unmodified.
    // Run this FIRST on a new model before trusting any curve logic -- it
    // confirms the exact fan count/type/level layout for that specific
    // model instead of assuming the reference implementations' values.
    static void RunProbe()
    {
        Console.WriteLine("=== OmniHub Hardware Probe ===");

        var model = ModelProfile.Detect();
        Console.WriteLine($"Manufacturer : {model.Manufacturer}");
        Console.WriteLine($"Product      : {model.Product}");
        Console.WriteLine($"Baseboard    : {model.BaseboardProduct}");
        Console.WriteLine();

        try
        {
            using var bios = new BiosInterop();
            var fan = new FanController(bios);
            var sys = new SystemController(bios);
            var gpu = new GpuController(bios);

            byte count = fan.GetFanCount();
            Console.WriteLine($"Fan count       : {count}");
            Console.WriteLine($"Fan types       : {BitConverter.ToString(fan.GetFanType(), 0, Math.Max(1, (int)count))}");
            Console.WriteLine($"Fan levels      : {BitConverter.ToString(fan.GetFanLevel(), 0, Math.Max(1, (int)count))}");
            Console.WriteLine($"Fan table (32B) : {BitConverter.ToString(fan.GetFanTable(), 0, 32)} ...");
            Console.WriteLine($"Temperature     : {sys.GetTemperatureC()} C (via ACPI thermal zones, not hpqBIntM)");
            Console.WriteLine($"Max fan active  : {sys.GetMaxFanActive()}");
            Console.WriteLine($"Throttling      : {sys.GetThrottling()}");
            Console.WriteLine($"GPU mode        : {gpu.GetMode()}");
            Console.WriteLine($"GPU power       : {gpu.GetPower()}");

            // What the board says about itself. This is the part that answers "will this work
            // on my laptop" for a model nobody has tried, without anyone maintaining a list of
            // model numbers by hand.
            Console.WriteLine();
            Console.WriteLine("--- capabilities, as reported by the firmware ---");
            var sysData = sys.ReadSystemData();
            Console.WriteLine($"System data     : {(sysData is { } sd ? sd.ToString() : "not reported (command unsupported on this board)")}");

            // Three outcomes, not two. A reply with every capability byte clear is a failed
            // read, and reporting it as "no fan control" once produced a flat contradiction:
            // the claim appeared on a machine this application was actively driving the fans
            // of. Whether the fan commands work is answered by trying them -- the fan count,
            // types and levels printed above -- not by trusting a blank capability block.
            Console.WriteLine($"Fan control     : {sysData switch
            {
                { LooksUnreported: true } => "capability block came back empty -- treat as unknown, not unsupported "
                                             + "(the fan readings above are the real evidence)",
                { SoftwareFanControl: true } => "supported",
                { } => "the board reports no software fan control",
                null => "unknown",
            }}");
            Console.WriteLine($"Adapter         : {(sys.ReadAdapterStatus() is { } ad ? ad.ToString() : "not reported")}");
            Console.WriteLine($"Keyboard type   : {(sys.ReadKeyboardType() is { } kt ? kt.ToString() : "not reported")}");
            Console.WriteLine($"Backlight       : {sys.HasKeyboardBacklight() switch { true => "supported", false => "not supported", null => "not reported" }}");

            Console.WriteLine();
            Console.WriteLine("Copy this whole block back -- it's the ground truth needed to");
            Console.WriteLine("calibrate the curve and confirm command bytes for this exact laptop.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BIOS probe failed: {ex}");
            Console.WriteLine("Make sure you're running as Administrator.");
        }
    }

    // Steps through candidate raw fan levels so a human can listen and report
    // where the fan actually stops getting louder. Raw is an RPM/100 target, not
    // 0-255 PWM (confirmed via decompiled Omen Gaming Hub source: its own
    // SetFanLevel handler logs "raw * 100 rpm"), and 20-55 is the
    // community-established usable range for this EC family -- see FanService.cs.
    static void RunCalibrate()
    {
        Console.WriteLine("=== OmniHub Fan Calibration ===");
        Console.WriteLine("Make sure OmniHub's GUI (if running) is set to BIOS Default first --");
        Console.WriteLine("otherwise its own Auto-mode loop will fight this for control.");
        Console.WriteLine();
        Console.WriteLine("For each raw value: listen for a few seconds, then press Enter to advance.");
        Console.WriteLine("Note the raw value where it stops getting louder -- that's your real ceiling.");
        Console.WriteLine();

        using var bios = new BiosInterop();
        var fan = new FanController(bios);

        try
        {
            fan.SetFanMode(FanMode.Performance);

            byte[] candidates = { 15, 20, 25, 30, 35, 40, 45, 50, 55, 60 };
            foreach (var raw in candidates)
            {
                fan.SetFanLevel(raw, raw);
                Console.Write($"raw={raw,3}  (press Enter for next) ");
                Console.ReadLine();
            }
        }
        finally
        {
            fan.RestoreAutomaticControl();
            Console.WriteLine();
            Console.WriteLine("Restored automatic BIOS fan control.");
        }
    }

    static void RunHeadlessFanService()
    {
        using var bios = new BiosInterop();
        var fanController = new FanController(bios);
        var sys = new SystemController(bios);
        var settings = AppSettings.Load();
        var curve = settings.BuildCurve();
        var service = new FanService(fanController, () => sys.ReadTemperature(), curve);

        Console.WriteLine("Starting headless fan service. Press Ctrl+C to stop and restore automatic control.");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; service.Stop(); Environment.Exit(0); };

        service.Start();
        Thread.Sleep(Timeout.Infinite);
    }
}
