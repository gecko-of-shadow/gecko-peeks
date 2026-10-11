using System.Text;

namespace GeckoPeeks.Logging;

public enum AppendResult
{
    Main,
    Pending,
}

/// <summary>
/// Append-only route log. The main CSV is never truncated or rotated.
/// A locked main file (Excel) sends the finished row to the pending file,
/// and the next successful open merges those rows back in.
/// </summary>
public sealed class CsvLog
{
    private readonly object _gate = new();
    private readonly Func<bool>? _mainUnavailable;
    private readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public CsvLog(string folder, Func<bool>? mainUnavailable = null)
    {
        Folder = folder;
        _mainUnavailable = mainUnavailable;
    }

    public string Folder { get; }
    public string MainPath => Path.Combine(Folder, "riot-route-log.csv");
    public string PendingPath => Path.Combine(Folder, "riot-route-log.pending.csv");
    public string BackupPath => Path.Combine(Folder, "riot-route-log.backup.csv");
    public string MergingPath => PendingPath + ".merging";

    public string? ReadProblem { get; private set; }
    public string? BackupProblem { get; private set; }

    public AppendResult Append(RouteRow row)
    {
        var line = CsvFormat.FormatLine(row);
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var unavailable = _mainUnavailable?.Invoke() ?? false;
            if (!unavailable)
            {
                try
                {
                    WriteMain(line);
                    return AppendResult.Main;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unavailable = true;
                }
            }

            if (!unavailable)
                return AppendResult.Main;

            WritePending(line);
            return AppendResult.Pending;
        }
    }

    public IReadOnlyList<RouteRow> ReadAll()
    {
        lock (_gate)
        {
            var rows = new List<RouteRow>();
            try
            {
                rows.AddRange(CsvFormat.Parse(ReadShared(MainPath)));
                ReadProblem = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    rows.AddRange(CsvFormat.Parse(ReadShared(BackupPath)));
                    ReadProblem = "Main log is locked. Showing the backup copy.";
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    ReadProblem = "The log is locked and the backup could not be read.";
                }
            }

            rows.AddRange(ReadPendingRows());
            return rows;
        }
    }

    private void WriteMain(string line)
    {
        using var stream = OpenExclusive(MainPath);
        MergePending(stream);
        if (stream.Length == 0)
            WriteFull(stream, Encoding.UTF8.GetPreamble().Concat(_utf8.GetBytes(CsvFormat.HeaderLine)).ToArray());
        WriteFull(stream, _utf8.GetBytes(line));
        RefreshBackup(stream);
    }

    private void WritePending(string line)
    {
        using var stream = OpenExclusive(PendingPath);
        WriteFull(stream, _utf8.GetBytes(line));
    }

    private void MergePending(FileStream main)
    {
        // Move pending aside first so a crash cannot append the same lines twice
        // while also leaving them out of the main file.
        if (File.Exists(PendingPath) && File.Exists(MergingPath))
        {
            var combined = File.ReadAllText(MergingPath) + File.ReadAllText(PendingPath);
            var temp = MergingPath + ".next";
            WriteBytesDurable(temp, _utf8.GetBytes(combined));
            File.Move(temp, MergingPath, overwrite: true);
            File.Delete(PendingPath);
        }
        else if (File.Exists(PendingPath))
        {
            File.Move(PendingPath, MergingPath);
        }

        if (!File.Exists(MergingPath))
            return;

        var lines = DataLines(File.ReadAllText(MergingPath));
        if (lines.Count == 0)
        {
            File.Delete(MergingPath);
            return;
        }

        if (main.Length == 0)
            WriteFull(main, Encoding.UTF8.GetPreamble().Concat(_utf8.GetBytes(CsvFormat.HeaderLine)).ToArray());

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

        File.Delete(MergingPath);
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

    private List<RouteRow> ReadPendingRows()
    {
        var rows = new List<RouteRow>();
        foreach (var path in new[] { PendingPath, MergingPath })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                rows.AddRange(CsvFormat.Parse(File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The pending file is busy. The rows are still on disk for the next merge.
            }
        }

        return rows;
    }

    private void RefreshBackup(FileStream main)
    {
        // The row is already flushed. A backup failure must not send the same row to pending.
        try
        {
            main.Flush(true);
            var temp = BackupPath + ".tmp";
            using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                main.Position = 0;
                main.CopyTo(dest);
                dest.Flush(true);
            }

            main.Seek(0, SeekOrigin.End);
            File.Move(temp, BackupPath, overwrite: true);
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

    private static FileStream OpenExclusive(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

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

    private static void WriteBytesDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private static string ReadShared(string path)
    {
        if (!File.Exists(path))
            return "";

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
