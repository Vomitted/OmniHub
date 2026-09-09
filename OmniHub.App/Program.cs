using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using OmniHub.Core.Diagnostics;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;

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

        // Captured as well as printed. The probe is the only evidence anyone has about a board
        // nobody here owns, and "copy this whole block back" asks someone to select the right
        // part of a console window without missing a line. A file they can attach is a better
        // request, and it is the mechanism per-model support has to grow from -- ModelProfile
        // has anticipated exactly this since it was written.
        var captured = new StringWriter();
        var console = Console.Out;
        Console.SetOut(new TeeWriter(console, captured));

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
            Console.SetOut(console);
            Console.WriteLine(SaveProbe(captured.ToString()));
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }
    }

    /// <summary>
    /// Runs the probe with its output captured instead of printed, saves it, and reports where.
    ///
    /// Shared with the Diagnostics tab so there is one probe rather than two that drift apart.
    /// The console is redirected rather than RunProbe rewritten to take a TextWriter: this is a
    /// Windows-subsystem executable with no console attached, so Console.Out is already going
    /// nowhere and swapping it costs nothing.
    ///
    /// Not for the UI thread; the probe is a series of BIOS round trips behind one send lock.
    /// </summary>
    public static string CaptureProbeReport()
    {
        var captured = new StringWriter();
        var previous = Console.Out;
        Console.SetOut(captured);

        try { RunProbe(); }
        catch (Exception ex) { captured.WriteLine($"Probe crashed: {ex}"); }
        finally { Console.SetOut(previous); }

        return SaveProbe(captured.ToString());
    }

    /// <summary>Writes the probe text beside the logs, and says where it went.</summary>
    static string SaveProbe(string text)
    {
        try
        {
            Directory.CreateDirectory(ThermalLog.LogDirectory);
            string path = Path.Combine(ThermalLog.LogDirectory, $"probe-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
            File.WriteAllText(path, text);
            return $"Saved to        : {path}";
        }
        catch (Exception ex)
        {
            // Never let a failed save look like a failed probe: the output is already on screen.
            return $"(could not save a copy: {ex.Message})";
        }
    }

    /// <summary>
    /// Writes to two places at once, so the probe can be shown and kept without every WriteLine
    /// in it having to know about both.
    /// </summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }

        public override System.Text.Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string? value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
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

    public static void RunLoadTestCli(string[] args)
    {
        AttachVisibleConsole();
        try
        {
            RunLoadTest(args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Load test crashed: {ex}");
        }
        finally
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }
    }

    /// <summary>
    /// Loads every core and records what the machine does about it.
    ///
    /// This exists because version 1.1.1 shipped a regression the unit tests passed over and a
    /// short idle sample looked fine through. It was caught only because the machine's owner
    /// said it felt hotter, and at that point there was no way to check. Two runs of this either
    /// side of a change turn that into a measurement.
    ///
    /// Reads only. It never commands a fan or writes a power limit, which is why it is safe
    /// beside the running application rather than instead of it -- and beside is the point,
    /// since what is being measured is the machine as it normally behaves, with OmniHub
    /// controlling the fans.
    /// </summary>
    static void RunLoadTest(string[] args)
    {
        double minutes = LoadTest.DefaultDuration.TotalMinutes;
        if (args.Length > 1 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && m > 0)
            minutes = Math.Min(m, 30);   // capped: this pins every core, and an unattended hour of that is nobody's intent
        var duration = TimeSpan.FromMinutes(minutes);

        Console.WriteLine("=== OmniHub Load Test ===");
        Console.WriteLine($"Loading every core for {duration.TotalMinutes:0.#} minutes. The machine will get hot.");
        Console.WriteLine("Nothing here commands a fan or writes a power limit -- it only reads.");
        Console.WriteLine();

        using var bios = new BiosInterop();
        var smu = RyzenSmu.TryOpen(out string? smuReason);
        var sys = new SystemController(bios, smu);
        var fan = new FanController(bios);

        AmdTuning? tuning = null;
        if (smu is not null)
        {
            var t = new AmdTuning(smu);
            if (t.IsSupported) tuning = t;
        }
        if (tuning is null)
            Console.WriteLine($"Package power will be unavailable: {smuReason ?? "this CPU is not supported for tuning"}");

        LoadSample Sample(TimeSpan elapsed)
        {
            // Each read is independently guarded, and anything that fails stays null. A run
            // record is evidence; a substituted zero inside one is indistinguishable from a
            // measurement of zero.
            double? temp = null;
            string sensor = "unavailable";
            try { var r = sys.ReadTemperature(); temp = r.Celsius; sensor = r.Source.ToString(); } catch { }

            double? watts = null;
            try { watts = tuning?.ReadPower()?.StapmWatts; } catch { }

            double? ghz = null;
            try { ghz = SystemPerfReader.Read()?.CpuClockGHz; } catch { }

            int? rpm = null;
            try { var levels = fan.GetFanLevel(); if (levels.Length > 0) rpm = FanService.RawToRpm(levels[0]); } catch { }

            // Recorded, but see SystemController.GetThrottling: this flag documents itself as
            // unverified on this firmware, so a run's throttle column is a hint, not the finding.
            bool throttling = false;
            try { throttling = sys.GetThrottling() == ThrottlingState.On; } catch { }

            return new LoadSample(elapsed, temp, watts, ghz, rpm, null, throttling, sensor);
        }

        var progress = new Progress<LoadSample>(s => Console.WriteLine(
            $"  {s.Elapsed.TotalSeconds,5:0}s  {Show(s.TempC, "0.0")}C  {Show(s.PackageWatts, "0.0")}W  "
            + $"{Show(s.CpuClockGhz, "0.00")}GHz  {(s.FanRpm is int r ? $"{r}rpm" : "--")}"));

        var samples = LoadTest.RunAsync(duration, Sample, progress: progress).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"Run written to  : {WriteRun(samples)}");

        var summary = RunStats.Summarise(samples);
        Console.WriteLine();
        Console.WriteLine("--- summary ---");
        Console.WriteLine($"Samples         : {summary.SampleCount} over {summary.Duration.TotalSeconds:0}s");
        Console.WriteLine($"Temperature     : median {Show(summary.MedianTempC, "0.0")}C, "
                          + $"p90 {Show(summary.P90TempC, "0.0")}C, max {Show(summary.MaxTempC, "0.0")}C");
        Console.WriteLine($"Package power   : median {Show(summary.MedianWatts, "0.0")}W");
        Console.WriteLine($"CPU clock       : median {Show(summary.MedianClockGhz, "0.00")}GHz, "
                          + $"floor {Show(summary.MinClockGhz, "0.00")}GHz");
        Console.WriteLine($"Throttling      : {(summary.TimeToThrottle is { } tt ? $"first seen at {tt.TotalSeconds:0}s" : "not observed")}"
                          + $", {summary.ThrottledFraction:P0} of samples");
        Console.WriteLine();
        Console.WriteLine("Run this again after a change and compare the two summaries. The clock");
        Console.WriteLine("floor and the p90 temperature are what move when sustained behaviour does.");
    }

    /// <summary>Formats a reading, or a dash where there is none. Never a substituted number.</summary>
    static string Show(double? value, string format) =>
        value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "--";

    /// <summary>
    /// Writes a run beside the thermal logs, in its own schema.
    ///
    /// Deliberately not ThermalLog's: that file's columns are fixed at the fan curve's own
    /// fields, and a run needs clocks and package power. What is borrowed is the discipline --
    /// invariant culture throughout, because a decimal comma would silently corrupt the column
    /// layout for anyone reading this back on a non-English system.
    /// </summary>
    static string WriteRun(IReadOnlyList<LoadSample> samples)
    {
        Directory.CreateDirectory(ThermalLog.LogDirectory);
        string path = Path.Combine(ThermalLog.LogDirectory,
            $"loadtest-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv");

        using var w = new StreamWriter(path);
        w.WriteLine("elapsed_s,temp_c,sensor,package_w,cpu_ghz,fan_rpm,throttling");
        foreach (var s in samples)
            w.WriteLine(string.Join(',',
                s.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                Csv(s.TempC, "0.0"),
                s.Sensor,
                Csv(s.PackageWatts, "0.0"),
                Csv(s.CpuClockGhz, "0.00"),
                s.FanRpm?.ToString(CultureInfo.InvariantCulture) ?? "",
                s.Throttling ? "True" : "False"));

        return path;

        // An empty field, not a zero: a reader must be able to tell "not measured" from
        // "measured zero".
        static string Csv(double? value, string format) =>
            value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "";
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

            // Which raw band the curve is scaling into. A profile changes how every percentage
            // is commanded, so leaving it implicit would make two machines' probe output look
            // identical while their fans behaved differently.
            var band = FanProfiles.Load(model);
            Console.WriteLine($"Fan band        : {(band ?? FanCalibration.Default) switch
            {
                var c => $"raw {c.MinRawLevel}-{c.MaxRawLevelFan1}"
                         + (c.MaxRawLevelFan2 != c.MaxRawLevelFan1 ? $" / {c.MaxRawLevelFan2} (fan 2)" : "")
                         + (band is null ? "  [built-in default, measured on board 8C2F]"
                                         : $"  [profile for board {model.BaseboardProduct}]"),
            }}");
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
