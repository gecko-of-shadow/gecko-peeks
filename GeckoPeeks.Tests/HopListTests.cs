using GeckoPeeks.Routing;
using Xunit;

namespace GeckoPeeks.Tests;

public class HopListTests
{
    private static HopObservation Hop(int number, string ip, string host = "", long? rtt = 12) =>
        new(number, ip, host, rtt, Responded: ip != "*", ReachedTarget: false);

    [Fact]
    public void Same_hops_in_a_fresh_copy_are_equal()
    {
        var first = new[] { Hop(1, "10.0.0.1"), Hop(2, "*", rtt: null), Hop(3, "192.207.0.5", "riot.example") };
        var copy = first.ToList();

        Assert.True(HopList.SameHops(first, copy));
    }

    [Fact]
    public void Empty_and_missing_lists_are_the_same_path()
    {
        Assert.True(HopList.SameHops(null, Array.Empty<HopObservation>()));
        Assert.True(HopList.SameHops(Array.Empty<HopObservation>(), null));
        Assert.True(HopList.SameHops(null, null));
    }

    [Fact]
    public void A_new_hop_changes_the_path()
    {
        var before = new[] { Hop(1, "10.0.0.1") };
        var after = new[] { Hop(1, "10.0.0.1"), Hop(2, "10.0.0.2") };

        Assert.False(HopList.SameHops(before, after));
        Assert.False(HopList.SameHops(after, before));
    }

    [Fact]
    public void A_resolved_hostname_changes_the_path()
    {
        var before = new[] { Hop(1, "10.0.0.1") };
        var after = new[] { Hop(1, "10.0.0.1", "ae5.cr1.zayo.com") };

        Assert.False(HopList.SameHops(before, after));
    }

    [Fact]
    public void A_different_rtt_changes_the_path()
    {
        var before = new[] { Hop(1, "10.0.0.1", rtt: 12) };
        var after = new[] { Hop(1, "10.0.0.1", rtt: 13) };

        Assert.False(HopList.SameHops(before, after));
    }

    [Fact]
    public void Restored_addresses_differ_from_a_measured_path()
    {
        var restored = RouteClassifier.HopsFromFullPath("10.0.0.1 > * > 192.207.0.5");
        var measured = new[] { Hop(1, "10.0.0.1"), Hop(2, "*", rtt: null), Hop(3, "192.207.0.5") };

        Assert.False(HopList.SameHops(restored, measured));
        Assert.True(HopList.SameHops(restored, RouteClassifier.HopsFromFullPath("10.0.0.1 > * > 192.207.0.5")));
    }
}
