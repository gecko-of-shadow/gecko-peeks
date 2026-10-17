using GeckoPeeks.Net;
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
}
