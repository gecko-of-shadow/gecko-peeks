namespace GeckoPeeks.Routing;

/// <summary>
/// Upstream is the provider of the last hop that matches one of the five
/// patterns and does not also name a different network.
/// A hostname like "zayo-telia..." names both, so it is the handoff
/// (HandoffNetwork = the other network, usually Telia), not another upstream hop.
/// The handoff is the first hop after that upstream hop whose hostname belongs
/// to a different recognized network (Telia, or another of the five providers).
/// </summary>
public static class RouteClassifier
{
    public const string Unknown = "Unknown";

    private static readonly (string Pattern, string Name)[] UpstreamPatterns =
    {
        ("zayo.com", "Zayo"),
        ("cogentco.com", "Cogent"),
        ("telus", "TELUS"),
        ("wolfpaw", "Wolfpaw"),
        ("he.net", "HE"),
    };

    public sealed record Features(
        string Upstream,
        string HandoffNetwork,
        string HandoffHost,
        string HandoffIP,
        string HandoffCity,
        string HandoffRttMs);

    public sealed record HopClass(IReadOnlyList<string> Networks, string? PureUpstream);

    public static Features Classify(IReadOnlyList<HopObservation> hops)
    {
        var lastPureIndex = -1;
        var upstream = Unknown;

        for (var i = 0; i < hops.Count; i++)
        {
            if (!hops[i].Responded)
                continue;

            var hop = ClassifyHop(hops[i].Hostname);
            if (hop.PureUpstream == null)
                continue;

            lastPureIndex = i;
            upstream = hop.PureUpstream;
        }

        if (lastPureIndex < 0)
            return new Features(Unknown, "", "", "", "", "");

        for (var i = lastPureIndex + 1; i < hops.Count; i++)
        {
            if (!hops[i].Responded || string.IsNullOrWhiteSpace(hops[i].Hostname))
                continue;

            var hop = ClassifyHop(hops[i].Hostname);
            var others = new List<string>();
            foreach (var name in hop.Networks)
            {
                if (!string.Equals(name, upstream, StringComparison.Ordinal) && !others.Contains(name))
                    others.Add(name);
            }

            if (others.Count == 0)
                continue;

            // Telia is the usual far-side network in a mixed name like zayo-telia.
            var handoffNetwork = others.Contains("Telia") ? "Telia" : others[0];
            var rtt = hops[i].RttMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return new Features(
                upstream,
                handoffNetwork,
                hops[i].Hostname,
                hops[i].Ip,
                CityCode.Extract(hops[i].Hostname),
                rtt);
        }

        return new Features(upstream, "", "", "", "", "");
    }

    public static HopClass ClassifyHop(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return new HopClass(Array.Empty<string>(), null);

        var host = hostname.ToLowerInvariant();
        var networks = new List<string>();

        void Add(string name)
        {
            if (!networks.Contains(name))
                networks.Add(name);
        }

        string? official = null;
        foreach (var (pattern, name) in UpstreamPatterns)
        {
            if (!host.Contains(pattern, StringComparison.Ordinal))
                continue;
            Add(name);
            official ??= name;
        }

        // Stems catch a handoff hostname that names a provider without the
        // official domain, e.g. "zayo-telia" (no "zayo.com") plus Telia.
        if (host.Contains("zayo", StringComparison.Ordinal))
            Add("Zayo");
        if (host.Contains("cogent", StringComparison.Ordinal))
            Add("Cogent");
        if (host.Contains("telus", StringComparison.Ordinal))
            Add("TELUS");
        if (host.Contains("wolfpaw", StringComparison.Ordinal))
            Add("Wolfpaw");
        if (host.Contains("twelve99.net", StringComparison.Ordinal) || host.Contains("telia", StringComparison.Ordinal))
            Add("Telia");

        string? pure = null;
        if (official != null)
        {
            var mixed = false;
            foreach (var name in networks)
            {
                if (!string.Equals(name, official, StringComparison.Ordinal))
                {
                    mixed = true;
                    break;
                }
            }

            if (!mixed)
                pure = official;
        }

        return new HopClass(networks, pure);
    }

    public static string DescribeHop(string? hostname)
    {
        var hop = ClassifyHop(hostname);
        if (hop.Networks.Count == 0)
            return "";
        return string.Join(" + ", hop.Networks);
    }

    public static string BuildFullPath(IReadOnlyList<HopObservation> hops)
    {
        if (hops.Count == 0)
            return "";

        var parts = new string[hops.Count];
        for (var i = 0; i < hops.Count; i++)
        {
            var hop = hops[i];
            parts[i] = hop.Responded && !string.IsNullOrWhiteSpace(hop.Ip) && hop.Ip != "*"
                ? hop.Ip
                : "*";
        }

        return string.Join(" > ", parts);
    }

    public static List<HopObservation> HopsFromFullPath(string? fullPath)
    {
        var list = new List<HopObservation>();
        if (string.IsNullOrWhiteSpace(fullPath))
            return list;

        var parts = fullPath.Split(" > ", StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            var ip = parts[i].Trim();
            var responded = ip.Length > 0 && ip != "*";
            list.Add(new HopObservation(i + 1, responded ? ip : "*", "", null, responded, false));
        }

        return list;
    }
}
