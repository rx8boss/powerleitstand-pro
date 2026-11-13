using System.Diagnostics;
using System.IO;
using System.Text;
using PowerControlProWpf;

internal static class RepairNetworkActionTests
{
    internal static void Run()
    {
        var selected = Guid.NewGuid();
        var other = Guid.NewGuid();
        var fixture = $$"""
            $ErrorActionPreference = 'Stop'
            $mockAdapters = @(
                [pscustomobject]@{ InterfaceGuid = '{{other:D}}'; ifIndex = 9; Name = 'Other'; Status = 'Up' },
                [pscustomobject]@{ InterfaceGuid = '{{selected:D}}'; ifIndex = 24; Name = 'Selected'; Status = 'Up' }
            )
            $mockV4 = ''
            $mockV6 = '2001:db8::1'
            function Test-Path($LiteralPath) { $true }
            function Get-ItemProperty($LiteralPath) {
                if (-not $LiteralPath.EndsWith($adapterId.ToString('B'))) { throw 'Wrong backup adapter' }
                if ($LiteralPath -like '*\Tcpip6\*') { [pscustomobject]@{ NameServer = $script:mockV6 } }
                else { [pscustomobject]@{ NameServer = $script:mockV4 } }
            }
            function Get-NetAdapter { $mockAdapters }
            function Get-DnsClientServerAddress($InterfaceIndex, $AddressFamily) {
                if ($InterfaceIndex -ne 24) { throw 'Wrong DNS adapter' }
                [pscustomobject]@{InterfaceIndex = $InterfaceIndex; AddressFamily = $AddressFamily; ServerAddresses = @('192.0.2.53')}
            }
            function Set-DnsClientServerAddress($InterfaceIndex, $InputObject, $ServerAddresses, [switch]$ResetServerAddresses) {
                if ($InputObject) {
                    if ($InputObject.InterfaceIndex -ne 24) { throw 'Wrong DNS adapter' }
                    $values = if ($ResetServerAddresses) { '' } else { $ServerAddresses -join ',' }
                    switch ($InputObject.AddressFamily) {
                        'IPv4' { $script:mockV4 = $values }
                        'IPv6' {
                            $script:mockV6 = $values
                            if ($simulateFailure -and $values -eq '2001:db8::54') { throw 'Mock IPv6 write failed after modifying DNS' }
                        }
                        default { throw 'Wrong DNS family' }
                    }
                    return
                }
                if ($InterfaceIndex -ne 24) { throw 'Wrong DNS adapter' }
                if ($ResetServerAddresses) { 'mock reset' }
                elseif (($ServerAddresses -join ',') -eq '192.0.2.54,2001:db8::53') { 'mock manual' }
                else { throw 'Wrong DNS addresses' }
            }
            function Get-CimInstance($ClassName, $Filter) {
                if ($ClassName -ne 'Win32_NetworkAdapterConfiguration' -or $Filter -ne 'InterfaceIndex = 24') { throw 'Wrong DHCP adapter' }
                [pscustomobject]@{ DHCPEnabled = $dhcp; ifIndex = 24 }
            }
            function Invoke-CimMethod($InputObject, $MethodName) {
                if (-not $InputObject.DHCPEnabled -or $InputObject.ifIndex -ne 24 -or $MethodName -ne 'RenewDHCPLease') { throw 'Unexpected DHCP call' }
                [pscustomobject]@{ ReturnValue = $dhcpResult }
            }
            function Restart-NetAdapter($InputObject, [switch]$Confirm) {
                if ($InputObject.ifIndex -ne 24) { throw 'Wrong restart adapter' }
                'mock restart'
            }
            function Clear-DnsClientCache { 'mock flush' }
            """;

        string Command(string mode, string? dns = null) =>
            RepairTool.Find(mode)!.CommandFor(english: true, new(selected, dns));
        string Scoped(string? v4, string? v6, string? beforeV4 = null, string? beforeV6 = null) =>
            RepairTool.Find("dns-servers")!.CommandFor(english: true,
                new(selected, v4, Ipv4Only: v6 is null, DnsV6: v6, PreviousV4: beforeV4, PreviousV6: beforeV6));
        string CheckScript(string command, bool succeeds)
        {
            var script = fixture + "\ntry {\n" + command + "\n} catch { Write-Output $_.Exception.Message; exit 1 }" +
                "\nfinally { 'State: v4={0};v6={1}' -f $mockV4,$mockV6 }\nexit 0";
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if ((process.ExitCode == 0) != succeeds) throw new InvalidOperationException(output);
            return output;
        }

        if (!CheckScript(Command("dns"), true).Contains("mock flush") ||
            !CheckScript(Command("dns-servers", "192.0.2.54, 2001:db8::53"), true).Contains("mock manual") ||
            !CheckScript(Command("dns-servers", ""), true).Contains("mock reset") ||
            !CheckScript("$mockV4 = '192.0.2.53'\n" + Scoped("", null, "192.0.2.53"), true).Contains("State: v4=;v6=2001:db8::1") ||
            !CheckScript(Scoped("192.0.2.54", null, ""), true).Contains("State: v4=192.0.2.54;v6=2001:db8::1") ||
            !CheckScript(Scoped(null, "", beforeV6: "2001:db8::1"), true).Contains("State: v4=;v6=" + Environment.NewLine) ||
            !CheckScript(Scoped("192.0.2.54", "2001:db8::54", "", "2001:db8::1"), true).Contains("State: v4=192.0.2.54;v6=2001:db8::54") ||
            !CheckScript("$simulateFailure = $true\n" + Scoped("192.0.2.54", "2001:db8::54", "", "2001:db8::1"), false)
                .Contains("Previous settings restored." + Environment.NewLine + "State: v4=;v6=2001:db8::1") ||
            !CheckScript(Scoped("192.0.2.54", "2001:db8::54", "", ""), false).Contains("State: v4=;v6=2001:db8::1") ||
            !CheckScript("$dhcp = $true; $dhcpResult = 0\n" + Command("dhcp-renew"), true).Contains("DHCP lease renewed.") ||
            !CheckScript("$dhcp = $true; $dhcpResult = 82\n" + Command("dhcp-renew"), false).Contains("82") ||
            !CheckScript("$dhcp = $false\n" + Command("dhcp-renew"), false).Contains("DHCP is disabled") ||
            !CheckScript(Command("adapter-restart"), true).Contains("mock restart") ||
            !CheckScript("$mockAdapters = @()\n" + Command("adapter-restart"), false).Contains("could not be found uniquely"))
            throw new InvalidOperationException("Network action did not use its expected simulated adapter or result.");

        const string resetMock = """
            function Invoke-NetworkReset {
                if (($args -join ' ') -eq 'winsock reset') {
                    $script:networkResetExitCode = 0
                    'Winsock reset fixture'
                } elseif ($args.Count -eq 4 -and ($args[0..2] -join ' ') -eq 'int ip reset') {
                    [IO.File]::WriteAllText($args[3], 'TCP/IP reset log fixture')
                    $script:networkResetExitCode = 0
                    if ($partialReset) { 'Zurücksetzen fehlgeschlagen. Zugriff verweigert.' }
                    else { 'TCP/IP reset fixture' }
                } else { throw 'Unexpected reset command' }
            }

            """;
        string SimulateReset(string id)
        {
            var script = Command(id);
            if (!script.Contains(RepairNetworkActions.RunNetsh, StringComparison.Ordinal))
                throw new InvalidOperationException("The test must replace the native reset runner.");
            return script.Replace(RepairNetworkActions.RunNetsh, resetMock, StringComparison.Ordinal);
        }
        if (!CheckScript(SimulateReset("winsock-reset"), true).Contains("Winsock reset fixture") ||
            !CheckScript(SimulateReset("tcpip-reset"), true).Contains("TCP/IP reset log fixture") ||
            !CheckScript("$partialReset = $true\n" + SimulateReset("tcpip-reset"), false).Contains("Some settings may already have changed."))
            throw new InvalidOperationException("The reset commands must retain output and report partial failures.");
    }
}
