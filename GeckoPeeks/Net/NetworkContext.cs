using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace GeckoPeeks.Net;

public readonly record struct NetworkSnapshot(string Adapter, string Wifi, string Gateway, string Vpn)
{
    public static NetworkSnapshot Empty { get; } = new("", "", "", "");
}

/// <summary>
/// The interface traffic to the trace target leaves on, sampled once per test.
/// A failure leaves every field blank and does not fail the test.
/// </summary>
public static class NetworkContext
{
    public const string Yes = "Y";
    public const string No = "N";

    private static readonly string[] VpnTokens =
    {
        "wireguard", "wintun", "nordlynx", "tailscale", "openvpn", "mullvad",
        "proton", "expressvpn", "anyconnect", "windscribe", "surfshark",
    };

    public sealed record Candidate(
        string Name,
        string Description,
        bool IsUp,
        bool IsLoopback,
        bool IsWireless,
        string? Gateway,
        int? Metric,
        string InterfaceId = "",
        int? Index = null);

    public static NetworkSnapshot Capture()
    {
        try
        {
            var metrics = DefaultRouteMetrics();
            var candidates = new List<Candidate>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                int? metric = null;
                int? nicIndex = null;
                try
                {
                    var index = nic.GetIPProperties().GetIPv4Properties().Index;
                    nicIndex = index;
                    if (metrics.TryGetValue(index, out var value))
                        metric = value;
                }
                catch
                {
                    // Some virtual adapters have no IPv4 properties.
                }

                candidates.Add(new Candidate(
                    nic.Name ?? "",
                    nic.Description ?? "",
                    nic.OperationalStatus == OperationalStatus.Up,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    FirstGateway(nic),
                    metric,
                    nic.Id ?? "",
                    nicIndex));
            }

            // The OS's own pick for the trace target sees split tunnels (0/1 + 128/1) that a
            // 0.0.0.0/0 metric comparison misses. Fall back to that comparison if it fails.
            var best = BestInterfaceIndex(TestRunner.TraceTarget);
            var chosen = (best is int bestIndex ? ChooseByIndex(candidates, bestIndex) : null)
                ?? Choose(candidates);
            if (chosen == null)
                return NetworkSnapshot.Empty;

            var wifi = chosen.IsWireless ? WifiSsid.Query(chosen.InterfaceId) : "";
            return FromCandidate(chosen, wifi);
        }
        catch
        {
            return NetworkSnapshot.Empty;
        }
    }

    public static Candidate? Choose(IReadOnlyList<Candidate> candidates)
    {
        Candidate? best = null;
        foreach (var candidate in candidates)
        {
            if (!candidate.IsUp || candidate.IsLoopback)
                continue;
            if (string.IsNullOrWhiteSpace(candidate.Gateway))
                continue;
            if (best == null || MetricOf(candidate) < MetricOf(best))
                best = candidate;
        }

        return best;
    }

    /// <summary>
    /// The interface Windows routes the trace target over. A tunnel may have no gateway;
    /// it is still the adapter in use.
    /// </summary>
    public static Candidate? ChooseByIndex(IReadOnlyList<Candidate> candidates, int index)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Index == index && !candidate.IsLoopback)
                return candidate;
        }

        return null;
    }

    public static NetworkSnapshot FromCandidate(Candidate chosen, string? wifi)
    {
        var vpn = IsVpn(chosen.Name, chosen.Description) ? Yes : No;
        var ssid = chosen.IsWireless ? (wifi ?? "").Trim() : "";
        return new NetworkSnapshot(chosen.Name.Trim(), ssid, chosen.Gateway?.Trim() ?? "", vpn);
    }

    public static bool IsVpn(string? name, string? description)
    {
        var text = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
        foreach (var token in VpnTokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int MetricOf(Candidate candidate) => candidate.Metric ?? int.MaxValue;

    private static string FirstGateway(NetworkInterface nic)
    {
        try
        {
            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    continue;
                if (gateway.Address.Equals(IPAddress.Any))
                    continue;
                return gateway.Address.ToString();
            }
        }
        catch
        {
            // No gateway list.
        }

        return "";
    }

    private static Dictionary<int, int> DefaultRouteMetrics()
    {
        var map = new Dictionary<int, int>();
        var size = 0;
        GetIpForwardTable(IntPtr.Zero, ref size, false);
        if (size <= 0)
            return map;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpForwardTable(buffer, ref size, false) != 0)
                return map;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpForwardRow>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibIpForwardRow>(buffer + 4 + i * rowSize);
                if (row.ForwardDest != 0 || row.ForwardMask != 0)
                    continue;

                var index = (int)row.ForwardIfIndex;
                var metric = (int)row.ForwardMetric1;
                if (!map.TryGetValue(index, out var existing) || metric < existing)
                    map[index] = metric;
            }
        }
        catch
        {
            return map;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return map;
    }

    /// <summary>GetBestInterfaceEx for an IPv4 destination; null when the call fails.</summary>
    private static int? BestInterfaceIndex(string destination)
    {
        try
        {
            if (!IPAddress.TryParse(destination, out var address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            // sockaddr_in: family (AF_INET = 2), port 0, address in network order, 8 zero bytes.
            var sockaddr = new byte[16];
            sockaddr[0] = 2;
            address.GetAddressBytes().CopyTo(sockaddr, 4);
            return GetBestInterfaceEx(sockaddr, out var index) == 0 ? (int)index : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterfaceEx(byte[] destAddr, out uint bestIfIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow
    {
        public uint ForwardDest;
        public uint ForwardMask;
        public uint ForwardPolicy;
        public uint ForwardNextHop;
        public uint ForwardIfIndex;
        public uint ForwardType;
        public uint ForwardProto;
        public uint ForwardAge;
        public uint ForwardNextHopAS;
        public uint ForwardMetric1;
        public uint ForwardMetric2;
        public uint ForwardMetric3;
        public uint ForwardMetric4;
        public uint ForwardMetric5;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);

    private static class WifiSsid
    {
        public static string Query(string interfaceId)
        {
            if (!Guid.TryParse(interfaceId, out var wanted))
                return "";

            var handle = IntPtr.Zero;
            var list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0)
                    return "";
                if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0 || list == IntPtr.Zero)
                    return "";

                var count = Marshal.ReadInt32(list);
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(list + 8 + i * itemSize);
                    if (info.InterfaceGuid != wanted)
                        continue;

                    var guid = info.InterfaceGuid;
                    if (WlanQueryInterface(handle, ref guid, 7, IntPtr.Zero, out var dataSize, out var data, IntPtr.Zero) != 0
                        || data == IntPtr.Zero)
                        return "";

                    try
                    {
                        return ReadSsid(data, dataSize);
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
            catch
            {
                return "";
            }
            finally
            {
                if (list != IntPtr.Zero)
                    WlanFreeMemory(list);
                if (handle != IntPtr.Zero)
                    WlanCloseHandle(handle, IntPtr.Zero);
            }

            return "";
        }

        /// <summary>
        /// WLAN_CONNECTION_ATTRIBUTES: two ints, a 256-char profile name, then DOT11_SSID
        /// (ULONG length + 32 bytes). Offset 520 is that length.
        /// </summary>
        private static string ReadSsid(IntPtr data, uint dataSize)
        {
            const int lengthOffset = 520;
            if (dataSize < lengthOffset + 4 + 32)
                return "";

            var length = Marshal.ReadInt32(data, lengthOffset);
            if (length <= 0 || length > 32)
                return "";

            var bytes = new byte[length];
            Marshal.Copy(data + lengthOffset + 4, bytes, 0, length);
            var text = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0', ' ');
            foreach (var ch in text)
            {
                if (char.IsControl(ch))
                    return "";
            }

            return text;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WlanInterfaceInfo
        {
            public Guid InterfaceGuid;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string Description;

            public int State;
        }

        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanQueryInterface(
            IntPtr clientHandle,
            ref Guid interfaceGuid,
            int opcode,
            IntPtr reserved,
            out uint dataSize,
            out IntPtr data,
            IntPtr opcodeType);
    }
}
