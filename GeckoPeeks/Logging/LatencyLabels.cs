using System.Globalization;

namespace GeckoPeeks.Logging;

/// <summary>
/// Names each ICMP number by what it measured. "Last hop" is ping to the last router that
/// answered the traceroute: path latency, not in-game ping. "Riot ICMP" is ping to the fixed
/// Riot address. Riot filters echo, so a fully lost series reads "no reply" instead of
/// borrowing another hop's number.
/// </summary>
public static class LatencyLabels
{
    public const string NoReply = "no reply";

    public static bool TryParseMs(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2].Trim();
        if (trimmed.Length == 0)
            return false;

        // Em dash / placeholders are not numbers.
        if (trimmed == "\u2014" || trimmed == "-" || trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            return false;

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>True when the Riot series has no average because every ping was lost.</summary>
    public static bool RiotNoReply(string? riotAvg, string? riotLossPct) =>
        !TryParseMs(riotAvg, out _) && TryParseMs(riotLossPct, out var loss) && loss >= 100;

    /// <summary>Recent-log cell for RiotHopAvg.</summary>
    public static string RiotCell(string? riotAvg, string? riotLossPct) =>
        RiotNoReply(riotAvg, riotLossPct) ? NoReply : (riotAvg ?? "").Trim();

    /// <summary>Recent-log header for a CSV column. The CSV file keeps its column names.</summary>
    public static string ColumnHeader(string csvColumn) => csvColumn switch
    {
        "Timestamp" => "Time",
        "LeagueRunning" => "League",
        "HandoffNetwork" => "Handoff",
        "HandoffHost" => "Handoff host",
        "HandoffIP" => "Handoff IP",
        "HandoffCity" => "City",
        "HandoffRttMs" => "Handoff ms",
        "LastHopIP" => "Last hop IP",
        "LastHopHost" => "Last hop host",
        "LastHopMin" => "Last hop min ms",
        "LastHopAvg" => "Last hop ms",
        "LastHopMax" => "Last hop max ms",
        "LastHopLossPct" => "Last hop loss %",
        "RiotHopAvg" => "Riot ICMP ms",
        "RiotHopLossPct" => "Riot ICMP loss %",
        "FullPath" => "Full path",
        _ => csvColumn,
    };

    /// <summary>
    /// Tooltip latency with its source: Riot ICMP when Riot answered, else the last-hop
    /// average, else the handoff hop's traceroute RTT.
    /// </summary>
    public static string Tooltip(string? riotAvg, string? lastHopAvg, string? handoffRttMs)
    {
        if (TryParseMs(riotAvg, out var riot))
            return "Riot " + Ms(riot);
        if (TryParseMs(lastHopAvg, out var lastHop))
            return "last hop " + Ms(lastHop);
        if (TryParseMs(handoffRttMs, out var handoff))
            return "handoff " + Ms(handoff);
        return "n/a";
    }

    private static string Ms(double value) =>
        Math.Round(value).ToString(CultureInfo.InvariantCulture) + " ms";
}
