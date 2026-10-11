namespace GeckoPeeks.Net;

/// <summary>
/// .NET Ping sets RoundtripTime to 0 for TtlExpired / TimeExceeded on Windows
/// even when the ICMP reply carried a real RTT. Prefer the API value when
/// positive; otherwise use a Stopwatch measured around SendPingAsync.
/// </summary>
public static class IcmpTiming
{
    public static long ResolveRttMs(long roundtripTime, long elapsedMilliseconds) =>
        ResolveRttMs(roundtripTime, (double)elapsedMilliseconds);

    /// <summary>
    /// Prefer a positive API RoundtripTime. Otherwise round wall-clock elapsed ms
    /// (Stopwatch TotalMilliseconds). Values under 0.5 ms round to 0 and are
    /// displayed as "&lt;1 ms" via <see cref="FormatHopRtt"/>.
    /// </summary>
    public static long ResolveRttMs(long roundtripTime, double elapsedMilliseconds)
    {
        if (roundtripTime > 0)
            return roundtripTime;
        if (elapsedMilliseconds <= 0)
            return 0;
        return (long)Math.Round(elapsedMilliseconds, MidpointRounding.AwayFromZero);
    }

    public static long? ResolveRespondedRttMs(bool responded, long roundtripTime, long elapsedMilliseconds) =>
        ResolveRespondedRttMs(responded, roundtripTime, (double)elapsedMilliseconds);

    public static long? ResolveRespondedRttMs(bool responded, long roundtripTime, double elapsedMilliseconds)
    {
        if (!responded)
            return null;
        return ResolveRttMs(roundtripTime, elapsedMilliseconds);
    }

    /// <summary>
    /// Live-table / hop RTT text. Never shows "0 ms" for a hop that replied —
    /// sub-millisecond (rounded &lt; 1) becomes "&lt;1 ms".
    /// </summary>
    public static string FormatHopRtt(bool responded, long? rttMs)
    {
        if (!responded || rttMs is null)
            return "";
        if (rttMs.Value < 1)
            return "<1 ms";
        return rttMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ms";
    }
}
