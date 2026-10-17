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

    private static TestRunner.ProbeResult Probe(IPStatus status, string? ip, long? rtt) => new(status, ip, rtt);
    private static TestRunner.ProbeResult Lost => new(IPStatus.TimedOut, null, null);

    [Fact]
    public void Hop_with_one_answering_probe_responded()
    {
        var hop = TestRunner.MergeProbes(4, new[] { Lost, Probe(IPStatus.TtlExpired, "64.125.1.1", 21), Lost });

        Assert.True(hop.Responded);
        Assert.Equal(4, hop.Number);
        Assert.Equal("64.125.1.1", hop.Ip);
        Assert.Equal(21, hop.RttMs);
        Assert.False(hop.ReachedTarget);
    }

    [Fact]
    public void Hop_uses_first_ip_and_its_lowest_rtt()
    {
        var hop = TestRunner.MergeProbes(5, new[]
        {
            Probe(IPStatus.TtlExpired, "64.125.1.1", 30),
            Probe(IPStatus.TtlExpired, "64.125.2.2", 5),
            Probe(IPStatus.TtlExpired, "64.125.1.1", 22),
        });

        Assert.Equal("64.125.1.1", hop.Ip);
        Assert.Equal(22, hop.RttMs);
    }

    [Fact]
    public void Hop_is_silent_only_when_every_probe_failed()
    {
        var hop = TestRunner.MergeProbes(6, new[] { Lost, Lost, Lost });

        Assert.False(hop.Responded);
        Assert.Equal("*", hop.Ip);
        Assert.Null(hop.RttMs);
        Assert.False(hop.ReachedTarget);
    }

    [Fact]
    public void Any_success_probe_reaches_the_target()
    {
        var hop = TestRunner.MergeProbes(9, new[] { Lost, Probe(IPStatus.Success, "192.207.0.5", 60), Lost });

        Assert.True(hop.ReachedTarget);
        Assert.Equal("192.207.0.5", hop.Ip);
    }

    [Fact]
    public void Trace_sends_three_probes_per_ttl() => Assert.Equal(3, TestRunner.ProbesPerHop);

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
