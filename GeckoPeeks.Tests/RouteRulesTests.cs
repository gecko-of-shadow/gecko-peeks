using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class CityCodeTests
{
    [Theory]
    [InlineData("ae-1.sea1.zayo.com", "sea1")]
    [InlineData("den3.example.net", "den3")]
    [InlineData("core-chi.telia.net", "chi")]
    [InlineData("bil1.wolfpaw.net", "bil1")]
    [InlineData("yyc2.telus.net", "yyc2")]
    [InlineData("sea1.den3.example.net", "sea1")]
    [InlineData("net.com.org", "")]
    [InlineData("rtr-edge.example.net", "")]
    [InlineData("www.example.com", "")]
    [InlineData("to-of.example.net", "")]
    [InlineData(null, "")]
    [InlineData("ae5.cr1.sea1.us.zayo.com", "sea1")]
    [InlineData("be3012.ccr31.sea02.atlas.cogentco.com", "sea02")]
    [InlineData("100ge0-45.core1.sea1.he.net", "sea1")]
    [InlineData("ae5.ccr31.core1.example.net", "")]
    public void Extracts_the_first_city_token(string? hostname, string expected) =>
        Assert.Equal(expected, CityCode.Extract(hostname));
}

public class RouteClassifierTests
{
    [Fact]
    public void Last_upstream_wins_and_telia_is_the_handoff()
    {
        var hops = new[]
        {
            Hop(1, "10.1.1.1", "ge-0.core.telus.net", 8),
            Hop(2, "1.2.3.4", "ae1.den3.zayo.com", 21),
            Hop(3, "4.5.6.7", "ic-1.sea1.twelve99.net", 40),
            Hop(4, "192.207.0.5", "", 55, reached: true),
        };

        var features = RouteClassifier.Classify(hops);

        Assert.Equal("Zayo", features.Upstream);
        Assert.Equal("Telia", features.HandoffNetwork);
        Assert.Equal("ic-1.sea1.twelve99.net", features.HandoffHost);
        Assert.Equal("4.5.6.7", features.HandoffIP);
        Assert.Equal("sea1", features.HandoffCity);
        Assert.Equal("40", features.HandoffRttMs);
    }

    [Fact]
    public void Mixed_zayo_telia_name_is_the_handoff_not_another_upstream()
    {
        var hops = new[]
        {
            Hop(1, "1.1.1.1", "ae1.zayo.com", 10),
            Hop(2, "2.2.2.2", "peer.sea1.zayo-telia.zayo.com", 22),
            Hop(3, "192.207.0.5", "", 30, reached: true),
        };

        var features = RouteClassifier.Classify(hops);

        Assert.Equal("Zayo", features.Upstream);
        Assert.Equal("Telia", features.HandoffNetwork);
        Assert.Equal("2.2.2.2", features.HandoffIP);
        Assert.Equal("sea1", features.HandoffCity);
        Assert.Null(RouteClassifier.ClassifyHop(hops[1].Hostname).PureUpstream);
    }

    [Fact]
    public void Unknown_when_no_provider_matches()
    {
        var features = RouteClassifier.Classify(new[] { Hop(1, "8.8.8.8", "dns.google", 5) });
        Assert.Equal("Unknown", features.Upstream);
        Assert.Equal("", features.HandoffNetwork);
    }

    [Fact]
    public void Substring_false_positives_are_rejected()
    {
        Assert.Empty(RouteClassifier.ClassifyHop("cdn.the.net").Networks);
        Assert.Empty(RouteClassifier.ClassifyHop("img.cache.net").Networks);
        Assert.Empty(RouteClassifier.ClassifyHop("www.apache.net").Networks);
        Assert.Empty(RouteClassifier.ClassifyHop("notelia.example.com").Networks);
        Assert.Empty(RouteClassifier.ClassifyHop("hotelus.example.com").Networks);

        Assert.Contains("HE", RouteClassifier.ClassifyHop("core1.sea1.he.net").Networks);
        Assert.Contains("TELUS", RouteClassifier.ClassifyHop("yyc2.telus.net").Networks);
        Assert.Contains("Telia", RouteClassifier.ClassifyHop("sea-b1-link.ip.twelve99.net").Networks);
        Assert.Contains("Telia", RouteClassifier.ClassifyHop("peer.zayo-telia.zayo.com").Networks);
    }

    [Fact]
    public void Full_path_keeps_stars_for_silent_hops()
    {
        var hops = new[]
        {
            new HopObservation(1, "*", "", null, false, false),
            Hop(2, "1.2.3.4", "ae1.zayo.com", 12),
            new HopObservation(3, "*", "", null, false, false),
            Hop(4, "192.207.0.5", "", 40, reached: true),
        };

        Assert.Equal(" * > 1.2.3.4 > * > 192.207.0.5".Trim(), RouteClassifier.BuildFullPath(hops));
        Assert.Equal("* > 1.2.3.4 > * > 192.207.0.5", RouteClassifier.BuildFullPath(hops));
    }

    private static HopObservation Hop(int number, string ip, string host, long rtt, bool reached = false) =>
        new(number, ip, host, rtt, true, reached);
}

public class TooltipAndBalloonTests
{
    [Fact]
    public void Tooltip_labels_riot_average_and_relative_time()
    {
        var now = new DateTime(2026, 10, 11, 12, 2, 0);
        var then = now.AddMinutes(-2);
        var text = TooltipText.Format("Zayo", "Telia", "sea1", "61", "21", "40", then, now);
        Assert.Equal("Zayo → Telia sea1 | Riot 61 ms | 2 min ago", text);
    }

    [Fact]
    public void Tooltip_falls_back_to_labeled_last_hop_then_handoff()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 30);
        Assert.Equal(
            "Zayo → Telia sea1 | last hop 21 ms | just now",
            TooltipText.Format("Zayo", "Telia", "sea1", "", "21", "40", now, now));
        Assert.Equal(
            "Zayo → Telia sea1 | handoff 40 ms | just now",
            TooltipText.Format("Zayo", "Telia", "sea1", "", "", "40", now, now));
        Assert.Equal(
            "Zayo → Telia sea1 | n/a | just now",
            TooltipText.Format("Zayo", "Telia", "sea1", "", "", "", now, now));
    }

    [Fact]
    public void Balloon_uses_city_names_and_skips_the_first_test()
    {
        Assert.Null(RouteChange.Describe(false, "", "", "", "Zayo", "sea1", "Telia"));

        var changed = RouteChange.Describe(true, "Zayo", "den3", "Telia", "Zayo", "sea1", "Telia");
        Assert.Equal("Route changed: Zayo/Denver → Zayo/Seattle", changed);

        Assert.Null(RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Zayo", "sea1", "Cogent"));
    }

    [Fact]
    public void Balloon_ignores_unknown_or_blank_probe_flaps()
    {
        Assert.Null(RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Unknown", "", ""));
        Assert.Null(RouteChange.Describe(true, "Zayo", "sea1", "Telia", "", "", ""));
        Assert.Null(RouteChange.Describe(true, "Unknown", "", "", "Zayo", "sea1", "Telia"));
        Assert.Null(RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Unknown", "Unknown", ""));

        var real = RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Cogent", "den3", "Telia");
        Assert.Equal("Route changed: Zayo/Seattle → Cogent/Denver", real);
    }
}
