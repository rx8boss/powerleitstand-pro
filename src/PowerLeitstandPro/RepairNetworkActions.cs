using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PowerControlProWpf;

internal sealed record RepairNetworkRequest(Guid AdapterId, string? Dns, bool Ipv4Only = false,
    string? DnsV6 = null, string? PreviousV4 = null, string? PreviousV6 = null)
{
    internal string Prelude(string mode)
    {
        var scoped = Ipv4Only || DnsV6 is not null;
        if (AdapterId == Guid.Empty || (mode == "dns-servers" ? Dns is null && DnsV6 is null : Dns is not null || scoped) ||
            (scoped && ((Dns is null) != (PreviousV4 is null) || (DnsV6 is null) != (PreviousV6 is null))) ||
            (!scoped && (PreviousV4 is not null || PreviousV6 is not null)))
            throw new ArgumentException("Invalid network action settings.");
        var target = $"$adapterId = [guid]'{AdapterId:D}'";
        if (!scoped) return target + "; $v4 = $null; $v6 = $null; $servers = " + Literal(Dns, null);
        return target + "; $v4 = " + Literal(Dns, AddressFamily.InterNetwork) +
            "; $v6 = " + Literal(DnsV6, AddressFamily.InterNetworkV6) +
            "; $beforeV4 = " + Literal(PreviousV4, AddressFamily.InterNetwork) +
            "; $beforeV6 = " + Literal(PreviousV6, AddressFamily.InterNetworkV6);
    }

    private static string Literal(string? servers, AddressFamily? family) =>
        servers is null ? "$null" : "@(" + string.Join(", ", Parse(servers, family).Select(ip => "'" + ip + "'")) + ")";

    internal static string[] Parse(string servers, AddressFamily? family)
    {
        var addresses = new List<string>();
        foreach (var input in servers.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IPAddress.TryParse(input, out var ip) ||
                (ip.AddressFamily == AddressFamily.InterNetwork && (input != ip.ToString() || ip.GetAddressBytes()[0] >= 224)) ||
                (family is not null && ip.AddressFamily != family) ||
                ip.IsIPv6Multicast || ip.IsIPv4MappedToIPv6 || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                throw new ArgumentException("Invalid DNS server address.");
            addresses.Add(ip.ToString());
        }
        if (addresses.Count > (family is null ? 6 : 64) || (servers.Length > 0 && addresses.Count == 0))
            throw new ArgumentException("Invalid DNS server list.");
        return addresses.Distinct().ToArray();
    }
}

internal sealed record RepairNetworkAdapter(Guid Id, string Name, bool Dhcp, string Dns,
    bool Connected = false, bool Wireless = false, string Ipv4 = "", string Gateway = "",
    string DnsV4 = "", string DnsV6 = "", bool HasIpv6 = false, int Index = 0, int Ipv6Index = 0)
{
    public override string ToString() => Name;

    internal static IEnumerable<RepairNetworkAdapter> Read()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT)
                || !Guid.TryParse(adapter.Id, out var id)) continue;
            RepairNetworkAdapter item;
            try
            {
                var properties = adapter.GetIPProperties();
                var connected = adapter.OperationalStatus == OperationalStatus.Up;
                var ipv4 = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties() : null;
                var ipv6 = adapter.Supports(NetworkInterfaceComponent.IPv6) ? properties.GetIPv6Properties() : null;
                var addresses = properties.UnicastAddresses.Select(address => address.Address).ToArray();
                item = new(id, adapter.Name, connected && ipv4?.IsDhcpEnabled == true, string.Join(", ", properties.DnsAddresses),
                    connected, adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                    Addresses(addresses, AddressFamily.InterNetwork), string.Join(", ", properties.GatewayAddresses.Select(gateway => gateway.Address)),
                    Addresses(properties.DnsAddresses, AddressFamily.InterNetwork), Addresses(properties.DnsAddresses, AddressFamily.InterNetworkV6),
                    addresses.Any(address => address.AddressFamily == AddressFamily.InterNetworkV6), ipv4?.Index ?? 0, ipv6?.Index ?? 0);
            }
            catch (NetworkInformationException) { continue; }
            yield return item;
        }
    }

    private static string Addresses(IEnumerable<IPAddress> addresses, AddressFamily family) =>
        string.Join(", ", addresses.Where(address => address.AddressFamily == family));
}

internal static class RepairNetworkActions
{
    internal static string ReadTcpIpConfiguration(bool english)
    {
        string T(string de, string en) => english ? en : de;
        string Values<T>(IEnumerable<T> values)
        {
            var text = string.Join(", ", values);
            return text.Length == 0 ? "—" : text;
        }

        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { return T("Windows konnte die Adapterkonfiguration nicht lesen.", "Windows could not read the adapter configuration."); }
        var lines = new List<string>();
        foreach (var adapter in adapters.Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(adapter.Name + " · " + (adapter.OperationalStatus == OperationalStatus.Up
                ? T("Aktiv", "Up") : T("Nicht aktiv", "Inactive")));
            try
            {
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses;
                IPv4InterfaceProperties? ipv4 = null;
                try { if (adapter.Supports(NetworkInterfaceComponent.IPv4)) ipv4 = properties.GetIPv4Properties(); }
                catch (NetworkInformationException) { }
                var mode = ipv4 is null ? "" : ipv4.IsDhcpEnabled ? " · DHCP" : T(" · Statische Zuweisung", " · Static assignment");
                string Addresses(AddressFamily family) => Values(addresses.Where(item => item.Address.AddressFamily == family)
                    .Select(item => $"{item.Address}/{item.PrefixLength}"));
                lines.Add("IPv4: " + Addresses(AddressFamily.InterNetwork) + mode);
                lines.Add("IPv6: " + Addresses(AddressFamily.InterNetworkV6));
                lines.Add("Gateway: " + Values(properties.GatewayAddresses.Select(item => item.Address)));
                lines.Add("DNS: " + Values(properties.DnsAddresses));
            }
            catch (NetworkInformationException)
            {
                lines.Add(T("Die Konfiguration dieses Adapters konnte nicht gelesen werden.", "This adapter’s configuration could not be read."));
            }
        }
        return lines.Count == 0 ? T("Windows meldet keine Netzwerkadapter.", "Windows reports no network adapters.")
            : string.Join(Environment.NewLine, lines);
    }

    internal const string RunNetsh = """
        function Invoke-NetworkReset {
            $netsh = Join-Path ([Environment]::SystemDirectory) 'netsh.exe'
            & $netsh @args 2>&1
            $script:networkResetExitCode = $LASTEXITCODE
        }

        """;

    private const string NetshRunner = RunNetsh + """
        function Complete-NetworkReset($lines) {
            $lines
            if ($networkResetExitCode -ne 0) { exit $networkResetExitCode }
            if (($lines -join "`n") -match '(?im)access is denied|zugriff (?:wurde )?verweigert|\bfailed\b|fehlgeschlagen') {
                Label 'Windows meldet Fehler beim Zurücksetzen. Teile der Einstellungen können bereits geändert sein.' 'Windows reported reset errors. Some settings may already have changed.'
                exit 1
            }
        }

        """;

    internal const string WinsockReset = NetshRunner + """
        $output = @(Invoke-NetworkReset winsock reset)
        Complete-NetworkReset $output
        """;

    internal const string TcpIpReset = NetshRunner + """
        $log = [IO.Path]::GetTempFileName()
        try {
            $output = @(Invoke-NetworkReset int ip reset $log)
            $output += @(Get-Content -LiteralPath $log -ErrorAction Stop)
            Complete-NetworkReset $output
        } finally {
            Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
        }
        """;

    internal const string Flush = """
        Clear-DnsClientCache -ErrorAction Stop
        Label 'DNS-Cache geleert.' 'DNS cache cleared.'
        """;

    internal const string FindAdapter = """
        if (-not $adapterId -or $adapterId -eq [guid]::Empty) { throw (Label 'Kein Adapter ausgewählt.' 'No adapter selected.') }
        $matches = @(Get-NetAdapter -ErrorAction Stop | Where-Object { [guid]$_.InterfaceGuid -eq $adapterId })
        if ($matches.Count -ne 1) { throw (Label 'Der ausgewählte Adapter wurde nicht eindeutig gefunden.' 'The selected adapter could not be found uniquely.') }
        $adapter = $matches[0]
        (Label 'Adapter: ' 'Adapter: ') + $adapter.Name

        """;

    internal const string SetDns = FindAdapter + """
        if ($null -ne $v4 -or $null -ne $v6) {
            function Read-StaticDns($service) {
                $path = 'HKLM:\SYSTEM\CurrentControlSet\Services\{0}\Parameters\Interfaces\{1}' -f $service, $adapterId.ToString('B')
                if (Test-Path -LiteralPath $path -ErrorAction Stop) {
                    $value = (Get-ItemProperty -LiteralPath $path -ErrorAction Stop).NameServer
                    ($value -join ' ') -split '[,;\s]+' | Where-Object { $_ } | ForEach-Object { ([ipaddress]::Parse($_)).ToString() } | Select-Object -Unique
                }
            }
            function Write-StaticDns($change, $values) {
                if ($values.Count -gt 0) {
                    Set-DnsClientServerAddress -InputObject $change.Adapter -ServerAddresses $values -ErrorAction Stop
                } else {
                    Set-DnsClientServerAddress -InputObject $change.Adapter -ResetServerAddresses -ErrorAction Stop
                }
            }
            $changes = @(
                @{ Family = 'IPv4'; Service = 'Tcpip'; Before = $beforeV4; After = $v4 }
                @{ Family = 'IPv6'; Service = 'Tcpip6'; Before = $beforeV6; After = $v6 }
            ) | Where-Object { $null -ne $_.After }
            foreach ($change in $changes) {
                $found = @(Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -AddressFamily $change.Family -ErrorAction Stop)
                if ($found.Count -ne 1) { throw ((Label 'DNS nicht verfügbar für ' 'DNS unavailable for ') + $change.Family) }
                $change.Adapter = $found[0]
                $actual = @(Read-StaticDns $change.Service)
                if (($actual -join ',') -ne ($change.Before -join ',')) {
                    throw ((Label 'DNS-Einstellung wurde inzwischen geändert: ' 'DNS settings changed in the meantime: ') + $change.Family)
                }
            }
            $attempted = [System.Collections.Generic.List[hashtable]]::new()
            try {
                foreach ($change in $changes) {
                    if (($change.After -join ',') -eq ($change.Before -join ',')) { continue }
                    $attempted.Add($change)
                    Write-StaticDns $change $change.After
                    if ((@(Read-StaticDns $change.Service) -join ',') -ne ($change.After -join ',')) {
                        throw ((Label 'DNS-Änderung nicht bestätigt: ' 'DNS change could not be confirmed: ') + $change.Family)
                    }
                }
            } catch {
                $failure = $_.Exception.Message
                $rollbackFailures = @()
                for ($i = $attempted.Count - 1; $i -ge 0; $i--) {
                    $change = $attempted[$i]
                    try {
                        if ((@(Read-StaticDns $change.Service) -join ',') -ne ($change.Before -join ',')) {
                            Write-StaticDns $change $change.Before
                        }
                        if ((@(Read-StaticDns $change.Service) -join ',') -ne ($change.Before -join ',')) { throw $change.Family }
                    } catch { $rollbackFailures += $change.Family }
                }
                if ($rollbackFailures.Count -gt 0) {
                    $failure += (Label ' Automatische Rückkehr nicht bestätigt für: ' ' Automatic rollback not confirmed for: ') + ($rollbackFailures -join ', ')
                } elseif ($attempted.Count -gt 0) {
                    $failure += (Label ' Bisherige Einstellungen wiederhergestellt.' ' Previous settings restored.')
                }
                throw $failure
            }
            foreach ($change in $changes) {
                $mode = if ($change.After.Count -gt 0) { $change.After -join ', ' } else { Label 'Automatisch' 'Automatic' }
                (Label 'DNS-Einstellung ' 'DNS setting ') + $change.Family + ': ' + $mode
            }
            return
        }
        if ($null -eq $servers) { throw (Label 'Kein DNS-Modus ausgewählt.' 'No DNS mode selected.') }
        $previous = @(Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ErrorAction Stop | ForEach-Object { $_.ServerAddresses })
        (Label 'DNS vorher: ' 'DNS before: ') + ($previous -join ', ')
        if ($servers.Count -gt 0) {
            Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses $servers -ErrorAction Stop
            (Label 'Manuelle DNS-Server gesetzt: ' 'Manual DNS servers set: ') + ($servers -join ', ')
        } else {
            Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ResetServerAddresses -ErrorAction Stop
            Label 'Automatische DNS-Zuweisung wiederhergestellt.' 'Automatic DNS assignment restored.'
        }
        $current = @(Get-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ErrorAction Stop | ForEach-Object { $_.ServerAddresses })
        (Label 'DNS aktuell: ' 'Current DNS: ') + ($current -join ', ')
        """;

    internal const string RenewDhcp = FindAdapter + """
        $config = @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter ('InterfaceIndex = {0}' -f $adapter.ifIndex) -ErrorAction Stop)
        if ($adapter.Status -ne 'Up' -or $config.Count -ne 1 -or -not $config[0].DHCPEnabled) {
            throw (Label 'Der Adapter ist nicht verbunden oder DHCP ist nicht aktiv.' 'The adapter is disconnected or DHCP is disabled.')
        }
        $result = Invoke-CimMethod -InputObject $config[0] -MethodName RenewDHCPLease -ErrorAction Stop
        if ($result.ReturnValue -eq 1) { Label 'DHCP-Adresse erneuert. Windows meldet, dass ein Neustart erforderlich ist.' 'DHCP lease renewed. Windows reports that a restart is required.' }
        elseif ($result.ReturnValue -eq 0) { Label 'DHCP-Adresse erneuert.' 'DHCP lease renewed.' }
        else { throw ((Label 'DHCP-Erneuerung fehlgeschlagen. Windows-Code: ' 'DHCP renewal failed. Windows code: ') + $result.ReturnValue) }
        """;

    internal const string Restart = FindAdapter + """
        if ($adapter.Status -eq 'Disabled') { throw (Label 'Der ausgewählte Adapter ist deaktiviert.' 'The selected adapter is disabled.') }
        Restart-NetAdapter -InputObject $adapter -Confirm:$false -ErrorAction Stop
        Label 'Adapter neu gestartet. Der Verbindungsaufbau kann noch einen Moment dauern.' 'Adapter restarted. The connection may take a moment to return.'
        """;
}
