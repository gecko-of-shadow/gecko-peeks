namespace GeckoPeeks.Text;

/// <summary>
/// Balloon only when Upstream or HandoffCity changes. The first logged test is silent.
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

        var upstreamChanged = !string.Equals(previousUpstream ?? "", upstream ?? "", StringComparison.OrdinalIgnoreCase);
        var cityChanged = !string.Equals(previousCity ?? "", city ?? "", StringComparison.OrdinalIgnoreCase);
        if (!upstreamChanged && !cityChanged)
            return null;

        return $"Route changed: {Place(previousUpstream, previousCity, previousNetwork)} → {Place(upstream, city, network)}";
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
