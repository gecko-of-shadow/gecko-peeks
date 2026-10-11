namespace GeckoPeeks.Text;

/// <summary>
/// What the window's status line says. A running or failed test keeps the phase.
/// Otherwise the line is the same route sentence as the tray tooltip.
/// </summary>
public static class WindowStatus
{
    public static bool IsTrouble(string? phase, string? lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError))
            return true;

        var text = phase ?? "";
        return text.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Last test problem", StringComparison.Ordinal);
    }

    public static string Headline(bool testRunning, string? phase, string? lastError, string? tooltip)
    {
        var phaseText = string.IsNullOrWhiteSpace(phase) ? "Idle" : phase.Trim();
        if (testRunning || IsTrouble(phase, lastError))
            return phaseText;

        if (!string.IsNullOrWhiteSpace(tooltip))
            return tooltip.Trim();

        return phaseText;
    }
}
