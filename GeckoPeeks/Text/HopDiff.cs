namespace GeckoPeeks.Text;

/// <summary>
/// Compares two FullPath strings hop by hop. Only an IP that became a different IP, or a
/// hop added or dropped at the end, is a change. A star on either side is not.
/// The first test has no previous path, and <see cref="Describe"/> returns empty for that.
/// </summary>
public static class HopDiff
{
    public const string SamePath = "Same path as the last test";

    public static IReadOnlyList<int> ChangedHopNumbers(string? previousPath, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(previousPath))
            return Array.Empty<int>();

        var previous = Split(previousPath);
        var current = Split(currentPath);
        var count = Math.Max(previous.Count, current.Count);
        var changed = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var before = i < previous.Count ? previous[i] : "";
            var after = i < current.Count ? current[i] : "";
            if (SameHop(before, after))
                continue;
            changed.Add(i + 1);
        }

        return changed;
    }

    public static string Describe(string? previousPath, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(previousPath))
            return "";

        var changed = ChangedHopNumbers(previousPath, currentPath);
        if (changed.Count == 0)
            return SamePath;
        if (changed.Count == 1)
            return "Hop " + changed[0].ToString(System.Globalization.CultureInfo.InvariantCulture) + " changed";
        if (changed.Count == 2)
            return "Hops " + Num(changed[0]) + " and " + Num(changed[1]) + " changed";
        if (changed.Count == 3)
            return "Hops " + Num(changed[0]) + ", " + Num(changed[1]) + ", and " + Num(changed[2]) + " changed";
        return "Path changed (" + changed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " hops)";
    }

    private static string Num(int hop) => hop.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static bool SameHop(string before, string after)
    {
        if (before.Length == 0 && after.Length == 0)
            return true;
        // A hop present on one side only is a length change.
        if (before.Length == 0 || after.Length == 0)
            return false;
        // A lost probe ("*") says nothing about the router, so IP to star and back is not a change.
        if (before == "*" || after == "*")
            return true;
        return string.Equals(before, after, StringComparison.Ordinal);
    }

    private static List<string> Split(string? fullPath)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(fullPath))
            return list;

        var parts = fullPath.Split(" > ", StringSplitOptions.None);
        foreach (var part in parts)
            list.Add(part.Trim());
        return list;
    }
}
