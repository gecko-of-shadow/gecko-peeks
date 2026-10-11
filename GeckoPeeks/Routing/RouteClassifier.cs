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

    private static readonly (string Domain, string Name)[] OfficialDomains =
    {
        ("zayo.com", "Zayo"),
        ("cogentco.com", "Cogent"),
        ("telus.net", "TELUS"),
        ("telus.com", "TELUS"),
        ("wolfpaw.net", "Wolfpaw"),
        ("he.net", "HE"),
        ("twelve99.net", "Telia"),
        ("telia.net", "Telia"),
        ("telia.com", "Telia"),
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
        foreach (var (domain, name) in OfficialDomains)
        {
            if (!HostMatchesDomain(host, domain))
                continue;
            Add(name);
            // Pure upstream is limited to the five ISP patterns, not Telia.
            if (official == null && name != "Telia")
                official = name;
        }

        // Stem matches for mixed handoff names (zayo-telia) and TELUS/Wolfpaw
        // hostnames that omit the full corporate domain. Anchored to label boundaries.
        if (LabelContains(host, "zayo"))
            Add("Zayo");
        if (LabelContains(host, "cogent"))
            Add("Cogent");
        if (LabelContains(host, "telus"))
            Add("TELUS");
        if (LabelContains(host, "wolfpaw"))
            Add("Wolfpaw");
        if (LabelContains(host, "telia") || LabelContains(host, "twelve99"))
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

    /// <summary>
    /// True when host equals domain or ends with ".domain".
    /// </summary>
    public static bool HostMatchesDomain(string host, string domain)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(domain))
            return false;

        if (host.Equals(domain, StringComparison.Ordinal))
            return true;

        return host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when a '.' / '-' separated label equals stem or starts with stem
    /// followed by a non-letter (for example zayo-telia, telus2).
    /// </summary>
    public static bool LabelContains(string host, string stem)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(stem))
            return false;

        var index = 0;
        while (index < host.Length)
        {
            var nextDot = host.IndexOf('.', index);
            var nextDash = host.IndexOf('-', index);
            var end = host.Length;
            if (nextDot >= 0)
                end = Math.Min(end, nextDot);
            if (nextDash >= 0)
                end = Math.Min(end, nextDash);

            var length = end - index;
            if (length >= stem.Length &&
                string.Compare(host, index, stem, 0, stem.Length, StringComparison.Ordinal) == 0 &&
                (length == stem.Length || !char.IsLetter(host[index + stem.Length])))
            {
                return true;
            }

            index = end + 1;
        }

        return false;
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
