using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed partial class DeveloperPackages
{
    internal string? RemovalTarget(string manager)
    {
        if (manager == "NuGet")
            return File.Exists(ManagedNuGet) && VerifyNuGet(ManagedNuGet) ? ManagedNuGet : null;
        if (manager == "PowerShell")
        {
            var folder = Path.Combine(ModuleRoot, ModuleVersion);
            return File.Exists(Path.Combine(folder, Module + ".psd1")) ? folder : null;
        }
        var target = SetupDetails(manager).Target;
        var root = ProgramManagedInventory.Roots().FirstOrDefault(r => r.Manager == manager && SamePath(r.Directory, target)
            && (manager != "Scoop" || r.Scope == "User"));
        if (root is null) return null;
        _ = ProgramManagedPackages.Engine(root);
        return target;
    }

    private static bool SamePath(string? a, string b) => a is not null
        && string.Equals(a.Trim().TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    internal static Guid? ChocolateyInstallerProduct()
    {
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var matches = new HashSet<Guid>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var entries = machine.OpenSubKey(uninstall);
            if (entries is null) continue;
            foreach (var name in entries.GetSubKeyNames())
            {
                using var key = entries.OpenSubKey(name);
                if (!string.Equals(key?.GetValue("DisplayName") as string, "Chocolatey (Install Only)", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(key?.GetValue("Publisher") as string, "Chocolatey Software, Inc.", StringComparison.OrdinalIgnoreCase)) continue;
                if (key?.GetValue("WindowsInstaller") is not 1 || !Guid.TryParseExact(name, "B", out var product)
                    || !IsMatchingChocolateyMsi(product, key.GetValue("UninstallString") as string))
                    throw new IOException("Alter Chocolatey-Eintrag nicht eindeutig bestätigt. / Old Chocolatey entry could not be verified.");
                matches.Add(product);
            }
        }
        if (matches.Count > 1) throw new IOException("Mehrere Chocolatey-MSI-Einträge gefunden. / Multiple Chocolatey MSI entries found.");
        return matches.Count == 1 ? matches.Single() : null;
    }

    internal static bool IsMatchingChocolateyMsi(Guid product, string? command)
    {
        if (command is null) return false;
        var match = Regex.Match(command, @"\A\s*msiexec(?:\.exe)?\s+/[IX]\s*(\{[0-9A-F-]{36}\})\s*\z", RegexOptions.IgnoreCase);
        return match.Success && Guid.TryParseExact(match.Groups[1].Value, "B", out var id) && id == product;
    }

    private static async Task RemoveChocolateyRegistrationAsync(Guid product)
    {
        if (ChocolateyInstallerProduct() != product)
            throw new IOException("Der Chocolatey-MSI-Eintrag hat sich geändert. / The Chocolatey MSI entry changed.");
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        { UseShellExecute = true, Verb = ElevationService.IsElevated() ? "" : "runas" };
        foreach (var arg in new[] { "/x", product.ToString("B"), "/qn", "/norestart" }) command.ArgumentList.Add(arg);
        using var process = Process.Start(command) ?? throw new IOException("Windows Installer konnte nicht gestartet werden. / Windows Installer could not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode is not (0 or 1605 or 3010) || ChocolateyInstallerProduct() is not null)
            throw new IOException($"Chocolatey-MSI-Eintrag nicht entfernt ({process.ExitCode}). / Chocolatey MSI entry was not removed ({process.ExitCode}).");
    }

    internal async Task<string?> RemoveManagerAsync(string manager, string expected)
    {
        var target = await Task.Run(() => RemovalTarget(manager));
        if (target is null || !SamePath(target, expected))
            throw new IOException("Die Installation konnte nicht eindeutig zugeordnet werden. / The installation could not be identified.");
        if (manager == "Scoop")
        {
            var root = new ProgramManagerRoot("Scoop", target, "User");
            ValidateDirectory(target);
            string engine = ProgramManagedPackages.Engine(root);
            ProgramManagedPackages.ScoopEngineOrigin(target, engine);
            var command = new ProcessStartInfo(Shell) { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "RemoteSigned", "-File", engine, "uninstall", "scoop" })
                command.ArgumentList.Add(arg);
            using var process = Process.Start(command) ?? throw new IOException("Scoop konnte nicht gestartet werden. / Could not start Scoop.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || File.Exists(engine) || File.Exists(Path.Combine(target, "shims", "scoop.ps1")))
                throw new IOException("Scoop wurde nicht vollständig entfernt oder der Vorgang wurde abgebrochen. / Scoop removal was incomplete or cancelled.");
            return null;
        }
        var registration = manager == "Chocolatey" ? ChocolateyInstallerProduct() : null;
        var saved = await Task.Run(() =>
        {
            if (manager == "NuGet")
            {
                ValidateDirectory(Path.GetDirectoryName(target)!);
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Verknüpfte NuGet-Datei wird nicht entfernt. / A linked NuGet executable cannot be removed.");
                File.Delete(target);
                if (File.Exists(target)) throw new IOException("NuGet wurde nicht entfernt. / NuGet was not removed.");
                if (SamePath(NuGetExecutable, target)) NuGetExecutable = null;
                return null;
            }
            ValidateDirectory(target);
            var suffix = ".PowerLeitstand-backup-" + Guid.NewGuid().ToString("N")[..8];
            if (manager == "PowerShell")
            {
                var backup = ModuleRoot + suffix;
                Directory.Move(target, backup);
                return backup;
            }
            if (!ElevationService.IsElevated())
                throw new IOException("Für Chocolatey PowerLeitstand als Administrator starten. / Start PowerLeitstand as administrator to remove Chocolatey.");
            return ArchiveChocolatey(target, target + suffix);
        });
        if (registration is { } product)
        {
            try { await RemoveChocolateyRegistrationAsync(product); }
            catch (Exception e) { throw new IOException(e.Message + "\nChocolatey-Backup: " + saved, e); }
        }
        return saved;
    }

    internal static string ArchiveChocolatey(string target, string backup)
    {
        ValidateDirectory(target);
        if (!Path.IsPathFullyQualified(backup)
            || !SamePath(Path.GetDirectoryName(Path.GetFullPath(backup)), Path.GetDirectoryName(Path.GetFullPath(target))!)
            || SamePath(target, backup))
            throw new IOException("Ungültiger Sicherungspfad. / Invalid backup path.");
        var edits = new List<(EnvironmentVariableTarget Scope, string Key, string? Old, string? New)>();
        foreach (var scope in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Process })
        {
            var install = Environment.GetEnvironmentVariable("ChocolateyInstall", scope);
            if (SamePath(install, target)) edits.Add((scope, "ChocolateyInstall", install, null));
            var path = Environment.GetEnvironmentVariable("Path", scope);
            if (path is null) continue;
            var filtered = string.Join(";", path.Split(';').Where(p => !SamePath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"')), Path.Combine(target, "bin"))
                && !(SamePath(install, target) && SamePath(p.Trim('"'), @"%ChocolateyInstall%\bin"))));
            if (path != filtered) edits.Add((scope, "Path", path, filtered));
        }
        Directory.Move(target, backup);
        int applied = 0;
        try
        {
            foreach (var edit in edits)
            {
                Environment.SetEnvironmentVariable(edit.Key, edit.New, edit.Scope);
                applied++;
            }
        }
        catch
        {
            Directory.Move(backup, target);
            foreach (var edit in edits.Take(applied).Reverse())
                Environment.SetEnvironmentVariable(edit.Key, edit.Old, edit.Scope);
            throw;
        }
        return backup;
    }
}
