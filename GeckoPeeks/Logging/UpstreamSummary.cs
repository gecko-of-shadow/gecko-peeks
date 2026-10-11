namespace GeckoPeeks.Logging;

/// <summary>
/// Aggregates CSV rows for the upstream summary grid.
/// Prefers RiotHopAvg, then LastHopAvg, then HandoffRttMs. Never shows
/// "\u2014" when any of those fields parse as a number on the rows.
/// </summary>
public static class UpstreamSummary
{
    public sealed record Row(string Upstream, int Count, string AvgMs);

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
            if (TryAverageMs(row, out var avg))
                bucket.Avgs.Add(avg);
        }

        var list = new List<Row>();
        foreach (var pair in groups.OrderByDescending(pair => pair.Value.Count))
        {
            var samples = pair.Value.Avgs;
            var avgText = samples.Count == 0
                ? "\u2014"
                : Math.Round(samples.Average()).ToString(System.Globalization.CultureInfo.InvariantCulture);
            list.Add(new Row(pair.Key, pair.Value.Count, avgText));
        }

        return list;
    }

    public static string SharePercent(int count, int total)
    {
        if (total <= 0)
            return "0%";
        return (100.0 * count / total).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// RiotHopAvg, else LastHopAvg, else HandoffRttMs.
    /// </summary>
    public static bool TryAverageMs(RouteRow row, out double avg)
    {
        if (TryParseMs(row.RiotHopAvg, out avg))
            return true;
        if (TryParseMs(row.LastHopAvg, out avg))
            return true;
        if (TryParseMs(row.HandoffRttMs, out avg))
            return true;
        avg = 0;
        return false;
    }

    private static bool TryParseMs(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2].Trim();
        if (trimmed.Length == 0)
            return false;

        // Em dash / placeholders are not averages.
        if (trimmed == "\u2014" || trimmed == "-" || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            return false;

        return double.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    private sealed class GroupBucket
    {
        public int Count;
        public List<double> Avgs { get; } = new();
    }
}
