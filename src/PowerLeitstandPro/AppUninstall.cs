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

    internal static void Launch(string executable, Registration? selected, string[] dataGroups, bool english)
    {
        if (!string.Equals(executable, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(FileVersionInfo.GetVersionInfo(executable).ProductName, typeof(AppUninstall).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
            || !ProgramPortableUpdates.PlainPath(executable) || !ProgramRemovalService.NoLinks(executable))
            throw new IOException(english ? "Cannot verify the running executable." : "Die laufende Programmdatei lässt sich nicht bestätigen.");
        if (selected is null && executable.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("WinGet", StringComparison.OrdinalIgnoreCase)
            || part.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)))
            throw new IOException(english ? "Check the installation with its package manager." : "Bitte die Installation über ihren Paketmanager prüfen.");
        if (Detect(executable) != selected)
            throw new IOException(english ? "The WinGet registration changed. Open the dialog again." : "Der WinGet-Eintrag hat sich geändert. Öffne den Dialog erneut.");
        using var self = Process.GetCurrentProcess();
        if (selected is not null && !File.Exists(ProgramRemovalService.PortableWinGetUninstallCommand(selected.ProductCode, selected.Hive).FileName))
            throw new IOException(english ? "WinGet is unavailable." : "WinGet ist nicht verfügbar.");
        var command = CreateWorker(executable, selected, self.Id, self.StartTime.ToUniversalTime().Ticks, dataGroups, english, ProgramPortableUpdates.Hash(executable));
        using var worker = Process.Start(command) ?? throw new IOException(english ? "The uninstaller could not start." : "Die Deinstallation konnte nicht gestartet werden.");
    }

    internal static ProcessStartInfo CreateWorker(string executable, Registration? registration, int parentId, long startTicks, string[] groups, bool english, string hash)
    {
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Executable = executable, Root = DataFolderName, PackageId, SourceId, Hash = hash, Key = UninstallKey,
            Hive = registration?.Hive.ToString(), View = registration?.View.ToString(),
            Command = registration is null ? [] : ProgramRemovalService.PortableWinGetUninstallCommand(registration.ProductCode, registration.Hive).ArgumentList.ToArray(),
            ParentId = parentId, StartTicks = startTicks.ToString(CultureInfo.InvariantCulture), Groups = groups, English = english
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
        function Check-Chain([string]$path) {
            for ($ancestor = [IO.DirectoryInfo]::new($path); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
                $null = Get-SafeAttributes $ancestor.FullName
            }
        }
        function Remove-OwnedItem([string]$root, [string]$leaf) {
            $path = Join-Path $root $leaf
            Check-Chain $path
            $attributes = Get-SafeAttributes $path
            if ($null -eq $attributes) { return }
            if ([bool]($attributes -band [IO.FileAttributes]::Directory) -ne ($leaf -ne 'settings.json')) {
                throw (L 'Unerwarteter Dateityp.' 'Unexpected file type.')
            }
            $items = [Collections.Generic.List[object]]::new()
            Collect-LogItems $path $items
            foreach ($item in $items) {
                Check-Chain $item.Path
                if ($item.Directory) { [IO.Directory]::Delete($item.Path) } else { [IO.File]::Delete($item.Path) }
            }
            if ($null -ne (Get-SafeAttributes $path)) { throw (L 'Noch vorhanden.' 'Still present.') }
        }
        $results = [Collections.Generic.List[string]]::new()
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
            Check-Chain $payload.Executable
            if ($payload.Hive) {
                $stream = [IO.File]::OpenRead($payload.Executable)
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                finally { $sha.Dispose(); $stream.Dispose() }
                if ($digest -ne $payload.Hash) { throw (L 'Die Programmdatei hat sich geändert.' 'The executable changed.') }
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
            }
            if (!$payload.Hive -or $null -ne (Get-SafeAttributes $payload.Executable)) {
                Add-Type -TypeDefinition @'
        using System;
        using System.IO;
        using System.Text;
        using System.ComponentModel;
        using System.Runtime.InteropServices;
        using System.Security.Cryptography;
        using Microsoft.Win32.SafeHandles;
        public static class PortableRemoval {
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
            static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint mode, uint flags, IntPtr template);
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
            static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
            [DllImport("kernel32.dll", SetLastError=true)]
            static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref byte value, uint size);
            public static void Remove(string path, string hash) {
                using (var handle = CreateFile(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero)) {
                    if (handle.IsInvalid) throw new Win32Exception();
                    var actual = new StringBuilder(32768);
                    uint length = GetFinalPathNameByHandle(handle, actual, (uint)actual.Capacity, 0);
                    if (length == 0 || length >= actual.Capacity || !String.Equals(actual.ToString(), @"\\?\" + Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Dateipfad geändert / File path changed");
                    using (var stream = new FileStream(handle, FileAccess.Read))
                    using (var sha = SHA256.Create()) {
                        if (!String.Equals(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""), hash, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("Programmdatei geändert / Executable changed");
                        byte delete = 1;
                        if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) throw new Win32Exception();
                    }
                }
            }
        }
        '@
                [PortableRemoval]::Remove($payload.Executable, $payload.Hash)
            }
            if ($null -ne (Get-SafeAttributes $payload.Executable)) { throw (L 'Die Programmdatei ist noch vorhanden.' 'The executable still exists.') }
            $removed = $true
            $root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $payload.Root
            $failed = $false
            foreach ($group in $payload.Groups) {
                $leaves = switch ($group) { 'logs' { 'logs' } 'settings' { 'settings.json'; 'sound-cache' } 'backups' { 'Backups' } default { throw 'Unknown selection' } }
                foreach ($leaf in $leaves) {
                    try { Remove-OwnedItem $root $leaf; $results.Add("$leaf - OK") }
                    catch { $failed = $true; $results.Add("$leaf - " + $_.Exception.Message) }
                }
            }
            if ($failed) { throw ($results -join [Environment]::NewLine) }
            Add-Type -AssemblyName PresentationFramework
            $null = [Windows.MessageBox]::Show(((L 'Programm entfernt.' 'Program removed.') + [Environment]::NewLine + ($results -join [Environment]::NewLine)), 'PowerLeitstand PRO', 'OK', 'Information')
        } catch {
            Add-Type -AssemblyName PresentationFramework
            $message = if ($removed) { L 'PowerLeitstand PRO wurde entfernt. Die Daten konnten nicht vollständig gelöscht werden.' 'PowerLeitstand PRO was removed. Some data could not be deleted.' }
                       else { L 'Die Deinstallation konnte nicht bestätigt werden. Einstellungen und Protokolle wurden behalten.' 'Uninstallation could not be verified. Settings and logs were kept.' }
            $null = [Windows.MessageBox]::Show(($message + "`n`n" + $_.Exception.Message), 'PowerLeitstand PRO', 'OK', 'Warning')
            exit 1
        }
        """;
}
