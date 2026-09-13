using System.Diagnostics;

namespace OmniHub.Core.Network;

/// <summary>What the loaded-latency measurement was able to conclude.</summary>
public enum LoadVerdict
{
    /// <summary>The load never materialised and the latency did not move. Nothing was learned.</summary>
    Inconclusive,

    /// <summary>The link carried real traffic and latency barely moved. This is the good answer.</summary>
    Clean,

    /// <summary>Latency rose under load. Queues somewhere on the path are holding packets.</summary>
    Bloated,
}

/// <summary>Idle latency, loaded latency, and whether the comparison between them means anything.</summary>
public sealed record LoadedLatencyResult(
    LatencyStats Idle,
    LatencyStats Loaded,
    double ThroughputMbps,
    LoadVerdict Verdict,
    string? Note)
{
    /// <summary>Milliseconds of latency the load added. A negative value is noise, not a benefit.</summary>
    public double AddedMs => Idle.IsEmpty || Loaded.IsEmpty ? 0 : Loaded.AvgMs - Idle.AvgMs;

    /// <summary>
    /// The worst case rather than the mean, because a single 300 ms excursion is what a player
    /// feels as a rubber-band, and averaging it away is how a connection gets called fine.
    /// </summary>
    public double WorstAddedMs => Idle.IsEmpty || Loaded.IsEmpty ? 0 : Loaded.MaxMs - Idle.AvgMs;
}

/// <summary>
/// Measures how much latency appears when the connection is busy.
///
/// This is the most useful network measurement there is for a gamer, and almost nothing on a
/// desktop reports it. Idle ping is the number everyone quotes and the one least likely to be the
/// problem: a connection that answers in 6 ms with nothing happening can answer in 300 ms the
/// moment a game patch, a cloud sync or somebody else's video call starts filling the same pipe.
/// Oversized buffers in the modem and at the ISP hold those packets rather than dropping them, so
/// nothing is lost -- it simply arrives far too late to be worth having, which is precisely the
/// failure a game cannot absorb.
///
/// The fix for a positive result is not in this application. It lives in the router, as SQM
/// running fq_codel or CAKE with the shaper set just below the real line rate. What this can
/// honestly do is tell the user whether they have the problem at all, with a number, so the
/// question stops being settled by guesswork.
/// </summary>
public static class LoadedLatencyTest
{
    /// <summary>
    /// Below this the download plainly never got going, so a flat latency reading proves nothing
    /// about queueing. Set low on purpose: the aim is to catch a load that did not happen, not to
    /// demand a fast connection.
    /// </summary>
    public const double MinimumUsefulMbps = 3.0;

    /// <summary>
    /// How much added latency counts as real rather than as sampling noise.
    ///
    /// Deliberately tighter than the usual scoring, which is relaxed enough to call 30 ms an "A".
    /// That is a fair grade for a video call and a poor one here: 30 ms is most of a frame at
    /// 60 Hz and is plainly visible in a shooter.
    /// </summary>
    public const double NoiseFloorMs = 12.0;

    /// <summary>
    /// A large file to pull during the load phase. Any URL that streams steadily will do; the
    /// bytes are read and thrown away.
    /// </summary>
    public const string DefaultLoadUrl = "https://speed.cloudflare.com/__down?bytes=500000000";

    /// <summary>
    /// Runs the test: a baseline, then the same measurement again while a download saturates the
    /// link.
    ///
    /// The saturation check is the part that matters, and it exists because leaving it out
    /// produced a confidently wrong answer during development. A first attempt started the
    /// download, pinged, compared, and reported "no bufferbloat, latency changed by -0.9 ms". The
    /// download had transferred zero bytes and failed silently, so what had actually been
    /// measured was an idle link, twice. A load test that cannot tell "your queues are fine"
    /// apart from "the load never ran" is worse than no test at all, because it hands back a
    /// clean bill of health for a question nobody asked.
    ///
    /// So throughput is measured, reported, and allowed to invalidate the run. The single
    /// exception is a latency rise: if the delay climbed, queueing happened, and that conclusion
    /// holds whether or not the transfer reached any particular rate.
    /// </summary>
    /// <param name="pingTarget">Host to time. A near, reliable one is best -- the point is the CHANGE, not the distance.</param>
    /// <param name="loadUrl">Where to pull bytes from. Defaults to <see cref="DefaultLoadUrl"/>.</param>
    /// <param name="seconds">Length of the loaded phase.</param>
    public static async Task<LoadedLatencyResult> RunAsync(
        string pingTarget = "1.1.1.1",
        string? loadUrl = null,
        int seconds = 8,
        CancellationToken ct = default)
    {
        var idle = await NetworkProbe.PingAsync(pingTarget, count: 10, intervalMs: 200, ct: ct)
            .ConfigureAwait(false);

        if (idle.IsEmpty)
            return new LoadedLatencyResult(idle, LatencyStats.None, 0, LoadVerdict.Inconclusive,
                $"{pingTarget} did not answer, so there is no baseline to compare against.");

        long bytes = 0;
        string? downloadNote = null;
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var sw = Stopwatch.StartNew();
        var download = Task.Run(async () =>
        {
            try
            {
                await DrainAsync(loadUrl ?? DefaultLoadUrl,
                    read => Interlocked.Add(ref bytes, read), loadCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { downloadNote = ex.Message; }
        }, CancellationToken.None);

        // Let the transfer reach full rate before sampling. Measuring through TCP slow start
        // would average a mostly-idle link into the "loaded" figure and understate the problem.
        await Task.Delay(1500, ct).ConfigureAwait(false);

        long bytesAtStart = Interlocked.Read(ref bytes);
        var loadedStart = sw.Elapsed;

        var loaded = await NetworkProbe.PingAsync(
            pingTarget, count: Math.Max(8, seconds * 3), intervalMs: 250, ct: ct).ConfigureAwait(false);

        double window = (sw.Elapsed - loadedStart).TotalSeconds;
        long moved = Interlocked.Read(ref bytes) - bytesAtStart;

        loadCts.Cancel();
        await download.ConfigureAwait(false);

        double mbps = window > 0 ? moved * 8.0 / (window * 1_000_000.0) : 0;

        return Decide(idle, loaded, mbps, pingTarget, downloadNote);
    }

    /// <summary>
    /// Turns a finished measurement into a verdict. Pure, and separated from the I/O above purely
    /// so it can be tested, because this is the logic that was wrong the first time and a test
    /// that needs a live network to run is a test nobody runs.
    ///
    /// Order of the branches is the whole design:
    ///
    ///   1. A latency rise is proof of queueing by itself and is therefore checked FIRST, before
    ///      the throughput gate can discard the run. A slow transfer that still bloated the queue
    ///      has told us what we came to find out.
    ///   2. Otherwise, a transfer too small to fill a queue makes a flat reading meaningless. This
    ///      is the branch whose absence produced a confident "no bufferbloat" from a download that
    ///      had moved zero bytes.
    ///   3. A target that answered while idle and went silent under load is not a clean result
    ///      either; that is loss, and it is worse than delay.
    /// </summary>
    public static LoadedLatencyResult Decide(
        LatencyStats idle, LatencyStats loaded, double mbps, string pingTarget, string? downloadNote = null)
    {
        double added = loaded.IsEmpty || idle.IsEmpty ? 0 : loaded.AvgMs - idle.AvgMs;

        if (!loaded.IsEmpty && added >= NoiseFloorMs)
            return new LoadedLatencyResult(idle, loaded, mbps, LoadVerdict.Bloated, null);

        if (mbps < MinimumUsefulMbps)
            return new LoadedLatencyResult(idle, loaded, mbps, LoadVerdict.Inconclusive,
                downloadNote is { Length: > 0 }
                    ? $"The load could not run ({downloadNote.Split('.')[0]}), so nothing was learned about queueing."
                    : $"Only {mbps:0.0} Mbps moved, too little to fill a queue. This result says nothing either way.");

        return loaded.IsEmpty
            ? new LoadedLatencyResult(idle, loaded, mbps, LoadVerdict.Inconclusive,
                $"{pingTarget} stopped answering once the link was busy, which is itself worth looking into.")
            : new LoadedLatencyResult(idle, loaded, mbps, LoadVerdict.Clean, null);
    }

    /// <summary>
    /// Pulls bytes and discards them, reporting each chunk so the caller can time a window of the
    /// transfer. Nothing is buffered: a 500 MB response must not become 500 MB of memory.
    /// </summary>
    private static async Task DrainAsync(string url, Action<int> onRead, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        // A default user agent gets refused by some CDNs, and a refused request then reads as
        // "no bufferbloat" rather than as "the request was rejected" -- measured against
        // Cloudflare while building this, which answered the framework default with a 403.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniHub/1.0 (+https://github.com/Vomitted/OmniHub)");

        using var resp = await http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];

        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n <= 0) break;
            onRead(n);
        }
    }
}
