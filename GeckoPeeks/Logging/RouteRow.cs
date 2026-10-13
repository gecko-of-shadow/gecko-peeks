namespace GeckoPeeks.Logging;

using GeckoPeeks.Routing;

/// <summary>
/// One CSV record. Strings stay blank when a field is unknown.
/// <see cref="Error"/>, <see cref="Cancelled"/>, and <see cref="Hops"/> are not columns.
/// </summary>
public sealed class RouteRow
{
    public string Timestamp { get; set; } = "";
    public string LeagueRunning { get; set; } = "";
    public string Upstream { get; set; } = "";
    public string HandoffNetwork { get; set; } = "";
    public string HandoffHost { get; set; } = "";
    public string HandoffIP { get; set; } = "";
    public string HandoffCity { get; set; } = "";
    public string HandoffRttMs { get; set; } = "";
    public string LastHopIP { get; set; } = "";
    public string LastHopHost { get; set; } = "";
    public string LastHopMin { get; set; } = "";
    public string LastHopAvg { get; set; } = "";
    public string LastHopMax { get; set; } = "";
    public string LastHopLossPct { get; set; } = "";
    public string RiotHopAvg { get; set; } = "";
    public string RiotHopLossPct { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Adapter { get; set; } = "";
    public string Wifi { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Vpn { get; set; } = "";

    public string Error { get; set; } = "";
    public bool Cancelled { get; set; }
    public List<HopObservation> Hops { get; set; } = new();

    public bool HasMeasuredData()
    {
        return Hops.Count > 0
            || FullPath.Length > 0
            || LastHopIP.Length > 0
            || RiotHopAvg.Length > 0
            || RiotHopLossPct.Length > 0
            || Upstream.Length > 0;
    }
}
