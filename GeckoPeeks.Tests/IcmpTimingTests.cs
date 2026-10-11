using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using Xunit;

namespace GeckoPeeks.Tests;

public class IcmpTimingTests
{
    [Theory]
    [InlineData(18, 25, 18)]   // Prefer API RoundtripTime when positive
    [InlineData(0, 25, 25)]    // Windows TtlExpired often reports 0 — use Stopwatch
    [InlineData(0, 0, 0)]
    [InlineData(12, 0, 12)]
    public void ResolveRttMs_prefers_api_then_elapsed(long api, long elapsed, long expected) =>
        Assert.Equal(expected, IcmpTiming.ResolveRttMs(api, elapsed));

    [Fact]
    public void ResolveRespondedRttMs_is_null_when_silent() =>
        Assert.Null(IcmpTiming.ResolveRespondedRttMs(false, 0, 40));

    [Fact]
    public void ResolveRespondedRttMs_uses_elapsed_when_api_is_zero() =>
        Assert.Equal(40, IcmpTiming.ResolveRespondedRttMs(true, 0, 40));
}

public class UpstreamSummaryTests
{
    [Fact]
    public void Avg_uses_riot_when_present()
    {
        var rows = new[]
        {
            Sample("Zayo", riot: "61", last: "40"),
            Sample("Zayo", riot: "59", last: "38"),
        };

        var summary = Assert.Single(UpstreamSummary.Build(rows));
        Assert.Equal("Zayo", summary.Upstream);
        Assert.Equal(2, summary.Count);
        Assert.Equal("60", summary.AvgMs);
    }

    [Fact]
    public void Avg_falls_back_to_last_hop_when_riot_blank()
    {
        var rows = new[]
        {
            Sample("Zayo", riot: "", last: "44"),
        };

        var summary = Assert.Single(UpstreamSummary.Build(rows));
        Assert.Equal("44", summary.AvgMs);
        Assert.NotEqual("\u2014", summary.AvgMs);
    }

    [Fact]
    public void Avg_falls_back_to_handoff_rtt()
    {
        var row = Sample("Zayo", riot: "", last: "");
        row.HandoffRttMs = "37";
        var summary = Assert.Single(UpstreamSummary.Build(new[] { row }));
        Assert.Equal("37", summary.AvgMs);
    }

    private static RouteRow Sample(string upstream, string riot, string last) => new()
    {
        Timestamp = "2026-10-11 12:00:00",
        LeagueRunning = "N",
        Upstream = upstream,
        RiotHopAvg = riot,
        LastHopAvg = last,
        FullPath = "1.1.1.1 > 192.207.0.5",
    };
}

public class PendingLogTests
{
    [Fact]
    public void Pending_write_creates_missing_file_and_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"), "nested");
        try
        {
            Assert.False(Directory.Exists(folder));
            var log = new CsvLog(folder, () => true);
            var row = new RouteRow
            {
                Timestamp = "2026-10-11 12:00:00",
                LeagueRunning = "N",
                Upstream = "Zayo",
                RiotHopAvg = "61",
                FullPath = "1.1.1.1 > 192.207.0.5",
            };

            Assert.Equal(AppendResult.Pending, log.Append(row));
            Assert.True(File.Exists(log.PendingPath));
            Assert.Contains("Zayo", File.ReadAllText(log.PendingPath), StringComparison.Ordinal);
            Assert.Contains("61", File.ReadAllText(log.PendingPath), StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    [Fact]
    public void Pending_write_succeeds_when_pending_file_was_absent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder, () => true);
            Assert.False(File.Exists(log.PendingPath));
            Assert.Equal(AppendResult.Pending, log.Append(new RouteRow
            {
                Timestamp = "2026-10-11 12:01:00",
                Upstream = "Cogent",
                RiotHopAvg = "55",
            }));
            Assert.True(File.Exists(log.PendingPath));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    [Fact]
    public async Task ReadAll_does_not_throw_while_append_holds_the_writer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(new RouteRow
            {
                Timestamp = "2026-10-11 12:00:00",
                Upstream = "Zayo",
                RiotHopAvg = "61",
                FullPath = "1.1.1.1 > 192.207.0.5",
            }));

            Exception? readError = null;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reader = Task.Run(() =>
            {
                started.TrySetResult();
                try
                {
                    for (var i = 0; i < 40; i++)
                        _ = log.ReadAll();
                }
                catch (Exception ex)
                {
                    readError = ex;
                }
            });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var i = 0; i < 20; i++)
            {
                log.Append(new RouteRow
                {
                    Timestamp = $"2026-10-11 12:00:{i:00}",
                    Upstream = "Zayo",
                    RiotHopAvg = "60",
                    FullPath = "1.1.1.1 > 192.207.0.5",
                });
            }

            await reader.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(readError);
            Assert.True(log.ReadAll().Count >= 1);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }
}
