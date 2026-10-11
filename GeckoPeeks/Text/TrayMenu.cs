namespace GeckoPeeks.Text;

public enum TrayCommand
{
    OpenWindow,
    RunTestNow,
    OpenLog,
    OpenLogFolder,
    TogglePause,
    StartWithWindows,
    Exit,
}

/// <summary>
/// Tray menu order and wording. The tray builds its ContextMenuStrip from <see cref="Layout"/>;
/// null entries are separators.
/// </summary>
public static class TrayMenu
{
    /// <summary>Bold first item. A tray click or double-click does the same thing.</summary>
    public const TrayCommand DefaultCommand = TrayCommand.OpenWindow;

    public const string ClosedHintTitle = "Gecko Peeks is still logging";
    public const string ClosedHintText =
        "Closed to the tray. Click the gecko icon (it may be under ^) or right-click > Open window. Exit quits.";

    public static IReadOnlyList<TrayCommand?> Layout { get; } = new TrayCommand?[]
    {
        TrayCommand.OpenWindow,
        null,
        TrayCommand.RunTestNow,
        TrayCommand.OpenLog,
        TrayCommand.OpenLogFolder,
        TrayCommand.TogglePause,
        TrayCommand.StartWithWindows,
        null,
        TrayCommand.Exit,
    };

    public static string Label(TrayCommand command, bool paused = false) => command switch
    {
        TrayCommand.OpenWindow => "Open window",
        TrayCommand.RunTestNow => "Run test now",
        TrayCommand.OpenLog => "Open log",
        TrayCommand.OpenLogFolder => "Open log folder",
        TrayCommand.TogglePause => paused ? "Resume logging" : "Pause logging",
        TrayCommand.StartWithWindows => "Start with Windows",
        TrayCommand.Exit => "Exit",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    public static bool IsCheckable(TrayCommand command) => command == TrayCommand.StartWithWindows;
}

/// <summary>
/// Start with Windows menu state. The checkmark mirrors the real HKCU Run value, including
/// after a failed write, never the intended toggle.
/// </summary>
public sealed class StartupToggle
{
    private readonly Func<bool> _isEnabled;
    private readonly Action<bool> _setEnabled;

    public StartupToggle(Func<bool> isEnabled, Action<bool> setEnabled)
    {
        _isEnabled = isEnabled;
        _setEnabled = setEnabled;
    }

    public bool Checked
    {
        get
        {
            try
            {
                return _isEnabled();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Flips the Run value. Returns false when the write failed.</summary>
    public bool Toggle()
    {
        try
        {
            _setEnabled(!Checked);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
