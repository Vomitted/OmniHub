using System.Diagnostics;
using System.Globalization;
using System.Management;

namespace OmniHub.Core.Hardware;

/// <summary>Where a GPU reading came from. Shown in the UI, because the two sources differ.</summary>
public enum GpuSource
{
    /// <summary>nvidia-smi: name, temperature, power, clock and utilisation.</summary>
    NvidiaSmi,

    /// <summary>Windows itself: name and utilisation only. Temperature is not exposed.</summary>
    WindowsCounters,
}

/// <summary>One sample of GPU state. Fields the source will not report come back null.</summary>
public sealed record GpuReading(
    string Name,
    double? TempC,
    double? PowerWatts,
    int? ClockMhz,
    int? UtilisationPercent,
    string Vendor = "",
    GpuSource Source = GpuSource.NvidiaSmi);

/// <summary>
/// GPU telemetry, from nvidia-smi where it exists and from Windows itself everywhere else.
///
/// nvidia-smi is the richer source and stays the preferred one: it reports temperature, power
/// and clock, which nothing in Windows exposes generically. The obvious alternative is NVAPI,
/// and it is the "proper" answer -- but it means shipping P/Invoke against an unversioned vendor
/// DLL whose entry points are looked up by numeric hash, for numbers nvidia-smi already prints.
/// nvidia-smi installs with every NVIDIA driver, lives in System32, and needs no privileges.
///
/// The fallback exists because the NVIDIA-only version reported *nothing at all* on an AMD or
/// Intel machine -- no name, no load, no panel. Windows can answer two of those questions on any
/// adapter: Win32_VideoController names it, and the GPU engine performance counters give
/// utilisation. It cannot answer the third. Temperature, power and clock stay null there rather
/// than being estimated, so the UI says "unavailable" instead of showing a number the machine
/// never reported.
///
/// The cost that matters for nvidia-smi is process startup, measured at about 56 ms on this
/// machine; the cost for the fallback is a WMI query. Both are cached behind the same 3 s window
/// so a caller cannot accidentally spawn one per frame.
///
/// ponytail: process spawn per refresh on the NVIDIA path, WMI query on the other. Move to NVAPI
/// or a perf-counter handle only if something needs this faster than once a second, which a
/// temperature readout does not.
/// </summary>
public static class GpuTelemetry
{
    private static readonly string ExePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");

    private static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(3);
    private static readonly object Gate = new();
    private static GpuReading? _cached;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    /// <summary>The refresh in flight, if any. Guarded by <see cref="Gate"/>; see Read().</summary>
    private static Task? _refresh;

    /// <summary>
    /// Whether nvidia-smi is installed.
    ///
    /// Cached for the process lifetime rather than re-tested per call. This was a file-system
    /// probe on every access, and it is reached from IsAvailable, HasThermalSource and every
    /// Query() -- several times a second, forever, to answer a question whose answer does not
    /// change while the app is running. Same reasoning as AnyAdapter below.
    /// </summary>
    private static readonly Lazy<bool> NvidiaSmiPresent = new(
        () => File.Exists(ExePath), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static bool HasNvidiaSmi => NvidiaSmiPresent.Value;

    /// <summary>
    /// Whether any GPU can be described at all.
    ///
    /// Cached for the process lifetime: it is called from the UI thread to decide whether to
    /// build a readout, and a WMI round trip per call would be felt. Adapters do not appear and
    /// disappear during a session in a way this readout needs to track.
    /// </summary>
    private static readonly Lazy<List<(string Name, string Vendor)>> AdapterList = new(
        Adapters, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<bool> AnyAdapter = new(
        () => AdapterList.Value.Count > 0, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when some GPU can be reported -- via nvidia-smi, or via Windows.</summary>
    public static bool IsAvailable => HasNvidiaSmi || AnyAdapter.Value;

    /// <summary>
    /// True when a source exists that can report GPU temperature and power, not merely name and
    /// load. Only the NVIDIA path can. A file-existence check, so it is cheap enough to call
    /// from the UI thread while building the readiness panel.
    /// </summary>
    public static bool HasThermalSource => HasNvidiaSmi;

    /// <summary>
    /// Latest GPU reading, or null when no GPU could be described at all.
    ///
    /// Null is a normal answer, not an error, and so is a reading whose temperature is null: a
    /// machine whose driver exposes no thermal sensor has no temperature to report, and the
    /// caller should say "unavailable" rather than show a zero that looks like a stone-cold card.
    /// </summary>
    public static GpuReading? Read()
    {
        lock (Gate)
        {
            // Refresh in the BACKGROUND and return what is already held. Never run Query() while
            // holding Gate.
            //
            // Query() launches nvidia-smi and waits up to three seconds for it. Doing that inside
            // the lock meant every other caller queued behind it -- and the callers are the 2s
            // fan control loop and the hardware poll thread, which holds its own re-entrancy
            // interlock while it runs. One slow GPU query therefore stalled the temperature poll
            // and the fan curve together, on a machine whose entire purpose is not letting the
            // fan curve stall. Measured from the thermal logs: the poll's nominal 2s period
            // actually lands at 2.33s, and this sat on that critical path.
            //
            // Single-flight, so the four independent timers that ask for this (2s, 2.33s, 4s and
            // 5s) collapse into one process launch instead of racing to start their own.
            if (DateTime.UtcNow - _cachedAtUtc >= CacheLife && (_refresh is null || _refresh.IsCompleted))
                _refresh = Task.Run(RefreshCache);

            // Before the first refresh completes this is null, which callers already render as
            // unavailable. That is honest for a reading not yet taken, and it corrects itself on
            // the next tick -- a far better trade than blocking the fan loop to avoid one "--"
            // at startup.
            return _cached;
        }
    }

    private static void RefreshCache()
    {
        var reading = Query();   // deliberately outside Gate
        lock (Gate)
        {
            _cached = reading;
            _cachedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Whether asking NVIDIA directly is worth what it costs right now.
    ///
    /// It is not, on battery. nvidia-smi talks to the card over PCIe, and on a laptop whose
    /// discrete GPU has been allowed to power down, that conversation is itself a wake
    /// interrupt: the card leaves D3, and a machine that was drawing nothing from it starts
    /// drawing twelve to fourteen watts. Polling every couple of seconds to report a temperature
    /// therefore CAUSES most of what it reports, and does it while unplugged.
    ///
    /// The Windows path still answers on battery. It reads the adapter name and 3D utilisation
    /// from counters the driver already maintains, so it costs nothing and wakes nothing;
    /// temperature, power and clock go unavailable, which is what they honestly are when nobody
    /// is willing to pay a wake to find out.
    ///
    /// Unknown counts as mains. Windows reports an unknown line status during resume and on some
    /// docks, and going quiet there would drop GPU telemetry on a plugged-in machine for no
    /// reason -- the same asymmetry the capability gating uses.
    /// </summary>
    private static bool WorthWakingTheCard() =>
        Optimize.PowerSourceWatcher.Read() != Optimize.PowerSource.Battery;

    // Falls through to the Windows path when nvidia-smi is present but fails -- a driver that
    // is installed but wedged should still leave the name and load readable.
    private static GpuReading? Query() =>
        (HasNvidiaSmi && WorthWakingTheCard() ? QueryNvidiaSmi() : null) ?? QueryWindows();

    private static GpuReading? QueryNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--query-gpu=name,temperature.gpu,power.draw,clocks.sm,utilization.gpu");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            // stderr is drained CONCURRENTLY, not left to fill.
            //
            // Both pipes are redirected but only stdout was ever read, and ReadToEnd runs
            // before WaitForExit -- so if the child wrote more than one pipe buffer to stderr
            // it would block on that write, this thread would stay blocked reading stdout, and
            // neither side would drain. The "bounded wait" below never gets the chance to
            // apply, because the deadlock happens before it.
            //
            // That matters here more than anywhere: this runs on the poll thread, which holds
            // the poll loop's re-entrancy interlock, so a hang stops temperature readings
            // outright -- the same symptom as a dead fan loop, from a different cause.
            _ = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();

            // Bounded wait: a hung query must not stall the caller's poll loop forever.
            if (!proc.WaitForExit(3000)) { try { proc.Kill(true); } catch { } return null; }
            if (proc.ExitCode != 0) return null;

            // First line only. A machine with two NVIDIA GPUs reports both, and this readout
            // describes the one doing the work rather than trying to merge them.
            string? line = output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
            if (line is null) return null;

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 5) return null;

            return new GpuReading(
                parts[0],
                Number(parts[1]),
                Number(parts[2]),
                (int?)Number(parts[3]),
                (int?)Number(parts[4]),
                "NVIDIA",
                GpuSource.NvidiaSmi);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Name and utilisation from Windows, for any adapter from any vendor.
    ///
    /// Temperature, power and clock are deliberately null: Windows exposes no generic thermal
    /// or power counter for a GPU, and there is no honest way to derive one from what it does
    /// expose. AdapterRAM is skipped for a related reason -- it is a uint32 that saturates at
    /// 4 GB, so on an 8 GB card it reports a number that is simply wrong.
    /// </summary>
    private static GpuReading? QueryWindows()
    {
        try
        {
            // The cached list, not a fresh WMI query.
            //
            // This ran SELECT ... FROM Win32_VideoController on every call, and on battery it is
            // the only GPU path left, so it ran every three seconds forever -- to re-read an
            // adapter name that cannot change while the process lives. AnyAdapter above had
            // already made exactly this argument and cached it; QueryWindows simply never got
            // the same treatment.
            var adapters = AdapterList.Value;
            if (adapters.Count == 0) return null;

            var (name, vendor) = PickAdapter(adapters);
            return new GpuReading(name, null, null, null, Utilisation(), vendor, GpuSource.WindowsCounters);
        }
        catch
        {
            return null;
        }
    }

    private static List<(string Name, string Vendor)> Adapters()
    {
        var found = new List<(string, string)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterCompatibility FROM Win32_VideoController");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                using (mo)
                {
                    string name = mo["Name"]?.ToString()?.Trim() ?? "";
                    if (name.Length > 0)
                        found.Add((name, mo["AdapterCompatibility"]?.ToString()?.Trim() ?? ""));
                }
            }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// Chooses which adapter to describe when a machine has more than one.
    ///
    /// Microsoft's fallback display driver is skipped: it appears when a real driver has failed
    /// and describes nothing useful about the hardware. Beyond that this takes the first
    /// enumerated adapter rather than guessing which is "the" GPU -- on a hybrid machine that
    /// choice is genuinely ambiguous, and the reading carries the adapter's name so whoever
    /// reads it can see which one answered.
    ///
    /// Pure and separated from WMI so it is testable without a graphics card.
    /// </summary>
    public static (string Name, string Vendor) PickAdapter(IReadOnlyList<(string Name, string Vendor)> adapters)
    {
        foreach (var a in adapters)
            if (!a.Name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                return a;

        return adapters[0];
    }

    /// <summary>
    /// Total 3D engine utilisation, summed across every process using it.
    ///
    /// Filtered in the query rather than in the loop: this class reports one instance per
    /// process per engine -- 532 of them on the machine this was written on -- and pulling all
    /// of those across the WMI boundary every few seconds to discard most of them is the kind
    /// of cost that shows up as a stutter rather than as a number.
    ///
    /// Null rather than zero when the counters are missing, so "no data" and "idle" stay
    /// distinguishable.
    /// </summary>
    private static int? Utilisation()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine " +
                "WHERE Name LIKE '%engtype_3D%'");

            double total = 0;
            bool any = false;
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                using (mo)
                {
                    any = true;
                    if (mo["UtilizationPercentage"] is { } v)
                        total += Convert.ToDouble(v, CultureInfo.InvariantCulture);
                }
            }

            // Summing per-process counters can exceed 100 across overlapping engines.
            return any ? (int)Math.Clamp(Math.Round(total), 0, 100) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parses a field, treating nvidia-smi's "[N/A]" placeholders as absent.</summary>
    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
}
