using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace PowerControlProWpf;

internal sealed partial class DeveloperPackages
{
    private const string Module = "Microsoft.PowerShell.PSResourceGet";
    private const string ModuleVersion = "1.1.1";
    private const string NuGetDownload = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe";
    private const string ScoopDownload = "https://raw.githubusercontent.com/ScoopInstaller/Install/master/install.ps1";
    private const string GalleryDownload = GallerySource + "/package/" + Module + "/" + ModuleVersion;
    private static string ManagedNuGet => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Tools", "nuget.exe");
    private string ModuleRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.GetFileName(Shell).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ? "PowerShell" : "WindowsPowerShell", "Modules", Module);

    internal (string Source, string Target) SetupDetails(string manager) => manager switch
    {
        "NuGet" => (NuGetDownload, ManagedNuGet),
        "PowerShell" => (GalleryDownload, Path.Combine(ModuleRoot, ModuleVersion)),
        "Scoop" => (ScoopDownload, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop")),
        "Chocolatey" => ("WinGet · Chocolatey.Chocolatey", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey")),
        _ => throw new ArgumentOutOfRangeException(nameof(manager))
    };

    internal static bool VerifyNuGet(string file) => ManufacturerSignature.Verify(file, "Microsoft Corporation");

    internal bool LocalSetupReady(string manager)
    {
        if (manager == "NuGet")
        {
            foreach (var file in new[] { NuGetExecutable, ManagedNuGet }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (file is null || !File.Exists(file) || !VerifyNuGet(file)) continue;
                NuGetExecutable = file;
                return true;
            }
            return false;
        }
        IOException? failure = null;
        foreach (var root in ProgramManagedInventory.Roots().Where(r => r.Manager == manager))
        {
            try { _ = ProgramManagedPackages.Engine(root); return true; }
            catch (IOException e) when (manager == "Scoop")
            {
                if (Directory.EnumerateFileSystemEntries(root.Directory).Any(p => !Path.GetFileName(p).Equals("persist", StringComparison.OrdinalIgnoreCase)))
                    failure = new IOException("Scoop-Ordner vorhanden, Programmdatei fehlt oder konnte nicht geprüft werden: " + root.Directory
                        + " / Scoop directory exists, but its executable is missing or could not be verified: " + root.Directory, e);
            }
            catch (IOException) { }
        }
        if (failure is not null) throw failure;
        return false;
    }

    internal async Task SetupAsync(string manager, CancellationToken token, Action<string> phase, Guid? approvedChocolateyEntry = null)
    {
        var (source, target) = SetupDetails(manager);
        bool repairModule = false;
        if (manager == "PowerShell")
        {
            phase("check");
            try { await InstalledAsync(false, token); return; }
            catch (MissingPSResourceGetException) { }
            catch (BrokenPSResourceGetException e) when (ModuleRepairMatches(target, e.Manifest)) { repairModule = true; }
        }
        else if (await Task.Run(() => LocalSetupReady(manager), token)) return;

        if (manager == "Chocolatey")
        {
            var winget = new WinGetService();
            if (!await winget.ProbeAsync(token))
                throw new IOException("WinGet fehlt. Bitte Microsoft App-Installer einrichten. / WinGet is missing. Set up Microsoft App Installer.");
            if (await winget.ResolveAsync("chocolatey", token, acceptSourceTerms: true) is null)
                throw new IOException("Chocolatey in WinGet nicht bestätigt. / Chocolatey was not verified in WinGet.");
            token.ThrowIfCancellationRequested();
            if (ChocolateyInstallerProduct() is { } product)
            {
                if (approvedChocolateyEntry != product)
                    throw new IOException("Alter Chocolatey-MSI-Eintrag gefunden. Dialog erneut öffnen und Bereinigung bestätigen. / Old Chocolatey MSI entry found. Reopen setup and confirm removal.");
                if (File.Exists(Path.Combine(target, "bin", "choco.exe"))
                    || ProgramManagedInventory.Roots().Any(r => r.Manager == "Chocolatey" && File.Exists(Path.Combine(r.Directory, "bin", "choco.exe"))))
                    throw new IOException("Eine Chocolatey-Programmdatei ist noch vorhanden. Bitte den Bestand erneut prüfen. / A Chocolatey executable still exists. Check the inventory again.");
                phase("cleanup");
                await RemoveChocolateyRegistrationAsync(product);
            }
            token.ThrowIfCancellationRequested();
            phase("install");
            var result = await _run(winget.Command("chocolatey", true, unattended: true), CancellationToken.None);
            if (result.ExitCode == unchecked((int)0x8A150061))
                throw new IOException("WinGet erkennt Chocolatey weiterhin als installiert. Bitte den Chocolatey-Eintrag unter „Installierte Apps“ prüfen. / WinGet still detects Chocolatey as installed. Check its entry in Installed apps.");
            if (result.ExitCode != 0)
                throw new IOException($"Chocolatey-Einrichtung beendet mit Code 0x{result.ExitCode:X8}. / Chocolatey setup exited with code 0x{result.ExitCode:X8}.");
        }
        else
        {
            if (manager == "Scoop" && ElevationService.IsElevated())
                throw new IOException("Scoop benötigt für diese Einrichtung einen Start ohne Administratorrechte. / Start the app without administrator privileges to set up Scoop.");
            var uri = new Uri(source);
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uri.Host };
            if (manager == "PowerShell") hosts.Add("cdn.powershellgallery.com");
            phase("download");
            var data = await CatalogWeb.ReadAsync(uri, hosts, 64 * 1024 * 1024, token);
            token.ThrowIfCancellationRequested();
            phase("install");
            if (manager == "PowerShell")
            {
                await ApplyModuleSetupAsync(data, target, repairModule, async () =>
                {
                    phase("verify");
                    await InstalledAsync(false, CancellationToken.None);
                });
                return;
            }
            if (manager == "NuGet")
            {
                var folder = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(folder);
                ValidateDirectory(folder);
                string staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".exe");
                try
                {
                    await File.WriteAllBytesAsync(staged, data);
                    if (!await Task.Run(() => VerifyNuGet(staged)))
                        throw new IOException("Microsoft-Signatur nicht bestätigt. / Microsoft signature not verified.");
                    File.Move(staged, target);
                    NuGetExecutable = target;
                }
                finally { File.Delete(staged); }
            }
            else
            {
                string script = Path.Combine(Path.GetTempPath(), "PowerLeitstand-Scoop-" + Guid.NewGuid().ToString("N") + ".ps1");
                try
                {
                    await File.WriteAllBytesAsync(script, data);
                    var start = new ProcessStartInfo(Shell) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File", script, "-ScoopDir", target })
                        start.ArgumentList.Add(arg);
                    var result = await _run(start, CancellationToken.None);
                    if (result.ExitCode != 0)
                    {
                        var details = result.Output.Trim();
                        if (details.Length > 1200) details = details[^1200..];
                        throw new IOException($"Scoop-Einrichtung fehlgeschlagen ({result.ExitCode}). / Scoop setup failed ({result.ExitCode}).\n{details}");
                    }
                }
                finally { File.Delete(script); }
            }
        }
        phase("verify");
        if (!await Task.Run(() => LocalSetupReady(manager)))
            throw new IOException("Einrichtung konnte nicht bestätigt werden. / Setup could not be verified.");
    }

    internal static bool ModuleRepairMatches(string target, string manifest) => Directory.Exists(target)
        && string.Equals(Path.Combine(target, Module + ".psd1"), manifest, StringComparison.OrdinalIgnoreCase);

    internal static async Task ApplyModuleSetupAsync(byte[] data, string target, bool repair, Func<Task> verify)
    {
        string? backup = null;
        bool installed = false;
        try
        {
            if (repair)
            {
                ValidateDirectory(target);
                string modules = Path.GetDirectoryName(Path.GetDirectoryName(target)!)!;
                backup = Path.Combine(modules, "." + Module + "-Backup-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, backup);
            }
            await Task.Run(() => InstallModuleArchive(data, target));
            installed = true;
            await verify();
        }
        catch
        {
            if (installed) Directory.Delete(target, true);
            if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, target);
            throw;
        }
        // The previous module stays outside the discovery path as a recovery copy.
    }

    internal static void InstallModuleArchive(byte[] data, string target)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2000 || zip.Entries.Sum(e => e.Length) > 128L * 1024 * 1024
            || zip.Entries.Any(e => e.FullName.Contains(':'))
            || zip.GetEntry(Module + ".psd1") is null)
            throw new InvalidDataException("Ungültiges PSResourceGet-Paket. / Invalid PSResourceGet package.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        ValidateDirectory(parent);
        string staging = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            zip.ExtractToDirectory(staging);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }
}
