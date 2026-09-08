using System.Diagnostics;

namespace OmniHub.Core.Diagnostics;

/// <summary>
/// One sampled moment during a run.
///
/// Every field that can be unreadable is nullable, and stays null rather than being filled with
/// a last-known or interpolated value. A run record is evidence; a plausible number inside one
/// is worse than a gap, because the gap is visible and the plausible number is not.
/// </summary>
public readonly record struct LoadSample(
    TimeSpan Elapsed,
    double? TempC,
    double? PackageWatts,
    double? CpuClockGhz,
    int? FanRpm,
    int? CommandedPercent,
    bool Throttling,
    string Sensor);

/// <summary>What a finished run amounts to. Pure summary of samples, no hardware.</summary>
public sealed record RunSummary(
    int SampleCount,
    TimeSpan Duration,
    double? MedianTempC,
    double? P90TempC,
    double? MaxTempC,
    double? MedianWatts,
    double? MedianClockGhz,
    double? MinClockGhz,
    TimeSpan? TimeToThrottle,
    double ThrottledFraction);

/// <summary>
/// Statistics over a run.
///
/// Pure and static, in the same spirit as <see cref="Optimize.AdaptiveTuning.Direction"/>: the
/// part worth testing is the arithmetic, and keeping it away from the hardware is what makes it
/// testable at all.
/// </summary>
public static class RunStats
{
    /// <summary>
    /// The p'th percentile of <paramref name="values"/>, p in [0,1], by nearest rank.
    ///
    /// Nearest rank rather than interpolation: these are sampled physical readings, and an
    /// interpolated percentile reports a temperature that was never measured. That is the same
    /// mistake as smoothing the fan curve chart, wearing a different hat.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return null;

        var sorted = values.ToArray();
        Array.Sort(sorted);

        // Clamp before indexing: p == 1 must land on the last element, not one past it.
        int rank = (int)Math.Ceiling(Math.Clamp(p, 0, 1) * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    public static double? Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>
    /// How far into the run throttling was first seen, or null if it never was.
    ///
    /// Null means "not observed in this run", which is not the same as "cannot happen" -- a
    /// longer run may still find it. Reported as absent rather than as a good result.
    /// </summary>
    public static TimeSpan? TimeToThrottle(IEnumerable<LoadSample> samples)
    {
        foreach (var s in samples)
            if (s.Throttling) return s.Elapsed;
        return null;
    }

    public static RunSummary Summarise(IReadOnlyList<LoadSample> samples)
    {
        if (samples.Count == 0)
            return new RunSummary(0, TimeSpan.Zero, null, null, null, null, null, null, null, 0);

        var temps = Present(samples, s => s.TempC);
        var watts = Present(samples, s => s.PackageWatts);
        var clocks = Present(samples, s => s.CpuClockGhz);

        return new RunSummary(
            SampleCount: samples.Count,
            Duration: samples[^1].Elapsed,
            MedianTempC: Median(temps),
            P90TempC: Percentile(temps, 0.90),
            MaxTempC: temps.Count == 0 ? null : temps.Max(),
            MedianWatts: Median(watts),
            MedianClockGhz: Median(clocks),
            // The floor matters more than the average on a sustained run: it is what the machine
            // fell back to once it had run out of thermal headroom.
            MinClockGhz: clocks.Count == 0 ? null : clocks.Min(),
            TimeToThrottle: TimeToThrottle(samples),
            ThrottledFraction: samples.Count(s => s.Throttling) / (double)samples.Count);
    }

    private static List<double> Present(IReadOnlyList<LoadSample> samples, Func<LoadSample, double?> pick)
    {
        var result = new List<double>(samples.Count);
        foreach (var s in samples)
            if (pick(s) is double v) result.Add(v);
        return result;
    }
}

/// <summary>
/// Runs a fixed sustained load and records what the machine did, so a tuning change can be
/// compared against a baseline instead of against an impression.
///
/// WHY THIS EXISTS
///
/// Version 1.1.1 shipped a fix for one real bug and a worse regression beside it: a stale cache
/// made the adaptive controller stop itself seconds after launch, leaving the CPU sustained
/// power limit frozen with nothing to lower it when the machine got hot. The unit tests passed.
/// A ninety-second idle sample looked better than the build it replaced. The regression was
/// found only because the machine's owner said it felt hotter.
///
/// There was no way to check. That is what this fixes -- not that individual bug.
///
/// The load is deliberately plain arithmetic rather than a benchmark: the goal is to hold the
/// package at a steady repeatable draw so the thermal response can be observed, not to produce a
/// score. Nothing here interprets the result; it records and summarises.
/// </summary>
public sealed class LoadTest
{
    /// <summary>Default run length. Long enough to leave the burst window and reach steady state.</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(3);

    /// <summary>Gap between samples. Matches the app's own poll so a run lines up with the thermal log.</summary>
    public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Loads every core for <paramref name="duration"/>, sampling as it goes.
    ///
    /// The sampler is injected rather than reached for, so this type holds no hardware reference
    /// and the caller decides what a sample contains -- which is also what keeps it testable.
    /// </summary>
    /// <param name="sample">Takes one reading. Called on a background thread, never the UI thread.</param>
    public static async Task<IReadOnlyList<LoadSample>> RunAsync(
        TimeSpan duration,
        Func<TimeSpan, LoadSample> sample,
        int? threads = null,
        TimeSpan? sampleInterval = null,
        IProgress<LoadSample>? progress = null,
        CancellationToken token = default)
    {
        int workers = Math.Max(1, threads ?? Environment.ProcessorCount);
        var interval = sampleInterval ?? DefaultSampleInterval;
        var samples = new List<LoadSample>((int)(duration.TotalSeconds / interval.TotalSeconds) + 4);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var burners = new Task[workers];
        for (int i = 0; i < workers; i++)
            burners[i] = Task.Factory.StartNew(
                () => Burn(stop.Token), stop.Token,
                // LongRunning keeps these off the thread pool. They never yield, and parking
                // every pool thread on a spin loop would starve the poll and fan loops -- which
                // are exactly what this run exists to observe.
                TaskCreationOptions.LongRunning, TaskScheduler.Default);

        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < duration && !token.IsCancellationRequested)
            {
                try { await Task.Delay(interval, token); }
                catch (OperationCanceledException) { break; }

                LoadSample reading;
                try { reading = sample(clock.Elapsed); }
                catch { continue; }   // one failed read must not end the run

                samples.Add(reading);
                progress?.Report(reading);
            }
        }
        finally
        {
            // Always stop the load -- on cancellation, and on an exception above. Leaving every
            // core pinned after a run is the one failure this must not have.
            stop.Cancel();
            try { await Task.WhenAll(burners); } catch { }
        }

        return samples;
    }

    /// <summary>
    /// The load. Plain floating-point work, with the accumulator returned so the JIT cannot
    /// prove the loop dead and remove it -- which would leave a load test that loads nothing.
    /// </summary>
    private static double Burn(CancellationToken token)
    {
        double acc = 0;
        while (!token.IsCancellationRequested)
            for (int i = 1; i <= 8192; i++)
                acc += Math.Sqrt(i) * Math.Sin(i);
        return acc;
    }
}
