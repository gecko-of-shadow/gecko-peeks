using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class HopDiffStarTests
{
    [Fact]
    public void Ip_to_star_and_back_is_not_a_change()
    {
        Assert.Empty(HopDiff.ChangedHopNumbers("1.1.1.1 > 2.2.2.2 > 3.3.3.3", "1.1.1.1 > * > 3.3.3.3"));
        Assert.Empty(HopDiff.ChangedHopNumbers("1.1.1.1 > * > 3.3.3.3", "1.1.1.1 > 2.2.2.2 > 3.3.3.3"));
        Assert.Equal(HopDiff.SamePath, HopDiff.Describe("1.1.1.1 > 2.2.2.2", "1.1.1.1 > *"));
    }

    [Fact]
    public void Ip_to_a_different_ip_is_still_a_change()
    {
        Assert.Equal(new[] { 3 }, HopDiff.ChangedHopNumbers("1.1.1.1 > * > 3.3.3.3", "1.1.1.1 > * > 9.9.9.9"));
    }

    [Fact]
    public void A_shorter_or_longer_path_is_a_change_even_with_stars()
    {
        Assert.Equal(new[] { 3 }, HopDiff.ChangedHopNumbers("1.1.1.1 > 2.2.2.2 > *", "1.1.1.1 > 2.2.2.2"));
        Assert.Equal(new[] { 3 }, HopDiff.ChangedHopNumbers("1.1.1.1 > 2.2.2.2", "1.1.1.1 > 2.2.2.2 > *"));
    }
}

public class RouteChangeCityTests
{
    [Fact]
    public void Codes_for_the_same_city_are_not_a_change()
    {
        Assert.Null(RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Zayo", "sea2", "Telia"));
        Assert.Null(RouteChange.Describe(true, "Zayo", "ord1", "Telia", "Zayo", "chi5", "Telia"));
    }

    [Fact]
    public void A_city_that_goes_blank_under_a_known_upstream_is_indeterminate()
    {
        Assert.Null(RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Cogent", "", "Telia"));
        Assert.True(RouteChange.IsIndeterminate("Zayo", "sea1", "Zayo", ""));
        Assert.False(RouteChange.IsIndeterminate("Zayo", "", "Zayo", ""));
    }

    [Fact]
    public void A_real_city_change_still_describes()
    {
        Assert.Equal(
            "Route changed: Zayo/Seattle → Zayo/Denver",
            RouteChange.Describe(true, "Zayo", "sea1", "Telia", "Zayo", "den3", "Telia"));
    }
}

public class RouteChangeConfirmerTests
{
    private static RouteChangeConfirmer Seeded()
    {
        var confirm = new RouteChangeConfirmer();
        confirm.Seed("Zayo", "sea1", "Telia");
        return confirm;
    }

    [Fact]
    public void First_test_without_a_seed_is_silent()
    {
        var confirm = new RouteChangeConfirmer();
        Assert.Null(confirm.Observe("Zayo", "sea1", "Telia"));
        Assert.Null(confirm.Observe("Zayo", "sea1", "Telia"));
    }

    [Fact]
    public void A_change_fires_on_the_second_agreeing_test()
    {
        var confirm = Seeded();
        Assert.Null(confirm.Observe("Cogent", "den3", "Telia"));
        Assert.Equal("Route changed: Zayo/Seattle → Cogent/Denver", confirm.Observe("Cogent", "den3", "Telia"));
        Assert.Null(confirm.Observe("Cogent", "den3", "Telia"));
    }

    [Fact]
    public void A_one_test_blip_never_fires()
    {
        var confirm = Seeded();
        Assert.Null(confirm.Observe("Cogent", "den3", "Telia"));
        Assert.Null(confirm.Observe("Zayo", "sea1", "Telia"));
        Assert.Null(confirm.Observe("Zayo", "sea2", "Telia"));
    }

    [Fact]
    public void Agreement_uses_city_names_not_codes()
    {
        var confirm = Seeded();
        Assert.Null(confirm.Observe("Cogent", "den1", "Telia"));
        Assert.NotNull(confirm.Observe("Cogent", "den3", "Telia"));
    }

    [Fact]
    public void A_different_second_change_restarts_the_count()
    {
        var confirm = Seeded();
        Assert.Null(confirm.Observe("Cogent", "den3", "Telia"));
        Assert.Null(confirm.Observe("Cogent", "lax1", "Telia"));
        Assert.Equal("Route changed: Zayo/Seattle → Cogent/Los Angeles", confirm.Observe("Cogent", "lax1", "Telia"));
    }

    [Fact]
    public void An_indeterminate_test_in_between_is_skipped()
    {
        var confirm = Seeded();
        Assert.Null(confirm.Observe("Cogent", "den3", "Telia"));
        Assert.Null(confirm.Observe("Unknown", "", ""));
        Assert.Null(confirm.Observe("Cogent", "", "Telia"));
        Assert.Equal("Route changed: Zayo/Seattle → Cogent/Denver", confirm.Observe("Cogent", "den3", "Telia"));
    }

    [Fact]
    public void Recovery_from_an_unknown_seed_is_silent()
    {
        var confirm = new RouteChangeConfirmer();
        confirm.Seed("Unknown", "", "");
        Assert.Null(confirm.Observe("Zayo", "sea1", "Telia"));
        Assert.Null(confirm.Observe("Zayo", "sea1", "Telia"));
        Assert.Null(confirm.Observe("Cogent", "den3", "Telia"));
        Assert.NotNull(confirm.Observe("Cogent", "den3", "Telia"));
    }
}
