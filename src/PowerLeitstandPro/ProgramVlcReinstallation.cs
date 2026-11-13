using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record VlcRegisteredCopy(string Executable, string Version, string Architecture, string InstallerType, string Scope, string ProductKey)
{
    internal string Root => Path.GetDirectoryName(Executable)!;
    internal string Label => Architecture + " · " + (InstallerType == "wix" ? "MSI" : "EXE") + " · " + Root;
}

// A registered VLC reinstall must retain the selected copy, not WinGet's preferred variant.
internal static class ProgramVlcReinstallation
{
    internal static VlcRegisteredCopy? FindPath(string path, IReadOnlyList<VlcRegisteredCopy> copies)
    {
        var matches = copies.Where(c => string.Equals(c.Executable, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" };
    internal static string Version(string value) => System.Version.TryParse(value, out var version) && version.Revision == 0
        ? version.ToString(3) : value;
    internal static bool SamePlace(VlcRegisteredCopy a, VlcRegisteredCopy b) => a.Architecture == b.Architecture && a.InstallerType == b.InstallerType
        && a.Scope == b.Scope && string.Equals(a.Executable, b.Executable, StringComparison.OrdinalIgnoreCase);
    internal static VlcRegisteredCopy? Select(ProgramInstallation proof, IReadOnlyList<VlcRegisteredCopy> copies)
    {
        string? root = ProgramLocation.DirectoryOf(proof.Path);
        var matches = copies.Where(c => string.Equals(c.Root, root, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 && (proof.Version is null || ProgramPortableUpdates.SameVersion(proof.Version, matches[0].Version)) ? matches[0] : null;
    }
    internal static IReadOnlyList<VlcRegisteredCopy> Read()
    {
        var copies = new List<VlcRegisteredCopy>(); var entry = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view); using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string key in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var item = uninstall!.OpenSubKey(key);
                    if (!ProgramLocalInventory.Matches(entry, item?.GetValue("DisplayName") as string, item?.GetValue("Publisher") as string)) continue;
                    string? location = item?.GetValue("InstallLocation") as string;
                    string? file = location is { Length: > 0 } && Path.IsPathFullyQualified(location.Trim('"'))
                        ? Path.Combine(location.Trim('"'), "vlc.exe") : ProgramLocalInventory.ExistingIcon(item?.GetValue("DisplayIcon") as string);
                    if (file is null || !ProgramPortableUpdates.PlainPath(file) || !Path.GetFileName(file).Equals("vlc.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = FileVersionInfo.GetVersionInfo(file);
                    if (!ProgramLocalInventory.MatchesPortable("vlc", info.ProductName, info.CompanyName)) continue;
                    string? version = ProgramVersions.ConfirmedVersion(item?.GetValue("DisplayVersion") as string);
                    if (version is null || info.FileVersion is null || !ProgramPortableUpdates.SameVersion(version, info.FileVersion)) continue;
                    string? architecture = ProgramPortableUpdates.Machine(file) switch { 0x14c => "x86", 0x8664 => "x64", 0xaa64 => "arm64", _ => null };
                    if (architecture is null) continue;
                    string? kind = item?.GetValue("WindowsInstaller") is int flag && flag == 1 && Guid.TryParse(key, out _) ? "wix" : null;
                    if (kind is null && key == "VLC media player")
                    {
                        string? command = item?.GetValue("UninstallString") as string;
                        string expected = Path.Combine(Path.GetDirectoryName(file)!, "uninstall.exe");
                        if (string.Equals(command?.Trim().Trim('"'), expected, StringComparison.OrdinalIgnoreCase) && ProgramPortableUpdates.PlainPath(expected)) kind = "nullsoft";
                    }
                    if (kind is null) continue;
                    copies.Add(new(Path.GetFullPath(file), Version(version), architecture, kind, hive == RegistryHive.LocalMachine ? "machine" : "user", key));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or System.ComponentModel.Win32Exception) { }
        }
        return copies.Distinct().ToArray();
    }
    internal static async Task<string?> ManifestAsync(ProgramPackage package, VlcRegisteredCopy copy, CancellationToken token)
    {
        if (package.Id != "VideoLAN.VLC" || package.Version is null) return null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(8));
            byte[] data = await CatalogWeb.ReadAsync(ProgramInstallTargets.ManifestUri(package.Id, package.Version), Hosts, 512 * 1024, limit.Token).ConfigureAwait(false);
            return ParseManifest(package.Version, copy, Encoding.UTF8.GetString(data));
        }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); return null; }
    }
    // Deliberately limited to explicit ordinary VLC installer blocks. No YAML tags/aliases.
    internal static string? ParseManifest(string version, VlcRegisteredCopy copy, string yaml)
    {
        if (yaml.Length > 512 * 1024 || yaml.Contains('\t') || copy.Architecture is not ("x86" or "x64" or "arm64")
            || copy.InstallerType is not ("wix" or "nullsoft") || copy.Scope is not ("machine" or "user")
            || Regex.IsMatch(yaml, @"(?m)(?:^|\s)[&*!][A-Za-z_]|:\s*[|>]|^---|^\.\.\.")) return null;
        try
        {
            var lines = yaml.TrimStart('\uFEFF').Replace("\r", "").Split('\n');
            string? Value(IEnumerable<string> source, string prefix)
            {
                var found = source.Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                if (found.Length > 1) throw new FormatException();
                if (found.Length != 1) return null;
                string value = found[0][prefix.Length..].Trim();
                if (value.StartsWith('\'') || value.StartsWith('"'))
                { if (value.Length < 2 || value[^1] != value[0]) throw new FormatException(); return value[1..^1]; }
                return value;
            }
            if (Value(lines, "PackageIdentifier:") != "VideoLAN.VLC" || Value(lines, "PackageVersion:") != version || Value(lines, "ManifestType:") != "installer"
                || lines.Count(s => s == "Installers:") != 1) return null;
            int start = Array.IndexOf(lines, "Installers:"); int matches = 0;
            for (int n = start + 1; n < lines.Length; n++)
            {
                if (!lines[n].StartsWith("- Architecture:", StringComparison.Ordinal)) continue;
                int end = n + 1; while (end < lines.Length && (lines[end].Length == 0 || lines[end][0] == ' ' || lines[end][0] == '#')) end++;
                var block = lines[(n + 1)..end];
                if (lines[n]["- Architecture:".Length..].Trim() != copy.Architecture || Value(block, "  InstallerType:") != copy.InstallerType || Value(block, "  Scope:") != copy.Scope) { n = end - 1; continue; }
                string bit = copy.Architecture switch { "x86" => "win32", "x64" => "win64", _ => "winarm64" };
                string extension = copy.InstallerType == "wix" ? "msi" : "exe";
                if (Value(block, "  InstallerUrl:") != "https://download.videolan.org/pub/videolan/vlc/" + version + "/" + bit + "/vlc-" + version + "-" + bit + "." + extension
                    || !Regex.IsMatch(Value(block, "  InstallerSha256:") ?? "", @"^[A-Fa-f0-9]{64}$")
                    || Value(block, "  UpgradeBehavior:") != "install") return null;
                if (copy.InstallerType == "wix" && Value(block, "    InstallLocation:") != "APPLICATIONFOLDER=\"<INSTALLPATH>\"") return null;
                matches++; n = end - 1;
            }
            return matches == 1 ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))) : null;
        }
        catch (FormatException) { return null; }
    }
    internal static void Restrict(ProcessStartInfo start, VlcRegisteredCopy copy)
    {
        if (!Path.IsPathFullyQualified(copy.Executable) || copy.Executable.StartsWith(@"\\", StringComparison.Ordinal)
            || copy.Executable.Any(c => char.IsControl(c) || c == '"') || copy.Architecture is not ("x86" or "x64" or "arm64")
            || copy.InstallerType is not ("wix" or "nullsoft") || copy.Scope is not ("machine" or "user")) throw new IOException("VLC-Installationskopie nicht bestätigt. / VLC installation copy is unconfirmed.");
        foreach (var value in new[] { "--architecture", copy.Architecture, "--installer-type", copy.InstallerType, "--scope", copy.Scope, "--location", copy.Root }) start.ArgumentList.Add(value);
    }
}
