using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal static class AppUninstall
{
    internal const string PackageId = "rx8boss.PowerLeitstandPRO";
    private const string SourceId = "Microsoft.Winget.Source_8wekyb3d8bbwe";
    internal const string ProductCode = PackageId + "_" + SourceId;
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode;
    internal const string DataFolderName = "Power Control PRO";
    internal static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);
    internal sealed record Registration(string ProductCode, RegistryHive Hive, RegistryView View);

    internal static Registration? Detect(string executable)
    {
        var matches = new HashSet<Registration>();
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(UninstallKey);
            if (key is null) continue;
            var id = key.GetValue("WinGetPackageIdentifier") as string;
            var source = key.GetValue("WinGetSourceIdentifier") as string;
            var type = key.GetValue("WinGetInstallerType") as string;
            if (Matches(executable, key.GetValue("PortableTargetFullPath") as string,
                key.GetValue("InstallLocation") as string, id, source, type))
                matches.Add(new(id + "_" + source, hive, view));
        }
        // Windows may expose the same user registration through both registry views.
        return matches.Select(x => x.Hive).Distinct().Count() == 1 ? matches.First() : null;
    }

    internal static bool Matches(string executable, string? target, string? directory, string? id, string? source, string? type)
    {
        if (ProgramLocalInventory.PortableWinGetProductCode(id, source, type, ProductCode, PackageId) is null
            || target is null || directory is null || !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(target)
            || !Path.IsPathFullyQualified(directory) || executable.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        return string.Equals(Path.GetFullPath(executable), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                StringComparison.OrdinalIgnoreCase);
    }

    internal static void Launch(string executable, Registration selected, bool keepData, bool english)
    {
        if (Detect(executable) != selected)
            throw new IOException(english ? "The WinGet registration changed. Open the dialog again." : "Der WinGet-Eintrag hat sich geändert. Öffne den Dialog erneut.");
        using var self = Process.GetCurrentProcess();
        var winget = ProgramRemovalService.PortableWinGetUninstallCommand(selected.ProductCode, selected.Hive).FileName;
        if (!File.Exists(winget))
            throw new IOException(english ? "WinGet is unavailable." : "WinGet ist nicht verfügbar.");
        var command = CreateWorker(executable, selected, self.Id, self.StartTime.ToUniversalTime().Ticks, keepData, english);
        using var worker = Process.Start(command) ?? throw new IOException(english ? "The uninstaller could not start." : "Die Deinstallation konnte nicht gestartet werden.");
    }

    internal static ProcessStartInfo CreateWorker(string executable, Registration registration, int parentId, long startTicks, bool keepData, bool english)
    {
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Executable = executable, Root = DataFolderName, PackageId, SourceId, registration.ProductCode, Key = UninstallKey,
            Hive = registration.Hive.ToString(), View = registration.View.ToString(),
            Command = ProgramRemovalService.PortableWinGetUninstallCommand(registration.ProductCode, registration.Hive).ArgumentList.ToArray(),
            ParentId = parentId, StartTicks = startTicks.ToString(CultureInfo.InvariantCulture), Keep = keepData, English = english
        }));
        var script = "$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + WorkerScript;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(arg);
        return start;
    }

    private const string WorkerScript = """
        $ErrorActionPreference = 'Stop'
        function L($de, $en) { if ($payload.English) { $en } else { $de } }
        function Read-Registration {
            $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($payload.Hive, $payload.View)
            try { $base.OpenSubKey($payload.Key) } finally { $base.Dispose() }
        }
        function Get-SafeAttributes([string]$path) {
            try { $attributes = [IO.File]::GetAttributes($path) }
            catch [IO.FileNotFoundException] { return $null }
            catch [IO.DirectoryNotFoundException] { return $null }
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw (L 'Die Daten enthalten eine Ordner- oder Dateiverknüpfung.' 'The data contains a directory or file link.')
            }
            return $attributes
        }
        function Collect-LogItems([string]$path, $items) {
            $attributes = Get-SafeAttributes $path
            if ($null -eq $attributes) { return }
            $directory = [bool]($attributes -band [IO.FileAttributes]::Directory)
            if ($directory) {
                foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($path)) { Collect-LogItems $child $items }
            }
            $items.Add(@{ Path = $path; Directory = $directory })
        }
        function Remove-AppData([string]$root) {
            for ($ancestor = [IO.DirectoryInfo]::new($root); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
                $null = Get-SafeAttributes $ancestor.FullName
            }
            $logs = Join-Path $root 'logs'
            $settings = Join-Path $root 'settings.json'
            $settingsAttributes = Get-SafeAttributes $settings
            $logAttributes = Get-SafeAttributes $logs
            if (($settingsAttributes -band [IO.FileAttributes]::Directory) -or
                ($null -ne $logAttributes -and -not ($logAttributes -band [IO.FileAttributes]::Directory))) {
                throw (L 'Einstellungsdatei oder Protokollordner nicht eindeutig.' 'The settings file or log directory could not be verified.')
            }
            $items = [Collections.Generic.List[object]]::new()
            Collect-LogItems $logs $items
            foreach ($item in $items) {
                $null = Get-SafeAttributes $item.Path
                if ($item.Directory) { [IO.Directory]::Delete($item.Path) }
                else { [IO.File]::Delete($item.Path) }
            }
            [IO.File]::Delete($settings)
        }
        $removed = $false
        try {
            $parent = $null
            try { $parent = [Diagnostics.Process]::GetProcessById($payload.ParentId) } catch [ArgumentException] {}
            if ($null -ne $parent) {
                try {
                    $started = 0
                    if (-not $parent.HasExited) {
                        try { $started = $parent.StartTime.ToUniversalTime().Ticks }
                        catch { if (-not $parent.HasExited) { throw } }
                    }
                    if ($started -eq [long]$payload.StartTicks -and -not $parent.WaitForExit(30000)) {
                        throw (L 'PowerLeitstand PRO wurde nicht beendet.' 'PowerLeitstand PRO did not close.')
                    }
                } finally { $parent.Dispose() }
            }
            $key = Read-Registration
            try {
                if ($null -eq $key -or $key.GetValue('WinGetPackageIdentifier') -ne $payload.PackageId -or
                    $key.GetValue('WinGetSourceIdentifier') -ne $payload.SourceId -or $key.GetValue('WinGetInstallerType') -ne 'portable' -or
                    $key.GetValue('PortableTargetFullPath') -ne $payload.Executable -or
                    ([string]$key.GetValue('InstallLocation')).TrimEnd('\') -ne [IO.Path]::GetDirectoryName($payload.Executable)) {
                    throw (L 'Der WinGet-Eintrag hat sich geändert.' 'The WinGet registration changed.')
                }
            } finally { if ($null -ne $key) { $key.Dispose() } }
            $winget = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\WindowsApps\winget.exe'
            $arguments = [string[]]$payload.Command
            & $winget @arguments | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw ((L 'WinGet-Rückgabecode ' 'WinGet exit code ') + ('0x{0:X8}' -f $LASTEXITCODE))
            }
            $remaining = Read-Registration
            if ($null -ne $remaining) {
                $remaining.Dispose()
                throw (L 'Der WinGet-Eintrag ist noch vorhanden.' 'The WinGet registration still exists.')
            }
            if ([IO.File]::Exists($payload.Executable)) { throw (L 'Die Programmdatei ist noch vorhanden.' 'The executable still exists.') }
            $removed = $true
            if (-not $payload.Keep) {
                $local = [Environment]::GetFolderPath('LocalApplicationData')
                if ([string]::IsNullOrWhiteSpace($local)) { throw (L 'Benutzerordner fehlt.' 'User directory unavailable.') }
                Remove-AppData (Join-Path $local $payload.Root)
            }
        } catch {
            Add-Type -AssemblyName PresentationFramework
            $message = if ($removed) { L 'PowerLeitstand PRO wurde entfernt. Die Daten konnten nicht vollständig gelöscht werden.' 'PowerLeitstand PRO was removed. Some data could not be deleted.' }
                       else { L 'Die Deinstallation konnte nicht bestätigt werden. Einstellungen und Protokolle wurden behalten.' 'Uninstallation could not be verified. Settings and logs were kept.' }
            $null = [Windows.MessageBox]::Show(($message + "`n`n" + $_.Exception.Message), 'PowerLeitstand PRO', 'OK', 'Warning')
            exit 1
        }
        """;
}
