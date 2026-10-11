namespace GeckoPeeks.Logging;

/// <summary>
/// Aggregates CSV rows for the upstream summary grid. Last hop ms averages LastHopAvg only.
/// Riot ICMP averages RiotHopAvg only and reads "no reply" when every Riot ping was lost.
/// Sources are never mixed, so a last-hop number is never shown as Riot latency.
/// </summary>
public static class UpstreamSummary
{
    public sealed record Row(string Upstream, int Count, string LastHopMs, string RiotMs);

    public static IReadOnlyList<Row> Build(IReadOnlyList<RouteRow> rows)
    {
        if (rows.Count == 0)
            return Array.Empty<Row>();

        var groups = new Dictionary<string, GroupBucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = string.IsNullOrWhiteSpace(row.Upstream) ? "Unknown" : row.Upstream.Trim();
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new GroupBucket();
                groups[key] = bucket;
            }

            bucket.Count++;
            if (LatencyLabels.TryParseMs(row.LastHopAvg, out var lastHop))
                bucket.LastHop.Add(lastHop);
            if (LatencyLabels.TryParseMs(row.RiotHopAvg, out var riot))
                bucket.Riot.Add(riot);
            else if (LatencyLabels.RiotNoReply(row.RiotHopAvg, row.RiotHopLossPct))
                bucket.RiotNoReply++;
        }

        var list = new List<Row>();
        foreach (var pair in groups.OrderByDescending(pair => pair.Value.Count))
        {
            var bucket = pair.Value;
            var riotText = bucket.Riot.Count > 0
                ? Average(bucket.Riot)
                : bucket.RiotNoReply > 0 ? LatencyLabels.NoReply : "\u2014";
            list.Add(new Row(pair.Key, bucket.Count, Average(bucket.LastHop), riotText));
        }

        return list;
    }

    public static string SharePercent(int count, int total)
    {
        if (total <= 0)
            return "0%";
        return (100.0 * count / total).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    private static string Average(List<double> samples) =>
        samples.Count == 0
            ? "\u2014"
            : Math.Round(samples.Average()).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private sealed class GroupBucket
    {
        public int Count;
        public int RiotNoReply;
        public List<double> LastHop { get; } = new();
        public List<double> Riot { get; } = new();
    }
}
