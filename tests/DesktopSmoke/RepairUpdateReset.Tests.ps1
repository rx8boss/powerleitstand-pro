$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath "$PSScriptRoot/../../src/PowerLeitstandPro/RepairUpdateReset.cs" -Raw
$match = [regex]::Match($source, '(?s)internal const string Script = """\s*(& \{.*?\})\s*""";')
if (-not $match.Success) { throw 'Reset script missing' }
$command = [scriptblock]::Create($match.Groups[1].Value)

function Assert($ok, $message) { if (-not $ok) { throw $message } }
function New-Object { param($ComObject) [pscustomobject]@{} | Add-Member -PassThru ScriptMethod CreateUpdateInstaller { @{ IsBusy = $script:busy } } }
function Get-Service { param($Name) $script:services }
function Stop-Service { param($InputObject, $ErrorAction) if ($InputObject.Name -eq $script:stopFailure) { throw 'Stop blocked' }; $InputObject.Status = 'Stopped' }
function Test-Path { param($LiteralPath, $PathType) $script:folders.Contains((Split-Path -Leaf $LiteralPath)) }
function Rename-Item {
    param($LiteralPath, $NewName)
    $previous = Split-Path -Leaf $LiteralPath
    if ($previous -eq $script:renameFailure) { throw 'Rename blocked' }
    [void]$script:folders.Remove($previous)
    $script:folders.Add($NewName)
}

foreach ($scenario in 'success', 'busy', 'stop failure', 'second rename failure', 'restart failure') {
    $script:busy = $scenario -eq 'busy'
    $script:stopFailure = if ($scenario -eq 'stop failure') { 'cryptsvc' }
    $script:renameFailure = if ($scenario -eq 'second rename failure') { 'catroot2' }
    $script:restartFailure = if ($scenario -eq 'restart failure') { 'cryptsvc' }
    $script:folders = [Collections.Generic.List[string]]::new()
    $script:folders.Add('SoftwareDistribution')
    $script:folders.Add('catroot2')
    $script:services = @(foreach ($name in 'wuauserv', 'bits', 'cryptsvc', 'msiserver') {
        [pscustomobject]@{ Name = $name; Status = if ($name -eq 'msiserver') { 'Stopped' } else { 'Running' } } |
            Add-Member -PassThru ScriptMethod Refresh {} |
            Add-Member -PassThru ScriptMethod WaitForStatus { param($Status, $Wait) if ($this.Status -ne $Status) { throw 'Unexpected status' } } |
            Add-Member -PassThru ScriptMethod Start { if ($this.Name -eq $script:restartFailure) { throw 'Start blocked' }; $this.Status = 'Running' }
    })
    $failure = $null
    try { & $command | Out-Null } catch { $failure = $_ }
    Assert (($null -eq $failure) -eq ($scenario -eq 'success')) "Unexpected result for $scenario"
    foreach ($service in $script:services) {
        $expected = if ($service.Name -in @('msiserver', $script:restartFailure)) { 'Stopped' } else { 'Running' }
        Assert ($service.Status -eq $expected) "$scenario failed to restore $($service.Name)"
    }
    if ($scenario -in 'success', 'restart failure') {
        Assert (@($script:folders | Where-Object { $_ -match '\.old-\d{8}-\d{6}-[a-f0-9]{8}$' }).Count -eq 2) 'Expected two uniquely named backup folders'
    } else {
        Assert ($script:folders.Contains('SoftwareDistribution') -and $script:folders.Contains('catroot2')) "$scenario did not preserve both original folders"
    }
    Write-Output "PASS $scenario"
}

