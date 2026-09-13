namespace OmniHub.Core.Network;

/// <summary>A place to measure the distance to, and the endpoint that stands in for it.</summary>
/// <param name="Name">How the region is shown.</param>
/// <param name="Host">A host physically in that region.</param>
/// <param name="ApproxKm">Great-circle distance from Jakarta, used to derive the physics floor.</param>
public sealed record ServerRegion(string Name, string Host, int ApproxKm);

/// <summary>One region's measured result, with the floor it is being judged against.</summary>
public sealed record RegionLatency(ServerRegion Region, ProbeResult Probe)
{
    /// <summary>
    /// The fastest a round trip to this region could possibly be.
    ///
    /// Light in fibre travels at about two thirds of its vacuum speed, so roughly 200,000 km/s,
    /// and a round trip covers the distance twice. Real paths are not great circles and add
    /// routing on top, so this is a floor nothing can beat rather than a target anyone should
    /// expect to hit. It is here because it changes what the measured number MEANS: 206 ms to
    /// Oregon sounds terrible until you notice no connection on earth can do it under about
    /// 135 ms.
    /// </summary>
    public double PhysicsFloorMs => Region.ApproxKm * 2.0 / 200.0;

    /// <summary>
    /// How much the real path costs above the theoretical floor. This is the only part of a
    /// distant server's latency that anything could ever improve, and it is the number a routing
    /// service would be selling.
    /// </summary>
    public double OverheadMs => Probe.Stats.IsEmpty ? 0 : Probe.Stats.MinMs - PhysicsFloorMs;
}

/// <summary>
/// Measures round-trip time to a spread of world regions, so "which server should I join" has an
/// answer with numbers behind it.
///
/// The endpoints are cloud regions rather than game servers, which is a deliberate compromise and
/// worth being straight about: a game's servers in a given city will be close to these but not
/// identical, because they sit behind different transit. What this measures reliably is the
/// SHAPE -- which regions are near, which are far, and by how much -- and that is what actually
/// decides where to queue.
///
/// They are also reached over TCP rather than ICMP by necessity. Every one of these endpoints
/// ignores ping and answers a handshake, which is exactly why
/// <see cref="NetworkProbe.MeasureAsync"/> falls back the way it does.
/// </summary>
public static class ServerRegions
{
    /// <summary>
    /// Distances are from Jakarta, since that is where this was built and measured. On a machine
    /// elsewhere the measured times stay correct and only the floor comparison shifts, which is
    /// why the floor is always shown beside the measurement rather than folded into a score.
    /// </summary>
    public static IReadOnlyList<ServerRegion> All { get; } = new[]
    {
        new ServerRegion("Jakarta",   "ec2.ap-southeast-3.amazonaws.com",    50),
        new ServerRegion("Singapore", "ec2.ap-southeast-1.amazonaws.com",   900),
        new ServerRegion("Tokyo",     "ec2.ap-northeast-1.amazonaws.com",  5800),
        new ServerRegion("Mumbai",    "ec2.ap-south-1.amazonaws.com",      5000),
        new ServerRegion("Sydney",    "ec2.ap-southeast-2.amazonaws.com",  5500),
        new ServerRegion("US West",   "ec2.us-west-2.amazonaws.com",      13500),
        new ServerRegion("US East",   "ec2.us-east-1.amazonaws.com",      16400),
        new ServerRegion("Frankfurt", "ec2.eu-central-1.amazonaws.com",   11000),
        new ServerRegion("Sao Paulo", "ec2.sa-east-1.amazonaws.com",      16000),
    };

    /// <summary>
    /// Measures every region, one at a time.
    ///
    /// Sequential on purpose. Running them together would have each measurement competing with
    /// the others for the same uplink, so the tool would be measuring its own congestion and
    /// reporting it as distance -- the same self-inflicted error the loaded-latency test exists
    /// to expose. It takes longer, and it is the only way the numbers mean anything.
    /// </summary>
    public static async Task<List<RegionLatency>> MeasureAllAsync(
        int samplesPerRegion = 6,
        IProgress<RegionLatency>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<RegionLatency>(All.Count);

        foreach (var region in All)
        {
            ct.ThrowIfCancellationRequested();

            var probe = await NetworkProbe.MeasureAsync(
                region.Host, count: samplesPerRegion, timeoutMs: 2000, intervalMs: 120, ct: ct)
                .ConfigureAwait(false);

            var row = new RegionLatency(region, probe);
            results.Add(row);
            progress?.Report(row);
        }

        return results;
    }
}
