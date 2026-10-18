using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerControlProWpf;

internal sealed record DriveDiagnostic(string Name, string Label, long? Total, long? Free)
{
    internal long? Used => Total is > 0 && Free is >= 0 && Free <= Total ? Total - Free : null;
    internal double? Percent => Used is { } used ? used * 100d / Total!.Value : null;
}

internal sealed record NetworkDiagnostic(string Id, string Name, string Kind, string? Ssid, double? Receive, double? Send);
internal sealed record DiagnosticsSnapshot(double? Cpu, double? Ram, ulong? TotalMemory, ulong? AvailableMemory,
    double? Disk, string SystemDrive, IReadOnlyList<DriveDiagnostic> Drives,
    IReadOnlyList<NetworkDiagnostic> Networks, bool NetworkAvailable)
{
    public RamHardwareInfo RamHardware { get; init; } = RamHardwareInfo.Empty;
    public IReadOnlyList<GpuDiagnostic> Gpus { get; init; } = [];
    public PcHardwareInfo? Pc { get; init; }
    public IReadOnlyList<ProcessorDiagnostic> Processors { get; init; } = [];
    internal static DiagnosticsSnapshot Empty { get; } = new(null, null, null, null, null,
        Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:", [], [], false);
    internal double? NetworkTotal
    {
        get
        {
            if (!NetworkAvailable) return null;
            if (Networks.Count == 0) return 0;
            // VPN/virtual traffic can also pass through LAN/WiFi; count it once.
            var measured = Networks.Where(n => n.Kind is "LAN" or "WLAN").ToArray();
            if (measured.Length == 0) measured = Networks.ToArray();
            return measured.All(n => n.Receive.HasValue && n.Send.HasValue) ? measured.Sum(n => n.Receive!.Value + n.Send!.Value) : null;
        }
    }
}

// Local, read-only sampling. One caller owns this instance; no WPF access here.
internal sealed class SystemDiagnosticsSampler : IDisposable
{
    private IntPtr _query, _cpu, _disk, _processors;
    private PcHardwareInfo? _pc;
    private RamHardwareInfo _ramHardware = RamHardwareInfo.Empty;
    private readonly GpuDiagnosticsSampler _gpuSampler = new();
    private bool _initialized, _disposed;
    private readonly Dictionary<string, (long Receive, long Send, long Time)> _networkPrevious = new();
    private Task<DriveDiagnostic[]>? _driveRead;
    private DriveDiagnostic[] _drives = [];
    private long _nextDriveRead;
    private long _nextWifiRead;
    private Dictionary<Guid, string> _wifi = new();

    internal DiagnosticsSnapshot Sample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) InitializeCounters();
        double? cpu = null, disk = null;
        if (_query != IntPtr.Zero && Native.PdhCollectQueryData(_query) == 0)
        {
            cpu = ReadCounter(_cpu);
            if (ReadCounter(_disk) is { } idle) disk = Math.Clamp(100 - idle, 0, 100);
        }
        var memory = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        var memoryValid = Native.GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0 && memory.AvailablePhysical <= memory.TotalPhysical;
        var now = Stopwatch.GetTimestamp();
        if (_driveRead is { IsCompleted: true })
        {
            if (_driveRead.IsCompletedSuccessfully) _drives = _driveRead.Result;
            _driveRead = null;
            _nextDriveRead = now + 5 * Stopwatch.Frequency;
        }
        // A slow or unavailable disk cannot stall CPU/network sampling or the UI.
        if (_driveRead is null && now >= _nextDriveRead) _driveRead = Task.Run(ReadDrives);
        var (networks, available) = ReadNetworks(now);
        return new DiagnosticsSnapshot(cpu, memoryValid ? (memory.TotalPhysical - memory.AvailablePhysical) * 100d / memory.TotalPhysical : null,
            memoryValid ? memory.TotalPhysical : null, memoryValid ? memory.AvailablePhysical : null,
            disk, DiagnosticsSnapshot.Empty.SystemDrive, _drives, networks, available) { Pc = _pc, Processors = ReadProcessors(), Gpus = _gpuSampler.Sample(now, _pc?.Graphics), RamHardware = _ramHardware };
    }

    private void InitializeCounters()
    {
        _initialized = true;
        _pc = PcHardwareInfo.Read();
        _ramHardware = RamHardwareInfo.Read();
        if (Native.PdhOpenQuery(null, UIntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        if (Native.PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Time", UIntPtr.Zero, out _cpu) != 0)
            Native.PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", UIntPtr.Zero, out _cpu);
        if (Native.PdhAddEnglishCounter(_query, @"\Processor Information(*)\% Processor Time", UIntPtr.Zero, out _processors) != 0)
            Native.PdhAddEnglishCounter(_query, @"\Processor(*)\% Processor Time", UIntPtr.Zero, out _processors);
        Native.PdhAddEnglishCounter(_query, $@"\LogicalDisk({DiagnosticsSnapshot.Empty.SystemDrive})\% Idle Time", UIntPtr.Zero, out _disk);
    }

    private static double? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero || Native.PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) != 0 ||
            value.Status > 1 || !double.IsFinite(value.Value)) return null;
        return Math.Clamp(value.Value, 0, 100);
    }

    private ProcessorDiagnostic[] ReadProcessors()
    {
        ProcessorDiagnostic[] Missing() => Enumerable.Range(0, _pc?.LogicalProcessors ?? Environment.ProcessorCount).Select(i => new ProcessorDiagnostic(i.ToString(), null)).ToArray();
        if (_processors == IntPtr.Zero) return Missing();
        uint bytes = 0, count = 0;
        Native.PdhGetFormattedCounterArray(_processors, 0x200, ref bytes, ref count, IntPtr.Zero);
        if (bytes == 0 || bytes > 16 * 1024 * 1024) return Missing();
        var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (Native.PdhGetFormattedCounterArray(_processors, 0x200, ref bytes, ref count, buffer) != 0) return Missing();
            var result = new List<ProcessorDiagnostic>();
            var size = Marshal.SizeOf<Native.CounterItem>();
            for (var i = 0; i < Math.Min(count, bytes / size); i++)
            {
                var item = Marshal.PtrToStructure<Native.CounterItem>(buffer + i * size);
                var name = Marshal.PtrToStringUni(item.Name);
                if (name is null || name.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new(name, item.Value.Status <= 1 && double.IsFinite(item.Value.Value) ? Math.Clamp(item.Value.Value, 0, 100) : null));
            }
            static int Part(string id, int index) => int.TryParse(id.Split(',').ElementAtOrDefault(index), out var value) ? value : 0;
            return result.Count == 0 ? Missing() : result.OrderBy(p => Part(p.Id, 0)).ThenBy(p => Part(p.Id, 1)).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static double? Rate(long previous, long current, double seconds) =>
        previous >= 0 && current >= previous && double.IsFinite(seconds) && seconds > 0 && seconds <= 10
            ? (current - previous) * 8d / seconds / 1_000_000 : null;

    private (NetworkDiagnostic[] Networks, bool Available) ReadNetworks(long now)
    {
        var result = new List<NetworkDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            if (now >= _nextWifiRead && adapters.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211))
            {
                _wifi = WifiNames.Read();
                _nextWifiRead = now + 10 * Stopwatch.Frequency;
            }
            foreach (var adapter in adapters)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    // NDIS/WFP filter interfaces have no IP addresses of their own.
                    if (!adapter.GetIPProperties().UnicastAddresses.Any(a => !IPAddress.IsLoopback(a.Address))) continue;
                    if (!seen.Add(adapter.Id)) continue;
                    double? receive = null, send = null;
                    try
                    {
                        var stats = adapter.GetIPStatistics();
                        var previous = _networkPrevious.GetValueOrDefault(adapter.Id);
                        var seconds = previous.Time == 0 ? double.NaN : (now - previous.Time) / (double)Stopwatch.Frequency;
                        receive = Rate(previous.Receive, stats.BytesReceived, seconds);
                        send = Rate(previous.Send, stats.BytesSent, seconds);
                        _networkPrevious[adapter.Id] = (stats.BytesReceived, stats.BytesSent, now);
                    }
                    catch (Exception exception) when (exception is NetworkInformationException or InvalidOperationException or NotSupportedException)
                    { _networkPrevious.Remove(adapter.Id); }
                    var kind = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "WLAN" :
                        adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT ? "LAN" : "Other";
                    if (kind == "LAN" && new[] { "Virtual", "VMware", "TAP-", "WireGuard", "VPN" }.Any(word => adapter.Description.Contains(word, StringComparison.OrdinalIgnoreCase))) kind = "Other";
                    var ssid = Guid.TryParse(adapter.Id, out var id) ? _wifi.GetValueOrDefault(id) : null;
                    result.Add(new NetworkDiagnostic(adapter.Id, adapter.Name, kind, ssid, receive, send));
                }
                catch (Exception exception) when (exception is NetworkInformationException or InvalidOperationException or NotSupportedException)
                {
                    _networkPrevious.Remove(adapter.Id);
                }
            }
            foreach (var id in _networkPrevious.Keys.Where(id => !seen.Contains(id)).ToArray()) _networkPrevious.Remove(id);
            return (result.OrderBy(n => n.Kind == "Other").ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), true);
        }
        catch (NetworkInformationException) { _networkPrevious.Clear(); return ([], false); }
    }

    private static DriveDiagnostic[] ReadDrives()
    {
        try
        {
            var letters = DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).Select(d =>
            {
                try
                {
                    return d.IsReady ? new DriveDiagnostic(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize, d.TotalFreeSpace)
                        : new DriveDiagnostic(d.Name.TrimEnd('\\'), "", null, null);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { return new DriveDiagnostic(d.Name.TrimEnd('\\'), "", null, null); }
            }).ToArray();
            var known = letters.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return letters.Concat(MountedDriveDiscovery.FolderMounts(known)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (IOException) { return []; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpuSampler.Dispose();
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = _cpu = _disk = _processors = IntPtr.Zero;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct CounterItem { internal IntPtr Name; internal CounterValue Value; }
        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] internal static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
        [StructLayout(LayoutKind.Sequential)] internal struct CounterValue { internal uint Status; internal double Value; }
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQuery(string? source, UIntPtr user, out IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)] internal static extern uint PdhAddEnglishCounter(IntPtr query, string path, UIntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out CounterValue value);
        [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(IntPtr query);
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
    }

    private static class WifiNames
    {
        internal static Dictionary<Guid, string> Read()
        {
            var result = new Dictionary<Guid, string>();
            IntPtr client = IntPtr.Zero, interfaces = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0 || WlanEnumInterfaces(client, IntPtr.Zero, out interfaces) != 0) return result;
                var count = Math.Clamp(Marshal.ReadInt32(interfaces), 0, 256);
                for (var index = 0; index < count; index++)
                {
                    // WLAN_INTERFACE_INFO: GUID, WCHAR[256], interface state.
                    var entry = IntPtr.Add(interfaces, 8 + index * 532);
                    if (Marshal.ReadInt32(entry, 528) != 1) continue;
                    var id = Marshal.PtrToStructure<Guid>(entry);
                    IntPtr data = IntPtr.Zero;
                    try
                    {
                        // current_connection: state, mode, WCHAR profile[256], DOT11_SSID.
                        if (WlanQueryInterface(client, ref id, 7, IntPtr.Zero, out var size, out data, out _) != 0 || size < 556) continue;
                        var length = Marshal.ReadInt32(data, 520);
                        if (length is < 1 or > 32) continue;
                        var bytes = new byte[length];
                        Marshal.Copy(IntPtr.Add(data, 524), bytes, 0, length);
                        result[id] = Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
                    }
                    finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
                }
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
            finally
            {
                if (interfaces != IntPtr.Zero) WlanFreeMemory(interfaces);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
            return result;
        }
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr interfaces);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid id, uint opcode, IntPtr reserved, out uint size, out IntPtr data, out uint valueType);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
    }
}
