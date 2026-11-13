using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record InstalledProgram(string Id, string Name, string Publisher, string? Version, string? Location,
    string Source, RegistryHive Hive = RegistryHive.CurrentUser, RegistryView View = RegistryView.Registry64,
    string? RegistryPath = null, string? Command = null, string? CatalogId = null, bool Protected = false,
    ProgramPathEvidence? LocationEvidence = null, string? PackageVersion = null);
internal sealed record ProgramRemainder(string Kind, string Path, string Description);

internal sealed class ProgramRemovalService
{
    internal static string? LastReadWarning { get; private set; }
    private const string UninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal static IReadOnlyList<InstalledProgram> Read(CancellationToken token)
        => ReadWithLocations(token, new ProgramPathResolver());
    internal static IReadOnlyList<InstalledProgram> ReadWithLocations(CancellationToken token, ProgramPathResolver locations)
    {
        var found = new List<InstalledProgram>();
        LastReadWarning = null;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view); using var parent = root.OpenSubKey(UninstallRoot);
            foreach (var child in parent?.GetSubKeyNames() ?? [])
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var key = parent!.OpenSubKey(child); string? name = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 250 || key?.GetValue("SystemComponent") is int system && system == 1) continue;
                    string publisher = key?.GetValue("Publisher") as string ?? "";
                    string? command = key?.GetValue("UninstallString") as string;
                    var catalog = ProgramCatalog.Entries.Where(e => ProgramLocalInventory.Matches(e, name, publisher)).ToArray();
                    string path = UninstallRoot + "\\" + child;
                    string? version = ProgramVersions.ConfirmedVersion(key?.GetValue("DisplayVersion") as string);
                    var location = locations.Resolve(ProgramPathResolver.Registration(key!, child, name, publisher, version, hive, view), catalog.Length == 1 ? catalog[0] : null, token);
                    found.Add(new(hive + "|" + view + "|" + child, name, publisher,
                        version, location?.Path, "Registry", hive, view, path, command,
                        catalog.Length == 1 ? catalog[0].Id : null, key?.GetValue("NoRemove") is int noRemove && noRemove == 1 || command is null,
                        LocationEvidence: location));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
        var local = ProgramLocalInventory.Read(ProgramCatalog.Entries, token, includeManaged: false, pathResolver: locations);
        foreach (var pair in local.Where(p => p.Value.Source is "Portable" or "Portable-Setup"))
        {
            var entry = ProgramCatalog.Entries.Single(e => e.Id == pair.Key); var proof = pair.Value;
            found.Add(new("portable|" + pair.Key + "|" + proof.Path, entry.Name, entry.Publisher, proof.Version, proof.Path, proof.Source, CatalogId: entry.Id));
        }
        var managed = ProgramManagedInventory.Read(ProgramCatalog.Entries, token);
        found.AddRange(ProgramManagedInventory.Rows(ProgramCatalog.Entries, managed));
        ProgramStoreApplicationsSnapshot store;
        try { store = ProgramStoreApplications.ReadAsync(token).GetAwaiter().GetResult(); }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { token.ThrowIfCancellationRequested(); store = new([], false, e.Message); }
        if (!store.Complete) LastReadWarning = store.Error;
        found.AddRange(ProgramStoreApplications.Rows(store.Applications));
        return found.DistinctBy(p => p.Source + "|" + p.Name + "|" + p.Version + "|" + p.Location + "|" + p.Command + "|" + (p.Source is "Scoop" or "Chocolatey" ? p.Id : ""), StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static ProcessStartInfo UninstallCommand(InstalledProgram program)
    {
        if (program.Protected || program.Source != "Registry" || program.Command is null || program.RegistryPath is null) throw new IOException("Kein bestätigter Deinstallationsweg. / No verified uninstaller.");
        using var root = RegistryKey.OpenBaseKey(program.Hive, program.View); using var key = root.OpenSubKey(program.RegistryPath);
        if ((key?.GetValue("UninstallString") as string) != program.Command || (key?.GetValue("DisplayName") as string) != program.Name)
            throw new IOException("Der Programmeintrag hat sich geändert. / Program entry changed.");
        return ParseCommand(program.Command);
    }

    internal static ProcessStartInfo ParseCommand(string value)
    {
        string command = Environment.ExpandEnvironmentVariables(value.Trim());
        var quoted = Regex.Match(command, "^\"([^\"]+\\.exe)\"(?:\\s+(.*))?$", RegexOptions.IgnoreCase);
        var bare = Regex.Match(command, @"^(.+?\.exe)(?:\s+(.*))?$", RegexOptions.IgnoreCase);
        var match = quoted.Success ? quoted : bare;
        if (!match.Success) throw new IOException("Uninstaller command is not a verified EXE");
        string file = match.Groups[1].Value, arguments = match.Groups[2].Value;
        if (string.Equals(file, "msiexec.exe", StringComparison.OrdinalIgnoreCase)) file = System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        if (!System.IO.Path.IsPathFullyQualified(file) || !File.Exists(file) || !ProgramPortableUpdates.PlainPath(file)
            || file.IndexOfAny(['\r', '\n', '"']) >= 0 || arguments.IndexOfAny(['\r', '\n']) >= 0) throw new IOException("Invalid uninstaller path");
        if (System.IO.Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            var product = Regex.Match(arguments, @"^/[IX]\s*(\{[0-9a-f-]{36}\})(?:\s.*)?$", RegexOptions.IgnoreCase);
            if (!product.Success) throw new IOException("MSI product identity not confirmed");
            arguments = "/x " + product.Groups[1].Value + " /norestart";
        }
        if (new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "regsvr32.exe" }.Contains(System.IO.Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Skriptbasierte Deinstallation bitte über Windows ausführen. / Use Windows for script-based uninstallers.");
        return new(file) { UseShellExecute = true, Arguments = arguments };
    }

    internal static async Task<int> RemoveAsync(InstalledProgram program)
    {
        if (program.Protected) throw new IOException("Kein bestätigter Deinstallationsweg. / No verified uninstaller.");
        if (program.Source == "Windows-App")
        {
            string family = ProgramVersions.StoreFamilies.GetValueOrDefault(program.CatalogId ?? "") ?? throw new IOException("Store identity not confirmed");
            if (!Regex.IsMatch(family, @"^[A-Za-z0-9._-]+$")) throw new IOException("Invalid Store family");
            var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
            string script = "$ErrorActionPreference='Stop'; $p=@(Get-AppxPackage -PackageTypeFilter Main | Where-Object {$_.PackageFamilyName -eq '" + family + "'}); if($p.Count -ne 1){throw 'Store identity changed'}; Remove-AppxPackage -Package $p[0].PackageFullName -ErrorAction Stop";
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
            var result = await WinGetService.QueryAsync(start, CancellationToken.None);
            if (result.ExitCode != 0) throw new IOException(result.Output); return result.ExitCode;
        }
        if (program.Source is "Portable" or "Portable-Setup")
        {
            string path = program.Location ?? throw new IOException("Portable location missing");
            if (program.CatalogId is null || !ProgramPortableUpdates.PlainPath(path)) throw new IOException("Portable target not verified");
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!ProgramLocalInventory.MatchesPortable(program.CatalogId, info.ProductName, info.CompanyName)) throw new IOException("Portable identity changed");
            ProgramGitHubUpdates.EnsureClosed(path);
            string backup = BackupDirectory(program); string target = System.IO.Path.Combine(backup, System.IO.Path.GetFileName(path));
            File.Copy(path, target); if (ProgramPortableUpdates.Hash(target) != ProgramPortableUpdates.Hash(path)) throw new IOException("Backup failed");
            File.Delete(path); return 0;
        }
        using var process = Process.Start(UninstallCommand(program)) ?? throw new IOException("Uninstaller could not start");
        await process.WaitForExitAsync(); return process.ExitCode;
    }

    internal static bool IsPresent(InstalledProgram program)
    {
        if (program.Source is "Portable" or "Portable-Setup") return File.Exists(program.Location);
        if (program.Source == "Windows-App") return ProgramVersions.ReadStoreInstallationsAsync(CancellationToken.None).GetAwaiter().GetResult().ContainsKey(program.CatalogId!);
        using var root = RegistryKey.OpenBaseKey(program.Hive, program.View); using var key = root.OpenSubKey(program.RegistryPath!);
        return key is not null; // An unchanged/stale uninstall key is not proof of successful removal.
    }

    internal static string BackupDirectory(InstalledProgram program)
    {
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Removal Backups",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); File.WriteAllText(System.IO.Path.Combine(root, "program.json"), JsonSerializer.Serialize(program)); return root;
    }

    internal static bool SafeLeaf(string name) => name.Length is >= 4 and <= 100 && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
        && !new[] { "Windows", "Microsoft", "Programs", "Software", "Common Files" }.Contains(name, StringComparer.OrdinalIgnoreCase) && name != "." && name != "..";

    internal static IReadOnlyList<ProgramRemainder> ScanRemainders(InstalledProgram program)
    {
        var found = new List<ProgramRemainder>();
        if (IsPresent(program)) return found;
        if (program.Location is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ProgramGitHubUpdates.EnsureClosed(exe);
        if (SafeLeaf(program.Name))
        {
            foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            {
                string root = Environment.GetFolderPath(folder);
                var names = new List<string> { program.Name };
                if (SafeLeaf(program.Publisher) && !program.Publisher.Equals(program.Name, StringComparison.OrdinalIgnoreCase)) names.Add(System.IO.Path.Combine(program.Publisher, program.Name));
                foreach (var name in names)
                {
                    string path = System.IO.Path.Combine(root, name);
                    if (Directory.Exists(path) && NoLinks(path) && TreeWithinLimits(path))
                        found.Add(new("Directory", path, "AppData · kann Einstellungen und persönliche Daten enthalten / may contain settings and personal data"));
                }
            }
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            foreach (string path in RegistryCandidates(program))
            { using var key = user.OpenSubKey(path); if (key is not null) found.Add(new("Registry", path, "HKCU · programmeigener Schlüssel / program-specific key")); }
        }
        // Known exact user shortcuts only. Never crawl or delete shared publisher directories.
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
        {
            string path = System.IO.Path.Combine(Environment.GetFolderPath(folder), program.Name + ".lnk");
            if (SafeLeaf(program.Name) && File.Exists(path) && NoLinks(path) && ShortcutMatches(path, program.Location)) found.Add(new("Shortcut", path, "Verknüpfung / shortcut"));
        }
        return found;
    }
    private static bool ShortcutMatches(string shortcut, string? location)
    {
        if (location is null || !System.IO.Path.IsPathFullyQualified(location)) return false;
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type is null) return false;
            shell = Activator.CreateInstance(type); link = ((dynamic)shell!).CreateShortcut(shortcut);
            string target = ((dynamic)link).TargetPath;
            if (!System.IO.Path.IsPathFullyQualified(target)) return false;
            target = System.IO.Path.GetFullPath(target); string expected = System.IO.Path.GetFullPath(location).TrimEnd('\\');
            return expected.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target.Equals(expected, StringComparison.OrdinalIgnoreCase)
                : target.StartsWith(expected + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentException or NotSupportedException) { return false; }
        finally
        {
            if (link is not null && System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
    internal static IEnumerable<string> RegistryCandidates(InstalledProgram p)
    {
        if (!SafeLeaf(p.Name)) yield break;
        yield return "Software\\" + p.Name;
        if (SafeLeaf(p.Publisher) && !p.Publisher.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) yield return "Software\\" + p.Publisher + "\\" + p.Name;
    }
    internal static bool NoLinks(string path)
    {
        for (string? p = System.IO.Path.GetFullPath(path); p is not null; p = System.IO.Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static bool TreeWithinLimits(string path)
    {
        int count = 0; long bytes = 0; var pending = new Stack<string>(); pending.Push(path);
        while (pending.TryPop(out var directory)) foreach (var child in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (++count > 5000 || (child.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (child is DirectoryInfo sub) pending.Push(sub.FullName);
            else { bytes += ((FileInfo)child).Length; if (bytes > 512L * 1024 * 1024) return false; }
        }
        return true;
    }
    internal static async Task<string> CleanAsync(InstalledProgram program, IReadOnlyList<ProgramRemainder> selected)
    {
        if (IsPresent(program)) throw new IOException("Die Deinstallation ist nicht bestätigt. / Uninstall not verified.");
        var allowed = ScanRemainders(program);
        if (selected.Count == 0 || selected.Any(p => !allowed.Contains(p))) throw new IOException("Remainder selection changed");
        string backup = BackupDirectory(program); int index = 0;
        foreach (var item in selected)
        {
            string target = System.IO.Path.Combine(backup, (index++).ToString());
            if (item.Kind == "Registry")
            {
                var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "reg.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "export", "HKCU\\" + item.Path, target + ".reg", "/y" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Registry backup could not start"); await process.WaitForExitAsync();
                if (process.ExitCode != 0 || !File.Exists(target + ".reg") || new FileInfo(target + ".reg").Length == 0) throw new IOException("Registry backup failed");
                using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64); user.DeleteSubKeyTree(item.Path, false);
            }
            else if (item.Kind == "Directory")
            {
                if (!NoLinks(item.Path) || !TreeWithinLimits(item.Path)) throw new IOException("Directory changed");
                Directory.Move(item.Path, target); // AppData targets and backup are on the same user volume.
            }
            else { if (!NoLinks(item.Path)) throw new IOException("Shortcut changed"); File.Copy(item.Path, target); File.Delete(item.Path); }
            File.AppendAllText(System.IO.Path.Combine(backup, "items.jsonl"), JsonSerializer.Serialize(new { item, Backup = target }) + Environment.NewLine);
        }
        return backup;
    }
}
