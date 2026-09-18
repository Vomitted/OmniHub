namespace OmniHub.Core.Telemetry;

/// <summary>
/// What a run of frames looked like.
///
/// Every figure is derived from present-to-present intervals, which is frame PACING and not
/// displayed frame time. Correlating a present through to scanout needs a per-present state machine
/// over Flip, MMIOFlip and VSyncDPC with per-vendor quirks -- thousands of lines in PresentMon's own
/// consumer -- so nothing here claims dropped frames, tearing, v-sync state, GPU render time or
/// click-to-photon. The interface says "frame time (present)" for the same reason.
/// </summary>
/// <param name="OnePercentLowFps">
/// The mean frame rate of the worst one per cent of frames.
///
/// NOT the 99th-percentile frame time converted to a rate. Those differ, and quoting one under the
/// other's name is the commonest error in this whole category of measurement: the percentile is a
/// single frame's duration, while the low is an average over the tail, so the percentile always
/// reads worse and a tool using it silently overstates every stutter it reports.
/// </param>
public sealed record FrameStats(
    int Frames,
    double AverageFps,
    double MedianMs,
    double P95Ms,
    double P99Ms,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    int Stutters);

/// <summary>
/// The arithmetic over a run of frame intervals.
///
/// THERE IS NO COLLECTOR BEHIND THIS YET, AND THE REASON IS MEASURED
///
/// Frame presents come from ETW, and the practical way to read them is Microsoft's TraceEvent
/// package. It was added, built and measured rather than argued about, and the numbers decided it:
/// the build output went from 2,053 KB to 13,313 KB, six and a half times, and brought native
/// folders for amd64, arm64 and x86 plus the Microsoft.Extensions dependency-injection and logging
/// stack.
///
/// Of the eleven megabytes, about seven are msdia140.dll across three architectures -- the debug
/// symbol resolver, which reading present events never touches -- and KernelTraceControl, which is
/// for kernel sessions rather than the user-mode providers this would use. Two of the three
/// architectures cannot run on this machine at all, and the installer copies its source directory
/// whole, so they would ship.
///
/// ExcludeAssets="native" does not remove them: the package copies them through its own targets.
/// Stripping them after the build would work only if the library never loads them at runtime, and
/// that cannot be established without driving a live session.
///
/// So: eleven megabytes, mostly for architectures this laptop cannot execute, to support a feature
/// that is off by default, in an application whose job is keeping the fans honest. The statistics
/// below are the half that can be quietly wrong and they are done; the collector is one package
/// reference away for anyone who decides the trade is worth it.
///
/// Separated from anything that collects frames for that reason, and because collection needs a
/// live tracing session while this needs a list of numbers.
///
/// Separated from anything that collects them, because the collection needs a live tracing session
/// and this needs a list of numbers -- and this is the half that can be quietly wrong.
/// </summary>
public static class FrameTimes
{
    /// <summary>
    /// How much longer than the median a frame must take to count as a stutter.
    ///
    /// Twice is the conventional line and it is a convention rather than a measurement, so it is
    /// named here instead of buried. Against the median rather than the mean: a handful of very
    /// long frames drags a mean far enough to hide the stutters that produced it.
    /// </summary>
    public const double StutterMultiple = 2.0;

    /// <summary>
    /// Below this many frames nothing is reported.
    ///
    /// A one per cent low over fewer than a hundred frames is one or two frames wearing the name of
    /// a statistic, and a tenth of a per cent needs a thousand before it means anything at all.
    /// </summary>
    public const int MinimumFrames = 100;

    /// <summary>
    /// Summarises a run, or null when there is not enough of it to summarise.
    /// </summary>
    /// <param name="intervalsMs">Present-to-present intervals in milliseconds, in any order.</param>
    public static FrameStats? Measure(IReadOnlyList<double> intervalsMs)
    {
        if (intervalsMs is null) return null;

        // A non-positive or non-finite interval is not a frame. Two presents with the same
        // timestamp happen at the boundaries of a trace, and dividing by one gives infinity.
        var frames = intervalsMs.Where(ms => ms > 0 && !double.IsNaN(ms) && !double.IsInfinity(ms))
                                .OrderBy(ms => ms)
                                .ToArray();

        if (frames.Length < MinimumFrames) return null;

        double totalMs = frames.Sum();
        double median = Percentile(frames, 50);

        return new FrameStats(
            Frames: frames.Length,

            // Frames divided by elapsed time, not the mean of the rates. Averaging rates weights
            // every frame equally regardless of how long it took, which flatters a stuttering run.
            AverageFps: frames.Length / (totalMs / 1000.0),

            MedianMs: median,
            P95Ms: Percentile(frames, 95),
            P99Ms: Percentile(frames, 99),

            OnePercentLowFps: TailLowFps(frames, 0.01),
            PointOnePercentLowFps: TailLowFps(frames, 0.001),

            Stutters: frames.Count(ms => ms > median * StutterMultiple));
    }

    /// <summary>
    /// The mean frame rate over the slowest <paramref name="share"/> of frames.
    ///
    /// At least one frame is always taken, so a short run reports its single worst frame rather
    /// than reporting nothing -- but see MinimumFrames for why a short run is not summarised at all.
    /// </summary>
    internal static double TailLowFps(double[] ascending, double share)
    {
        int take = Math.Max(1, (int)(ascending.Length * share));

        double sum = 0;
        for (int i = ascending.Length - take; i < ascending.Length; i++) sum += ascending[i];

        return take / (sum / 1000.0);
    }

    /// <summary>
    /// Linear-interpolated percentile over an ascending array.
    ///
    /// Interpolated rather than nearest-rank so that two runs differing by one frame do not report
    /// a percentile that jumps, which reads as a change when nothing changed.
    /// </summary>
    internal static double Percentile(double[] ascending, double percent)
    {
        if (ascending.Length == 0) return 0;
        if (ascending.Length == 1) return ascending[0];

        double position = (ascending.Length - 1) * Math.Clamp(percent, 0, 100) / 100.0;
        int low = (int)Math.Floor(position);
        int high = (int)Math.Ceiling(position);

        return low == high ? ascending[low] : ascending[low] + (ascending[high] - ascending[low]) * (position - low);
    }
}
