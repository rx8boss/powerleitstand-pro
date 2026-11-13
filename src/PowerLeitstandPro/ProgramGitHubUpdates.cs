using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record GitHubProgramUpdate(string Id, string Repository, string InstalledVersion, string Version,
    string Path, string Url, string Sha256, long Size, string Kind, string OriginalHash);

internal sealed class ProgramGitHubUpdates
{
    internal const long DownloadLimit = 768L * 1024 * 1024;
    internal static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
        { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" };
    internal static bool Eligible(string id, ProgramInstallation proof) => proof.Path is { } path && File.Exists(path)
        && proof.ManagedCopies is not { Count: > 0 } && proof.Version is not null
        && (id is "dlss-swapper" or "artcraft-launcher" or "powershell" or "powertoys" && proof.Source == "Registry"
            || id == "wowup-cf" && proof.Source == "Registry" && proof.LocationEvidence?.Alternatives is not { Count: > 1 }
                && path.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "wowup-cf", "WowUp-CF.exe"), StringComparison.OrdinalIgnoreCase)
            || id == "extra-ventoy-ventoy" && proof.Source == "Portable");

    internal static async Task<GitHubProgramUpdate?> ResolveAsync(ProgramEntry entry, ProgramInstallation proof, CancellationToken token, bool reinstall = false)
    {
        if (!Eligible(entry.Id, proof) || !ProgramPortableUpdates.PlainPath(proof.Path!)) return null;
        var product = FileVersionInfo.GetVersionInfo(proof.Path!);
        if (!ProductMatches(entry.Id, System.IO.Path.GetFileName(proof.Path!), product.ProductName, product.CompanyName)) return null;
        string? repository = ProgramVersions.GithubRepository(entry); if (repository is null) return null;
        var json = await CatalogWeb.ReadAsync(new("https://api.github.com/repos/" + repository + "/releases/latest"), Hosts, 2 * 1024 * 1024, token);
        return Parse(entry, proof, Encoding.UTF8.GetString(json), ProgramPortableUpdates.Hash(proof.Path!), reinstall);
    }

    internal static bool ProductMatches(string id, string filename, string? product, string? company) => id switch
    {
        "dlss-swapper" => filename.Equals("DLSS Swapper.exe", StringComparison.OrdinalIgnoreCase) && product == "DLSS Swapper" && company is "DLSS Swapper" or "beeradmoore",
        "extra-ventoy-ventoy" => filename.Equals("Ventoy2Disk.exe", StringComparison.OrdinalIgnoreCase) && ProgramLocalInventory.MatchesPortable(id, product, company),
        "artcraft-launcher" => filename.Equals("artcraft-launcher.exe", StringComparison.OrdinalIgnoreCase) && ProgramLocalInventory.MatchesPortable(id, product, company),
        "powershell" => filename.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) && product == "PowerShell" && company == "Microsoft Corporation",
        "powertoys" => filename.Equals("PowerToys.exe", StringComparison.OrdinalIgnoreCase) && product is "PowerToys" or "Microsoft PowerToys" && company == "Microsoft Corporation",
        "wowup-cf" => filename.Equals("WowUp-CF.exe", StringComparison.OrdinalIgnoreCase) && product == "WowUp-CF" && company == "WowUp LLC",
        _ => false
    };

    internal static GitHubProgramUpdate? Parse(ProgramEntry entry, ProgramInstallation proof, string json, string originalHash, bool reinstall = false)
    {
        if (!Eligible(entry.Id, proof)) return null;
        string? repository = ProgramVersions.GithubRepository(entry);
        string? version = ProgramVersions.ParseReleaseVersion(json);
        if (repository is null || version is null || !(ProgramVersions.IsNewer(proof.Version, version)
            || reinstall && ProgramPortableUpdates.SameVersion(proof.Version!, version))) return null;
        using var doc = JsonDocument.Parse(json);
        string tag = doc.RootElement.GetProperty("tag_name").GetString()!;
        string? name = AssetName(entry.Id, proof.Path!, version); if (name is null) return null;
        var candidates = doc.RootElement.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (candidates.Length != 1) return null;
        var asset = candidates[0]; var sha = asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null;
        long size = asset.GetProperty("size").GetInt64(); string? url = asset.GetProperty("browser_download_url").GetString();
        if (!Regex.IsMatch(sha ?? "", "^sha256:[a-fA-F0-9]{64}$") || size is <= 0 or > DownloadLimit
            || url != $"https://github.com/{repository}/releases/download/{tag}/{name}") return null;
        return new(entry.Id, repository, proof.Version!, version, proof.Path!, url, sha![7..], size,
            entry.Id switch { "dlss-swapper" or "wowup-cf" => "NSIS", "powertoys" => "WiX", "extra-ventoy-ventoy" => "VentoyArchive", _ => "MSI" }, originalHash);
    }

    internal static string? AssetName(string id, string path, string version)
    {
        if (id == "dlss-swapper") return $"DLSS.Swapper-{version}-installer.exe";
        if (id == "extra-ventoy-ventoy") return $"ventoy-{version}-windows.zip";
        string? architecture = ProgramPortableUpdates.Machine(path) switch { 0x8664 => "x64", 0xAA64 => "arm64", 0x14c => "x86", _ => null };
        if (id == "wowup-cf") return architecture == "x64" ? $"WowUp-CF-Setup-{version}.exe" : null;
        if (architecture is null) return null;
        if (id == "artcraft-launcher") return $"artcraft-launcher-{version}-windows-{architecture}.msi";
        if (id == "powershell") return $"PowerShell-{version}-win-{architecture}.msi";
        if (id == "powertoys" && architecture is "x64" or "arm64")
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + System.IO.Path.DirectorySeparatorChar;
            string machine = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + System.IO.Path.DirectorySeparatorChar;
            if (path.StartsWith(local, StringComparison.OrdinalIgnoreCase)) return $"PowerToysUserSetup-{version}-{architecture}.exe";
            if (path.StartsWith(machine, StringComparison.OrdinalIgnoreCase)) return $"PowerToysSetup-{version}-{architecture}.exe";
        }
        return null;
    }

    internal static void EnsureClosed(string path)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process) try
            { if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Bitte die Anwendung zuerst schließen. / Close the application first."); }
            catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { } catch (NotSupportedException) { }
        }
    }

    internal static async Task<int> ApplyAsync(ProgramEntry entry, GitHubProgramUpdate update, Action<ProgramStepEvent> step,
        Action<ProgramDownload> progress, CancellationToken token, bool reinstall = false)
    {
        EnsureClosed(update.Path);
        var proof = ProgramLocalInventory.Read([entry], token).GetValueOrDefault(entry.Id);
        if (proof is null || !string.Equals(proof.Path, update.Path, StringComparison.OrdinalIgnoreCase) || proof.Version != update.InstalledVersion
            || !ProgramPortableUpdates.PlainPath(update.Path) || ProgramPortableUpdates.Hash(update.Path) != update.OriginalHash)
            throw new IOException("Das installierte Ziel hat sich geändert. / Installed target changed.");
        if (await ResolveAsync(entry, proof, token, reinstall) != update) throw new IOException("Die GitHub-Quelle hat sich geändert. / GitHub source changed.");
        bool same = ProgramPortableUpdates.SameVersion(update.InstalledVersion, update.Version);
        if (reinstall && same && update.Kind == "WiX") throw new IOException("Dieser Installer unterstützt hier nur Reparatur. / This installer only supports repair here.");
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "GitHub Updates");
        string stage = System.IO.Path.Combine(root, "Download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            string download = System.IO.Path.Combine(stage, update.Kind switch { "NSIS" or "WiX" => "installer.exe", "MSI" => "installer.msi", _ => "package.zip" });
            step(new(ProgramStep.Download, ProgramStepState.Active));
            using (var response = await CatalogWeb.OpenAsync(new(update.Url), Hosts, token))
            {
                if (response.Content.Headers.ContentLength is { } length && length != update.Size) throw new IOException("Download size mismatch");
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var file = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await ProgramPortableUpdates.CopyDownloadAsync(source, file, update.Size, progress, token, DownloadLimit);
            }
            if (!ProgramPortableUpdates.Hash(download).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Checksum mismatch");
            step(new(ProgramStep.Download, ProgramStepState.Done)); token.ThrowIfCancellationRequested(); EnsureClosed(update.Path);
            if (ProgramPortableUpdates.Hash(update.Path) != update.OriginalHash) throw new IOException("Installed file changed");
            step(new(ProgramStep.Install, ProgramStepState.Active));
            if (update.Kind == "VentoyArchive")
            {
                await Task.Run(() => ApplyVentoyArchive(update, download, root), token);
                step(new(ProgramStep.Install, ProgramStepState.Done)); return 0;
            }
            var directory = System.IO.Path.GetDirectoryName(update.Path)!;
            if (directory.IndexOfAny(['\r', '\n', '"']) >= 0) throw new IOException("Invalid installer path");
            // Official NSIS installer: /D must be the final argument and must not be quoted.
            if (update.Id == "powertoys" && !ManufacturerSignature.Verify(download, "Microsoft Corporation")) throw new IOException("Microsoft signature not confirmed");
            var start = update.Kind == "MSI"
                ? new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = true, Arguments = "/i \"" + download + "\" /passive /norestart" + (reinstall && same ? " REINSTALL=ALL REINSTALLMODE=vomus" : "") }
                : new ProcessStartInfo(download) { UseShellExecute = true, Arguments = update.Kind == "WiX" ? "/install /quiet /norestart"
                    : (entry.Id == "wowup-cf" ? "/S /currentuser /D=" : "/S /D=") + directory };
            using var installer = Process.Start(start) ?? throw new IOException("Installer could not start");
            await installer.WaitForExitAsync(); // Cancellation never kills a running installer.
            if (installer.ExitCode == 0) step(new(ProgramStep.Install, ProgramStepState.Done));
            return installer.ExitCode;
        }
        finally { try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal static IReadOnlyList<(string Relative, byte[] Bytes)> VentoyPayload(string archive, string version)
    {
        var files = new List<(string Relative, byte[] Bytes)>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        using var zip = ZipFile.OpenRead(archive); string prefix = "ventoy-" + version + "/";
        foreach (var member in zip.Entries)
        {
            if (member.FullName.EndsWith('/')) continue;
            if (!member.FullName.StartsWith(prefix, StringComparison.Ordinal) || member.Length is <= 0 or > ProgramPortableUpdates.Limit
                || (member.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new IOException("Invalid Ventoy archive");
            string relative = member.FullName[prefix.Length..];
            if (relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or "..") || !names.Add(relative)) throw new IOException("Invalid archive path");
            if (relative.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase)) continue;
            total += member.Length; if (total > 512L * 1024 * 1024 || files.Count >= 3000) throw new IOException("Archive too large");
            using var input = member.Open(); using var bytes = new MemoryStream(); var buffer = new byte[81920]; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + read > member.Length || bytes.Length + read > ProgramPortableUpdates.Limit) throw new IOException("Archive size exceeded");
                bytes.Write(buffer, 0, read);
            }
            if (bytes.Length != member.Length) throw new IOException("Incomplete archive");
            files.Add((relative, bytes.ToArray()));
        }
        if (!files.Any(f => f.Relative == "Ventoy2Disk.exe") || !files.Any(f => f.Relative == "boot/boot.img")
            || !files.Any(f => f.Relative == "ventoy/version" && Encoding.UTF8.GetString(f.Bytes).Trim() == version)) throw new IOException("Ventoy package version not confirmed");
        return files;
    }

    internal static string ApplyVentoyArchive(GitHubProgramUpdate update, string archive, string backupRoot, Action<int>? beforeWrite = null)
    {
        if (update.Kind != "VentoyArchive" || System.IO.Path.GetFileName(update.Path) != "Ventoy2Disk.exe"
            || !ProgramPortableUpdates.PlainPath(update.Path) || ProgramPortableUpdates.Hash(update.Path) != update.OriginalHash
            || ProgramLocalInventory.PortableVersion(update.Id, update.Path) != update.InstalledVersion) throw new IOException("Ventoy target not verified");
        EnsureClosed(update.Path);
        if (!ProgramPortableUpdates.Hash(archive).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Checksum mismatch");
        var files = VentoyPayload(archive, update.Version);
        string target = System.IO.Path.GetDirectoryName(update.Path)!;
        string backup = System.IO.Path.Combine(backupRoot, "Ventoy-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        string verify = System.IO.Path.Combine(backup, "payload-check.exe");
        try
        {
            File.WriteAllBytes(verify, files.Single(f => f.Relative == "Ventoy2Disk.exe").Bytes);
            var product = FileVersionInfo.GetVersionInfo(verify);
            if (!ProgramLocalInventory.MatchesPortable(update.Id, product.ProductName, product.CompanyName)
                || ProgramPortableUpdates.Machine(verify) == 0 || ProgramPortableUpdates.Machine(verify) != ProgramPortableUpdates.Machine(update.Path))
                throw new IOException("Ventoy executable identity or architecture mismatch");
        }
        finally { if (File.Exists(verify)) File.Delete(verify); }
        var changed = new List<(string Path, string? Backup)>();
        try
        {
            int i = 0;
            foreach (var file in files)
            {
                beforeWrite?.Invoke(i++);
                string destination = System.IO.Path.GetFullPath(System.IO.Path.Combine(target, file.Relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(target + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid target");
                for (string? p = destination; p is not null && p.Length >= target.Length; p = System.IO.Path.GetDirectoryName(p))
                    if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked target rejected");
                string? previous = null;
                if (File.Exists(destination))
                {
                    previous = System.IO.Path.Combine(backup, file.Relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(previous)!); File.Copy(destination, previous);
                }
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllBytes(temporary, file.Bytes); File.Move(temporary, destination, true); changed.Add((destination, previous)); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (ProgramLocalInventory.PortableVersion(update.Id, update.Path) != update.Version) throw new IOException("Ventoy version verification failed");
            File.WriteAllText(System.IO.Path.Combine(backup, "backup.json"), JsonSerializer.Serialize(update)); return backup;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var file in changed.AsEnumerable().Reverse()) try
            { if (file.Backup is null) File.Delete(file.Path); else File.Copy(file.Backup, file.Path, true); }
            catch (Exception restore) { failures.Add(restore); }
            if (failures.Count > 0) throw new IOException("Restore failed. Backup: " + backup, new AggregateException(failures.Prepend(error)));
            throw;
        }
    }
}
