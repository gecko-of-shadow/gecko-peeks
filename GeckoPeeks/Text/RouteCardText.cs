using System.Globalization;
using GeckoPeeks.Logging;

namespace GeckoPeeks.Text;

/// <summary>
/// Words for the route card and the short recent-tests grid. Last hop and Riot ICMP
/// stay in separate figures so one number is never shown as the other.
/// </summary>
public static class RouteCardText
{
    public static string RouteLine(string? upstream, string? handoffNetwork, string? city)
    {
        var provider = string.IsNullOrWhiteSpace(upstream) ? "Unknown" : upstream.Trim();
        var network = string.IsNullOrWhiteSpace(handoffNetwork) ? "" : handoffNetwork.Trim();
        var place = string.IsNullOrWhiteSpace(city) ? "" : city.Trim();
        if (network.Length == 0 && place.Length == 0)
            return provider;

        var far = network;
        if (place.Length > 0)
            far = far.Length == 0 ? place : far + " " + place;
        return provider + " → " + far;
    }

    public static string LastHopFigure(string? avg, string? lossPct)
    {
        if (LatencyLabels.TryParseMs(avg, out var ms))
            return Ms(ms);
        if (LatencyLabels.TryParseMs(lossPct, out var loss) && loss >= 100)
            return LatencyLabels.NoReply;
        return "\u2014";
    }

    public static string RiotFigure(string? avg, string? lossPct)
    {
        if (LatencyLabels.RiotNoReply(avg, lossPct))
            return LatencyLabels.NoReply;
        if (LatencyLabels.TryParseMs(avg, out var ms))
            return Ms(ms);
        return "\u2014";
    }

    /// <summary>Live League flag, the interface from the last test, and how long ago that test was.</summary>
    public static string Context(bool leagueRunning, string? adapter, string? wifi, string? gateway, string? vpn, string? ago)
    {
        var parts = new List<string> { leagueRunning ? "League running" : "League not running" };
        AppendNetwork(parts, adapter, wifi, gateway, vpn);
        if (!string.IsNullOrWhiteSpace(ago))
            parts.Add(ago.Trim());
        return string.Join(" · ", parts);
    }

    public static string NetworkLine(string? adapter, string? wifi, string? gateway, string? vpn)
    {
        var parts = new List<string>();
        AppendNetwork(parts, adapter, wifi, gateway, vpn);
        return string.Join(" · ", parts);
    }

    public static string RecentNetwork(string? adapter, string? wifi, string? vpn)
    {
        var name = "";
        if (IsVpn(vpn) && !string.IsNullOrWhiteSpace(adapter))
            name = adapter.Trim();
        else if (!string.IsNullOrWhiteSpace(adapter))
            name = adapter.Trim();

        if (!string.IsNullOrWhiteSpace(wifi))
            name = name.Length == 0 ? wifi.Trim() : name + " · " + wifi.Trim();
        return name.Length == 0 ? "\u2014" : name;
    }

    public static string LeagueCell(string? leagueRunning)
    {
        if (string.Equals(leagueRunning, "Y", StringComparison.OrdinalIgnoreCase))
            return "Yes";
        if (string.Equals(leagueRunning, "N", StringComparison.OrdinalIgnoreCase))
            return "No";
        return "\u2014";
    }

    private static void AppendNetwork(List<string> parts, string? adapter, string? wifi, string? gateway, string? vpn)
    {
        if (IsVpn(vpn) && !string.IsNullOrWhiteSpace(adapter))
            parts.Add("VPN " + adapter.Trim());
        else if (!string.IsNullOrWhiteSpace(adapter))
            parts.Add(adapter.Trim());

        if (!string.IsNullOrWhiteSpace(wifi))
            parts.Add(wifi.Trim());
        if (!string.IsNullOrWhiteSpace(gateway))
            parts.Add("gateway " + gateway.Trim());
    }

    private static bool IsVpn(string? vpn) =>
        string.Equals(vpn?.Trim(), "Y", StringComparison.OrdinalIgnoreCase);

    private static string Ms(double value) =>
        Math.Round(value).ToString(CultureInfo.InvariantCulture) + " ms";
}
