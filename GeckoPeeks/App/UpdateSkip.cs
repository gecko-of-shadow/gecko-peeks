namespace GeckoPeeks.App;

/// <summary>
/// Remembers a version the user skipped ("Not this version") so the auto-check does not nag
/// again. Lives under LocalAppData so Controlled folder access on Documents does not block it.
/// </summary>
public static class UpdateSkip
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GeckoPeeks",
        "skipped-update.txt");

    public static bool IsSkipped(Version version)
    {
        try
        {
            if (!File.Exists(FilePath))
                return false;
            var text = File.ReadAllText(FilePath).Trim();
            var skipped = AppUpdate.ParseTag(text);
            return skipped != null && AppUpdate.Compare(skipped, version) == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Skip(Version version)
    {
        try
        {
            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            File.WriteAllText(FilePath, AppUpdate.FormatVersion(version));
        }
        catch
        {
            // Skipping is best-effort; the tray Install item still works.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch
        {
            // Best-effort.
        }
    }
}
