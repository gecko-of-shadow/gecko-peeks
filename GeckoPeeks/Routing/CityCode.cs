using System.Text.RegularExpressions;

namespace GeckoPeeks.Routing;

/// <summary>
/// Pulls a city code out of a router hostname.
/// Tokens are split on '.' and '-'. The first token matching
/// ^[a-zA-Z]{2,4}\d{0,2}$ wins when it contains a digit (sea1, den3, yyc2)
/// or is a 3-letter token that is not a generic word (chi, but not net/core).
/// </summary>
public static partial class CityCode
{
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "net", "com", "org", "dns", "ptr", "rev", "www", "core", "edge", "agg",
        "peer", "rtr", "xe", "et", "ae", "ge", "lo", "ip", "be", "to", "of",
    };

    public static string Extract(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return "";

        var tokens = hostname.Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (!TokenPattern().IsMatch(token))
                continue;

            var hasDigit = false;
            foreach (var ch in token)
            {
                if (char.IsDigit(ch))
                {
                    hasDigit = true;
                    break;
                }
            }

            // A digit is enough (sea1, be12). Generic words are excluded only
            // for the 3-letter, no-digit case (chi is kept, net/core are not).
            if (hasDigit)
                return token.ToLowerInvariant();

            if (token.Length == 3 && !Generic.Contains(token))
                return token.ToLowerInvariant();
        }

        return "";
    }

    [GeneratedRegex("^[a-zA-Z]{2,4}[0-9]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
