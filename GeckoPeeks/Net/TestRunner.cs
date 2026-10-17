using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using GeckoPeeks.Logging;
using GeckoPeeks.Routing;

namespace GeckoPeeks.Net;

public sealed class ProbeProgress
{
    public string Phase { get; init; } = "";
    public IReadOnlyList<HopObservation>? Hops { get; init; }
}

/// <summary>
/// One route test: traceroute, reverse DNS, last-hop ping, Riot ping.
/// Network and DNS failures leave fields blank. This method does not throw.
/// </summary>
public static class TestRunner
{
    public const string TraceTarget = "192.207.0.5";
    /// <summary>The address the Riot ICMP series pings. Same as the trace target; kept separate so it can change later.</summary>
    public const string RiotPingTarget = TraceTarget;
    public const int MaxHops = 25;
    public const int HopTimeoutMs = 1000;
    /// <summary>Traceroute probes per TTL. A TTL is silent only when every probe failed.</summary>
    public const int ProbesPerHop = 3;
    public const int DnsTimeoutMs = 800;
    public const int PingCount = 10;
    /// <summary>The Riot series stops after this many pings with no reply (100% loss either way).</summary>
    public const int RiotGiveUpAfter = 3;
    /// <summary>Stop traceroute after this many silent hops past the last responder (or from the start).</summary>
    public const int MaxConsecutiveSilentHops = 5;

    public static async Task<RouteRow> RunAsync(
        bool leagueRunning,
        IProgress<ProbeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var row = new RouteRow
        {
            Timestamp = CsvFormat.FormatTimestamp(DateTime.Now),
            LeagueRunning = leagueRunning ? "Y" : "N",
        };

        try
        {
            var net = NetworkContext.Capture();
            row.Adapter = net.Adapter;
            row.Wifi = net.Wifi;
            row.Gateway = net.Gateway;
            row.Vpn = net.Vpn;
            await ExecuteAsync(row, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            row.Cancelled = true;
        }
        catch (Exception ex)
        {
            row.Error = ex.Message;
            Report(progress, "Error: " + ex.Message, row.Hops);
        }

        return row;
    }

    private static async Task ExecuteAsync(RouteRow row, IProgress<ProbeProgress>? progress, CancellationToken cancellationToken)
    {
        var hops = await TracerouteAsync(progress, cancellationToken).ConfigureAwait(false);
        row.Hops = hops;
        row.FullPath = RouteClassifier.BuildFullPath(hops);

        if (hops.Count > 0 && hops.All(hop => !hop.Responded))
        {
            row.Error = "No hops responded (offline or filtered ICMP).";
            Report(progress, row.Error, hops);
            return;
        }

        var features = RouteClassifier.Classify(hops);
        row.Upstream = features.Upstream;
        row.HandoffNetwork = features.HandoffNetwork;
        row.HandoffHost = features.HandoffHost;
        row.HandoffIP = features.HandoffIP;
        row.HandoffCity = features.HandoffCity;
        row.HandoffRttMs = features.HandoffRttMs;

        // mtr-style: TTL-limited echoes toward the trace target, counted only when that hop
        // answers Time Exceeded. Pinging the router's own IP would test its control plane.
        var last = LastHop(hops);
        if (last != null
            && IPAddress.TryParse(last.Ip, out var lastAddress)
            && IPAddress.TryParse(TraceTarget, out var traceAddress))
        {
            row.LastHopIP = last.Ip;
            row.LastHopHost = last.Hostname;
            var stats = await PingSeriesAsync(
                traceAddress,
                PingCount,
                HopTimeoutMs,
                index => Report(progress, $"Ping last hop {index}/{PingCount}", hops),
                cancellationToken,
                options: new PingOptions(last.Number, dontFragment: false),
                accept: (status, from) => IsHopReply(status, from, lastAddress)).ConfigureAwait(false);
            row.LastHopMin = stats.Min;
            row.LastHopAvg = stats.Avg;
            row.LastHopMax = stats.Max;
            row.LastHopLossPct = stats.LossPct;
        }

        if (IPAddress.TryParse(RiotPingTarget, out var riotAddress))
        {
            var riot = await PingSeriesAsync(
                riotAddress,
                PingCount,
                HopTimeoutMs,
                index => Report(progress, $"Ping Riot {index}/{PingCount}", hops),
                cancellationToken,
                RiotGiveUpAfter).ConfigureAwait(false);
            row.RiotHopAvg = riot.Avg;
            row.RiotHopLossPct = riot.LossPct;
        }

        Report(progress, "Writing log", hops);
    }

    private static async Task<List<HopObservation>> TracerouteAsync(
        IProgress<ProbeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var hops = new List<HopObservation>();
        if (!IPAddress.TryParse(TraceTarget, out var target))
            return hops;

        using var ping = new Ping();
        var buffer = new byte[32];
        var silentStreak = 0;

        for (var ttl = 1; ttl <= MaxHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, $"Hop {ttl}/{MaxHops}", hops);

            var probes = new List<ProbeResult>(ProbesPerHop);
            for (var probe = 0; probe < ProbesPerHop; probe++)
            {
                try
                {
                    var options = new PingOptions(ttl, dontFragment: false);
                    var clock = Stopwatch.StartNew();
                    var reply = await ping.SendPingAsync(
                        target,
                        TimeSpan.FromMilliseconds(HopTimeoutMs),
                        buffer,
                        options,
                        cancellationToken).ConfigureAwait(false);
                    clock.Stop();

                    var responded = reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded
                        && reply.Address != null;
                    // RoundtripTime is often 0 for TtlExpired on Windows; use wall time
                    // (TotalMilliseconds) so sub-ms hops are not lost to long truncation.
                    var rtt = IcmpTiming.ResolveRespondedRttMs(
                        responded,
                        reply.RoundtripTime,
                        clock.Elapsed.TotalMilliseconds);
                    probes.Add(new ProbeResult(reply.Status, responded ? reply.Address!.ToString() : null, rtt));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    probes.Add(new ProbeResult(IPStatus.Unknown, null, null));
                }
            }

            var hop = MergeProbes(ttl, probes);
            hops.Add(hop);
            Report(progress, hop.ReachedTarget ? "Reached target" : $"Hop {ttl}/{MaxHops}", hops);
            if (hop.ReachedTarget)
                break;

            if (hop.Responded)
                silentStreak = 0;
            else
            {
                silentStreak++;
                if (silentStreak >= MaxConsecutiveSilentHops)
                {
                    Report(progress, "Stopped after consecutive silent hops", hops);
                    break;
                }
            }
        }

        var dnsIndexes = new List<int>();
        for (var i = 0; i < hops.Count; i++)
        {
            if (hops[i].Responded)
                dnsIndexes.Add(i);
        }

        if (dnsIndexes.Count > 0)
        {
            Report(progress, $"DNS {dnsIndexes.Count} hops", hops);
            var tasks = new Task[dnsIndexes.Count];
            for (var t = 0; t < dnsIndexes.Count; t++)
            {
                var index = dnsIndexes[t];
                tasks[t] = ResolveHopDnsAsync(hops, index, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            Report(progress, "DNS done", hops);
        }

        return hops;
    }

    private static async Task ResolveHopDnsAsync(List<HopObservation> hops, int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hostname = await ReverseDnsAsync(hops[index].Ip).ConfigureAwait(false);
        hops[index] = hops[index] with { Hostname = hostname };
    }

    private static async Task<string> ReverseDnsAsync(string ip)
    {
        try
        {
            using var timeout = new CancellationTokenSource(DnsTimeoutMs);
            var entry = await Dns.GetHostEntryAsync(ip, timeout.Token).ConfigureAwait(false);
            return entry.HostName ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<PingStats> PingSeriesAsync(
        IPAddress address,
        int count,
        int timeoutMs,
        Action<int> onEach,
        CancellationToken cancellationToken,
        int giveUpAfter = 0,
        PingOptions? options = null,
        Func<IPStatus, IPAddress?, bool>? accept = null)
    {
        accept ??= (status, _) => status == IPStatus.Success;
        var sent = 0;
        var ok = 0;
        long sum = 0;
        long min = long.MaxValue;
        long max = 0;
        using var ping = new Ping();
        var buffer = new byte[32];

        for (var i = 1; i <= count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IcmpTiming.ShouldGiveUp(sent, ok, giveUpAfter))
                break;
            onEach(i);
            sent++;
            try
            {
                var clock = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(
                    address,
                    TimeSpan.FromMilliseconds(timeoutMs),
                    buffer,
                    options ?? new PingOptions(),
                    cancellationToken).ConfigureAwait(false);
                clock.Stop();

                if (!accept(reply.Status, reply.Address))
                    continue;

                var rtt = IcmpTiming.ResolveRttMs(reply.RoundtripTime, clock.Elapsed.TotalMilliseconds);
                ok++;
                sum += rtt;
                if (rtt < min)
                    min = rtt;
                if (rtt > max)
                    max = rtt;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One lost reply is loss, not a failed test.
            }
        }

        if (sent == 0)
            return new PingStats("", "", "", "");

        var loss = ((sent - ok) * 100.0 / sent).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        if (ok == 0)
            return new PingStats("", "", "", loss);

        var avg = Math.Round(sum / (double)ok).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PingStats(
            min.ToString(System.Globalization.CultureInfo.InvariantCulture),
            avg,
            max.ToString(System.Globalization.CultureInfo.InvariantCulture),
            loss);
    }

    /// <summary>
    /// One hop from several probes at the same TTL. It responded if any probe answered; the IP is
    /// the first answering one, the RTT the lowest from that IP. Success from any probe reaches the target.
    /// </summary>
    public static HopObservation MergeProbes(int ttl, IReadOnlyList<ProbeResult> probes)
    {
        string? ip = null;
        long? rtt = null;
        foreach (var probe in probes)
        {
            if (probe.Ip == null)
                continue;
            ip ??= probe.Ip;
            if (probe.Ip == ip && probe.RttMs is long value && (rtt == null || value < rtt))
                rtt = value;
        }

        var reached = probes.Any(probe => probe.Status == IPStatus.Success);
        return ip == null
            ? new HopObservation(ttl, "*", "", null, false, reached)
            : new HopObservation(ttl, ip, "", rtt, true, reached);
    }

    /// <summary>
    /// The last responding hop short of the target. Null when only the target answered,
    /// so the last-hop fields stay blank.
    /// </summary>
    public static HopObservation? LastHop(IReadOnlyList<HopObservation> hops)
    {
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            if (hops[i].Responded && !hops[i].ReachedTarget && IPAddress.TryParse(hops[i].Ip, out _))
                return hops[i];
        }

        return null;
    }

    /// <summary>A TTL-limited echo counts only when the expected hop sent Time Exceeded. Anything else is loss.</summary>
    public static bool IsHopReply(IPStatus status, IPAddress? from, IPAddress hop) =>
        status is IPStatus.TtlExpired or IPStatus.TimeExceeded
        && from != null
        && from.Equals(hop);

    private static void Report(IProgress<ProbeProgress>? progress, string phase, IReadOnlyList<HopObservation>? hops)
    {
        try
        {
            // Snapshot under the caller so Progress handlers never see a live mutating list.
            IReadOnlyList<HopObservation>? copy = hops == null ? null : hops.ToArray();
            progress?.Report(new ProbeProgress { Phase = phase, Hops = copy });
        }
        catch
        {
            // Progress is for the window. It must not fail the test.
        }
    }

    private readonly record struct PingStats(string Min, string Avg, string Max, string LossPct);

    /// <summary>One traceroute probe. <see cref="Ip"/> is null when nothing answered.</summary>
    public readonly record struct ProbeResult(IPStatus Status, string? Ip, long? RttMs);
}
