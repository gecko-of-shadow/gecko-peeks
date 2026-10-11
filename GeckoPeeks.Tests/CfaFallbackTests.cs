using GeckoPeeks.App;
using GeckoPeeks.Logging;
using GeckoPeeks.Text;
using Xunit;

namespace GeckoPeeks.Tests;

/// <summary>
/// The v1.0.8 window: Controlled folder access refused Documents\RiotRouteLog, .NET reported
/// the refused pending file as "Could not find file ...riot-route-log.pending.csv", and that text
/// showed in red while the Logging chip still said On.
/// </summary>
public class MissingPendingTests
{
    [Fact]
    public void No_pending_file_is_normal_and_never_an_error()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary);

        Assert.False(File.Exists(log.PendingPath));
        Assert.Empty(log.ReadAll());
        Assert.Null(log.ReadProblem);

        Assert.Equal(AppendResult.Main, log.Append(CfaFolders.Row("Zayo", "12:00:00")));
        Assert.Equal(LogHealth.Saved, log.Health);
        Assert.Null(log.LastWriteError);
        Assert.Null(log.ReadProblem);
        Assert.False(File.Exists(log.PendingPath));
        Assert.Single(log.ReadAll());
    }

    [Fact]
    public void Pending_file_removed_before_the_merge_is_a_no_op()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary);
        Assert.Equal(AppendResult.Main, log.Append(CfaFolders.Row("Zayo", "12:00:00")));
        Assert.Equal(AppendResult.Pending, new CsvLog(folders.Primary, () => true).Append(CfaFolders.Row("TELUS", "12:01:00")));

        File.Delete(log.PendingPath);

        Assert.Equal(AppendResult.Main, log.Append(CfaFolders.Row("Cogent", "12:02:00")));
        Assert.Null(log.LastWriteError);
        Assert.Null(log.ReadProblem);
        Assert.Equal(new[] { "Zayo", "Cogent" }, log.ReadAll().Select(r => r.Upstream));
    }

    [Fact]
    public void Refused_pending_file_never_surfaces_file_not_found_text()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary, writeFault: folders.RefusePrimary);

        var ex = Assert.ThrowsAny<IOException>(() => log.Append(CfaFolders.Row("Zayo", "12:00:00")));

        Assert.Equal(LogHealth.Blocked, log.Health);
        foreach (var text in new[] { ex.Message, log.LastWriteError!, StripText(log) })
        {
            Assert.DoesNotContain("Could not find file", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CsvLog.PendingFileName, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(folders.Primary, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    private static string StripText(CsvLog log) =>
        LogStatusText.StripLine(log.Health, "", log.ReadProblem, "", log.UnsavedRows).Text;
}

public class ControlledFolderAccessTests
{
    [Fact]
    public void Startup_moves_a_refused_documents_log_to_appdata_with_its_rows()
    {
        using var folders = new CfaFolders();
        folders.SeedPrimary("Zayo", "TELUS");
        var documentsBytes = File.ReadAllBytes(Path.Combine(folders.Primary, CsvLog.MainFileName));

        var log = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback, writeFault: folders.RefusePrimary);
        log.PrepareFolder();

        Assert.True(log.UsingFallback);
        Assert.Equal(folders.Fallback, log.Folder);
        Assert.Equal(Path.Combine(folders.Fallback, CsvLog.MainFileName), log.MainPath);
        Assert.True(log.MovedThisSession);
        Assert.Equal(2, log.CopiedRows);
        Assert.Equal(LogHealth.Waiting, log.Health);
        Assert.Null(log.LastWriteError);
        Assert.Equal(new[] { "Zayo", "TELUS" }, log.ReadAll().Select(r => r.Upstream));
        Assert.Equal(documentsBytes, File.ReadAllBytes(Path.Combine(folders.Primary, CsvLog.MainFileName)));
    }

    [Fact]
    public void First_refused_write_lands_in_appdata_after_the_earlier_rows()
    {
        using var folders = new CfaFolders();
        folders.SeedPrimary("Zayo", "TELUS");
        var log = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback, writeFault: folders.RefusePrimary);

        Assert.Equal(AppendResult.Main, log.Append(CfaFolders.Row("Cogent", "12:05:00")));

        Assert.True(log.UsingFallback);
        Assert.Equal(LogHealth.Saved, log.Health);
        Assert.Null(log.LastWriteError);
        Assert.Equal(0, log.UnsavedRows);
        Assert.Equal(2, log.CopiedRows);
        Assert.Equal(new[] { "Zayo", "TELUS", "Cogent" }, log.ReadAll().Select(r => r.Upstream));
        Assert.True(File.Exists(log.BackupPath));
        Assert.False(File.Exists(Path.Combine(folders.Primary, CsvLog.PendingFileName)));
    }

    [Fact]
    public void Later_runs_start_in_appdata_and_never_write_documents_again()
    {
        using var folders = new CfaFolders();
        folders.SeedPrimary("Zayo");
        var first = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback, writeFault: folders.RefusePrimary);
        first.PrepareFolder();
        first.Append(CfaFolders.Row("TELUS", "12:05:00"));

        // Documents writable again (exe allowed): the log still stays in one place.
        var primaryWrites = 0;
        var next = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback, writeFault: path =>
        {
            if (path.StartsWith(folders.Primary, StringComparison.Ordinal))
                primaryWrites++;
            return null;
        });
        next.PrepareFolder();
        next.Append(CfaFolders.Row("Cogent", "12:20:00"));

        Assert.True(next.UsingFallback);
        Assert.False(next.MovedThisSession);
        Assert.Equal(0, primaryWrites);
        Assert.Equal(new[] { "Zayo", "TELUS", "Cogent" }, next.ReadAll().Select(r => r.Upstream));
        Assert.Equal(1, CfaFolders.CountOf(File.ReadAllText(next.MainPath), "Zayo"));
    }

    [Fact]
    public void Both_folders_refused_is_blocked_with_one_short_message()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback, writeFault: folders.RefuseBoth);
        log.PrepareFolder();

        Assert.Equal(LogHealth.Blocked, log.Health);
        Assert.Equal(LogStatusText.BlockedReason, log.LastWriteError);

        var ex = Assert.ThrowsAny<IOException>(() => log.Append(CfaFolders.Row("Zayo", "12:00:00")));
        Assert.Equal(LogStatusText.BlockedReason, ex.Message);
        Assert.Equal(1, log.UnsavedRows);
        Assert.False(log.UsingFallback);
    }

    [Fact]
    public void Unsaved_rows_are_written_in_order_once_a_folder_accepts_them()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback, writeFault: folders.RefuseBoth);
        Assert.ThrowsAny<IOException>(() => log.Append(CfaFolders.Row("Zayo", "12:00:00")));
        Assert.ThrowsAny<IOException>(() => log.Append(CfaFolders.Row("TELUS", "12:15:00")));
        Assert.Equal(2, log.UnsavedRows);

        folders.Refusing = false;
        Assert.Equal(AppendResult.Main, log.Append(CfaFolders.Row("Cogent", "12:30:00")));

        Assert.Equal(LogHealth.Saved, log.Health);
        Assert.Null(log.LastWriteError);
        Assert.Equal(0, log.UnsavedRows);
        Assert.Equal(new[] { "Zayo", "TELUS", "Cogent" }, log.ReadAll().Select(r => r.Upstream));
    }

    [Fact]
    public void Excel_lock_still_uses_the_pending_file_not_appdata()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary, mainUnavailable: () => true, fallbackFolder: folders.Fallback);

        Assert.Equal(AppendResult.Pending, log.Append(CfaFolders.Row("Zayo", "12:00:00")));

        Assert.False(log.UsingFallback);
        Assert.Equal(LogHealth.Pending, log.Health);
        Assert.Null(log.LastWriteError);
        Assert.True(File.Exists(Path.Combine(folders.Primary, CsvLog.PendingFileName)));
        Assert.False(Directory.Exists(folders.Fallback));
    }

    [Fact]
    public void Writable_documents_stays_put_and_leaves_no_probe_file()
    {
        using var folders = new CfaFolders();
        var log = new CsvLog(folders.Primary, fallbackFolder: folders.Fallback);
        log.PrepareFolder();
        log.Append(CfaFolders.Row("Zayo", "12:00:00"));

        Assert.False(log.UsingFallback);
        Assert.Equal(folders.Primary, log.Folder);
        Assert.False(Directory.Exists(folders.Fallback));
        Assert.DoesNotContain(Directory.GetFiles(folders.Primary), path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Fallback_is_geckopeeks_riotroutelog_under_local_appdata()
    {
        var documents = Path.Combine("Users", "gecko", "Documents");
        var local = Path.Combine("Users", "gecko", "AppData", "Local");
        var paths = LogPaths.For(documents, local);

        Assert.Equal(Path.Combine(documents, "RiotRouteLog"), paths.Folder);
        Assert.Equal(Path.Combine(local, "GeckoPeeks", "RiotRouteLog"), paths.FallbackFolder);
        Assert.Equal(Path.Combine(local, "GeckoPeeks", "RiotRouteLog"), LogPaths.For("", local).Folder);
    }

    [Fact]
    public void Activity_history_follows_the_log_folder()
    {
        using var folders = new CfaFolders();
        Directory.CreateDirectory(folders.Fallback);
        var activity = new ActivityLog(Path.Combine(folders.Primary, "activity.log"));
        activity.Write("Starting the first test.");

        activity.MoveTo(Path.Combine(folders.Fallback, "activity.log"));
        activity.Write("Log moved.");

        Assert.Equal(2, activity.Recent().Count);
        Assert.Contains("Log moved.", File.ReadAllText(Path.Combine(folders.Fallback, "activity.log")));
        Assert.DoesNotContain("Log moved.", File.ReadAllText(Path.Combine(folders.Primary, "activity.log")));
    }
}

public class LogStatusTextTests
{
    [Theory]
    [InlineData(false, LogHealth.Waiting, "On", StatusTone.Good)]
    [InlineData(false, LogHealth.Saved, "On", StatusTone.Good)]
    [InlineData(false, LogHealth.Pending, "On", StatusTone.Good)]
    [InlineData(true, LogHealth.Saved, "Paused", StatusTone.Warn)]
    [InlineData(false, LogHealth.Blocked, "Blocked", StatusTone.Bad)]
    [InlineData(true, LogHealth.Blocked, "Blocked", StatusTone.Bad)]
    public void Logging_chip_says_blocked_in_coral_only_when_writes_fail(bool paused, LogHealth health, string text, StatusTone tone) =>
        Assert.Equal((text, tone), LogStatusText.LoggingChip(paused, health));

    [Fact]
    public void Blocked_line_is_short_one_line_and_names_the_fix()
    {
        var (text, tone) = LogStatusText.StripLine(LogHealth.Blocked, "Saved row at 12:00:00.", "", "", 2);

        Assert.Equal(StatusTone.Bad, tone);
        Assert.StartsWith("Blocked:", text, StringComparison.Ordinal);
        Assert.Contains("Controlled folder access", text, StringComparison.Ordinal);
        Assert.Contains("GeckoPeeks.exe", text, StringComparison.Ordinal);
        Assert.Contains("Ransomware protection", text, StringComparison.Ordinal);
        Assert.EndsWith("2 rows are waiting to save.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.True(text.Length <= 160, $"{text.Length} chars");
    }

    [Fact]
    public void Moved_line_says_why_and_how_many_rows_came_along()
    {
        var location = LogStatusText.Location(usingFallback: true, movedThisSession: true, copiedRows: 42);
        var (text, tone) = LogStatusText.StripLine(LogHealth.Saved, LogStatusText.Saved(new DateTime(2026, 10, 11, 10, 15, 2), AppendResult.Main), "", location, 0);

        Assert.Equal(StatusTone.Good, tone);
        Assert.Equal(
            "Saved row at 10:15:02. Controlled folder access protects Documents, so the log moved here (42 earlier rows copied).",
            text);
        Assert.Equal("Logging here because Controlled folder access protects Documents.", LogStatusText.Location(true, false, 0));
        Assert.Equal("", LogStatusText.Location(false, false, 0));
        Assert.Contains("1 earlier row copied", LogStatusText.MovedNote(1), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_lines_carry_no_paths_and_fit_two_lines()
    {
        var at = new DateTime(2026, 10, 11, 10, 15, 2);
        var lines = new[]
        {
            LogStatusText.StripLine(LogHealth.Saved, LogStatusText.Saved(at, AppendResult.Main), "", LogStatusText.Location(true, true, 12345), 0).Text,
            LogStatusText.StripLine(LogHealth.Pending, LogStatusText.Saved(at, AppendResult.Pending), "", "", 0).Text,
            LogStatusText.StripLine(LogHealth.Waiting, LogStatusText.Waiting("2026-10-11 10:00:00"), "", LogStatusText.Location(true, false, 0), 0).Text,
            LogStatusText.StripLine(LogHealth.Blocked, "", "", "", CsvLog.MaxUnsavedRows).Text,
            LogStatusText.MovedNote(12345),
            LogStatusText.NotSavedNote,
        };

        foreach (var line in lines)
        {
            Assert.DoesNotContain(":\\", line, StringComparison.Ordinal);
            Assert.DoesNotContain("AppData\\", line, StringComparison.Ordinal);
            Assert.True(line.Length <= 200, $"{line.Length} chars: {line}");
        }
    }

    [Theory]
    [InlineData(LogHealth.Waiting, StatusTone.Normal)]
    [InlineData(LogHealth.Saved, StatusTone.Good)]
    [InlineData(LogHealth.Pending, StatusTone.Warn)]
    public void Write_states_other_than_blocked_are_never_coral(LogHealth health, StatusTone expected)
    {
        var (_, tone) = LogStatusText.StripLine(health, "status", "", "", 0);
        Assert.Equal(expected, tone);
    }

    [Fact]
    public void Read_problem_beats_the_last_write_but_not_a_block()
    {
        Assert.Equal(
            ("Main log is locked. Showing the backup copy.", StatusTone.Warn),
            LogStatusText.StripLine(LogHealth.Saved, "Saved row at 10:15:02.", "Main log is locked. Showing the backup copy.", "", 0));
        Assert.Equal(StatusTone.Bad, LogStatusText.StripLine(LogHealth.Blocked, "", "Main log is locked.", "", 0).Tone);
    }
}

internal sealed class CfaFolders : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));

    public CfaFolders()
    {
        Primary = Path.Combine(_root, "Documents", "RiotRouteLog");
        Fallback = Path.Combine(_root, "AppData", "Local", "GeckoPeeks", "RiotRouteLog");
        Directory.CreateDirectory(Primary);
    }

    public string Primary { get; }
    public string Fallback { get; }
    public bool Refusing { get; set; } = true;

    /// <summary>What .NET sees when Controlled folder access refuses a create in a protected folder.</summary>
    public Exception? RefusePrimary(string path) => Refusing && Inside(path, Primary) ? Refused(path) : null;

    public Exception? RefuseBoth(string path) =>
        Refusing && (Inside(path, Primary) || Inside(path, Fallback)) ? Refused(path) : null;

    public void SeedPrimary(params string[] upstreams)
    {
        var log = new CsvLog(Primary);
        for (var i = 0; i < upstreams.Length; i++)
            log.Append(Row(upstreams[i], $"11:{i:00}:00"));
    }

    public static RouteRow Row(string upstream, string time) => new()
    {
        Timestamp = "2026-10-11 " + time,
        LeagueRunning = "N",
        Upstream = upstream,
        LastHopAvg = "21",
        RiotHopLossPct = "100",
        FullPath = "1.1.1.1 > 192.207.0.5",
    };

    public static int CountOf(string text, string token)
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

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp only.
        }
    }

    private static bool Inside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static Exception Refused(string path) => new FileNotFoundException($"Could not find file '{path}'.", path);
}
