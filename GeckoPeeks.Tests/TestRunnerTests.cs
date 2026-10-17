using System.Net;
using System.Net.NetworkInformation;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using Xunit;

namespace GeckoPeeks.Tests;

public class TestRunnerTests
{
    [Fact]
    public void Riot_ping_target_is_the_trace_target()
    {
        Assert.Equal("192.207.0.5", TestRunner.TraceTarget);
        Assert.Equal(TestRunner.TraceTarget, TestRunner.RiotPingTarget);
    }

    private static HopObservation Hop(int n, string ip, bool reached = false) =>
        new(n, ip, "", ip == "*" ? null : 10, ip != "*", reached);

    [Fact]
    public void Last_hop_skips_the_target()
    {
        var hops = new[]
        {
            Hop(1, "10.0.0.1"),
            Hop(2, "64.125.1.1"),
            Hop(3, "*"),
            Hop(4, "192.207.0.5", reached: true),
        };

        Assert.Equal("64.125.1.1", TestRunner.LastHop(hops)?.Ip);
    }

    [Fact]
    public void Last_hop_is_last_responder_when_target_not_reached()
    {
        var hops = new[] { Hop(1, "10.0.0.1"), Hop(2, "64.125.1.1"), Hop(3, "*"), Hop(4, "*") };

        Assert.Equal(2, TestRunner.LastHop(hops)?.Number);
    }

    [Fact]
    public void Last_hop_is_blank_when_only_the_target_responded()
    {
        var hops = new[] { Hop(1, "*"), Hop(2, "*"), Hop(3, "192.207.0.5", reached: true) };

        Assert.Null(TestRunner.LastHop(hops));
    }

    [Theory]
    [InlineData(IPStatus.TtlExpired, "64.125.1.1", true)]
    [InlineData(IPStatus.TimeExceeded, "64.125.1.1", true)]
    [InlineData(IPStatus.TtlExpired, "64.125.9.9", false)]
    [InlineData(IPStatus.Success, "64.125.1.1", false)]
    [InlineData(IPStatus.Success, "192.207.0.5", false)]
    [InlineData(IPStatus.TimedOut, null, false)]
    public void Hop_reply_counts_only_time_exceeded_from_that_hop(IPStatus status, string? from, bool expected)
    {
        var hop = IPAddress.Parse("64.125.1.1");
        var address = from == null ? null : IPAddress.Parse(from);

        Assert.Equal(expected, TestRunner.IsHopReply(status, address, hop));
    }
}
