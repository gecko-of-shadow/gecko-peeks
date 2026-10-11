namespace GeckoPeeks.Routing;

/// <summary>
/// Compares traceroute paths. The runner reports the same hops with every ping, so the
/// window only rebuilds its grid when the path itself changed.
/// </summary>
public static class HopList
{
    /// <summary>
    /// True when both lists hold equal hops in the same order. A missing list counts as empty.
    /// <see cref="HopObservation"/> is a record, so a hop that gained a hostname or an RTT differs.
    /// </summary>
    public static bool SameHops(IReadOnlyList<HopObservation>? first, IReadOnlyList<HopObservation>? second)
    {
        var a = first ?? Array.Empty<HopObservation>();
        var b = second ?? Array.Empty<HopObservation>();
        if (ReferenceEquals(a, b))
            return true;
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }
}
