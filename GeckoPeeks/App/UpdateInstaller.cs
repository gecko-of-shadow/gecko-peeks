using System.Diagnostics;
using System.Text;

namespace GeckoPeeks.App;

/// <summary>
/// Replaces the running exe after this process exits. A short cmd script waits for our PID,
/// swaps <c>.new</c> into place, and starts the new build with the same arguments.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>
    /// Starts the swap helper and returns. The caller must then exit so the file lock drops.
    /// </summary>
    public static void BeginReplace(string exePath, string newExePath, string arguments)
    {
        if (!File.Exists(newExePath))
            throw new FileNotFoundException("The downloaded update is missing.", newExePath);

        var pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), "gecko-peeks-update-" + pid + ".cmd");
        var content = new StringBuilder();
        content.AppendLine("@echo off");
        content.AppendLine("setlocal");
        content.AppendLine($"set \"PID={pid}\"");
        content.AppendLine($"set \"EXE={exePath}\"");
        content.AppendLine($"set \"NEW={newExePath}\"");
        content.AppendLine($"set \"ARGS={arguments}\"");
        content.AppendLine(":wait");
        content.AppendLine("tasklist /FI \"PID eq %PID%\" 2>NUL | find \"%PID%\" >NUL");
        content.AppendLine("if not errorlevel 1 (");
        content.AppendLine("  timeout /t 1 /nobreak >NUL");
        content.AppendLine("  goto wait");
        content.AppendLine(")");
        content.AppendLine("if exist \"%EXE%.old\" del /f /q \"%EXE%.old\" >NUL 2>&1");
        content.AppendLine("if exist \"%EXE%\" move /y \"%EXE%\" \"%EXE%.old\" >NUL");
        content.AppendLine("move /y \"%NEW%\" \"%EXE%\" >NUL");
        content.AppendLine("if errorlevel 1 (");
        content.AppendLine("  if exist \"%EXE%.old\" move /y \"%EXE%.old\" \"%EXE%\" >NUL");
        content.AppendLine("  exit /b 1");
        content.AppendLine(")");
        content.AppendLine("start \"\" \"%EXE%\" %ARGS%");
        content.AppendLine("del /f /q \"%~f0\" >NUL 2>&1");

        File.WriteAllText(script, content.ToString(), Encoding.ASCII);

        Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Arguments = "/c \"" + script + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        });
    }

    /// <summary>Arguments to hand the new process (same as this launch, minus the exe path).</summary>
    public static string RestartArguments()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length <= 1)
            return "";

        var builder = new StringBuilder();
        for (var i = 1; i < args.Length; i++)
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(Quote(args[i]));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
