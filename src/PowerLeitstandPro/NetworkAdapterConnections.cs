using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PowerControlProWpf;

internal static class NetworkAdapterConnections
{
    internal static IReadOnlyList<NetworkAdapterEntry> Attach(IReadOnlyList<NetworkAdapterEntry> devices,
        IReadOnlySet<Guid>? disableCandidates = null)
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return devices; }
        var v4 = BestInterface(IPAddress.Parse("1.1.1.1"));
        var v6 = BestInterface(IPAddress.Parse("2606:4700:4700::1111"));
        var matches = interfaces.Where(item => Guid.TryParse(item.Id, out _)).ToLookup(item => Guid.Parse(item.Id));
        var uniqueDevices = devices.ToLookup(item => item.InterfaceId);
        return devices.Select(entry =>
        {
            var adapter = entry.InterfaceId != Guid.Empty && uniqueDevices[entry.InterfaceId].Count() == 1 &&
                matches[entry.InterfaceId].ToArray() is [var match] ? match : null;
            var connection = entry.DeviceDisabled == true ? AdapterConnection.Disabled : AdapterConnection.Unknown;
            if (adapter is null) return entry with { Connection = connection };
            try
            {
                var properties = adapter.GetIPProperties();
                var indexV4 = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties()?.Index : null;
                var indexV6 = adapter.Supports(NetworkInterfaceComponent.IPv6) ? properties.GetIPv6Properties()?.Index : null;
                var up = adapter.OperationalStatus;
                return entry with
                {
                    InternetV4 = v4 is not null && v4 == indexV4,
                    InternetV6 = v6 is not null && v6 == indexV6,
                    Connection = up == OperationalStatus.Up ? AdapterConnection.Connected
                        : entry.DeviceDisabled == true ? AdapterConnection.Disabled
                        : up is OperationalStatus.Down or OperationalStatus.LowerLayerDown ? AdapterConnection.Disconnected
                        : AdapterConnection.Unknown,
                    InterfaceType = (int)adapter.NetworkInterfaceType,
                    DisableVerified = (v4 is not null || v6 is not null) && disableCandidates?.Contains(entry.InterfaceId) == true
                };
            }
            catch (NetworkInformationException) { return entry with { Connection = connection }; }
        }).ToArray();
    }

    private static int? BestInterface(IPAddress address)
    {
        // Windows resolves the route locally. No probe packets are sent.
        var socketAddress = new IPEndPoint(address, 0).Serialize();
        var bytes = new byte[socketAddress.Size];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = socketAddress[index];
        return GetBestInterfaceEx(bytes, out var best) == 0 && best > 0 ? best : null;
    }

    internal static async Task<IReadOnlySet<Guid>> ReadDisableCandidatesAsync()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $adapters = @(Get-NetAdapter -IncludeHidden)
            $routes = @(Get-NetRoute -PolicyStore ActiveStore -IncludeAllCompartments)
            $ids = @(foreach ($adapter in $adapters) {
                if (-not $adapter.HardwareInterface -or $adapter.Virtual -or $adapter.Hidden -or $adapter.AdminLocked -or
                    $adapter.iSCSIInterface -or $adapter.InterfaceType -notin 6,71 -or
                    $adapter.InterfaceOperationalStatus -ne 2 -or $adapter.MediaConnectState -ne 2 -or
                    $adapter.InterfaceAdminStatus -ne 1 -or $adapter.InterfaceIndex -eq 0 -or
                    $adapter.HigherLayerInterfaceIndices -or $adapter.LowerLayerInterfaceIndices) { continue }
                $index = $adapter.InterfaceIndex
                if (@($adapters | Where-Object InterfaceGuid -eq $adapter.InterfaceGuid).Count -ne 1) { continue }
                if ($adapters | Where-Object { $_.LowerLayerInterfaceIndices -contains $index -or $_.HigherLayerInterfaceIndices -contains $index }) { continue }
                if ($routes | Where-Object { $_.InterfaceIndex -eq $index }) { continue }
                $name = [System.Management.Automation.WildcardPattern]::Escape($adapter.Name)
                $bindings = @(Get-NetAdapterBinding -Name $name -AllBindings -IncludeHidden)
                if (-not $bindings -or ($bindings | Where-Object { $_.Enabled -and $_.ComponentID -in 'vms_pp','ms_bridge','ms_implat','ms_lbfo' })) { continue }
                ([guid]$adapter.InterfaceGuid).ToString('D')
            })
            ConvertTo-Json -InputObject $ids -Compress
            """;
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            command.ArgumentList.Add(argument);
        try
        {
            var output = await UserQueryProcess.ReadAsync(command, TimeSpan.FromSeconds(12)).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<Guid[]>(output) ?? []).ToHashSet();
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException or TimeoutException or JsonException)
        { return new HashSet<Guid>(); }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetBestInterfaceEx(byte[] destinationAddress, out int interfaceIndex);
}
