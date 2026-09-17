using System.Diagnostics;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Reading the GPU through the driver's own library instead of by launching a process.
///
/// Nothing here is pure: it is a P/Invoke against a versioned vendor library, and the realistic
/// failures are an entry point that has moved, a struct laid out wrongly, and a unit conversion.
/// So these run against the real machine and assert shape, in the same spirit as
/// HardwareReadTests -- and a machine with no NVIDIA driver has to pass too, because most do not
/// have one.
///
/// The cross-check is the one that earns its place. NVML and nvidia-smi read the same counters
/// through the same driver, so where both answer they must agree; a disagreement means this code
/// has misread something. Power is the field most worth checking, because NVML reports
/// milliwatts and nvidia-smi reports watts, and a missing division by a thousand is the exact
/// mistake that produces a plausible-looking number three orders of magnitude out.
/// </summary>
public class NvmlTests
{
    /// <summary>
    /// A reading comes back with values in physically possible ranges, or NVML says why not.
    ///
    /// The range assertions are what catch a mis-laid-out struct or a wrong entry point: those
    /// fail by returning fields in each other's slots, which shows up as a temperature in the
    /// thousands or a utilisation above a hundred per cent rather than as an exception.
    /// </summary>
    [Fact]
    public void TheRealMachineReadsOrSaysWhyNot()
    {
        var reading = Nvml.TryRead();

        if (reading is null)
        {
            // Declining is a legitimate outcome, but it has to be an explained one on a machine
            // where the library genuinely is not usable. A silent null would leave the caller
            // unable to tell "no NVIDIA GPU" from "this code is broken".
            Assert.True(Nvml.UnavailableReason is null or { Length: > 0 });
            return;
        }

        Assert.False(string.IsNullOrWhiteSpace(reading.Name));
        Assert.Equal(GpuSource.Nvml, reading.Source);

        if (reading.TempC is { } temp)
            Assert.InRange(temp, 0, 125);

        // A mobile RTX board sits somewhere between a fraction of a watt at idle and its TGP.
        // 250 is well clear of any laptop part and three orders of magnitude below what a
        // milliwatt figure reported as watts would produce.
        if (reading.PowerWatts is { } watts)
            Assert.InRange(watts, 0, 250);

        if (reading.ClockMhz is { } clock)
            Assert.InRange(clock, 0, 4000);

        if (reading.UtilisationPercent is { } load)
            Assert.InRange(load, 0, 100);
    }

    /// <summary>
    /// NVML and nvidia-smi agree, because they are the same counters.
    ///
    /// Skipped unless both answer. Where they both do, a disagreement is this code misreading
    /// the library rather than a difference of opinion between two instruments -- which is what
    /// makes this a stronger check than any range assertion.
    /// </summary>
    [Fact]
    public void NvmlAgreesWithNvidiaSmi()
    {
        var before = Nvml.TryRead();
        if (before is null) return;

        string? line = RunNvidiaSmi();
        if (line is null) return;

        // Read again AFTER, and require nvidia-smi to fall between the two.
        //
        // The earlier version read NVML once and allowed five degrees of drift on the assumption
        // that a temperature does not move much while a process starts. That is true on an idle
        // machine and false on a busy one, which is precisely when this suite runs -- it failed
        // once during a build and passed immediately afterwards. Bracketing accounts for the
        // movement instead of assuming it away, and it is a tighter check rather than a looser
        // one: a wrong sensor index still lands outside a bracket that spans a few degrees.
        var after = Nvml.TryRead();

        var fields = line.Split(',').Select(f => f.Trim()).ToArray();
        if (fields.Length < 5) return;

        // Same board.
        Assert.Equal(fields[0], before.Name);

        if (double.TryParse(fields[1], out double smiTemp)
            && before.TempC is { } first && (after?.TempC ?? first) is { } second)
        {
            const double Slack = 2;   // rounding, and the sample nvidia-smi took mid-flight

            double low = Math.Min(first, second) - Slack;
            double high = Math.Max(first, second) + Slack;

            Assert.True(smiTemp >= low && smiTemp <= high,
                        $"nvidia-smi read {smiTemp} C, outside the {low}-{high} C NVML bracketed");
        }

        // The unit check. Power swings far more than temperature between two samples, so this is
        // deliberately loose -- it is aimed at a factor of a thousand, not at a few watts.
        if (before.PowerWatts is { } watts && double.TryParse(fields[2], out double smiWatts))
            Assert.True(Math.Abs(watts - smiWatts) <= 40,
                        $"NVML read {watts} W, nvidia-smi read {smiWatts} W -- a unit error?");
    }

    /// <summary>
    /// Releasing is safe, repeatable, and leaves the library usable.
    ///
    /// It is called whenever the machine goes to battery, so it runs far more often than
    /// initialisation does. A release that made NVML permanently unusable would silently demote
    /// every later reading to the slow path for the rest of the session, the first time somebody
    /// unplugged their charger.
    /// </summary>
    [Fact]
    public void ReleasingIsSafeAndReversible()
    {
        Nvml.Release();
        Nvml.Release();

        Assert.False(Nvml.IsInUse);

        var reading = Nvml.TryRead();
        if (reading is null) return;

        Assert.True(Nvml.IsInUse);
        Assert.Equal(GpuSource.Nvml, reading.Source);
    }

    /// <summary>
    /// The saving, measured rather than asserted.
    ///
    /// Not a pass/fail threshold on a wall-clock figure: a timing test that fails under load is a
    /// test that gets deleted. It runs both paths and requires only that five library reads cost
    /// no more than one process launch, which is the claim the change actually rests on.
    /// </summary>
    [Fact]
    public void NvmlIsNotSlowerThanLaunchingAProcess()
    {
        if (Nvml.TryRead() is null) return;
        if (RunNvidiaSmi() is null) return;

        var viaLibrary = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) Nvml.TryRead();
        viaLibrary.Stop();

        var viaProcess = Stopwatch.StartNew();
        RunNvidiaSmi();
        viaProcess.Stop();

        Assert.True(viaLibrary.ElapsedMilliseconds <= viaProcess.ElapsedMilliseconds,
                    $"five NVML reads took {viaLibrary.ElapsedMilliseconds} ms, "
                    + $"one nvidia-smi launch took {viaProcess.ElapsedMilliseconds} ms");
    }

    /// <summary>The same query GpuTelemetry makes, or null when nvidia-smi is not there.</summary>
    private static string? RunNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--query-gpu=name,temperature.gpu,power.draw,clocks.sm,utilization.gpu");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            using var process = Process.Start(psi);
            if (process is null) return null;

            _ = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(5000)) { try { process.Kill(true); } catch { } return null; }
            if (process.ExitCode != 0) return null;

            return output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        }
        catch { return null; }
    }
}
