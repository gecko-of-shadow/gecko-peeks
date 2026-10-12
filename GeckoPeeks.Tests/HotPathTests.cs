using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Net;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

public class TrayMenuTests
{
    [Fact]
    public void Open_window_is_the_first_item_and_the_default()
    {
        Assert.Equal(TrayCommand.OpenWindow, TrayMenu.Layout[0]);
        Assert.Equal("Open window", TrayMenu.Label(TrayCommand.OpenWindow));
        Assert.Equal(TrayCommand.OpenWindow, TrayMenu.DefaultCommand);
    }

    [Fact]
    public void Every_command_appears_exactly_once()
    {
        var commands = TrayMenu.Layout.Where(entry => entry.HasValue).Select(entry => entry!.Value).ToList();
        Assert.Equal(Enum.GetValues<TrayCommand>().OrderBy(c => c), commands.OrderBy(c => c));
        Assert.Single(commands, command => command == TrayCommand.OpenWindow);
    }

    [Fact]
    public void Exit_is_last_behind_a_separator()
    {
        Assert.Equal(TrayCommand.Exit, TrayMenu.Layout[^1]);
        Assert.Null(TrayMenu.Layout[^2]);
    }

    [Fact]
    public void Check_for_updates_sits_above_Exit_with_separators()
    {
        Assert.Equal(TrayCommand.CheckForUpdates, TrayMenu.Layout[^3]);
        Assert.Null(TrayMenu.Layout[^4]);
        Assert.Equal("Check for updates", TrayMenu.Label(TrayCommand.CheckForUpdates));
        Assert.Equal(
            "Install update (v1.0.12)",
            TrayMenu.Label(TrayCommand.CheckForUpdates, updateVersion: "1.0.12"));
    }

    [Fact]
    public void Open_window_stands_apart_from_the_logging_commands()
    {
        Assert.Null(TrayMenu.Layout[1]);
        Assert.Equal(TrayCommand.RunTestNow, TrayMenu.Layout[2]);
    }

    [Fact]
    public void Separators_never_double_up_or_sit_at_the_edges()
    {
        Assert.NotNull(TrayMenu.Layout[0]);
        Assert.NotNull(TrayMenu.Layout[^1]);
        for (var i = 1; i < TrayMenu.Layout.Count; i++)
            Assert.False(TrayMenu.Layout[i] is null && TrayMenu.Layout[i - 1] is null, $"Double separator at {i}");
    }

    [Fact]
    public void Every_command_has_a_label()
    {
        foreach (var command in Enum.GetValues<TrayCommand>())
            Assert.False(string.IsNullOrWhiteSpace(TrayMenu.Label(command)), command.ToString());
    }

    [Fact]
    public void Pause_label_follows_the_scheduler_state()
    {
        Assert.Equal("Pause logging", TrayMenu.Label(TrayCommand.TogglePause, paused: false));
        Assert.Equal("Resume logging", TrayMenu.Label(TrayCommand.TogglePause, paused: true));
    }

    [Fact]
    public void Only_start_with_windows_carries_a_checkmark()
    {
        Assert.Equal(
            new[] { TrayCommand.StartWithWindows },
            Enum.GetValues<TrayCommand>().Where(TrayMenu.IsCheckable));
        Assert.Equal("Start with Windows", TrayMenu.Label(TrayCommand.StartWithWindows));
    }

    [Fact]
    public void Closed_hint_says_how_to_get_back_and_how_to_quit()
    {
        Assert.Contains("Open window", TrayMenu.ClosedHintText);
        Assert.Contains("Exit", TrayMenu.ClosedHintText);
        Assert.Contains("logging", TrayMenu.ClosedHintTitle);
    }
}

public class StartupToggleTests
{
    [Fact]
    public void Checked_mirrors_the_run_value()
    {
        var key = new FakeRunKey { Enabled = true };
        var toggle = new StartupToggle(key.IsEnabled, key.SetEnabled);
        Assert.True(toggle.Checked);

        key.Enabled = false;
        Assert.False(toggle.Checked);
    }

    [Fact]
    public void Toggle_flips_the_run_value_and_the_checkmark_follows()
    {
        var key = new FakeRunKey();
        var toggle = new StartupToggle(key.IsEnabled, key.SetEnabled);

        Assert.True(toggle.Toggle());
        Assert.True(key.Enabled);
        Assert.True(toggle.Checked);

        Assert.True(toggle.Toggle());
        Assert.False(key.Enabled);
        Assert.False(toggle.Checked);
    }

    [Fact]
    public void Failed_write_reports_false_and_keeps_the_real_state()
    {
        var key = new FakeRunKey { Enabled = true, FailWrites = true };
        var toggle = new StartupToggle(key.IsEnabled, key.SetEnabled);

        Assert.False(toggle.Toggle());
        Assert.Equal(1, key.Writes);
        Assert.True(toggle.Checked);
    }

    [Fact]
    public void Unreadable_run_key_reads_as_unchecked()
    {
        var toggle = new StartupToggle(() => throw new UnauthorizedAccessException(), _ => { });
        Assert.False(toggle.Checked);
    }

    private sealed class FakeRunKey
    {
        public bool Enabled { get; set; }
        public bool FailWrites { get; init; }
        public int Writes { get; private set; }

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabled)
        {
            Writes++;
            if (FailWrites)
                throw new UnauthorizedAccessException("Run key is read-only.");
            Enabled = enabled;
        }
    }
}

public class StartupCommandTests
{
    private const string Exe = @"C:\Users\gecko\Downloads\GeckoPeeks.exe";

    [Fact]
    public void Run_value_quotes_the_exe_and_starts_in_the_tray() =>
        Assert.Equal("\"" + Exe + "\" --tray", StartupRegistration.CommandFor(Exe));

    [Fact]
    public void Value_written_by_v107_is_repaired() =>
        Assert.True(StartupRegistration.NeedsRepair("\"" + Exe + "\"", Exe));

    [Fact]
    public void Value_pointing_at_an_older_download_moves_to_this_exe() =>
        Assert.True(StartupRegistration.NeedsRepair(
            StartupRegistration.CommandFor(@"C:\Users\gecko\Downloads\GeckoPeeks (1).exe"),
            Exe));

    [Fact]
    public void Current_value_is_left_alone_regardless_of_case()
    {
        Assert.False(StartupRegistration.NeedsRepair(StartupRegistration.CommandFor(Exe), Exe));
        Assert.False(StartupRegistration.NeedsRepair(StartupRegistration.CommandFor(Exe.ToUpperInvariant()), Exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Startup_off_is_never_switched_on_by_a_repair(string? value) =>
        Assert.False(StartupRegistration.NeedsRepair(value, Exe));
}

public class LatencyLabelTests
{
    [Theory]
    [InlineData("", "100", true)]
    [InlineData("", "100.0", true)]
    [InlineData("\u2014", "100", true)]
    [InlineData("", "90", false)]
    [InlineData("61", "100", false)]
    [InlineData("", "", false)]
    public void Riot_reads_no_reply_only_when_every_ping_was_lost(string avg, string loss, bool expected) =>
        Assert.Equal(expected, LatencyLabels.RiotNoReply(avg, loss));

    [Fact]
    public void Riot_cell_says_no_reply_instead_of_a_blank_or_a_borrowed_number()
    {
        Assert.Equal("no reply", LatencyLabels.RiotCell("", "100"));
        Assert.Equal("61", LatencyLabels.RiotCell("61", "0"));
        Assert.Equal("", LatencyLabels.RiotCell("", ""));
    }

    [Fact]
    public void Every_log_column_has_a_header_and_riot_headers_say_icmp()
    {
        foreach (var column in CsvFormat.Columns)
            Assert.False(string.IsNullOrWhiteSpace(LatencyLabels.ColumnHeader(column)), column);

        Assert.Contains("ICMP", LatencyLabels.ColumnHeader("RiotHopAvg"));
        Assert.Contains("ICMP", LatencyLabels.ColumnHeader("RiotHopLossPct"));
        Assert.Equal("Last hop ms", LatencyLabels.ColumnHeader("LastHopAvg"));
    }

    [Fact]
    public void No_header_passes_a_path_number_off_as_plain_avg_ms()
    {
        foreach (var column in CsvFormat.Columns)
            Assert.NotEqual("Avg ms", LatencyLabels.ColumnHeader(column));
    }

    [Theory]
    [InlineData("21", 21.0)]
    [InlineData(" 21 ms ", 21.0)]
    [InlineData("20.6", 20.6)]
    public void Ms_text_parses_with_or_without_the_unit(string text, double expected)
    {
        Assert.True(LatencyLabels.TryParseMs(text, out var value));
        Assert.Equal(expected, value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u2014")]
    [InlineData("-")]
    [InlineData("n/a")]
    [InlineData("no reply")]
    [InlineData(null)]
    public void Placeholders_are_not_numbers(string? text) =>
        Assert.False(LatencyLabels.TryParseMs(text, out _));

    [Fact]
    public void Tooltip_names_the_source_of_each_number()
    {
        Assert.Equal("Riot 61 ms", LatencyLabels.Tooltip("61", "21", "40"));
        Assert.Equal("last hop 21 ms", LatencyLabels.Tooltip("", "20.6", "40"));
        Assert.Equal("handoff 40 ms", LatencyLabels.Tooltip("", "", ""));
        Assert.Equal("n/a", LatencyLabels.Tooltip("", "", ""));
    }
}

public class RiotGiveUpTests
{
    [Theory]
    [InlineData(0, 0, 3, false)]
    [InlineData(2, 0, 3, false)]
    [InlineData(3, 0, 3, true)]
    [InlineData(3, 1, 3, false)]
    [InlineData(9, 0, 0, false)]
    public void Series_stops_only_after_the_limit_with_no_reply(int sent, int replies, int giveUpAfter, bool expected) =>
        Assert.Equal(expected, IcmpTiming.ShouldGiveUp(sent, replies, giveUpAfter));
}

public class BranV107LogTests
{
    [Fact]
    public void Summary_of_a_full_riot_loss_log_shows_last_hop_and_no_reply()
    {
        var rows = new[]
        {
            Row("Zayo", last: "19"),
            Row("Zayo", last: "21"),
            Row("Zayo", last: "23"),
            Row("Cogent", last: "22"),
        };

        var summary = UpstreamSummary.Build(rows);
        Assert.Equal(2, summary.Count);
        Assert.All(summary, row => Assert.Equal(LatencyLabels.NoReply, row.RiotMs));
        Assert.Equal("21", summary.Single(row => row.Upstream == "Zayo").LastHopMs);
        Assert.Equal("22", summary.Single(row => row.Upstream == "Cogent").LastHopMs);
    }

    [Fact]
    public void Tooltip_for_a_full_riot_loss_row_reads_last_hop_not_riot()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0);
        var text = TooltipText.Format("Zayo", "Telia", "sea1", "", "21", "40", now, now);
        Assert.Contains("last hop 21 ms", text);
        Assert.DoesNotContain("Riot", text);
    }

    private static RouteRow Row(string upstream, string last) => new()
    {
        Timestamp = "2026-10-11 12:00:00",
        LeagueRunning = "N",
        Upstream = upstream,
        LastHopAvg = last,
        LastHopLossPct = "0",
        RiotHopAvg = "",
        RiotHopLossPct = "100",
        FullPath = "1.1.1.1 > 192.207.0.5",
    };
}
