using GeckoPeeks.Logging;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class LogDisplayTests
{
    [Fact]
    public void Every_csv_column_has_its_own_cell()
    {
        var row = Sample();
        foreach (var column in CsvFormat.Columns)
        {
            var cell = LogDisplay.Cell(row, column);
            Assert.NotNull(cell);
            Assert.False(string.IsNullOrWhiteSpace(LatencyLabels.ColumnHeader(column)), column);
        }
    }

    [Fact]
    public void Cells_stay_with_their_column_when_the_row_is_read_by_name()
    {
        var row = Sample();
        Assert.Equal("2026-10-11 12:00:00", LogDisplay.Cell(row, "Timestamp"));
        Assert.Equal("Yes", LogDisplay.Cell(row, "LeagueRunning"));
        Assert.Equal("Zayo", LogDisplay.Cell(row, "Upstream"));
        Assert.Equal("Telia", LogDisplay.Cell(row, "HandoffNetwork"));
        Assert.Equal("ic-1.sea1.twelve99.net", LogDisplay.Cell(row, "HandoffHost"));
        Assert.Equal("4.5.6.7", LogDisplay.Cell(row, "HandoffIP"));
        Assert.Equal("sea1 · Seattle", LogDisplay.Cell(row, "HandoffCity"));
        Assert.Equal("40", LogDisplay.Cell(row, "HandoffRttMs"));
        Assert.Equal("9.9.9.9", LogDisplay.Cell(row, "LastHopIP"));
        Assert.Equal("ae1.den3.zayo.com", LogDisplay.Cell(row, "LastHopHost"));
        Assert.Equal("18", LogDisplay.Cell(row, "LastHopMin"));
        Assert.Equal("21", LogDisplay.Cell(row, "LastHopAvg"));
        Assert.Equal("24", LogDisplay.Cell(row, "LastHopMax"));
        Assert.Equal("0", LogDisplay.Cell(row, "LastHopLossPct"));
        Assert.Equal("61", LogDisplay.Cell(row, "RiotHopAvg"));
        Assert.Equal("0", LogDisplay.Cell(row, "RiotHopLossPct"));
        Assert.Equal("1.2.3.4 > 192.207.0.5", LogDisplay.Cell(row, "FullPath"));
    }

    [Fact]
    public void League_yes_and_no_replace_the_csv_letters_only_on_screen()
    {
        Assert.Equal("Yes", LogDisplay.League("Y"));
        Assert.Equal("No", LogDisplay.League("n"));
        Assert.Equal("", LogDisplay.League(""));
        Assert.Equal("maybe", LogDisplay.League("maybe"));
    }

    [Fact]
    public void Unknown_city_codes_stay_bare_and_a_missing_column_is_refused()
    {
        Assert.Equal("zzz9", LogDisplay.City("zzz9"));
        Assert.Equal("", LogDisplay.City("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogDisplay.Cell(new RouteRow(), "NotAColumn"));
    }

    [Fact]
    public void Riot_no_reply_stays_on_the_riot_cell_only()
    {
        var row = Sample();
        row.RiotHopAvg = "";
        row.RiotHopLossPct = "100";
        Assert.Equal("no reply", LogDisplay.Cell(row, "RiotHopAvg"));
        Assert.Equal("21", LogDisplay.Cell(row, "LastHopAvg"));
    }

    [Fact]
    public void Headers_name_league_city_and_path_without_calling_a_number_avg_ms()
    {
        Assert.Equal("League", LatencyLabels.ColumnHeader("LeagueRunning"));
        Assert.Equal("City", LatencyLabels.ColumnHeader("HandoffCity"));
        Assert.Equal("Full path", LatencyLabels.ColumnHeader("FullPath"));
        Assert.Equal("Time", LatencyLabels.ColumnHeader("Timestamp"));
    }

    [Fact]
    public void Copy_status_names_the_log_file()
    {
        Assert.Contains("log path", LogDisplay.CopiedFileStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folder", LogDisplay.CopiedFileStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_formatted_row_has_one_field_per_csv_column()
    {
        var line = CsvFormat.FormatLine(Sample());
        var fields = line.TrimEnd('\r', '\n').Split(',');
        Assert.Equal(CsvFormat.Columns.Length, fields.Length);
    }

    private static RouteRow Sample() => new()
    {
        Timestamp = "2026-10-11 12:00:00",
        LeagueRunning = "Y",
        Upstream = "Zayo",
        HandoffNetwork = "Telia",
        HandoffHost = "ic-1.sea1.twelve99.net",
        HandoffIP = "4.5.6.7",
        HandoffCity = "sea1",
        HandoffRttMs = "40",
        LastHopIP = "9.9.9.9",
        LastHopHost = "ae1.den3.zayo.com",
        LastHopMin = "18",
        LastHopAvg = "21",
        LastHopMax = "24",
        LastHopLossPct = "0",
        RiotHopAvg = "61",
        RiotHopLossPct = "0",
        FullPath = "1.2.3.4 > 192.207.0.5",
    };
}

public class WindowStatusTests
{
    private const string Route = "Zayo → Telia sea1 | last hop 21 ms | just now";

    [Fact]
    public void Idle_window_shows_the_route_sentence()
    {
        Assert.Equal(Route, WindowStatus.Headline(false, "Idle", "", Route));
    }

    [Fact]
    public void A_running_test_keeps_the_phase()
    {
        Assert.Equal("Tracing hop 3", WindowStatus.Headline(true, "Tracing hop 3", "", Route));
    }

    [Theory]
    [InlineData("Error: timeout")]
    [InlineData("Last test problem: no reply from the first hop")]
    public void A_failed_test_keeps_the_phase_even_when_a_route_exists(string phase)
    {
        Assert.Equal(phase, WindowStatus.Headline(false, phase, "", Route));
        Assert.True(WindowStatus.IsTrouble(phase, ""));
    }

    [Fact]
    public void A_stored_error_counts_as_trouble()
    {
        Assert.True(WindowStatus.IsTrouble("Idle", "ping failed"));
        Assert.Equal("Idle", WindowStatus.Headline(false, "Idle", "ping failed", Route));
    }

    [Fact]
    public void Blank_phase_and_tooltip_read_idle()
    {
        Assert.Equal("Idle", WindowStatus.Headline(false, "  ", "", "  "));
    }
}

public class ShareLabelTests
{
    [Theory]
    [InlineData(1, 4, "25% · 1")]
    [InlineData(0, 4, "0% · 0")]
    [InlineData(2, 2, "100% · 2")]
    public void Share_label_includes_the_sample_count(int count, int total, string expected) =>
        Assert.Equal(expected, UpstreamSummary.ShareLabel(count, total));
}
