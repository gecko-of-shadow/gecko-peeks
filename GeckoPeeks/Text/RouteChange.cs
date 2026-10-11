namespace GeckoPeeks.Text;

/// <summary>
/// Balloon only when Upstream or HandoffCity changes. The first logged test is silent.
/// A single failed probe that lands on blank/Unknown does not count as a route change
/// against a known-good previous row (and the recovery back is also silent).
/// The place in the sentence is the city when present, otherwise the handoff network.
/// </summary>
public static class RouteChange
{
    public static string? Describe(
        bool hasPrevious,
        string? previousUpstream,
        string? previousCity,
        string? previousNetwork,
        string? upstream,
        string? city,
        string? network)
    {
        if (!hasPrevious)
            return null;

        if (IsIndeterminate(previousUpstream, previousCity) || IsIndeterminate(upstream, city))
            return null;

        var upstreamChanged = !string.Equals(previousUpstream ?? "", upstream ?? "", StringComparison.OrdinalIgnoreCase);
        var cityChanged = !string.Equals(previousCity ?? "", city ?? "", StringComparison.OrdinalIgnoreCase);
        if (!upstreamChanged && !cityChanged)
            return null;

        return $"Route changed: {Place(previousUpstream, previousCity, previousNetwork)} → {Place(upstream, city, network)}";
    }

    /// <summary>
    /// Blank, Unknown, or missing city with Unknown upstream — treat as a bad probe, not a real route.
    /// </summary>
    public static bool IsIndeterminate(string? upstream, string? city)
    {
        var provider = (upstream ?? "").Trim();
        var place = (city ?? "").Trim();
        if (provider.Length == 0 || provider.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return true;
        // City may be blank when there is no handoff; that alone is not indeterminate
        // if upstream is a real provider. Only treat blank city as noise when the
        // upstream side is also empty/Unknown (handled above).
        _ = place;
        return false;
    }

    public static string Place(string? upstream, string? city, string? network)
    {
        var provider = string.IsNullOrWhiteSpace(upstream) ? "Unknown" : upstream.Trim();
        string where;
        if (!string.IsNullOrWhiteSpace(city))
            where = CityNames.Display(city);
        else if (!string.IsNullOrWhiteSpace(network))
            where = network.Trim();
        else
            where = "Unknown";

        return provider + "/" + where;
    }
}
