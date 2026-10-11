namespace GeckoPeeks.App;

public sealed class LogPaths
{
    public LogPaths(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }
    public string SettingsPath => Path.Combine(Folder, "gecko-peeks-settings.json");
    public string ActivityPath => Path.Combine(Folder, "activity.log");

    public static LogPaths Resolve()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new LogPaths(Path.Combine(documents, "RiotRouteLog"));
    }
}
