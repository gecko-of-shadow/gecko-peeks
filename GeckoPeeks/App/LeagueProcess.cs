using System.Diagnostics;

namespace GeckoPeeks.App;

public static class LeagueProcess
{
    public static bool IsRunning()
    {
        try
        {
            // "League of Legends.exe" is reported without the extension.
            var found = Process.GetProcessesByName("League of Legends");
            try
            {
                return found.Length > 0;
            }
            finally
            {
                foreach (var process in found)
                    process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }
}
