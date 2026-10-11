using System.Text;

namespace GeckoPeeks.Logging;

/// <summary>
/// RFC 4180-style encoding. A row is one complete line ending in CRLF, or it is not written.
/// </summary>
public static class CsvFormat
{
    public static readonly string[] Columns =
    {
        "Timestamp",
        "LeagueRunning",
        "Upstream",
        "HandoffNetwork",
        "HandoffHost",
        "HandoffIP",
        "HandoffCity",
        "HandoffRttMs",
        "LastHopIP",
        "LastHopHost",
        "LastHopMin",
        "LastHopAvg",
        "LastHopMax",
        "LastHopLossPct",
        "RiotHopAvg",
        "RiotHopLossPct",
        "FullPath",
    };

    public static string HeaderLine => string.Join(",", Columns) + "\r\n";

    public static string FormatTimestamp(DateTime local) =>
        local.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.IndexOfAny(new[] { '"', ',', '\r', '\n' }) >= 0)
            return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return text;
    }

    public static string FormatLine(RouteRow row)
    {
        var fields = new[]
        {
            row.Timestamp,
            row.LeagueRunning,
            row.Upstream,
            row.HandoffNetwork,
            row.HandoffHost,
            row.HandoffIP,
            row.HandoffCity,
            row.HandoffRttMs,
            row.LastHopIP,
            row.LastHopHost,
            row.LastHopMin,
            row.LastHopAvg,
            row.LastHopMax,
            row.LastHopLossPct,
            row.RiotHopAvg,
            row.RiotHopLossPct,
            row.FullPath,
        };

        var builder = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(Escape(fields[i]));
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    public static List<RouteRow> Parse(string? text)
    {
        var rows = new List<RouteRow>();
        if (string.IsNullOrEmpty(text))
            return rows;

        foreach (var fields in ParseRecords(text))
        {
            if (fields.Count == 0)
                continue;
            if (fields.Count == 1 && string.IsNullOrWhiteSpace(fields[0]))
                continue;
            if (string.Equals(fields[0], "Timestamp", StringComparison.Ordinal))
                continue;

            rows.Add(new RouteRow
            {
                Timestamp = Field(fields, 0),
                LeagueRunning = Field(fields, 1),
                Upstream = Field(fields, 2),
                HandoffNetwork = Field(fields, 3),
                HandoffHost = Field(fields, 4),
                HandoffIP = Field(fields, 5),
                HandoffCity = Field(fields, 6),
                HandoffRttMs = Field(fields, 7),
                LastHopIP = Field(fields, 8),
                LastHopHost = Field(fields, 9),
                LastHopMin = Field(fields, 10),
                LastHopAvg = Field(fields, 11),
                LastHopMax = Field(fields, 12),
                LastHopLossPct = Field(fields, 13),
                RiotHopAvg = Field(fields, 14),
                RiotHopLossPct = Field(fields, 15),
                FullPath = Field(fields, 16),
            });
        }

        return rows;
    }

    public static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(fields);
                fields = new List<string>();
            }
            else if (ch != '\r')
            {
                field.Append(ch);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add(fields);
        }

        return records;
    }

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index < fields.Count ? fields[index] : "";
}
