namespace AppLedger;

internal sealed class NetworkSampler
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NetworkEvent> _events = [];

    public IReadOnlyList<NetworkEvent> Events => _events;

    public void Sample(IReadOnlySet<int> sessionPids)
    {
        foreach (var row in TcpTable.ReadIPv4())
        {
            if (!sessionPids.Contains(row.ProcessId) || row.RemoteAddress == "0.0.0.0" || row.RemotePort == 0)
            {
                continue;
            }

            var key = $"{row.ProcessId}|{row.RemoteAddress}|{row.RemotePort}";
            if (_seen.Add(key))
            {
                _events.Add(new NetworkEvent(
                    row.ProcessId,
                    row.LocalAddress,
                    row.LocalPort,
                    row.RemoteAddress,
                    row.RemotePort,
                    row.State,
                    DateTimeOffset.Now,
                    null));
            }
        }
    }
}

internal sealed record NetworkEvent(
    int ProcessId,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    DateTimeOffset FirstSeen,
    string? RemoteHost,
    ProcessIdentity? Process = null);

internal static class NetworkResolver
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<Dictionary<string, string>> DnsCacheIndex = new(BuildDnsCacheIndex, true);

    public static List<NetworkEvent> Enrich(IReadOnlyList<NetworkEvent> events)
    {
        if (events.Count == 0)
        {
            return [];
        }

        var resolved = events
            .Select(item => item with
            {
                RemoteHost = ResolveHost(item.RemoteAddress)
            })
            .ToList();

        return resolved;
    }

    public static string DisplayHost(NetworkEvent item)
    {
        if (!string.IsNullOrWhiteSpace(item.RemoteHost))
        {
            return item.RemoteHost;
        }

        return item.RemoteAddress;
    }

    public static string DisplayHostLabel(NetworkEvent item) =>
        !string.IsNullOrWhiteSpace(item.RemoteHost)
            ? item.RemoteHost
            : $"{item.RemoteAddress} (unresolved)";

    private static string? ResolveHost(string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
        {
            return null;
        }

        if (!IPAddress.TryParse(remoteAddress, out var ip))
        {
            return remoteAddress;
        }

        if (DnsCacheIndex.Value.TryGetValue(remoteAddress, out var cachedHost))
        {
            Cache[remoteAddress] = cachedHost;
            return cachedHost;
        }

        if (Cache.TryGetValue(remoteAddress, out var cached))
        {
            return cached;
        }

        try
        {
            var lookup = Dns.GetHostEntryAsync(remoteAddress);
            if (!lookup.Wait(TimeSpan.FromMilliseconds(500)))
            {
                Cache[remoteAddress] = null;
                return null;
            }

            var entry = lookup.GetAwaiter().GetResult();
            var host = NormalizeHost(entry.HostName);
            Cache[remoteAddress] = host;
            return host;
        }
        catch
        {
            Cache[remoteAddress] = null;
            return null;
        }
    }

    private static string? NormalizeHost(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        return hostName.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static Dictionary<string, string> BuildDnsCacheIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2",
                "SELECT Entry, Data, Type FROM MSFT_DNSClientCache");

            foreach (ManagementObject entry in searcher.Get())
            {
                var type = Convert.ToInt32(entry["Type"] ?? 0, CultureInfo.InvariantCulture);
                var recordName = entry["Entry"]?.ToString();
                var data = entry["Data"]?.ToString();

                if (type == 1 && IPAddress.TryParse(data, out _))
                {
                    RememberHost(index, data!, recordName);
                }
                else if (type == 12 && TryReversePointerToIpv4(recordName, out var address))
                {
                    RememberHost(index, address, data);
                }
            }
        }
        catch
        {
            return index;
        }

        return index;
    }

    private static void RememberHost(Dictionary<string, string> index, string address, string? host)
    {
        var normalized = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (!index.TryGetValue(address, out var existing)
            || ShouldReplaceHost(existing, normalized))
        {
            index[address] = normalized;
        }
    }

    private static bool ShouldReplaceHost(string existing, string candidate)
    {
        var existingLabels = existing.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var candidateLabels = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return candidateLabels.Length < existingLabels.Length
            || (candidate.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
                && !existing.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
            || (candidate.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)
                && !existing.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReversePointerToIpv4(string? pointer, out string address)
    {
        address = string.Empty;
        if (string.IsNullOrWhiteSpace(pointer)
            || !pointer.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var trimmed = pointer[..^".in-addr.arpa".Length];
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        Array.Reverse(parts);
        address = string.Join(".", parts);
        return IPAddress.TryParse(address, out _);
    }
}

internal static class TcpTable
{
    private const int AfInet = 2;

    public static IEnumerable<TcpRow> ReadIPv4()
    {
        var bufferLength = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref bufferLength, true, AfInet, TcpTableClass.TcpTableOwnerPidAll, 0);
        var buffer = Marshal.AllocHGlobal(bufferLength);

        try
        {
            var result = GetExtendedTcpTable(buffer, ref bufferLength, true, AfInet, TcpTableClass.TcpTableOwnerPidAll, 0);
            if (result != 0)
            {
                yield break;
            }

            var rowCount = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (var i = 0; i < rowCount; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                yield return new TcpRow(
                    row.OwningPid,
                    new IPAddress(row.LocalAddr).ToString(),
                    ConvertPort(row.LocalPort),
                    new IPAddress(row.RemoteAddr).ToString(),
                    ConvertPort(row.RemotePort),
                    ((TcpState)row.State).ToString());
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ConvertPort(uint port) => (int)(((port & 0xFF) << 8) + ((port & 0xFF00) >> 8));

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int tcpTableLength,
        bool sort,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    private enum TcpTableClass
    {
        TcpTableBasicListener,
        TcpTableBasicConnections,
        TcpTableBasicAll,
        TcpTableOwnerPidListener,
        TcpTableOwnerPidConnections,
        TcpTableOwnerPidAll
    }

    private enum TcpState
    {
        Closed = 1,
        Listen = 2,
        SynSent = 3,
        SynReceived = 4,
        Established = 5,
        FinWait1 = 6,
        FinWait2 = 7,
        CloseWait = 8,
        Closing = 9,
        LastAck = 10,
        TimeWait = 11,
        DeleteTcb = 12
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public int OwningPid;
    }
}

internal sealed record TcpRow(int ProcessId, string LocalAddress, int LocalPort, string RemoteAddress, int RemotePort, string State);
