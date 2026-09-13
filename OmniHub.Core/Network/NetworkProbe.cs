using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OmniHub.Core.Network;

/// <summary>How a round-trip time was obtained.</summary>
public enum ProbeMethod
{
    /// <summary>Nothing answered, by either method.</summary>
    None,

    /// <summary>ICMP echo. The cheapest, and the closest thing to a bare round trip.</summary>
    Icmp,

    /// <summary>
    /// Time to complete a TCP handshake. Roughly one round trip, and usable against hosts that
    /// drop ICMP.
    /// </summary>
    Tcp,
}

/// <summary>One measurement of a host, and how it was taken.</summary>
public sealed record ProbeResult(string Host, ProbeMethod Method, LatencyStats Stats)
{
    public static ProbeResult Unreachable(string host) => new(host, ProbeMethod.None, LatencyStats.None);
}

/// <summary>
/// Measures round-trip time to a host.
///
/// Both methods exist because ICMP alone is no longer enough to measure the internet. Hosts worth
/// measuring routinely drop echo requests while answering TCP perfectly happily: measured while
/// building this, DigitalOcean's endpoints did not answer one of twelve pings and Amazon's
/// regional endpoints answered none either, yet every one of them completed a TCP handshake on
/// port 443 in a stable and entirely sensible time. A tool that only speaks ICMP reports those
/// hosts as unreachable, which is false, and it is the more misleading of the two possible
/// errors: it looks like the network is broken when the network is fine.
///
/// A handshake is not identical to an echo. It includes the server's accept path, so it reads a
/// little higher and a little noisier than a true ICMP round trip. That is worth stating rather
/// than hiding, which is why <see cref="ProbeResult.Method"/> travels alongside the number.
/// </summary>
public static class NetworkProbe
{
    /// <summary>
    /// ICMP first, TCP only if ICMP produced nothing at all.
    ///
    /// The fallback triggers on total silence rather than on partial loss. Partial ICMP loss is
    /// a real measurement -- very often it is the answer the user needs -- and quietly switching
    /// methods would replace a true "you are losing 4% of packets" with a clean TCP number that
    /// conceals it.
    /// </summary>
    public static async Task<ProbeResult> MeasureAsync(
        string host, int count = 20, int tcpPort = 443,
        int timeoutMs = 1500, int intervalMs = 200, CancellationToken ct = default)
    {
        var icmp = await PingAsync(host, count, timeoutMs, intervalMs, ct).ConfigureAwait(false);
        if (!icmp.IsEmpty) return new ProbeResult(host, ProbeMethod.Icmp, icmp);

        var tcp = await TcpRttAsync(host, tcpPort, count, timeoutMs, intervalMs, ct).ConfigureAwait(false);
        return tcp.IsEmpty
            ? ProbeResult.Unreachable(host)
            : new ProbeResult(host, ProbeMethod.Tcp, tcp);
    }

    /// <summary>ICMP echo sampling. A timeout or an error is recorded as a lost probe, never skipped.</summary>
    public static async Task<LatencyStats> PingAsync(
        string host, int count, int timeoutMs = 1500, int intervalMs = 200, CancellationToken ct = default)
    {
        var samples = new List<double>(count);
        int sent = 0;

        using var ping = new Ping();
        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
            catch (SocketException) { }

            if (i < count - 1) await DelayQuietly(intervalMs, ct).ConfigureAwait(false);
        }

        return LatencyStats.From(sent, samples);
    }

    /// <summary>
    /// TCP handshake timing.
    ///
    /// A fresh socket per sample, deliberately: a reused connection measures nothing, and the
    /// handshake is the part that costs the round trip. Each socket is disposed immediately so a
    /// long run does not leave a pile of them in TIME_WAIT on the caller's own machine.
    /// </summary>
    public static async Task<LatencyStats> TcpRttAsync(
        string host, int port, int count, int timeoutMs = 1500, int intervalMs = 200, CancellationToken ct = default)
    {
        var samples = new List<double>(count);
        int sent = 0;

        for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            sent++;
            using var client = new TcpClient();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(timeoutMs);

            var sw = Stopwatch.StartNew();
            try
            {
                await client.ConnectAsync(host, port, attemptCts.Token).ConfigureAwait(false);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            // The caller cancelling and this attempt timing out both surface here, and they mean
            // opposite things. Cancellation must propagate; a timeout is simply a lost probe.
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { }
            catch (SocketException) { }

            if (i < count - 1) await DelayQuietly(intervalMs, ct).ConfigureAwait(false);
        }

        return LatencyStats.From(sent, samples);
    }

    /// <summary>
    /// A delay that ends on cancellation instead of throwing through the sampling loop.
    ///
    /// The loops above already test the token every iteration, so a cancelled run should return
    /// the samples it managed to take rather than lose them to an exception unwinding past the
    /// caller. A cancelled measurement is still a measurement.
    /// </summary>
    private static async Task DelayQuietly(int ms, CancellationToken ct)
    {
        if (ms <= 0) return;
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
