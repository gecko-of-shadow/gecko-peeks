namespace GeckoPeeks.App;

public sealed class LogPaths
{
    public const string FolderName = "RiotRouteLog";

    public LogPaths(string folder, string? fallbackFolder = null)
    {
        Folder = folder;
        FallbackFolder = fallbackFolder;
    }

    /// <summary>Documents\RiotRouteLog: where the log lives unless Windows refuses writes there.</summary>
    public string Folder { get; }

    /// <summary>%LOCALAPPDATA%\GeckoPeeks\RiotRouteLog: outside the folders Controlled folder access protects.</summary>
    public string? FallbackFolder { get; }

    public static LogPaths Resolve() => For(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static LogPaths For(string? documents, string? localAppData)
    {
        var fallback = string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, "GeckoPeeks", FolderName);
        if (string.IsNullOrWhiteSpace(documents))
            return new LogPaths(fallback ?? FolderName);

        return new LogPaths(Path.Combine(documents, FolderName), fallback);
    }
}
