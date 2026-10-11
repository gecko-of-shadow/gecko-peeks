using GeckoPeeks.Logging;

namespace GeckoPeeks.Text;

/// <summary>
/// Window-only text for a CSV row. The file keeps raw column values; the grid asks for
/// a cell by column name so reordering or inserting a column cannot shift the others.
/// </summary>
public static class LogDisplay
{
    public const string CopiedFileStatus = "Copied log path to clipboard.";

    public static string Cell(RouteRow row, string column) => column switch
    {
        "Timestamp" => row.Timestamp,
        "LeagueRunning" => League(row.LeagueRunning),
        "Upstream" => row.Upstream,
        "HandoffNetwork" => row.HandoffNetwork,
        "HandoffHost" => row.HandoffHost,
        "HandoffIP" => row.HandoffIP,
        "HandoffCity" => City(row.HandoffCity),
        "HandoffRttMs" => row.HandoffRttMs,
        "LastHopIP" => row.LastHopIP,
        "LastHopHost" => row.LastHopHost,
        "LastHopMin" => row.LastHopMin,
        "LastHopAvg" => row.LastHopAvg,
        "LastHopMax" => row.LastHopMax,
        "LastHopLossPct" => row.LastHopLossPct,
        "RiotHopAvg" => LatencyLabels.RiotCell(row.RiotHopAvg, row.RiotHopLossPct),
        "RiotHopLossPct" => row.RiotHopLossPct,
        "FullPath" => row.FullPath,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "Add a LogDisplay.Cell arm when adding a CSV column."),
    };

    public static string League(string? value)
    {
        if (string.Equals(value, "Y", StringComparison.OrdinalIgnoreCase))
            return "Yes";
        if (string.Equals(value, "N", StringComparison.OrdinalIgnoreCase))
            return "No";
        return value ?? "";
    }

    /// <summary>Keep the CSV code and append a known city name. Unknown codes stay as written.</summary>
    public static string City(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";

        var trimmed = code.Trim();
        if (!CityNames.IsKnownCode(trimmed))
            return trimmed;

        var name = CityNames.Display(trimmed);
        if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return trimmed + " · " + name;
    }
}
