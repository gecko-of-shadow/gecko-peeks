using System.Globalization;
using GeckoPeeks.Logging;

namespace GeckoPeeks.Text;

public enum StatusTone
{
    Normal,
    Good,
    Warn,
    Bad,
}

/// <summary>
/// Words for the Logging chip and the LOCAL LOG status line. One short line per state: the CSV
/// path has its own line, and raw exception text never reaches the window (Controlled folder
/// access surfaces as "Could not find file", which reads like a missing file).
/// </summary>
public static class LogStatusText
{
    public const string BlockedReason =
        "Blocked: Windows refused to save the log. If Controlled folder access is on, "
        + "allow GeckoPeeks.exe under Ransomware protection.";

    public const string NotSavedNote = "Row not saved: the log folder is blocked. See LOCAL LOG.";

    public const string BlockedAtStartNote = "The log folder is blocked. See LOCAL LOG.";

    private const string Protected = "Controlled folder access protects Documents";

    /// <summary>A save that failed everywhere outranks Paused: the user needs to act on it.</summary>
    public static (string Text, StatusTone Tone) LoggingChip(bool paused, LogHealth health)
    {
        if (health == LogHealth.Blocked)
            return ("Blocked", StatusTone.Bad);
        return paused ? ("Paused", StatusTone.Warn) : ("On", StatusTone.Good);
    }

    public static string Saved(DateTime savedAt, AppendResult where) =>
        where == AppendResult.Pending
            ? $"Saved row at {Clock(savedAt)} to {CsvLog.PendingFileName}: the main CSV is open in another app. It merges on the next save."
            : $"Saved row at {Clock(savedAt)}.";

    public static string Waiting(string? lastCsvTimestamp) =>
        string.IsNullOrWhiteSpace(lastCsvTimestamp)
            ? "Waiting for the first row."
            : $"Last row {lastCsvTimestamp}. Waiting for the next test.";

    /// <summary>Why the log is outside Documents; empty when it is not.</summary>
    public static string Location(bool usingFallback, bool movedThisSession, int copiedRows)
    {
        if (!usingFallback)
            return "";
        if (!movedThisSession)
            return $"Logging here because {Protected}.";
        return copiedRows > 0
            ? $"{Protected}, so the log moved here ({Rows(copiedRows)} copied)."
            : $"{Protected}, so the log moved here.";
    }

    /// <summary>Activity line for the move. The full path is on the LOCAL LOG File line.</summary>
    public static string MovedNote(int copiedRows) =>
        copiedRows > 0
            ? $"Log moved to AppData because {Protected} ({Rows(copiedRows)} copied)."
            : $"Log moved to AppData because {Protected}.";

    public static string Unsaved(int rows) => rows switch
    {
        <= 0 => "",
        1 => "1 row is waiting to save.",
        _ => $"{rows} rows are waiting to save.",
    };

    /// <summary>
    /// The single LOCAL LOG status line: blocked beats a read problem beats the last write.
    /// </summary>
    public static (string Text, StatusTone Tone) StripLine(
        LogHealth health,
        string? writeStatus,
        string? readProblem,
        string? location,
        int unsavedRows)
    {
        if (health == LogHealth.Blocked)
            return (Join(BlockedReason, Unsaved(unsavedRows)), StatusTone.Bad);
        if (!string.IsNullOrWhiteSpace(readProblem))
            return (readProblem.Trim(), StatusTone.Warn);

        var status = string.IsNullOrWhiteSpace(writeStatus) ? Waiting(null) : writeStatus.Trim();
        var tone = health switch
        {
            LogHealth.Saved => StatusTone.Good,
            LogHealth.Pending => StatusTone.Warn,
            _ => StatusTone.Normal,
        };
        return (Join(status, location), tone);
    }

    private static string Rows(int count) => count == 1 ? "1 earlier row" : $"{count} earlier rows";

    private static string Clock(DateTime local) => local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Join(string first, string? second) =>
        string.IsNullOrWhiteSpace(second) ? first : first + " " + second.Trim();
}
