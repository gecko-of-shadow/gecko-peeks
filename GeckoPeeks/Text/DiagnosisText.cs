using GeckoPeeks.Logging;

namespace GeckoPeeks.Text;

public readonly record struct DiagnosisHop(int Number, string Ip, string Hostname);

public sealed record DiagnosisInput(
    string Version,
    bool HasRoute,
    string Timestamp,
    string Upstream,
    string HandoffNetwork,
    string HandoffCity,
    string LastHopAvg,
    string LastHopLossPct,
    string RiotHopAvg,
    string RiotHopLossPct,
    string LeagueRunning,
    string Adapter,
    string Wifi,
    string Gateway,
    string Vpn,
    string ChangeLine,
    IReadOnlyList<DiagnosisHop> Hops);

/// <summary>
/// Plain text for the clipboard. Last hop and Riot ICMP are always named, and a Riot
/// "no reply" is never filled from the last-hop number.
/// </summary>
public static class DiagnosisText
{
    public const string NoRoute = "Gecko Peeks — no route yet";

    public static string Format(DiagnosisInput input)
    {
        if (!input.HasRoute)
            return NoRoute;

        var lines = new List<string>
        {
            "Gecko Peeks " + (string.IsNullOrWhiteSpace(input.Version) ? "?" : input.Version.Trim()),
            input.Timestamp.Trim(),
            RouteCardText.RouteLine(input.Upstream, input.HandoffNetwork, input.HandoffCity),
            LastHopLine(input.LastHopAvg, input.LastHopLossPct),
            RiotLine(input.RiotHopAvg, input.RiotHopLossPct),
            LeagueLine(input.LeagueRunning),
        };

        var network = RouteCardText.NetworkLine(input.Adapter, input.Wifi, input.Gateway, input.Vpn);
        if (network.Length > 0)
            lines.Add(network);
        if (!string.IsNullOrWhiteSpace(input.ChangeLine))
            lines.Add(input.ChangeLine.Trim());

        foreach (var hop in input.Hops)
            lines.Add(HopLine(hop));

        return string.Join("\r\n", lines);
    }

    public static string LastHopLine(string? avg, string? lossPct)
    {
        var figure = RouteCardText.LastHopFigure(avg, lossPct);
        if (figure == "\u2014")
            return "Last hop \u2014";
        if (figure == LatencyLabels.NoReply)
            return "Last hop no reply";
        return WithLoss("Last hop " + figure, lossPct);
    }

    public static string RiotLine(string? avg, string? lossPct)
    {
        var figure = RouteCardText.RiotFigure(avg, lossPct);
        if (figure == "\u2014")
            return "Riot ICMP \u2014";
        if (figure == LatencyLabels.NoReply)
            return "Riot ICMP no reply";
        return WithLoss("Riot ICMP " + figure, lossPct);
    }

    private static string WithLoss(string line, string? lossPct)
    {
        if (!LatencyLabels.TryParseMs(lossPct, out var loss))
            return line;
        var text = loss.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return line + " (" + text + "% loss)";
    }

    private static string LeagueLine(string? leagueRunning) =>
        string.Equals(leagueRunning, "Y", StringComparison.OrdinalIgnoreCase)
            ? "League running"
            : "League not running";

    private static string HopLine(DiagnosisHop hop)
    {
        var ip = string.IsNullOrWhiteSpace(hop.Ip) ? "*" : hop.Ip.Trim();
        var line = hop.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "  " + ip;
        if (!string.IsNullOrWhiteSpace(hop.Hostname))
            line += "  " + hop.Hostname.Trim();
        return line;
    }
}
