namespace GeckoPeeks.Logging;

/// <summary>
/// Aggregates CSV rows for the upstream summary grid.
/// Prefers RiotHopAvg; falls back to LastHopAvg when Riot is blank but a
/// measured average exists so the UI does not show "\u2014" for a real sample.
/// </summary>
public static class UpstreamSummary
{
    public sealed record Row(string Upstream, int Count, string AvgMs);

    public static IReadOnlyList<Row> Build(IReadOnlyList<RouteRow> rows)
    {
        if (rows.Count == 0)
            return Array.Empty<Row>();

        var groups = new Dictionary<string, (int Count, List<double> Avgs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = string.IsNullOrWhiteSpace(row.Upstream) ? "Unknown" : row.Upstream;
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = (0, new List<double>());
                groups[key] = bucket;
            }

            bucket.Count++;
            if (TryAverageMs(row, out var avg))
                bucket.Avgs.Add(avg);
            groups[key] = bucket;
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
        return double.TryParse(
            text.Trim(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }
}
