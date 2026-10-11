using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Routing;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// Edge cases for logging durability, RTT resolution, and classifier noise.
/// Keeps main green without a release bump.
/// </summary>
public class CsvDurabilityTests
{
    [Fact]
    public void Merge_combines_pending_and_merging_without_duplicating()
    {
        var folder = TempFolder();
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));

            var pendingA = CsvFormat.FormatLine(Row("TELUS", "12:01:00"));
            var pendingB = CsvFormat.FormatLine(Row("Cogent", "12:02:00"));
            File.WriteAllText(log.MergingPath, pendingA);
            File.WriteAllText(log.PendingPath, pendingB);

            Assert.Equal(AppendResult.Main, log.Append(Row("HE", "12:03:00")));
            Assert.False(File.Exists(log.PendingPath));
            Assert.False(File.Exists(log.MergingPath));

            var rows = log.ReadAll();
            Assert.Equal(4, rows.Count);
            Assert.Equal(new[] { "Zayo", "TELUS", "Cogent", "HE" }, rows.Select(r => r.Upstream).ToArray());
            Assert.Equal(1, CountOf(File.ReadAllText(log.MainPath), "TELUS"));
            Assert.Equal(1, CountOf(File.ReadAllText(log.MainPath), "Cogent"));
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void Merge_is_idempotent_when_payload_already_at_end_of_main()
    {
        var folder = TempFolder();
        try
        {
            var log = new CsvLog(folder);
            var first = Row("Zayo", "12:00:00");
            var second = Row("Cogent", "12:01:00");
            Assert.Equal(AppendResult.Main, log.Append(first));
            Assert.Equal(AppendResult.Main, log.Append(second));

            // Crash after WriteFull succeeded but before MergingPath delete.
            File.WriteAllText(log.MergingPath, CsvFormat.FormatLine(second));

            Assert.Equal(AppendResult.Main, log.Append(Row("HE", "12:02:00")));
            Assert.False(File.Exists(log.MergingPath));

            var main = File.ReadAllText(log.MainPath);
            Assert.Equal(1, CountOf(main, "Cogent"));
            Assert.Equal(3, log.ReadAll().Count);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void Empty_merging_file_is_discarded()
    {
        var folder = TempFolder();
        try
        {
            var log = new CsvLog(folder);
            File.WriteAllText(log.MergingPath, "");
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));
            Assert.False(File.Exists(log.MergingPath));
            Assert.Single(log.ReadAll());
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void ReadAll_includes_pending_rows_while_main_is_current()
    {
        var folder = TempFolder();
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Row("Zayo", "12:00:00")));

            var forced = new CsvLog(folder, () => true);
            Assert.Equal(AppendResult.Pending, forced.Append(Row("TELUS", "12:01:00")));

            var rows = log.ReadAll();
            Assert.Equal(2, rows.Count);
            Assert.Equal("TELUS", rows[1].Upstream);
            Assert.Null(log.ReadProblem);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    [Fact]
    public void Append_access_denied_message_mentions_controlled_folder_access()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"), "blocked");
        // Folder path is under a file so CreateDirectory / writes fail.
        var blocker = Path.GetDirectoryName(folder)!;
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "not-a-directory");
        try
        {
            var log = new CsvLog(folder, () => true);
            var ex = Assert.ThrowsAny<IOException>(() => log.Append(Row("Zayo", "12:00:00")));
            Assert.Contains("Controlled folder access", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GeckoPeeks.exe", ex.Message, StringComparison.Ordinal);
            Assert.Contains("RiotRouteLog", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                File.Delete(blocker);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    private static RouteRow Row(string upstream, string time) => new()
    {
        Timestamp = "2026-10-11 " + time,
        LeagueRunning = "N",
        Upstream = upstream,
        RiotHopAvg = "50",
        FullPath = "1.1.1.1 > 192.207.0.5",
    };

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDelete(string folder)
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

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}

public class CsvFormatEdgeTests
{
    [Theory]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("\tTAB", "'\tTAB")]
    public void Escape_neutralizes_formula_prefixes(string input, string expectedPrefix)
    {
        var escaped = CsvFormat.Escape(input);
        Assert.StartsWith(expectedPrefix, escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_tolerates_short_rows_and_empty_input()
    {
        Assert.Empty(CsvFormat.Parse(null));
        Assert.Empty(CsvFormat.Parse(""));
        Assert.Empty(CsvFormat.Parse(CsvFormat.HeaderLine));

        var shortRow = "2026-10-11 12:00:00,Y,Zayo\r\n";
        var row = Assert.Single(CsvFormat.Parse(CsvFormat.HeaderLine + shortRow));
        Assert.Equal("Zayo", row.Upstream);
        Assert.Equal("", row.FullPath);
        Assert.Equal("", row.RiotHopAvg);
    }

    [Fact]
    public void Parse_preserves_quoted_commas_inside_full_path()
    {
        var row = new RouteRow
        {
            Timestamp = "2026-10-11 12:00:00",
            Upstream = "Zayo",
            FullPath = "a,b > c",
        };
        var line = CsvFormat.FormatLine(row);
        var back = Assert.Single(CsvFormat.Parse(CsvFormat.HeaderLine + line));
        Assert.Equal("a,b > c", back.FullPath);
    }
}

public class UpstreamSummaryEdgeTests
{
    [Fact]
    public void Blank_upstream_buckets_as_unknown()
    {
        var row = new RouteRow { Upstream = "  ", LastHopAvg = "40" };
        var summary = Assert.Single(UpstreamSummary.Build(new[] { row }));
        Assert.Equal("Unknown", summary.Upstream);
        Assert.Equal("40", summary.LastHopMs);
    }

    [Fact]
    public void No_numeric_samples_shows_em_dash()
    {
        var row = new RouteRow { Upstream = "Zayo", RiotHopAvg = "", LastHopAvg = "", HandoffRttMs = "" };
        var summary = Assert.Single(UpstreamSummary.Build(new[] { row }));
        Assert.Equal("\u2014", summary.LastHopMs);
        Assert.Equal("\u2014", summary.RiotMs);
        Assert.Equal(1, summary.Count);
    }

    [Fact]
    public void Groups_case_insensitively_and_orders_by_count()
    {
        var rows = new[]
        {
            new RouteRow { Upstream = "zayo", LastHopAvg = "10" },
            new RouteRow { Upstream = "Zayo", LastHopAvg = "20" },
            new RouteRow { Upstream = "Cogent", LastHopAvg = "30" },
        };
        var summary = UpstreamSummary.Build(rows);
        Assert.Equal(2, summary.Count);
        Assert.Equal("zayo", summary[0].Upstream);
        Assert.Equal(2, summary[0].Count);
        Assert.Equal("15", summary[0].LastHopMs);
        Assert.Equal("Cogent", summary[1].Upstream);
    }

    [Theory]
    [InlineData(1, 4, "25%")]
    [InlineData(0, 4, "0%")]
    [InlineData(1, 0, "0%")]
    [InlineData(1, 3, "33.3%")]
    public void SharePercent_formats(int count, int total, string expected) =>
        Assert.Equal(expected, UpstreamSummary.SharePercent(count, total));
}

public class IcmpTimingEdgeTests
{
    [Fact]
    public void Negative_api_rtt_falls_back_to_elapsed() =>
        Assert.Equal(22, IcmpTiming.ResolveRttMs(-1, 22));

    [Fact]
    public void Responded_with_both_zero_returns_zero() =>
        Assert.Equal(0, IcmpTiming.ResolveRespondedRttMs(true, 0, 0));
}

public class ClassifierEdgeTests
{
    [Theory]
    [InlineData("ae5.ccr31.core1.edge.example.net", "")]
    [InlineData("lo0.rtr1.example.net", "")]
    [InlineData("peer1.bb2.example.net", "")]
    [InlineData("xe-0-0-0.example.net", "")]
    [InlineData("foobar.example.net", "")]
    public void City_code_skips_interface_junk(string hostname, string expected) =>
        Assert.Equal(expected, CityCode.Extract(hostname));

    [Fact]
    public void HostMatchesDomain_requires_label_boundary()
    {
        Assert.True(RouteClassifier.HostMatchesDomain("ae1.zayo.com", "zayo.com"));
        Assert.True(RouteClassifier.HostMatchesDomain("zayo.com", "zayo.com"));
        Assert.False(RouteClassifier.HostMatchesDomain("fakezayo.com", "zayo.com"));
        Assert.False(RouteClassifier.HostMatchesDomain("zayo.com.evil.example", "zayo.com"));
        Assert.False(RouteClassifier.HostMatchesDomain("notelia.net", "telia.net"));
    }

    [Fact]
    public void LabelContains_rejects_embedded_stems()
    {
        Assert.False(RouteClassifier.LabelContains("cdn.the.net", "he"));
        Assert.False(RouteClassifier.LabelContains("www.apache.net", "he"));
        Assert.False(RouteClassifier.LabelContains("hotelus.example.com", "telus"));
        Assert.False(RouteClassifier.LabelContains("notelia.example.com", "telia"));
        Assert.True(RouteClassifier.LabelContains("zayo-telia.zayo.com", "zayo"));
        Assert.True(RouteClassifier.LabelContains("zayo-telia.zayo.com", "telia"));
    }

    [Fact]
    public void Classify_skips_silent_hops_when_picking_upstream()
    {
        var hops = new[]
        {
            new HopObservation(1, "1.1.1.1", "ae1.zayo.com", 10, true, false),
            new HopObservation(2, "*", "", null, false, false),
            new HopObservation(3, "9.9.9.9", "yyc2.telus.net", 20, true, false),
            new HopObservation(4, "192.207.0.5", "", 40, true, true),
        };

        var features = RouteClassifier.Classify(hops);
        Assert.Equal("TELUS", features.Upstream);
    }

    [Fact]
    public void HopsFromFullPath_round_trips_stars()
    {
        const string path = "* > 1.2.3.4 > * > 192.207.0.5";
        var hops = RouteClassifier.HopsFromFullPath(path);
        Assert.Equal(4, hops.Count);
        Assert.False(hops[0].Responded);
        Assert.True(hops[1].Responded);
        Assert.Equal(path, RouteClassifier.BuildFullPath(hops));
        Assert.Empty(RouteClassifier.HopsFromFullPath(""));
        Assert.Empty(RouteClassifier.HopsFromFullPath(null));
    }

    [Fact]
    public void Balloon_is_indeterminate_for_unknown_or_blank_upstream()
    {
        Assert.True(RouteChange.IsIndeterminate("Unknown", "sea1"));
        Assert.True(RouteChange.IsIndeterminate("", "sea1"));
        Assert.True(RouteChange.IsIndeterminate(null, null));
        Assert.False(RouteChange.IsIndeterminate("Zayo", ""));
        Assert.False(RouteChange.IsIndeterminate("Zayo", "sea1"));
    }
}
