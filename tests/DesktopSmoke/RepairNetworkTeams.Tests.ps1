$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath "$PSScriptRoot/../../src/PowerLeitstandPro/RepairNetworkQueries.cs" -Raw -Encoding UTF8
$match = [regex]::Match($source, '(?s)internal const string TeamInventory = """(.*?)""";')
if (-not $match.Success) { throw 'Network team inventory script missing' }
$command = [scriptblock]::Create($match.Groups[1].Value)

function Label($de, $en) { if ($english) { $en } else { $de } }
function AssertLine($result, $line) { if ($line -cnotin $result) { throw "Expected output: $line" } }
$adapters = @(
    [pscustomobject]@{ Name = 'LAN [1]'; Description = 'Physical [1]'; OperationalStatus = 'Up' }
    [pscustomobject]@{ Name = 'LAN 1'; Description = 'Physical 1'; OperationalStatus = 'Down' }
    [pscustomobject]@{ Name = 'LAN 2'; Description = 'Shared'; OperationalStatus = 'Up' }
    [pscustomobject]@{ Name = 'LAN 3'; Description = 'Shared'; OperationalStatus = 'Down' }
)

$lbfo = New-Module -Name NetLbfo -ScriptBlock {
    function Get-NetLbfoTeam {
        [CmdletBinding()] param()
        switch ($script:scenario) {
            'populated' {
                [pscustomobject]@{ Name = 'Office'; Status = 2; Members = @('LAN [1]', 'Missing') }
                [pscustomobject]@{ Name = 'Lab'; Status = 'Up'; Members = 'LAN 1' }
            }
            'failed' { throw [UnauthorizedAccessException]::new() }
        }
    }
}
$hyperV = New-Module -Name Hyper-V -ScriptBlock {
    function Get-VMSwitchTeam {
        [CmdletBinding()] param()
        switch ($script:scenario) {
            'populated' { [pscustomobject]@{ Name = 'Virtual'; NetAdapterInterfaceDescription = @('Physical [1]', 'Shared') } }
            'missing' { throw [System.Management.Automation.CommandNotFoundException]::new() }
        }
    }
}
$lbfo, $hyperV | Import-Module
try {
    & $lbfo { $script:scenario = 'populated' }
    & $hyperV { $script:scenario = 'populated' }
    $english = $false
    $result = & $command
    AssertLine $result 'Netzwerkteams'
    AssertLine $result "Office (LBFO) | Teamstatus: eingeschr$([char]0xE4)nkt"
    AssertLine $result 'Lab (LBFO) | Teamstatus: aktiv'
    AssertLine $result '  Mitglied: LAN [1] | Adapterstatus: aktiv'
    AssertLine $result '  Mitglied: LAN 1 | Adapterstatus: inaktiv'
    AssertLine $result '  Mitglied: Missing | Adapterstatus: nicht gemeldet'
    AssertLine $result 'Virtual (Hyper-V SET) | Teamstatus: nicht gemeldet'
    AssertLine $result '  Mitglied: Shared | Adapterstatus: nicht gemeldet'
    if (@($result | Where-Object { $_ -ceq '  Mitglied: LAN [1] | Adapterstatus: aktiv' }).Count -ne 2) {
        throw 'SET did not resolve the exact interface description'
    }

    $english = $true
    & $lbfo { $script:scenario = 'failed' }
    $result = & $command
    AssertLine $result 'LBFO: Teams could not be queried.'
    AssertLine $result 'Virtual (Hyper-V SET) | Team status: not reported'
    AssertLine $result '  Member: LAN [1] | Adapter status: up'

    & $lbfo { $script:scenario = 'empty' }
    & $hyperV { $script:scenario = 'missing' }
    $result = & $command
    AssertLine $result 'LBFO: No teams reported.'
    AssertLine $result 'Hyper-V SET: Windows module unavailable.'
} finally {
    Remove-Module -ModuleInfo @($lbfo, $hyperV)
}
'Network team inventory fixtures passed.'
