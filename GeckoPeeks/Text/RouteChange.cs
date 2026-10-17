namespace GeckoPeeks.Text;

/// <summary>
/// Balloon only when Upstream or HandoffCity changes. The first logged test is silent.
/// A single failed probe that lands on blank/Unknown does not count as a route change
/// against a known-good previous row (and the recovery back is also silent).
/// Cities compare by display name, so sea1 and sea2 are the same city.
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

        if (IsIndeterminate(previousUpstream, previousCity, upstream, city))
            return null;

        if (SameRoute(previousUpstream, previousCity, upstream, city))
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

    /// <summary>
    /// Either side is a bad probe, or the city went blank after a known one (the handoff
    /// hop most likely did not answer this time).
    /// </summary>
    public static bool IsIndeterminate(string? previousUpstream, string? previousCity, string? upstream, string? city) =>
        IsIndeterminate(previousUpstream, previousCity)
        || IsIndeterminate(upstream, city)
        || (!string.IsNullOrWhiteSpace(previousCity) && string.IsNullOrWhiteSpace(city));

    /// <summary>Same upstream and same city by display name (sea1 and sea2 are both Seattle).</summary>
    public static bool SameRoute(string? upstreamA, string? cityA, string? upstreamB, string? cityB) =>
        string.Equals((upstreamA ?? "").Trim(), (upstreamB ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(CityNames.Display(cityA?.Trim()), CityNames.Display(cityB?.Trim()), StringComparison.OrdinalIgnoreCase);

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

/// <summary>
/// Holds a route change until two logged tests in a row agree on the new upstream and city.
/// An indeterminate test in between is skipped: it neither confirms nor clears the pending change.
/// State lives in memory only; a restart starts from the last logged row.
/// </summary>
public sealed class RouteChangeConfirmer
{
    private Route? _current;
    private Route? _pending;

    /// <summary>The last logged row at startup. Sets the known route without a balloon.</summary>
    public void Seed(string? upstream, string? city, string? network)
    {
        _current = new Route(upstream ?? "", city ?? "", network ?? "");
        _pending = null;
    }

    /// <summary>One logged test. Returns the balloon text once a change is confirmed, otherwise null.</summary>
    public string? Observe(string? upstream, string? city, string? network)
    {
        var next = new Route(upstream ?? "", city ?? "", network ?? "");
        if (_current is not { } current)
        {
            _current = next;
            return null;
        }

        // Recovery from a bad first row is silent, as before.
        if (RouteChange.IsIndeterminate(current.Upstream, current.City))
        {
            if (!RouteChange.IsIndeterminate(next.Upstream, next.City))
                _current = next;
            _pending = null;
            return null;
        }

        if (RouteChange.IsIndeterminate(current.Upstream, current.City, next.Upstream, next.City))
            return null;

        if (RouteChange.SameRoute(current.Upstream, current.City, next.Upstream, next.City))
        {
            _current = next;
            _pending = null;
            return null;
        }

        if (_pending is { } pending && RouteChange.SameRoute(pending.Upstream, pending.City, next.Upstream, next.City))
        {
            var message = RouteChange.Describe(true, current.Upstream, current.City, current.Network, next.Upstream, next.City, next.Network);
            _current = next;
            _pending = null;
            return message;
        }

        _pending = next;
        return null;
    }

    private readonly record struct Route(string Upstream, string City, string Network);
}
