using OmniHub.Core.Hardware;

namespace OmniHub.Core.Telemetry;

/// <summary>What was holding the processor back at one instant.</summary>
/// <param name="AtUtc">When the sample was taken.</param>
/// <param name="Name">The binding constraint, or <see cref="LimitHistory.Unconstrained"/>.</param>
/// <param name="Percent">How close that constraint was to its own limit.</param>
public sealed record LimitSample(DateTime AtUtc, string Name, double Percent);

/// <summary>A stretch of time during which one constraint was the binding one.</summary>
public sealed record LimitSegment(DateTime FromUtc, DateTime ToUtc, string Name)
{
    public TimeSpan Duration => ToUtc - FromUtc;
}

/// <summary>How much of the measured time one constraint accounted for.</summary>
/// <param name="Fraction">Of the time actually covered by samples, not of the wall clock.</param>
public sealed record LimitShare(string Name, TimeSpan Duration, double Fraction);

/// <summary>
/// Which limit was binding, over time.
///
/// The application can already say what is holding the processor back at this instant. That
/// answers "why is it slow right now" and not "why has it been slow", and the second question is
/// the one somebody actually asks -- the answer to which is a sentence like "you were current-
/// limited for forty per cent of the last hour", which no tool in this class reports.
///
/// Held in memory rather than written to a file. Nothing else records these limits, so there is
/// no history to read back and a new log would only ever work forwards; a two-hour ring costs
/// nothing and answers the question as asked. A persisted version is a later decision, made once
/// the live one has proved it earns its place.
///
/// Two things this has to get right, and they are the same two the thermal history had to.
///
/// Samples arrive only while something is asking the SMU, which is when a screen that shows
/// these numbers is open. The gaps in between are not quiet periods, they are unmeasured ones,
/// so shares are of the time actually covered and the coverage is reported alongside them. A
/// fraction quoted against the wall clock would say the machine was unconstrained during hours
/// nobody was watching it.
///
/// And a machine at idle is not "limited by sustained power at eleven per cent". Something is
/// always nearest its limit, so the tightest constraint is only named once it is genuinely close
/// to binding; below that the sample records that nothing was holding the processor back, which
/// is a real answer rather than an absence of one.
/// </summary>
public sealed class LimitHistory
{
    /// <summary>
    /// How close a constraint has to be to its own limit before it is called the binding one.
    ///
    /// The same threshold the live readout uses, so the strip on screen and the band underneath
    /// it cannot disagree about whether the machine is being held back.
    /// </summary>
    public const double BindingPercent = 95.0;

    /// <summary>The name recorded when nothing was close enough to its limit to be binding.</summary>
    public const string Unconstrained = "Not limited";

    /// <summary>
    /// Two hours at one sample per five seconds.
    ///
    /// Bounded by the SMU's own cache rather than chosen: reads are coalesced to one every five
    /// seconds because the mailbox transaction holds a global PCI mutex, so 1,440 entries is as
    /// much history as can exist.
    /// </summary>
    public const int Capacity = 1440;

    /// <summary>
    /// Longer than this between samples and the two are not joined.
    ///
    /// Four times the sampling interval. Nothing is known about what was binding in a gap, and
    /// drawing one long band across it would invent the most confident-looking stretch on the
    /// whole chart out of the period with no data at all.
    /// </summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(20);

    private readonly object _lock = new();
    private readonly LimitSample[] _ring = new LimitSample[Capacity];
    private int _next;
    private int _count;

    /// <summary>Records what was binding at this instant. Cheap enough for the read path.</summary>
    public void Record(DateTime atUtc, PowerSnapshot snapshot)
    {
        var (name, percent) = snapshot.TightestLimit();

        lock (_lock)
        {
            _ring[_next] = new LimitSample(
                atUtc,
                percent >= BindingPercent ? name : Unconstrained,
                percent);

            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Everything recorded at or after the given instant, oldest first.</summary>
    public IReadOnlyList<LimitSample> Since(DateTime fromUtc)
    {
        var samples = new List<LimitSample>(_count);

        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                var sample = _ring[(_next - _count + i + Capacity) % Capacity];
                if (sample.AtUtc >= fromUtc) samples.Add(sample);
            }
        }

        return samples;
    }

    /// <summary>How much history there is, for a caller that wants to say so.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>
    /// Consecutive samples of the same limit, collapsed into stretches.
    ///
    /// A segment ends where the limit changes or where the samples stop. A run of one sample is
    /// an instant and gets no width of its own -- giving it a nominal duration would be inventing
    /// time it was not observed for.
    /// </summary>
    public static IReadOnlyList<LimitSegment> Segments(IReadOnlyList<LimitSample> samples) =>
        Segments(samples, MaxGap);

    /// <inheritdoc cref="Segments(IReadOnlyList{LimitSample})"/>
    public static IReadOnlyList<LimitSegment> Segments(IReadOnlyList<LimitSample> samples, TimeSpan maxGap)
    {
        var segments = new List<LimitSegment>();
        if (samples.Count < 2) return segments;

        DateTime from = samples[0].AtUtc;
        string name = samples[0].Name;

        for (int i = 1; i < samples.Count; i++)
        {
            bool gap = samples[i].AtUtc - samples[i - 1].AtUtc > maxGap;
            bool changed = samples[i].Name != name;

            if (!gap && !changed) continue;

            // Closed at the previous sample, which is the last instant this limit was observed.
            if (samples[i - 1].AtUtc > from)
                segments.Add(new LimitSegment(from, samples[i - 1].AtUtc, name));

            from = samples[i].AtUtc;
            name = samples[i].Name;
        }

        if (samples[^1].AtUtc > from)
            segments.Add(new LimitSegment(from, samples[^1].AtUtc, name));

        return segments;
    }

    /// <summary>
    /// How much of the measured time each limit accounted for, largest first.
    ///
    /// Of the measured time. The denominator is the total duration of the segments, never the
    /// width of the window asked for, because the periods between them were not observed.
    /// </summary>
    public static IReadOnlyList<LimitShare> Shares(IReadOnlyList<LimitSegment> segments)
    {
        double total = segments.Sum(s => s.Duration.TotalSeconds);
        if (total <= 0) return Array.Empty<LimitShare>();

        return segments
            .GroupBy(s => s.Name)
            .Select(g =>
            {
                double seconds = g.Sum(s => s.Duration.TotalSeconds);
                return new LimitShare(g.Key, TimeSpan.FromSeconds(seconds), seconds / total * 100.0);
            })
            .OrderByDescending(s => s.Fraction)
            .ToList();
    }

    /// <summary>Total time the segments actually cover, which is not the window's width.</summary>
    public static TimeSpan Covered(IReadOnlyList<LimitSegment> segments) =>
        TimeSpan.FromSeconds(segments.Sum(s => s.Duration.TotalSeconds));

    /// <summary>
    /// The history in a sentence, leading with what held the machine back most.
    ///
    /// Says how much of the window was actually measured, because a share computed over four
    /// minutes of a one-hour window is a true statement about four minutes and a misleading one
    /// about the hour.
    /// </summary>
    public static string Describe(IReadOnlyList<LimitSegment> segments, TimeSpan window)
    {
        var shares = Shares(segments);
        if (shares.Count == 0)
            return "Nothing has been recorded yet. The limits are sampled while a screen that "
                 + "reads them is open.";

        TimeSpan covered = Covered(segments);

        var limited = shares.Where(s => s.Name != Unconstrained).ToList();

        string lead = limited.Count == 0
            ? "Nothing was holding the processor back for any of the measured time."
            : $"{limited[0].Name} was the binding constraint for {limited[0].Fraction:0}% of it"
              + (limited.Count > 1
                  ? $", {limited[1].Name} for {limited[1].Fraction:0}%."
                  : ".");

        string free = shares.FirstOrDefault(s => s.Name == Unconstrained) is { } idle && limited.Count > 0
            ? $" Nothing was binding for the other {idle.Fraction:0}%."
            : "";

        return $"{Humanise(covered)} measured out of the last {Humanise(window)}. {lead}{free}";
    }

    private static string Humanise(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:0} min"
        : $"{span.TotalHours:0.#} h";
}
