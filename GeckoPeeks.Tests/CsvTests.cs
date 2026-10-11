using GeckoPeeks.Logging;
using Xunit;

namespace GeckoPeeks.Tests;

public class CsvTests
{
    [Fact]
    public void Escapes_commas_quotes_and_round_trips()
    {
        var row = Sample();
        row.HandoffHost = "say \"hi\", friend";
        row.FullPath = "* > 1.2.3.4 > * > 192.207.0.5";

        var line = CsvFormat.FormatLine(row);
        Assert.Contains("\"say \"\"hi\"\", friend\"", line, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", line, StringComparison.Ordinal);

        var parsed = CsvFormat.Parse(CsvFormat.HeaderLine + line);
        var back = Assert.Single(parsed);
        Assert.Equal(row.HandoffHost, back.HandoffHost);
        Assert.Equal(row.FullPath, back.FullPath);
        Assert.Equal("Y", back.LeagueRunning);
        Assert.Equal("Zayo", back.Upstream);
    }

    [Fact]
    public void Append_writes_header_once_backup_and_merges_pending()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var log = new CsvLog(folder);
            Assert.Equal(AppendResult.Main, log.Append(Sample()));
            Assert.Equal(AppendResult.Main, log.Append(Sample("Cogent")));

            var main = File.ReadAllText(log.MainPath);
            Assert.Equal(1, CountOf(main, "Timestamp"));
            Assert.Contains("Zayo", main, StringComparison.Ordinal);
            Assert.Contains("Cogent", main, StringComparison.Ordinal);
            Assert.True(new FileInfo(log.MainPath).Length > 0);
            // Backup is written on the first append; later refreshes may be throttled.
            Assert.True(File.Exists(log.BackupPath));
            Assert.Contains("Zayo", File.ReadAllText(log.BackupPath), StringComparison.Ordinal);

            var forced = new CsvLog(folder, () => true);
            var pendingRow = Sample("TELUS");
            pendingRow.Timestamp = "2026-10-11 12:30:00";
            Assert.Equal(AppendResult.Pending, forced.Append(pendingRow));
            Assert.True(File.Exists(log.PendingPath));
            Assert.DoesNotContain("TELUS", File.ReadAllText(log.MainPath), StringComparison.Ordinal);

            Assert.Equal(AppendResult.Main, log.Append(Sample("HE")));
            Assert.False(File.Exists(log.PendingPath));
            Assert.False(File.Exists(log.MergingPath));

            var rows = log.ReadAll();
            Assert.Equal(4, rows.Count);
            Assert.Equal("TELUS", rows[2].Upstream);
            Assert.Equal("HE", rows[3].Upstream);
            Assert.Contains("TELUS", File.ReadAllText(log.BackupPath), StringComparison.Ordinal);

            var length = new FileInfo(log.MainPath).Length;
            log.Append(Sample("Wolfpaw"));
            Assert.True(new FileInfo(log.MainPath).Length > length);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // The temp folder is outside the repo. Leaving it is harmless.
            }
        }
    }

    [Fact]
    public void Settings_round_trip_is_atomic()
    {
        var folder = Path.Combine(Path.GetTempPath(), "gecko-peeks-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "gecko-peeks-settings.json");
        try
        {
            SettingsStore.Save(path, new AppSettings
            {
                Paused = true,
                Last = new SavedResult { Upstream = "Zayo", HandoffCity = "sea1", Timestamp = "2026-10-11 12:00:00" },
            });

            var loaded = SettingsStore.Load(path);
            Assert.True(loaded.Paused);
            Assert.Equal("Zayo", loaded.Last?.Upstream);
            Assert.Equal("sea1", loaded.Last?.HandoffCity);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp only.
            }
        }
    }

    private static RouteRow Sample(string upstream = "Zayo") => new()
    {
        Timestamp = "2026-10-11 12:00:00",
        LeagueRunning = "Y",
        Upstream = upstream,
        HandoffNetwork = "Telia",
        HandoffHost = "ic-1.sea1.twelve99.net",
        HandoffIP = "4.5.6.7",
        HandoffCity = "sea1",
        HandoffRttMs = "40",
        LastHopIP = "192.207.0.5",
        LastHopHost = "",
        LastHopMin = "50",
        LastHopAvg = "61",
        LastHopMax = "80",
        LastHopLossPct = "0",
        RiotHopAvg = "61",
        RiotHopLossPct = "0",
        FullPath = "10.0.0.1 > 1.2.3.4 > * > 192.207.0.5",
    };

    private static int CountOf(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
