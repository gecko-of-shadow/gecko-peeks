namespace GeckoPeeks.Net;

/// <summary>
/// .NET Ping sets RoundtripTime to 0 for TtlExpired / TimeExceeded on Windows
/// even when the ICMP reply carried a real RTT. Prefer the API value when
/// positive; otherwise use a Stopwatch measured around SendPingAsync.
/// </summary>
public static class IcmpTiming
{
    public static long ResolveRttMs(long roundtripTime, long elapsedMilliseconds)
    {
        if (roundtripTime > 0)
            return roundtripTime;
        if (elapsedMilliseconds > 0)
            return elapsedMilliseconds;
        return 0;
    }

    public static long? ResolveRespondedRttMs(bool responded, long roundtripTime, long elapsedMilliseconds)
    {
        if (!responded)
            return null;
        return ResolveRttMs(roundtripTime, elapsedMilliseconds);
    }
}
