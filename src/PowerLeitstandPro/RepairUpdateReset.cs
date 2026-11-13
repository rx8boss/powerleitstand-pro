namespace PowerControlProWpf;

internal static class RepairUpdateReset
{
    internal static string WithDism => Script.Insert(Script.LastIndexOf('}'),
        "    & (Join-Path ([Environment]::SystemDirectory) 'dism.exe') /Online /Cleanup-Image /RestoreHealth" + Environment.NewLine);

    internal const string Script = """
        & {
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            $session = New-Object -ComObject Microsoft.Update.Session
            if ($session.CreateUpdateInstaller().IsBusy) {
                throw 'Windows Update is installing updates. / Windows Update installiert gerade Updates.'
            }

            $services = @(Get-Service -Name wuauserv, bits, cryptsvc, msiserver)
            $running = @()
            foreach ($service in $services) {
                if ($service.Status -eq 'Running') { $running += $service }
                elseif ($service.Status -ne 'Stopped') { throw "$($service.Name): $($service.Status)" }
            }

            $touched = [Collections.Generic.List[object]]::new()
            $renamed = [Collections.Generic.List[object]]::new()
            $wait = [TimeSpan]::FromSeconds(20)
            $suffix = '.old-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
            $restartErrors = @()
            try {
                foreach ($service in $running) {
                    $touched.Add($service)
                    Write-Output "Stop: $($service.Name)"
                    Stop-Service -InputObject $service -ErrorAction Stop
                    $service.WaitForStatus('Stopped', $wait)
                }
                foreach ($service in $services) {
                    $service.Refresh()
                    if ($service.Status -ne 'Stopped') { throw "$($service.Name): $($service.Status)" }
                }
                $windows = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
                $system = [Environment]::SystemDirectory
                foreach ($path in @((Join-Path $windows 'SoftwareDistribution'), (Join-Path $system 'catroot2'))) {
                    if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
                    $name = (Split-Path -Leaf $path) + $suffix
                    Rename-Item -LiteralPath $path -NewName $name
                    $renamed.Add(@{ Original = $path; Backup = (Join-Path (Split-Path -Parent $path) $name) })
                    Write-Output "Backup: $name"
                }
            }
            catch {
                for ($i = $renamed.Count - 1; $i -ge 0; $i--) {
                    $entry = $renamed[$i]
                    try { Rename-Item -LiteralPath $entry.Backup -NewName (Split-Path -Leaf $entry.Original) }
                    catch { Write-Output "Rollback: $($entry.Backup) - $($_.Exception.Message)" }
                }
                throw
            }
            finally {
                for ($i = $touched.Count - 1; $i -ge 0; $i--) {
                    $service = $touched[$i]
                    try {
                        $service.Refresh()
                        if ($service.Status -eq 'StopPending') { $service.WaitForStatus('Stopped', $wait) }
                        $service.Refresh()
                        if ($service.Status -eq 'Stopped') { $service.Start() }
                        $service.WaitForStatus('Running', $wait)
                        Write-Output "Running: $($service.Name)"
                    }
                    catch { $restartErrors += "$($service.Name): $($_.Exception.Message)" }
                }
                if ($restartErrors.Count) { throw "Service recovery / Dienstwiederherstellung: $($restartErrors -join ' | ')" }
            }
        }
        """;
}

