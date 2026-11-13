using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PowerControlProWpf;

internal static class NetworkConnectionTest
{
    internal static int QueryDns(string host, RepairNetworkAdapter adapter)
    {
        var index = adapter.Index > 0 ? adapter.Index : adapter.Ipv6Index;
        if (index <= 0) throw new ArgumentOutOfRangeException(nameof(adapter));
        var status = Query(host, index, 1);
        return status is 9501 or 9003 ? Query(host, index, 28) : status;
    }

    private static int Query(string host, int index, ushort type)
    {
        const ulong dnsOnlyBypassCache = 0x00000008 | 0x00000100;
        var request = new QueryRequest { Version = 1, Name = host, Type = type, Options = dnsOnlyBypassCache, Index = (uint)index };
        var result = new QueryResult { Version = 1 };
        var status = DnsQueryEx(ref request, ref result, IntPtr.Zero);
        if (result.Records != IntPtr.Zero) DnsRecordListFree(result.Records, 1);
        return status != 0 ? status : result.Status;
    }

    internal static async Task TestTcpAsync(string host, int port, RepairNetworkAdapter adapter, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, token);
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var ipv6 = address.AddressFamily == AddressFamily.InterNetworkV6;
            var index = ipv6 ? adapter.Ipv6Index : adapter.Index;
            if (index <= 0) continue;
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                const SocketOptionName unicastInterface = (SocketOptionName)31;
                socket.SetSocketOption(ipv6 ? SocketOptionLevel.IPv6 : SocketOptionLevel.IP,
                    unicastInterface, ipv6 ? index : IPAddress.HostToNetworkOrder(index));
                await socket.ConnectAsync(new IPEndPoint(address, port), token);
                return;
            }
            catch (SocketException error) { lastError = error; }
        }
        throw lastError ?? new SocketException((int)SocketError.AddressFamilyNotSupported);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryRequest
    {
        public uint Version;
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public ushort Type;
        public ulong Options;
        public IntPtr Servers;
        public uint Index;
        public IntPtr Callback, Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryResult
    {
        public uint Version;
        public int Status;
        public ulong Options;
        public IntPtr Records, Reserved;
    }

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern int DnsQueryEx(ref QueryRequest request, ref QueryResult result, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
