using GeckoPeeks.Logging;

namespace GeckoPeeks.Text;

public static class TooltipText
{
    public static string Format(
        string? upstream,
        string? handoffNetwork,
        string? handoffCity,
        string? riotHopAvg,
        string? lastHopAvg,
        string? handoffRttMs,
        DateTime? timestampLocal,
        DateTime now)
    {
        if (timestampLocal == null
            && string.IsNullOrWhiteSpace(upstream)
            && string.IsNullOrWhiteSpace(handoffNetwork))
        {
            return "Gecko Peeks — no route yet";
        }

        var provider = string.IsNullOrWhiteSpace(upstream) ? "Unknown" : upstream.Trim();
        var network = string.IsNullOrWhiteSpace(handoffNetwork) ? "Unknown" : handoffNetwork.Trim();
        var city = string.IsNullOrWhiteSpace(handoffCity) ? "" : " " + handoffCity.Trim();
        var latency = LatencyLabels.Tooltip(riotHopAvg, lastHopAvg, handoffRttMs);
        var when = timestampLocal == null ? "just now" : Ago(timestampLocal.Value, now);
        var text = $"{provider} → {network}{city} | {latency} | {when}";
        return text.Length <= 127 ? text : text[..127];
    }

    public static string Ago(DateTime timestampLocal, DateTime now)
    {
        var delta = now - timestampLocal;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 45)
            return "just now";

        if (delta.TotalMinutes < 90)
        {
            var minutes = Math.Max(1, (int)Math.Round(delta.TotalMinutes));
            return minutes == 1 ? "1 min ago" : $"{minutes} min ago";
        }

        if (delta.TotalHours < 36)
        {
            var hours = Math.Max(1, (int)Math.Round(delta.TotalHours));
            return hours == 1 ? "1 hr ago" : $"{hours} hr ago";
        }

        var days = Math.Max(1, (int)Math.Round(delta.TotalDays));
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }
}
