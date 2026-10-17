using System.Text;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class DiagnosisTests
{
    [Fact]
    public void No_route_is_one_line()
    {
        var text = DiagnosisText.Format(Sample() with { HasRoute = false });
        Assert.Equal(DiagnosisText.NoRoute, text);
    }

    [Fact]
    public void Report_names_both_latency_sources_and_does_not_borrow_last_hop()
    {
        var text = DiagnosisText.Format(Sample());
        Assert.Contains("Gecko Peeks 1.0.13", text, StringComparison.Ordinal);
        Assert.Contains("Zayo → Telia sea1", text, StringComparison.Ordinal);
        Assert.Contains("Last hop 21 ms (0% loss)", text, StringComparison.Ordinal);
        Assert.Contains("Riot ICMP no reply", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Riot ICMP 21", text, StringComparison.Ordinal);
        Assert.Contains("League running", text, StringComparison.Ordinal);
        Assert.Contains("Ethernet · gateway 192.168.1.1", text, StringComparison.Ordinal);
        Assert.Contains("Same path as the last test", text, StringComparison.Ordinal);
        Assert.Contains("1  192.168.1.1  router.home", text, StringComparison.Ordinal);
        Assert.Contains("2  *", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Vpn_adapter_is_labeled()
    {
        var input = Sample() with
        {
            Adapter = "NordLynx",
            Wifi = "",
            Gateway = "10.5.0.1",
            Vpn = "Y",
            ChangeLine = "",
        };
        var text = DiagnosisText.Format(input);
        Assert.Contains("VPN NordLynx · gateway 10.5.0.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Same path", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_line_omits_a_missing_handoff()
    {
        Assert.Equal("Zayo", RouteCardText.RouteLine("Zayo", "", ""));
        Assert.Equal("Zayo → Telia", RouteCardText.RouteLine("Zayo", "Telia", ""));
        Assert.Equal("Zayo → Telia sea1", RouteCardText.RouteLine("Zayo", "Telia", "sea1"));
        Assert.Equal("Unknown", RouteCardText.RouteLine("  ", "", ""));
    }

    [Fact]
    public void Figures_keep_last_hop_and_riot_apart()
    {
        Assert.Equal("21 ms", RouteCardText.LastHopFigure("21", "0"));
        Assert.Equal("no reply", RouteCardText.RiotFigure("", "100"));
        Assert.Equal("\u2014", RouteCardText.RiotFigure("", ""));
        Assert.Equal("61 ms", RouteCardText.RiotFigure("61", "0"));
    }

    private static DiagnosisInput Sample() => new(
        "1.0.13",
        true,
        "2026-10-13 21:04:02",
        "Zayo",
        "Telia",
        "sea1",
        "21",
        "0",
        "",
        "100",
        "Y",
        "Ethernet",
        "",
        "192.168.1.1",
        "N",
        HopDiff.SamePath,
        new DiagnosisHop[]
        {
            new(1, "192.168.1.1", "router.home"),
            new(2, "*", ""),
        });
}

public class HopDiffTests
{
    [Fact]
    public void First_test_says_nothing()
    {
        Assert.Equal("", HopDiff.Describe(null, "1.1.1.1 > 2.2.2.2"));
        Assert.Empty(HopDiff.ChangedHopNumbers("", "1.1.1.1"));
    }

    [Fact]
    public void A_star_that_stays_a_star_is_not_a_change()
    {
        var path = "10.0.0.1 > * > 192.207.0.5";
        Assert.Equal(HopDiff.SamePath, HopDiff.Describe(path, path));
        Assert.Empty(HopDiff.ChangedHopNumbers(path, "10.0.0.1 > * > 192.207.0.5"));
    }

    [Fact]
    public void Names_one_two_three_and_many_hops()
    {
        Assert.Equal("Hop 2 changed", HopDiff.Describe("1.1.1.1 > 2.2.2.2", "1.1.1.1 > 9.9.9.9"));
        Assert.Equal(
            "Hops 2 and 3 changed",
            HopDiff.Describe("1.1.1.1 > 2.2.2.2 > 3.3.3.3", "1.1.1.1 > 9.9.9.9 > 8.8.8.8"));
        Assert.Equal(
            "Hops 1, 2, and 3 changed",
            HopDiff.Describe("1.1.1.1 > 2.2.2.2 > 3.3.3.3", "9.9.9.9 > 8.8.8.8 > 7.7.7.7"));
        Assert.Equal(
            "Path changed (4 hops)",
            HopDiff.Describe("1 > 2 > 3 > 4", "9 > 8 > 7 > 6"));
    }

    [Fact]
    public void A_longer_path_counts_the_new_hop()
    {
        var changed = HopDiff.ChangedHopNumbers("1.1.1.1 > 2.2.2.2", "1.1.1.1 > 2.2.2.2 > 3.3.3.3");
        Assert.Equal(new[] { 3 }, changed);
    }
}

public class NetworkContextTests
{
    [Fact]
    public void Chooses_the_lowest_metric_gateway_and_skips_loopback()
    {
        var chosen = NetworkContext.Choose(new[]
        {
            Nic("Loopback", up: true, loopback: true, wireless: false, gateway: "127.0.0.1", metric: 1),
            Nic("Wi-Fi", up: true, loopback: false, wireless: true, gateway: "192.168.1.1", metric: 50),
            Nic("Ethernet", up: true, loopback: false, wireless: false, gateway: "10.0.0.1", metric: 25),
            Nic("Down", up: false, loopback: false, wireless: false, gateway: "10.1.1.1", metric: 1),
        });

        Assert.NotNull(chosen);
        Assert.Equal("Ethernet", chosen!.Name);
    }

    [Fact]
    public void Best_interface_index_picks_a_split_tunnel_without_a_gateway()
    {
        // OpenVPN-style 0/1 + 128/1: the tunnel has no 0.0.0.0/0 route and no gateway,
        // so the metric comparison alone picks Ethernet.
        var candidates = new[]
        {
            Nic("Ethernet", true, false, false, "192.168.1.1", 25) with { Index = 3 },
            Nic("OpenVPN Data Channel Offload", true, false, false, "", null, "OpenVPN Data Channel Offload") with { Index = 17 },
        };

        Assert.Equal("Ethernet", NetworkContext.Choose(candidates)!.Name);

        var chosen = NetworkContext.ChooseByIndex(candidates, 17);
        Assert.NotNull(chosen);
        var snapshot = NetworkContext.FromCandidate(chosen!, wifi: null);
        Assert.Equal("OpenVPN Data Channel Offload", snapshot.Adapter);
        Assert.Equal("", snapshot.Gateway);
        Assert.Equal("Y", snapshot.Vpn);
    }

    [Fact]
    public void Best_interface_index_with_no_match_returns_null()
    {
        var candidates = new[] { Nic("Ethernet", true, false, false, "192.168.1.1", 25) with { Index = 3 } };

        Assert.Null(NetworkContext.ChooseByIndex(candidates, 99));
        Assert.Equal("Ethernet", NetworkContext.ChooseByIndex(candidates, 3)!.Name);
    }

    [Fact]
    public void Vpn_tokens_do_not_match_a_bare_tap()
    {
        Assert.False(NetworkContext.IsVpn("Ethernet", "TAP-Windows Adapter V9"));
        Assert.True(NetworkContext.IsVpn("NordLynx", "WireGuard Tunnel"));
        Assert.False(NetworkContext.IsVpn("Ethernet", "Intel I219-V"));

        var wired = NetworkContext.FromCandidate(
            Nic("Ethernet", true, false, false, "192.168.1.1", 10),
            wifi: "ShouldNotStick");
        Assert.Equal("N", wired.Vpn);
        Assert.Equal("", wired.Wifi);
        Assert.Equal("192.168.1.1", wired.Gateway);

        var wifi = NetworkContext.FromCandidate(
            Nic("Wi-Fi", true, false, true, "192.168.1.1", 40, "WireGuard Tunnel"),
            wifi: "Home");
        Assert.Equal("Y", wifi.Vpn);
        Assert.Equal("Home", wifi.Wifi);
    }

    private static NetworkContext.Candidate Nic(
        string name,
        bool up,
        bool loopback,
        bool wireless,
        string gateway,
        int? metric,
        string description = "") =>
        new(name, description, up, loopback, wireless, gateway, metric);
}

public class HeaderWidenTests
{
    [Fact]
    public void Legacy_header_is_the_original_seventeen_columns()
    {
        Assert.Equal(
            CsvFormat.LegacyHeaderLine,
            string.Join(",", CsvFormat.Columns.Take(17)) + "\r\n");
        Assert.Equal(21, CsvFormat.Columns.Length);
    }

    [Fact]
    public void Append_widens_an_old_file_and_keeps_the_old_row()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            var oldLine = "2026-10-11 12:00:00,Y,Zayo,Telia,host,4.5.6.7,sea1,40,9.9.9.9,lasthost,50,61,80,0,61,0,10.0.0.1 > 1.2.3.4\r\n";
            var bytes = Encoding.UTF8.GetPreamble()
                .Concat(Encoding.UTF8.GetBytes(CsvFormat.LegacyHeaderLine + oldLine))
                .ToArray();
            File.WriteAllBytes(log.MainPath, bytes);

            var row = new RouteRow
            {
                Timestamp = "2026-10-13 21:04:02",
                LeagueRunning = "N",
                Upstream = "Cogent",
                FullPath = "10.0.0.1 > 8.8.8.8",
                Adapter = "Ethernet",
                Gateway = "192.168.1.1",
                Vpn = "N",
            };
            Assert.Equal(AppendResult.Main, log.Append(row));

            var text = File.ReadAllText(log.MainPath);
            var shown = text.TrimStart('\uFEFF');
            var actualLine = shown.Split('\n')[0].Replace("\r", "\\r");
            var expectedLine = CsvFormat.HeaderLine.Split('\n')[0].Replace("\r", "\\r");
            Assert.True(shown.StartsWith(CsvFormat.HeaderLine, StringComparison.Ordinal),
                "actual=[" + actualLine + "] expected=[" + expectedLine + "]");
            Assert.Contains("10.0.0.1 > 1.2.3.4", text, StringComparison.Ordinal);
            Assert.Contains("Ethernet", text, StringComparison.Ordinal);

            var rows = log.ReadAll();
            Assert.Equal(2, rows.Count);
            Assert.Equal("Zayo", rows[0].Upstream);
            Assert.Equal("", rows[0].Adapter);
            Assert.Equal("Cogent", rows[1].Upstream);
            Assert.Equal("Ethernet", rows[1].Adapter);
            Assert.Equal("192.168.1.1", rows[1].Gateway);
            Assert.Equal("N", rows[1].Vpn);
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
