namespace GeckoPeeks.Logging;

/// <summary>
/// Capped sibling of the CSV. The route CSV itself is never capped.
/// </summary>
public sealed class ActivityLog
{
    public const int MaxLines = 400;
    private readonly object _gate = new();
    private readonly List<string> _recent = new();

    public ActivityLog(string path)
    {
        Path = path;
        try
        {
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadAllLines(path).TakeLast(12))
                _recent.Add(line);
        }
        catch
        {
            // The activity file is optional. A bad read must not stop the monitor.
        }
    }

    public string Path { get; private set; }

    /// <summary>Later lines go to <paramref name="path"/>; the recent lines stay on screen.</summary>
    public void MoveTo(string path)
    {
        lock (_gate)
            Path = path;
    }

    public IReadOnlyList<string> Recent()
    {
        lock (_gate)
            return _recent.ToArray();
    }

    public string Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
        lock (_gate)
        {
            _recent.Add(line);
            if (_recent.Count > 12)
                _recent.RemoveAt(0);

            try
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.AppendAllText(Path, line + Environment.NewLine);
                TrimIfNeeded();
            }
            catch
            {
                // Activity history is best-effort. The CSV write is separate.
            }
        }

        return line;
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(Path);
        if (!info.Exists || info.Length < 96 * 1024)
            return;

        var lines = File.ReadAllLines(Path);
        if (lines.Length <= MaxLines)
            return;

        File.WriteAllLines(Path, lines[^MaxLines..]);
    }
}
