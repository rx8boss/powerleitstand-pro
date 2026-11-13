namespace PowerControlProWpf;

internal static class RepairNetworkQueries
{
    internal const string Connectivity = """
        function Addresses($values) {
            if ($values) { $values -join ', ' } else { Label 'keine' 'none' }
        }

        function Test-PublicEndpoint([System.Net.IPAddress]$address) {
            $client = [System.Net.Sockets.TcpClient]::new($address.AddressFamily)
            try {
                return $client.ConnectAsync($address, 443).Wait(1500) -and $client.Connected
            } catch { return $false }
            finally { $client.Dispose() }
        }

        $adapters = @([System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            Where-Object { $_.NetworkInterfaceType -ne 'Loopback' })
        $active = @($adapters | Where-Object { $_.OperationalStatus -eq 'Up' })
        '{0}: {1}/{2}' -f (Label 'Aktive Adapter' 'Active adapters'), $active.Count, $adapters.Count

        if ($active.Count -gt 0) {
            $gateways = @($active | ForEach-Object { $_.GetIPProperties().GatewayAddresses } |
                ForEach-Object { $_.Address.ToString() } | Select-Object -Unique -First 4)
            if ($gateways.Count -eq 0) { Label 'Lokale Verbindung: Kein Standardgateway gemeldet.' 'Local connection: No default gateway reported.' }
            $ping = [System.Net.NetworkInformation.Ping]::new()
            try {
                foreach ($gateway in $gateways) {
                    $reply = $null
                    try { $reply = $ping.Send([System.Net.IPAddress]::Parse($gateway), 800) } catch {}
                    $status = if ($reply.Status -eq 'Success') { Label 'antwortet' 'responded' }
                        else { Label 'keine Ping-Antwort (kann blockiert sein)' 'no ping response (may be blocked)' }
                    '{0} {1}: {2}' -f (Label 'Gateway' 'Gateway'), $gateway, $status
                }
            } finally { $ping.Dispose() }

            $internet = (Test-PublicEndpoint '1.1.1.1') -or (Test-PublicEndpoint '2606:4700:4700::1111')
            if ($internet) { Label 'Internet: Cloudflare per TCP erreichbar.' 'Internet: Cloudflare reachable over TCP.' }
            else { Label 'Internet: Keine Verbindung zum Testziel. Sperren am Netzwerk sind möglich.' 'Internet: Could not connect to the test endpoint. The network may block it.' }

            try {
                $records = @(Resolve-DnsName -Name 'dns.msftncsi.com' -Type A_AAAA -DnsOnly -NoHostsFile -QuickTimeout -ErrorAction Stop |
                    Where-Object { $_.IPAddress })
                if ($records.Count -gt 0) { Label 'DNS: dns.msftncsi.com wurde aufgelöst.' 'DNS: dns.msftncsi.com was resolved.' }
                else { Label 'DNS: Keine IP-Adresse für den Testnamen gemeldet.' 'DNS: No IP address returned for the test name.' }
            } catch { Label 'DNS: Auflösung des Testnamens fehlgeschlagen.' 'DNS: Could not resolve the test name.' }
        } else {
            Label 'Gateway, Internet und DNS übersprungen: Kein aktiver Adapter.' 'Gateway, internet and DNS checks skipped: No active adapter.'
        }

        foreach ($adapter in $adapters) {
            $kind = switch -Regex ($adapter.NetworkInterfaceType.ToString()) {
                '^Wireless80211$' { Label 'WLAN' 'Wi-Fi' }
                'Ethernet' { Label 'LAN/Ethernet' 'LAN/Ethernet' }
                '^(Ppp|Tunnel)$' { Label 'Tunnel/PPP' 'Tunnel/PPP' }
                default { $adapter.NetworkInterfaceType.ToString() }
            }
            $status = if ($adapter.OperationalStatus -eq 'Up') { Label 'aktiv' 'up' }
                else { Label 'nicht aktiv' 'not up' }
            $ip = $adapter.GetIPProperties()
            '{0} | {1} | {2}' -f $adapter.Name, $kind, $status
            '  IP: {0} | Gateway: {1}' -f (Addresses $ip.UnicastAddresses.Address), (Addresses $ip.GatewayAddresses.Address)
            '  DNS: {0}' -f (Addresses $ip.DnsAddresses)
        }
        """ + "\n\n" + TeamInventory;

    internal const string TeamInventory = """
        function Write-NetworkTeam($name, $source, $status, $members, [switch]$byDescription) {
            '{0} ({1}) | {2}: {3}' -f $name, $source, (Label 'Teamstatus' 'Team status'), $status
            if (-not $members) {
                '  ' + (Label 'Keine Mitgliedsadapter gemeldet.' 'No member adapters reported.')
                return
            }

            foreach ($member in $members) {
                $memberAdapters = @(foreach ($adapter in $adapters) {
                    $identity = if ($byDescription) { $adapter.Description } else { $adapter.Name }
                    if ($identity -eq $member) { $adapter }
                })
                $memberName = $member
                $link = Label 'nicht gemeldet' 'not reported'
                if ($memberAdapters.Count -eq 1) {
                    $memberName = $memberAdapters[0].Name
                    $link = switch ($memberAdapters[0].OperationalStatus) {
                        'Up' { Label 'aktiv' 'up' }
                        'Down' { Label 'inaktiv' 'down' }
                        'LowerLayerDown' { Label 'Verbindung auf unterer Ebene getrennt' 'lower-layer link down' }
                        default { Label 'unbekannt' 'unknown' }
                    }
                }
                '  {0}: {1} | {2}: {3}' -f (Label 'Mitglied' 'Member'), $memberName, (Label 'Adapterstatus' 'Adapter status'), $link
            }
        }

        ''
        Label 'Netzwerkteams' 'Network teams'
        try {
            $teams = @(NetLbfo\Get-NetLbfoTeam -ErrorAction Stop -WarningAction SilentlyContinue)
            if ($teams.Count -eq 0) { Label 'LBFO: Keine Teams gemeldet.' 'LBFO: No teams reported.' }
            foreach ($team in $teams) {
                $state = switch ([string]$team.Status) {
                    { $_ -in 'Up', '0' } { Label 'aktiv' 'up' }
                    { $_ -in 'Down', '1' } { Label 'inaktiv' 'down' }
                    { $_ -in 'Degraded', '2' } { Label 'eingeschränkt' 'degraded' }
                    default { Label 'nicht gemeldet' 'not reported' }
                }
                Write-NetworkTeam $team.Name 'LBFO' $state $team.Members
            }
        } catch [System.Management.Automation.CommandNotFoundException] {
            Label 'LBFO: Windows-Modul nicht verfügbar.' 'LBFO: Windows module unavailable.'
        } catch {
            Label 'LBFO: Teams konnten nicht abgefragt werden.' 'LBFO: Teams could not be queried.'
        }

        try {
            $teams = @(Hyper-V\Get-VMSwitchTeam -ErrorAction Stop -WarningAction SilentlyContinue)
            if ($teams.Count -eq 0) { Label 'Hyper-V SET: Keine Teams gemeldet.' 'Hyper-V SET: No teams reported.' }
            foreach ($team in $teams) {
                Write-NetworkTeam $team.Name 'Hyper-V SET' (Label 'nicht gemeldet' 'not reported') $team.NetAdapterInterfaceDescription -byDescription
            }
        } catch [System.Management.Automation.CommandNotFoundException] {
            Label 'Hyper-V SET: Windows-Modul nicht verfügbar.' 'Hyper-V SET: Windows module unavailable.'
        } catch {
            Label 'Hyper-V SET: Teams konnten nicht abgefragt werden.' 'Hyper-V SET: Teams could not be queried.'
        }
        """;

    internal const string Services = """
        $services = Get-Service -Name Dhcp,Dnscache,WlanSvc -ErrorAction SilentlyContinue
        foreach ($name in 'Dhcp', 'Dnscache', 'WlanSvc') {
            $service = $services | Where-Object Name -eq $name
            $caption = switch ($name) {
                'Dhcp' { Label 'DHCP-Client' 'DHCP Client' }
                'Dnscache' { Label 'DNS-Client' 'DNS Client' }
                'WlanSvc' { Label 'WLAN-Dienst' 'WLAN AutoConfig' }
            }
            $status = if ($null -eq $service) { Label 'nicht abrufbar' 'unavailable' } else {
                switch ($service.Status.ToString()) {
                    'Running' { Label 'läuft' 'running' }
                    'Stopped' { Label 'beendet' 'stopped' }
                    'Paused' { Label 'angehalten' 'paused' }
                    default { Label 'im Übergang' 'changing state' }
                }
            }
            '{0} ({1}): {2}' -f $caption, $name, $status
        }
        """;
}
