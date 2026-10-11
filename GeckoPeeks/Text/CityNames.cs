namespace GeckoPeeks.Text;

/// <summary>
/// Balloon text uses a city name when we know the code (den3 → Denver, sea1 → Seattle).
/// The CSV keeps the raw code. Unknown codes stay as written.
/// </summary>
public static class CityNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sea"] = "Seattle",
        ["den"] = "Denver",
        ["chi"] = "Chicago",
        ["ord"] = "Chicago",
        ["yyc"] = "Calgary",
        ["bil"] = "Billings",
        ["lax"] = "Los Angeles",
        ["sjc"] = "San Jose",
        ["sfo"] = "San Francisco",
        ["dfw"] = "Dallas",
        ["dal"] = "Dallas",
        ["iad"] = "Ashburn",
        ["ash"] = "Ashburn",
        ["nyc"] = "New York",
        ["ewr"] = "Newark",
        ["atl"] = "Atlanta",
        ["mia"] = "Miami",
        ["phx"] = "Phoenix",
        ["slc"] = "Salt Lake City",
        ["pdx"] = "Portland",
        ["yvr"] = "Vancouver",
        ["van"] = "Vancouver",
        ["yyz"] = "Toronto",
        ["tor"] = "Toronto",
        ["yul"] = "Montreal",
    };

    public static string Display(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "";

        var letters = new string(code.TakeWhile(char.IsLetter).ToArray());
        if (letters.Length > 0 && Names.TryGetValue(letters, out var name))
            return name;

        return code;
    }

    public static bool IsKnownCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        if (Names.ContainsKey(code))
            return true;

        var letters = new string(code.TakeWhile(char.IsLetter).ToArray());
        return letters.Length > 0 && Names.ContainsKey(letters);
    }
}
