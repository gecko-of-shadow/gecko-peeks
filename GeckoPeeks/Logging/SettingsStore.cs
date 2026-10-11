using System.Text.Json;

namespace GeckoPeeks.Logging;

public sealed class SavedHop
{
    public int Number { get; set; }
    public string Ip { get; set; } = "";
    public string Hostname { get; set; } = "";
    public long? RttMs { get; set; }
    public bool Responded { get; set; }
    public bool ReachedTarget { get; set; }
}

public sealed class SavedResult
{
    public string Timestamp { get; set; } = "";
    public string Upstream { get; set; } = "";
    public string HandoffNetwork { get; set; } = "";
    public string HandoffCity { get; set; } = "";
    public string HandoffRttMs { get; set; } = "";
    public string RiotHopAvg { get; set; } = "";
    public string LastHopAvg { get; set; } = "";
    public string FullPath { get; set; } = "";
}

public sealed class AppSettings
{
    public bool Paused { get; set; }
    public SavedResult? Last { get; set; }
    public List<SavedHop> Hops { get; set; } = new();
}

/// <summary>
/// Paused flag and last result, written beside the CSV by temp file plus move.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(settings, Json);
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
