using System.Diagnostics;
using GeckoPeeks.App;
using GeckoPeeks.Ui;

namespace GeckoPeeks;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var instance = SingleInstance.Acquire();
        if (!instance.IsFirst)
        {
            instance.Dispose();
            return;
        }

        try
        {
            // Stay out of the way of a game. Best-effort: some hosts refuse the change.
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Keep running at the default priority.
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => CrashLog.Write(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog.Write(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception);
            args.SetObserved();
        };

        Application.Run(new TrayApplicationContext(instance));
        instance.Dispose();
    }
}

internal static class CrashLog
{
    public static void Write(Exception? exception)
    {
        if (exception == null)
            return;

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "RiotRouteLog");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "crash.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exception}{Environment.NewLine}");
            TrimCrashLog(path);
        }
        catch
        {
            // A failed crash log must not throw again.
        }
    }

    private static void TrimCrashLog(string path)
    {
        const int maxBytes = 256 * 1024;
        const int keepLines = 200;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < maxBytes)
            return;

        var lines = File.ReadAllLines(path);
        if (lines.Length <= keepLines)
            return;

        File.WriteAllLines(path, lines[^keepLines..]);
    }
}
