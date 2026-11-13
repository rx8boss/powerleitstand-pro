using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http;

namespace PowerControlProWpf;

internal sealed partial class ProgramPortableUpdates
{
    private Task<byte[]> Read(string url, CancellationToken token) => _read(new(url), token);
    internal static string? ManifestField(string text, string key)
    {
        var values = Regex.Matches(text, @"(?m)^\s*" + Regex.Escape(key) + @":\s*([^\r\n]+)")
            .Select(m => m.Groups[1].Value.Trim().Trim('\'', '"')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 1 ? values[0] : null;
    }
    internal static (string Url, string Sha)? ManifestArtifact(string text, string id, string version, string expectedUrl)
    {
        var sha = ManifestField(text, "InstallerSha256");
        return ManifestField(text, "PackageIdentifier") == id && ManifestField(text, "PackageVersion") == version
            && ManifestField(text, "InstallerUrl") == expectedUrl && Regex.IsMatch(sha ?? "", "^[a-fA-F0-9]{64}$")
            ? (expectedUrl, sha!) : null;
    }
    internal static string? CpuChecksum(string text, string version)
    {
        var matches = Regex.Matches(text, @"(?m)^cpu-z_" + Regex.Escape(version) + @"-en\.zip\s*:\s*([a-fA-F0-9]{64})\s*$");
        return matches.Count == 1 ? matches[0].Groups[1].Value : null;
    }
    internal static bool CpuArtifactMatches(byte[] zipBytes, string expectedHash, IReadOnlyList<PortablePart> parts)
    {
        if (!Convert.ToHexString(SHA256.HashData(zipBytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (var part in parts)
        {
            var members = archive.Entries.Where(e => e.FullName == part.Member).ToArray();
            if (members.Length != 1 || members[0].Length is <= 0 or > Limit) return false;
            using var file = members[0].Open();
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(part.OriginalHash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return parts.Count > 0;
    }
    internal static string? GpuFileId(string page, string version, string sha)
    {
        var match = Regex.Match(page, @"GPU-Z\." + Regex.Escape(version) + @"\.exe</div>([\s\S]{0,2500}?)name=""id"" value=""(\d+)""");
        if (!match.Success || !match.Groups[1].Value.Contains(sha, StringComparison.OrdinalIgnoreCase)) return null;
        return match.Groups[2].Value;
    }
    private async Task<HttpResponseMessage> OpenGpuDownloadAsync(PortableUpdate update, CancellationToken token)
    {
        const string url = "https://www.techpowerup.com/download/techpowerup-gpu-z/";
        var page = Encoding.UTF8.GetString(await Read(url, token));
        var id = GpuFileId(page, update.Version, update.Sha256) ?? throw new IOException("Der GPU-Z-Herstellerdownload hat sich geändert. Bitte erneut prüfen.");
        async Task<HttpResponseMessage> Post(Dictionary<string, string> form)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
            return await CatalogWeb.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
        async Task<HttpResponseMessage> Redirect(HttpResponseMessage response)
        {
            var uri = response.Headers.Location;
            if ((int)response.StatusCode is not (302 or 303) || uri is null || !uri.IsAbsoluteUri || !CatalogWeb.IsAllowed(uri, Hosts)
                || !uri.Host.EndsWith("-dl.techpowerup.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.EndsWith("/GPU-Z." + update.Version + ".exe", StringComparison.Ordinal))
                throw new IOException("Das GPU-Z-Downloadziel wurde nicht bestätigt.");
            return await CatalogWeb.OpenAsync(uri, Hosts, token);
        }
        using var selection = await Post(new() { ["id"] = id });
        if ((int)selection.StatusCode is 302 or 303) return await Redirect(selection);
        selection.EnsureSuccessStatusCode();
        await using var stream = await selection.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await OnlineConnection.ReadAsync(stream, buffer, token)) > 0)
        { if (memory.Length + count > 1024 * 1024) throw new IOException("GPU-Z server page too large"); memory.Write(buffer, 0, count); }
        string html = Encoding.UTF8.GetString(memory.ToArray());
        var server = Regex.Match(html, @"name=""server_id"" value=""(\d+)""");
        if (!server.Success || !html.Contains("name=\"id\" value=\"" + id + "\"", StringComparison.Ordinal)) throw new IOException("Die GPU-Z-Serverauswahl wurde nicht bestätigt.");
        using var download = await Post(new() { ["id"] = id, ["server_id"] = server.Groups[1].Value });
        return await Redirect(download);
    }
    private async Task<PortableUpdate?> ResolvePublisherAsync(string id, string path, string old, string hash, CancellationToken token)
    {
        foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.Windows) }.Where(f => f.Length > 0))
            if (path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        if (id == "autoruns")
        {
            var page = Encoding.UTF8.GetString(await Read("https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns", token));
            var match = Regex.Match(page, @"Autoruns v(\d+(?:\.\d+){1,3})"); if (!match.Success) return null;
            string version = match.Groups[1].Value;
            var manifest = Encoding.UTF8.GetString(await Read($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/m/Microsoft/Sysinternals/Autoruns/{version}/Microsoft.Sysinternals.Autoruns.installer.yaml", token));
            var source = ManifestArtifact(manifest, "Microsoft.Sysinternals.Autoruns", version, "https://download.sysinternals.com/files/Autoruns.zip");
            if (source is null || ManifestField(manifest, "NestedInstallerType") != "portable") return null;
            var names = new[] { "Autoruns.exe", "Autoruns64.exe", "Autoruns64a.exe", "autorunsc.exe", "autorunsc64.exe", "autorunsc64a.exe" };
            if (!names.Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return null;
            var parts = new List<PortablePart>();
            foreach (var name in names)
            {
                string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name);
                if (!File.Exists(file)) continue;
                if (!PlainPath(file) || !SameVersion(FileVersion(id, file) ?? "", old) || !ManufacturerSignature.Verify(file, "Microsoft Corporation")) return null;
                parts.Add(new(file, name, Hash(file)));
            }
            var split = version.Split('.');
            string canonicalVersion = split.Length == 2 ? split[0] + "." + split[1].PadRight(2, '0') : version;
            return new(id, path, old, canonicalVersion, source.Value.Url, source.Value.Sha, null, System.IO.Path.GetFileName(path), hash, parts);
        }
        if (id == "cpu-z")
        {
            var page = Encoding.UTF8.GetString(await Read("https://www.cpuid.com/softwares/cpu-z.html", token));
            var link = Regex.Match(page, @"href=""/downloads/cpu-z/cpu-z_(\d+(?:\.\d+){1,3})-en\.zip"""); if (!link.Success) return null;
            string version = link.Groups[1].Value;
            var checksums = Encoding.UTF8.GetString(await Read("https://download.cpuid.com/checksum.txt", token));
            var sha = CpuChecksum(checksums, version); if (sha is null) return null;
            var parts = new List<PortablePart>();
            var names = new[] { "cpuz.exe", "cpuz_x32.exe", "cpuz_x64.exe" };
            if (!names.Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return null;
            foreach (var name in names)
            {
                string file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name); if (!File.Exists(file)) continue;
                if (!PlainPath(file) || !SameVersion(FileVersion(id, file) ?? "", old)) return null;
                ushort machine = Machine(file); if (machine is not (0x14c or 0x8664)) return null;
                parts.Add(new(file, machine == 0x8664 ? "cpuz_x64.exe" : "cpuz_x32.exe", Hash(file)));
            }
            if (ProgramVersions.IsNewer(old, version))
            {
                string key = string.Join('|', parts.Select(p => p.OriginalHash)); bool known; lock (_verifiedCpu) known = _verifiedCpu.Contains(key);
                if (!known)
                {
                    // Prevent changing a themed/Chinese build into the standard English distribution.
                    var oldSha = CpuChecksum(checksums, old); if (oldSha is null) return null;
                    var archive = await _archiveRead(new($"https://download.cpuid.com/cpu-z/cpu-z_{old}-en.zip"), token);
                    if (!CpuArtifactMatches(archive, oldSha, parts)) return null;
                    lock (_verifiedCpu) _verifiedCpu.Add(key);
                }
            }
            string member = Machine(path) == 0x8664 ? "cpuz_x64.exe" : "cpuz_x32.exe";
            return new(id, path, old, version, $"https://download.cpuid.com/cpu-z/cpu-z_{version}-en.zip", sha, null, member, hash, parts);
        }
        if (id == "gpu-z")
        {
            string oldManifest = Encoding.UTF8.GetString(await Read($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/t/TechPowerUp/GPU-Z/{old}/TechPowerUp.GPU-Z.installer.yaml", token));
            // Standard executable is also the portable executable; the ROG file has a different digest.
            if (ManifestField(oldManifest, "PackageIdentifier") != "TechPowerUp.GPU-Z" || ManifestField(oldManifest, "PackageVersion") != old
                || !string.Equals(ManifestField(oldManifest, "InstallerSha256"), hash, StringComparison.OrdinalIgnoreCase)) return null;
            var page = Encoding.UTF8.GetString(await Read("https://www.techpowerup.com/download/techpowerup-gpu-z/", token));
            var versionMatch = Regex.Match(page, @"GPU-Z v(\d+(?:\.\d+){1,3})"); if (!versionMatch.Success) return null;
            string version = versionMatch.Groups[1].Value;
            var manifest = Encoding.UTF8.GetString(await Read($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/t/TechPowerUp/GPU-Z/{version}/TechPowerUp.GPU-Z.installer.yaml", token));
            var url = ManifestField(manifest, "InstallerUrl");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CatalogWeb.IsAllowed(uri, Hosts) || !uri.Host.EndsWith("-dl.techpowerup.com", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath != $"/files/GPU-Z.{version}.exe" || uri.Query.Length > 0) return null;
            var source = ManifestArtifact(manifest, "TechPowerUp.GPU-Z", version, url!); if (source is null) return null;
            var standard = Regex.Match(page, @"GPU-Z\." + Regex.Escape(version) + @"\.exe</div>[\s\S]{0,2000}?class=""hash-value"">([a-fA-F0-9]{64})</div>");
            if (!standard.Success || !standard.Groups[1].Value.Equals(source.Value.Sha, StringComparison.OrdinalIgnoreCase)) return null;
            return new(id, path, old, version, source.Value.Url, source.Value.Sha, null, null, hash);
        }
        return null;
    }
}
