using System.Text.RegularExpressions;
using GeckoPeeks.Text;

namespace GeckoPeeks.Routing;

/// <summary>
/// Pulls a city code out of a router hostname.
/// Tokens are split on '.' and '-'. Interface/role tokens (ae5, ccr31, core1)
/// are skipped. Prefer a token whose letter prefix is a known city, otherwise
/// the first remaining city-shaped token with a digit, otherwise a bare 3-letter city.
/// A bare 4-letter token is used only when it is a known code (kanc, palo).
/// </summary>
public static partial class CityCode
{
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "net", "com", "org", "dns", "ptr", "rev", "www", "core", "edge", "agg",
        "peer", "rtr", "xe", "et", "ae", "ge", "lo", "ip", "be", "to", "of",
        "cr", "ccr", "po", "te", "hu", "mpr", "br", "bb", "ic",
    };

    /// <summary>
    /// Letter prefixes of interface / role tokens, with or without trailing digits.
    /// </summary>
    private static readonly string[] InterfacePrefixes =
    {
        "ae", "be", "xe", "et", "ge", "po", "te", "hu", "cr", "ccr", "core",
        "agg", "edge", "mpr", "br", "bb", "lo", "ic", "peer", "rtr", "cs",
    };

    public static string Extract(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return "";

        var tokens = hostname.Split(new[] { '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        string? firstDigitCity = null;
        string? firstBareCity = null;

        foreach (var token in tokens)
        {
            if (!TokenPattern().IsMatch(token))
                continue;
            if (IsInterfaceOrRole(token))
                continue;

            var lower = token.ToLowerInvariant();
            var hasDigit = HasDigit(token);
            var letters = LettersOnly(token);

            if (hasDigit)
            {
                if (CityNames.IsKnownCode(letters) || CityNames.IsKnownCode(lower))
                    return lower;
                firstDigitCity ??= lower;
                continue;
            }

            if (token.Length == 3 && !Generic.Contains(token))
            {
                if (CityNames.IsKnownCode(lower))
                    return lower;
                firstBareCity ??= lower;
            }
            else if (token.Length == 4 && CityNames.IsKnownCode(lower))
            {
                // Four-letter codes (kanc) only when known; plenty of 4-letter labels are not cities.
                return lower;
            }
        }

        return firstDigitCity ?? firstBareCity ?? "";
    }

    private static bool IsInterfaceOrRole(string token)
    {
        var letters = LettersOnly(token);
        foreach (var prefix in InterfacePrefixes)
        {
            if (string.Equals(letters, prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return Generic.Contains(token) || Generic.Contains(letters);
    }

    private static bool HasDigit(string token)
    {
        foreach (var ch in token)
        {
            if (char.IsDigit(ch))
                return true;
        }

        return false;
    }

    private static string LettersOnly(string token)
    {
        var chars = new char[token.Length];
        var n = 0;
        foreach (var ch in token)
        {
            if (char.IsLetter(ch))
                chars[n++] = ch;
        }

        return n == 0 ? "" : new string(chars, 0, n);
    }

    [GeneratedRegex("^[a-zA-Z]{2,4}[0-9]{0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
