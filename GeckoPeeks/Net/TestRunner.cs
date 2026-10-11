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
    public const string RiotPingTarget = "104.160.141.51";
    public const int MaxHops = 25;
    public const int HopTimeoutMs = 1000;
    public const int DnsTimeoutMs = 800;
    public const int PingCount = 10;

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

        var features = RouteClassifier.Classify(hops);
        row.Upstream = features.Upstream;
        row.HandoffNetwork = features.HandoffNetwork;
        row.HandoffHost = features.HandoffHost;
        row.HandoffIP = features.HandoffIP;
        row.HandoffCity = features.HandoffCity;
        row.HandoffRttMs = features.HandoffRttMs;

        var last = LastResponding(hops);
        if (last != null && IPAddress.TryParse(last.Ip, out var lastAddress))
        {
            row.LastHopIP = last.Ip;
            row.LastHopHost = last.Hostname;
            var stats = await PingSeriesAsync(
                lastAddress,
                PingCount,
                HopTimeoutMs,
                index => Report(progress, $"Ping last hop {index}/{PingCount}", hops),
                cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
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

        for (var ttl = 1; ttl <= MaxHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, $"Hop {ttl}/{MaxHops}", hops);

            HopObservation hop;
            try
            {
                var options = new PingOptions(ttl, dontFragment: false);
                var reply = await ping.SendPingAsync(
                    target,
                    TimeSpan.FromMilliseconds(HopTimeoutMs),
                    buffer,
                    options,
                    cancellationToken).ConfigureAwait(false);

                var responded = reply.Status is IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimeExceeded
                    && reply.Address != null;
                var ip = responded ? reply.Address!.ToString() : "*";
                long? rtt = responded ? reply.RoundtripTime : null;
                hop = new HopObservation(ttl, ip, "", rtt, responded, reply.Status == IPStatus.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                hop = new HopObservation(ttl, "*", "", null, false, false);
            }

            hops.Add(hop);
            Report(progress, hop.ReachedTarget ? "Reached target" : $"Hop {ttl}/{MaxHops}", hops);
            if (hop.ReachedTarget)
                break;
        }

        for (var i = 0; i < hops.Count; i++)
        {
            if (!hops[i].Responded)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, $"DNS hop {hops[i].Number}/{MaxHops}", hops);
            var hostname = await ReverseDnsAsync(hops[i].Ip).ConfigureAwait(false);
            hops[i] = hops[i] with { Hostname = hostname };
            Report(progress, $"DNS hop {hops[i].Number}/{MaxHops}", hops);
        }

        return hops;
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
        CancellationToken cancellationToken)
    {
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
            onEach(i);
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(
                    address,
                    TimeSpan.FromMilliseconds(timeoutMs),
                    buffer,
                    new PingOptions(),
                    cancellationToken).ConfigureAwait(false);

                if (reply.Status != IPStatus.Success)
                    continue;

                ok++;
                sum += reply.RoundtripTime;
                if (reply.RoundtripTime < min)
                    min = reply.RoundtripTime;
                if (reply.RoundtripTime > max)
                    max = reply.RoundtripTime;
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

    private static HopObservation? LastResponding(IReadOnlyList<HopObservation> hops)
    {
        for (var i = hops.Count - 1; i >= 0; i--)
        {
            if (hops[i].Responded && IPAddress.TryParse(hops[i].Ip, out _))
                return hops[i];
        }

        return null;
    }

    private static void Report(IProgress<ProbeProgress>? progress, string phase, IReadOnlyList<HopObservation>? hops)
    {
        try
        {
            progress?.Report(new ProbeProgress { Phase = phase, Hops = hops });
        }
        catch
        {
            // Progress is for the window. It must not fail the test.
        }
    }

    private readonly record struct PingStats(string Min, string Avg, string Max, string LossPct);
}
