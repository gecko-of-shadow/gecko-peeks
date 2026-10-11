using System.Text;
using GeckoPeeks.Text;

namespace GeckoPeeks.Logging;

public enum AppendResult
{
    Main,
    Pending,
}

/// <summary>What the last write left behind. Only <see cref="Blocked"/> is a problem.</summary>
public enum LogHealth
{
    /// <summary>Nothing written since launch.</summary>
    Waiting,

    /// <summary>The last row reached the main CSV.</summary>
    Saved,

    /// <summary>The main CSV was locked (Excel), so the last row went to the pending file.</summary>
    Pending,

    /// <summary>Windows refused every folder Gecko Peeks may write to.</summary>
    Blocked,
}

/// <summary>
/// Append-only route log. The main CSV is never truncated or rotated.
/// A locked main file (Excel) sends the finished row to the pending file,
/// and the next successful open merges those rows back in.
/// When Windows refuses writes in the primary folder (Controlled folder access protects
/// Documents), the log moves once to the fallback folder with the earlier rows and stays
/// there. Files in the primary folder are only read, never changed.
/// </summary>
public sealed class CsvLog
{
    public const string MainFileName = "riot-route-log.csv";
    public const string PendingFileName = "riot-route-log.pending.csv";
    public const string BackupFileName = "riot-route-log.backup.csv";

    /// <summary>About ten days of 15-minute tests.</summary>
    public const int MaxUnsavedRows = 1000;

    private const string ProbeFileName = ".gecko-peeks-write-test.tmp";
    private static readonly TimeSpan BackupMinInterval = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Func<bool>? _mainUnavailable;
    private readonly Func<string, Exception?>? _writeFault;
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly List<string> _unsaved = new();
    private DateTime _lastBackupUtc = DateTime.MinValue;
    private bool _forceBackup;
    private volatile int _unsavedCount;

    /// <param name="folder">Primary folder (Documents\RiotRouteLog).</param>
    /// <param name="mainUnavailable">Test seam: report the main CSV as locked.</param>
    /// <param name="fallbackFolder">Where the log moves when Windows refuses <paramref name="folder"/>.</param>
    /// <param name="writeFault">
    /// Test seam: an exception to throw instead of writing a path, the way Controlled folder access refuses it.
    /// </param>
    public CsvLog(
        string folder,
        Func<bool>? mainUnavailable = null,
        string? fallbackFolder = null,
        Func<string, Exception?>? writeFault = null)
    {
        PrimaryFolder = folder;
        FallbackFolder = string.IsNullOrWhiteSpace(fallbackFolder) || SamePath(folder, fallbackFolder)
            ? null
            : fallbackFolder;
        _mainUnavailable = mainUnavailable;
        _writeFault = writeFault;

        // A main CSV in the fallback means an earlier run already moved there. Staying keeps
        // every row in one file even if Documents becomes writable again.
        Folder = FallbackFolder != null && File.Exists(Path.Combine(FallbackFolder, MainFileName))
            ? FallbackFolder
            : folder;
    }

    public string PrimaryFolder { get; }

    public string? FallbackFolder { get; }

    /// <summary>The folder in use: the primary folder, or the fallback once the log has moved.</summary>
    public string Folder { get; private set; }

    public bool UsingFallback =>
        FallbackFolder != null && string.Equals(Folder, FallbackFolder, StringComparison.OrdinalIgnoreCase);

    public string MainPath => Path.Combine(Folder, MainFileName);
    public string PendingPath => Path.Combine(Folder, PendingFileName);
    public string BackupPath => Path.Combine(Folder, BackupFileName);
    public string MergingPath => PendingPath + ".merging";

    public string? ReadProblem { get; private set; }
    public string? BackupProblem { get; private set; }

    /// <summary>Local time of the last successful main or pending append.</summary>
    public DateTime? LastSuccessfulWriteLocal { get; private set; }

    /// <summary>Full path written on the last successful append (main or pending).</summary>
    public string? LastSuccessfulWritePath { get; private set; }

    public AppendResult? LastAppendResult { get; private set; }

    public LogHealth Health { get; private set; } = LogHealth.Waiting;

    /// <summary>
    /// Short reason shown while the log is blocked; null otherwise. Never raw exception text:
    /// Controlled folder access reports a refused create as "Could not find file", which reads
    /// like a missing file rather than a block.
    /// </summary>
    public string? LastWriteError { get; private set; }

    /// <summary>Finished rows still waiting for a folder that accepts them.</summary>
    public int UnsavedRows => _unsavedCount;

    /// <summary>True when this run moved the log from the primary folder to the fallback.</summary>
    public bool MovedThisSession { get; private set; }

    /// <summary>Earlier rows copied into the fallback by that move.</summary>
    public int CopiedRows { get; private set; }

    /// <summary>
    /// Startup check with a throwaway file, so a refused folder is found before the first test
    /// and the window shows the folder that will really hold the log.
    /// </summary>
    public void PrepareFolder()
    {
        lock (_gate)
        {
            if (CanCreateFileIn(Folder))
                return;

            if (!UsingFallback && FallbackFolder != null)
            {
                try
                {
                    var copied = CopyHistoryInto(FallbackFolder);
                    if (CanCreateFileIn(FallbackFolder))
                    {
                        MoveTo(FallbackFolder, copied);
                        return;
                    }
                }
                catch (Exception ex) when (IsWriteFailure(ex))
                {
                    // The fallback refused too.
                }
            }

            MarkBlocked();
        }
    }

    public AppendResult Append(RouteRow row)
    {
        var line = CsvFormat.FormatLine(row);
        lock (_gate)
        {
            // Rows from blocked attempts go first so the file stays in time order.
            var payload = string.Concat(_unsaved) + line;
            if (TryAppendIn(Folder, payload, out var result, out var failure))
                return Saved(result);

            if (!UsingFallback && FallbackFolder != null)
            {
                try
                {
                    var copied = CopyHistoryInto(FallbackFolder);
                    if (TryAppendIn(FallbackFolder, payload, out result, out failure))
                    {
                        MoveTo(FallbackFolder, copied);
                        return Saved(result);
                    }
                }
                catch (Exception ex) when (IsWriteFailure(ex))
                {
                    failure = ex;
                }
            }

            _unsaved.Add(line);
            if (_unsaved.Count > MaxUnsavedRows)
                _unsaved.RemoveAt(0);
            _unsavedCount = _unsaved.Count;
            MarkBlocked();
            throw new IOException(LastWriteError, failure);
        }
    }

    public IReadOnlyList<RouteRow> ReadAll()
    {
        // Shared reads do not need the writer lock. Avoid blocking the UI behind
        // an in-flight append that is flushing and refreshing the backup.
        var folder = Folder;
        var rows = new List<RouteRow>();
        string? problem = null;
        try
        {
            rows.AddRange(CsvFormat.Parse(ReadShared(Path.Combine(folder, MainFileName))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                rows.AddRange(CsvFormat.Parse(ReadShared(Path.Combine(folder, BackupFileName))));
                problem = "Main log is locked. Showing the backup copy.";
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                problem = "The log is locked and the backup could not be read.";
            }
        }

        rows.AddRange(ReadPendingRows(folder));
        ReadProblem = problem;
        return rows;
    }

    private AppendResult Saved(AppendResult result)
    {
        _unsaved.Clear();
        _unsavedCount = 0;
        LastAppendResult = result;
        LastSuccessfulWriteLocal = DateTime.Now;
        LastSuccessfulWritePath = result == AppendResult.Main ? MainPath : PendingPath;
        LastWriteError = null;
        Health = result == AppendResult.Main ? LogHealth.Saved : LogHealth.Pending;
        return result;
    }

    private void MarkBlocked()
    {
        Health = LogHealth.Blocked;
        LastWriteError = LogStatusText.BlockedReason;
    }

    private void MoveTo(string folder, int copiedRows)
    {
        Folder = folder;
        MovedThisSession = true;
        CopiedRows = copiedRows;
        _forceBackup = true;
    }

    /// <summary>Main CSV first; a locked or refused main sends the rows to the pending file.</summary>
    private bool TryAppendIn(string folder, string payload, out AppendResult result, out Exception? failure)
    {
        result = AppendResult.Main;
        failure = null;
        try
        {
            CreateFolder(folder);
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            failure = ex;
            return false;
        }

        if (!(_mainUnavailable?.Invoke() ?? false))
        {
            try
            {
                WriteMain(folder, payload);
                result = AppendResult.Main;
                return true;
            }
            catch (Exception ex) when (IsWriteFailure(ex))
            {
                failure = ex;
            }
        }

        try
        {
            WritePending(folder, payload);
            result = AppendResult.Pending;
            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            failure = ex;
            return false;
        }
    }

    private void WriteMain(string folder, string payload)
    {
        using var stream = OpenForWrite(Path.Combine(folder, MainFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite);
        MergePending(folder, stream);
        if (stream.Length == 0)
            WriteFull(stream, Header());
        WriteFull(stream, _utf8.GetBytes(payload));
        RefreshBackup(folder, stream);
    }

    private void WritePending(string folder, string payload)
    {
        using var stream = OpenForWrite(Path.Combine(folder, PendingFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite);
        WriteFull(stream, _utf8.GetBytes(payload));
    }

    /// <summary>
    /// Starts the fallback log with every row the primary folder holds. Controlled folder access
    /// still allows reads, so the Documents rows come along. Returns the number of rows copied
    /// (0 when the fallback already has a log).
    /// </summary>
    private int CopyHistoryInto(string folder)
    {
        CreateFolder(folder);
        var main = Path.Combine(folder, MainFileName);
        if (File.Exists(main) && new FileInfo(main).Length > 0)
            return 0;

        var lines = new List<string>();
        lines.AddRange(DataLines(ReadPrimary(MainFileName, orBackup: true)));
        lines.AddRange(DataLines(ReadPrimary(PendingFileName + ".merging", orBackup: false)));
        lines.AddRange(DataLines(ReadPrimary(PendingFileName, orBackup: false)));

        var temp = main + ".copy";
        WriteBytesDurable(temp, Header().Concat(_utf8.GetBytes(string.Concat(lines))).ToArray());
        File.Move(temp, main, overwrite: true);
        return lines.Count;
    }

    private string ReadPrimary(string fileName, bool orBackup)
    {
        try
        {
            return ReadShared(Path.Combine(PrimaryFolder, fileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!orBackup)
                return "";
            try
            {
                return ReadShared(Path.Combine(PrimaryFolder, BackupFileName));
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }

    private bool CanCreateFileIn(string folder)
    {
        try
        {
            CreateFolder(folder);
            using (OpenForWrite(Path.Combine(folder, ProbeFileName), FileMode.Create, FileAccess.Write, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            return false;
        }
    }

    private void MergePending(string folder, FileStream main)
    {
        var pending = Path.Combine(folder, PendingFileName);
        var merging = pending + ".merging";

        // Move pending aside first so a crash cannot append the same lines twice
        // while also leaving them out of the main file.
        try
        {
            if (File.Exists(pending) && File.Exists(merging))
            {
                var combined = ReadShared(merging) + ReadShared(pending);
                var temp = merging + ".next";
                WriteBytesDurable(temp, _utf8.GetBytes(combined));
                File.Move(temp, merging, overwrite: true);
                try
                {
                    File.Delete(pending);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Merging copy has the payload; pending can be cleaned up later.
                }
            }
            else if (File.Exists(pending))
            {
                File.Move(pending, merging);
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // No pending file, or it went away after the check. Nothing to merge.
        }

        if (!File.Exists(merging))
            return;

        var lines = DataLines(ReadShared(merging));
        if (lines.Count == 0)
        {
            File.Delete(merging);
            return;
        }

        if (main.Length == 0)
            WriteFull(main, Header());

        // One write for every pending row. If it fails, WriteFull rolls the file back
        // and the merging file is still intact. If it succeeds and we crash before
        // deleting the merging file, the next open sees the payload already at the
        // end of the main file and does not write it again.
        var payload = string.Concat(lines);
        main.Position = 0;
        using (var reader = new StreamReader(main, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            var existing = reader.ReadToEnd();
            if (!existing.EndsWith(payload, StringComparison.Ordinal))
                WriteFull(main, _utf8.GetBytes(payload));
        }

        File.Delete(merging);
        _forceBackup = true;
    }

    private static List<string> DataLines(string text)
    {
        var lines = new List<string>();
        foreach (var record in CsvFormat.ParseRecords(text))
        {
            if (record.Count == 0)
                continue;
            if (record.Count == 1 && string.IsNullOrWhiteSpace(record[0]))
                continue;
            if (string.Equals(record[0], "Timestamp", StringComparison.Ordinal))
                continue;

            var builder = new StringBuilder();
            for (var i = 0; i < record.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(CsvFormat.Escape(record[i]));
            }

            builder.Append("\r\n");
            lines.Add(builder.ToString());
        }

        return lines;
    }

    private static List<RouteRow> ReadPendingRows(string folder)
    {
        var rows = new List<RouteRow>();
        var pending = Path.Combine(folder, PendingFileName);
        foreach (var path in new[] { pending, pending + ".merging" })
        {
            try
            {
                rows.AddRange(CsvFormat.Parse(ReadShared(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The pending file is busy. The rows are still on disk for the next merge.
            }
        }

        return rows;
    }

    private void RefreshBackup(string folder, FileStream main)
    {
        // The row is already flushed. A backup failure must not send the same row to pending.
        // Full-file copy is throttled so OneDrive / AV do not rewrite the whole log every append.
        var backup = Path.Combine(folder, BackupFileName);
        try
        {
            main.Flush(true);
            var now = DateTime.UtcNow;
            var missing = !File.Exists(backup);
            if (!missing && !_forceBackup && now - _lastBackupUtc < BackupMinInterval)
            {
                main.Seek(0, SeekOrigin.End);
                return;
            }

            var temp = backup + ".tmp";
            using (var dest = OpenForWrite(temp, FileMode.Create, FileAccess.Write))
            {
                main.Position = 0;
                main.CopyTo(dest);
                dest.Flush(true);
            }

            main.Seek(0, SeekOrigin.End);
            File.Move(temp, backup, overwrite: true);
            _lastBackupUtc = now;
            _forceBackup = false;
            BackupProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                main.Seek(0, SeekOrigin.End);
            }
            catch
            {
                // The main handle is still the durable copy.
            }

            BackupProblem = ex.Message;
        }
    }

    private byte[] Header() =>
        Encoding.UTF8.GetPreamble().Concat(_utf8.GetBytes(CsvFormat.HeaderLine)).ToArray();

    private void CreateFolder(string folder)
    {
        ThrowIfRefused(folder);
        Directory.CreateDirectory(folder);
    }

    private FileStream OpenForWrite(string path, FileMode mode, FileAccess access, FileOptions options = FileOptions.None)
    {
        ThrowIfRefused(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return new FileStream(path, mode, access, FileShare.None, 4096, options);
    }

    private void ThrowIfRefused(string path)
    {
        var refusal = _writeFault?.Invoke(path);
        if (refusal != null)
            throw refusal;
    }

    private static bool IsWriteFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static bool SamePath(string a, string b)
    {
        try
        {
            var left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            var right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Writes one complete buffer and flushes it to disk. On failure the file
    /// is put back to the length it had before this buffer, so a row is never torn.
    /// </summary>
    private static void WriteFull(FileStream stream, byte[] bytes)
    {
        var start = stream.Length;
        stream.Seek(0, SeekOrigin.End);
        try
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        catch
        {
            try
            {
                stream.SetLength(start);
                stream.Flush(true);
            }
            catch
            {
                // The previous complete rows are still the durable prefix.
            }

            throw;
        }
    }

    private void WriteBytesDurable(string path, byte[] bytes)
    {
        using var stream = OpenForWrite(path, FileMode.Create, FileAccess.Write);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    /// <summary>A missing file reads as empty: no pending file is the normal case, not an error.</summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
    }
}
