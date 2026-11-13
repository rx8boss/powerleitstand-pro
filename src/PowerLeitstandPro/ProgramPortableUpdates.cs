using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record ProgramDownload(long Received, long? Total)
{
    internal double? Percent => Total is > 0 && Received <= Total ? Received * 100d / Total.Value : null;
}
internal interface IProgramDownloadSource { event Action<ProgramDownload>? DownloadChanged; }
internal sealed record PortablePart(string Path, string Member, string OriginalHash);
internal sealed record PortableUpdate(string Id, string Path, string InstalledVersion, string Version,
    string Url, string Sha256, long? Size, string? ZipMember, string OriginalHash, IReadOnlyList<PortablePart>? Parts = null);

// Each recipe preserves the product/distribution. No installer is converted into a portable app.
internal sealed partial class ProgramPortableUpdates
{
    internal const long Limit = 256L * 1024 * 1024;
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
        { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "geekuninstaller.com", "raw.githubusercontent.com",
          "learn.microsoft.com", "download.sysinternals.com", "www.cpuid.com", "download.cpuid.com", "www.techpowerup.com", "us1-dl.techpowerup.com", "uk1-dl.techpowerup.com", "de1-dl.techpowerup.com", "sg1-dl.techpowerup.com", "download.videolan.org", "adwcleaner.malwarebytes.com" };
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _read;
    private readonly Func<PortableUpdate, string, Action<ProgramDownload>, CancellationToken, Task>? _download;
    private readonly string? _backupRoot;
    private readonly Dictionary<string, (DateTime Time, PortableUpdate? Value)> _cache = [];
    private readonly HashSet<string> _verifiedCpu = [];
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _archiveRead;
    private readonly ObsPortableUpdates _obs;
    private readonly VlcPortableUpdates _vlc;
    private readonly ProgramTukuiUpdates _tukui;
    internal ProgramPortableUpdates(Func<Uri, CancellationToken, Task<byte[]>>? read = null,
        Func<PortableUpdate, string, Action<ProgramDownload>, CancellationToken, Task>? download = null, string? backupRoot = null,
        Func<Uri, CancellationToken, Task<byte[]>>? archiveRead = null, ObsPortableUpdates? obs = null, VlcPortableUpdates? vlc = null, ProgramTukuiUpdates? tukui = null)
    { _read = read ?? ((uri, token) => CatalogWeb.ReadAsync(uri, Hosts, 4 * 1024 * 1024, token)); _archiveRead = archiveRead ?? ((uri, token) => CatalogWeb.ReadAsync(uri, Hosts, 40 * 1024 * 1024, token)); _download = download; _backupRoot = backupRoot; _obs = obs ?? new(_read, download, backupRoot); _vlc = vlc ?? new(_read, download, backupRoot); _tukui = tukui ?? new(root: backupRoot); }
    internal Task<PortableUpdate?> ProbeTukuiAsync(ProgramInstallation proof, Action<ProgramDownload> report, CancellationToken token) => Task.Run(() => _tukui.ProbeAsync(proof, report, token), token);
    internal bool HasTukuiPackage(ProgramInstallation proof) => _tukui.HasPackage(proof);
    internal void ClearTukuiPackage() => _tukui.Clear();
    internal static bool Eligible(string id, ProgramInstallation proof) => id == "tukui-client" ? ProgramTukuiUpdates.Eligible(proof) : id == "vlc" ? VlcPortableUpdates.ProofEligible(proof) : id is "geek-uninstaller" or "melonloader" or "compactgui" or "rufus" or "autoruns" or "cpu-z" or "gpu-z" or "stability-matrix" or "obs-studio" or "adwcleaner"
        && proof.Source is "Portable" or "Portable-Setup" && proof.Path is not null && proof.ManagedCopies is not { Count: > 0 };
    internal static string? FileVersion(string id, string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!ProgramLocalInventory.MatchesPortable(id, info.ProductName, info.CompanyName)) return null;
        if (id == "autoruns") return info.FileMajorPart + "." + info.FileMinorPart;
        if (id is "compactgui" or "stability-matrix" && (info.ProductVersion?.Contains('-') == true || info.FileVersion?.Contains('-') == true)) return null;
        var value = id == "melonloader" ? info.ProductVersion : info.FileVersion ?? info.ProductVersion;
        if (id == "cpu-z")
        {
            var digits = Regex.Match(value ?? "", @"^(\d+),\s*(\d+),\s*(\d+),\s*(\d+)$");
            if (digits.Success) return digits.Groups[1].Value + "." + digits.Groups[2].Value + digits.Groups[3].Value
                + (digits.Groups[4].Value == "0" ? "" : "." + digits.Groups[4].Value);
        }
        var match = Regex.Match(value ?? "", @"^\d+(?:\.\d+){1,3}(?=$|[+\s])");
        return match.Success ? id == "rufus" ? string.Join('.', match.Value.Split('.').Take(2)) : id == "gpu-z" ? string.Join('.', match.Value.Split('.').Take(3)) : match.Value : null;
    }
    internal static bool PlainPath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        for (string? part = System.IO.Path.GetFullPath(path); part is not null; part = System.IO.Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static ushort Machine(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) return 0;
        stream.Position = 0x3C; int offset = reader.ReadInt32();
        if (offset < 64 || offset > 1024 * 1024 || offset > stream.Length - 6) return 0;
        stream.Position = offset; return reader.ReadUInt32() == 0x4550 ? reader.ReadUInt16() : (ushort)0;
    }
    internal static bool SameVersion(string a, string b) => !ProgramVersions.IsNewer(a, b) && !ProgramVersions.IsNewer(b, a)
        && Regex.IsMatch(a, @"^\d+(?:\.\d+){1,3}$") && Regex.IsMatch(b, @"^\d+(?:\.\d+){1,3}$");
    internal async Task<PortableUpdate?> ResolveAsync(string id, ProgramInstallation proof, CancellationToken token, bool fresh = false)
    {
        if (id == "tukui-client") return await Task.Run(() => _tukui.Cached(proof), token).ConfigureAwait(false);
        if (id == "obs-studio") return await _obs.ResolveAsync(proof, token).ConfigureAwait(false);
        if (id == "vlc") return await _vlc.ResolveAsync(proof, token).ConfigureAwait(false);
        if (!Eligible(id, proof) || !PlainPath(proof.Path!)) return null;
        var path = System.IO.Path.GetFullPath(proof.Path!);
        if (new FileInfo(path).Length > Limit) return null;
        var oldVersion = FileVersion(id, path);
        if (oldVersion is null) return null;
        // Rufus must actually be the portable distribution. Program Files fallbacks cannot prove this.
        if (id == "rufus" && !Regex.IsMatch(System.IO.Path.GetFileName(path), @"^rufus-\d+(?:\.\d+)*p\.exe$", RegexOptions.IgnoreCase)) return null;
        // Current recipes are x86/x64; never silently switch an ARM application to x64.
        if (id is "rufus" or "compactgui" or "stability-matrix" && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64) return null;
        if (id is "rufus" or "compactgui" or "stability-matrix" && Machine(path) != 0x8664) return null;
        var key = id + "|" + path;
        var hash = await Task.Run(() => Hash(path), token);
        lock (_cache)
            if (!fresh && _cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(5)
                && (cached.Value is null || cached.Value.OriginalHash == hash)) return cached.Value;
        PortableUpdate? result;
        if (id == "adwcleaner") result = await ResolveAdwCleanerAsync(path, oldVersion, hash, token);
        else if (id is "autoruns" or "cpu-z" or "gpu-z") result = await ResolvePublisherAsync(id, path, oldVersion, hash, token);
        else if (id == "geek-uninstaller")
        {
            var html = Encoding.UTF8.GetString(await _read(new("https://geekuninstaller.com/download"), token));
            var match = Regex.Match(html, @"\b1\.\d+\.\d+\.\d+\b");
            result = null;
            if (match.Success)
            {
                var version = match.Value;
                var manifest = Encoding.UTF8.GetString(await _read(new($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/g/GeekUninstaller/GeekUninstaller/{version}/GeekUninstaller.GeekUninstaller.installer.yaml"), token));
                result = ParseGeek(manifest, path, oldVersion, hash, version);
            }
        }
        else
        {
            string repo = id switch { "melonloader" => "LavaGang/MelonLoader.Installer", "compactgui" => "IridiumIO/CompactGUI", "stability-matrix" => "LykosAI/StabilityMatrix", _ => "pbatard/rufus" };
            string suffix = id == "compactgui" ? "releases?per_page=20" : "releases/latest";
            var json = Encoding.UTF8.GetString(await _read(new($"https://api.github.com/repos/{repo}/{suffix}"), token));
            result = ParseGithub(id, json, path, oldVersion, hash);
            // CompactGUI mono and framework-dependent builds can be renamed to the same filename.
            // Require the current file's official asset digest to prove the exact normal variant.
            if (id == "compactgui" && !CompactVariant(json, oldVersion, hash)) result = null;
        }
        lock (_cache) _cache[key] = (DateTime.UtcNow, result); return result;
    }
    internal static bool CompactVariant(string json, string version, string hash)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.EnumerateArray().Any(r =>
            SameVersion((r.GetProperty("tag_name").GetString() ?? "").TrimStart('v'), version)
            && r.GetProperty("assets").EnumerateArray().Any(a => a.GetProperty("name").GetString() == "CompactGUI.exe"
                && a.TryGetProperty("digest", out var d) && string.Equals(d.GetString(), "sha256:" + hash, StringComparison.OrdinalIgnoreCase)));
    }
    internal static PortableUpdate? ParseGeek(string manifest, string path, string old, string hash, string version)
    {
        string? Field(string name)
        { var matches = Regex.Matches(manifest, @"(?m)^\s*" + name + @":\s*([^\r\n]+)\s*$"); return matches.Count == 1 ? matches[0].Groups[1].Value.Trim() : null; }
        var sha = Field("InstallerSha256");
        return Field("PackageIdentifier") == "GeekUninstaller.GeekUninstaller" && Field("PackageVersion") == version
            && Field("InstallerType") == "zip" && Field("NestedInstallerType") == "portable"
            && Regex.IsMatch(manifest, @"(?m)^\s*- RelativeFilePath: geek\.exe\s*$")
            && Field("InstallerUrl") == "https://geekuninstaller.com/geek.zip" && Regex.IsMatch(sha ?? "", "^[a-fA-F0-9]{64}$")
            ? new("geek-uninstaller", path, old, version, "https://geekuninstaller.com/geek.zip", sha!, null, "geek.exe", hash) : null;
    }
    internal static PortableUpdate? ParseGithub(string id, string json, string path, string old, string hash)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToArray() : [doc.RootElement];
        foreach (var release in releases)
        {
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? ""; var version = tag.TrimStart('v');
            if (!Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}$")) continue;
            string repo = id switch { "melonloader" => "LavaGang/MelonLoader.Installer", "compactgui" => "IridiumIO/CompactGUI", "rufus" => "pbatard/rufus", "stability-matrix" => "LykosAI/StabilityMatrix", _ => "" };
            string name = id switch { "melonloader" => "MelonLoader.Installer.exe", "compactgui" => "CompactGUI.exe", "rufus" => $"rufus-{version}p.exe", "stability-matrix" => "StabilityMatrix-win-x64.zip", _ => "" };
            if (repo.Length == 0) return null;
            var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
            if (assets.Length != 1) continue;
            var a = assets[0]; var digest = a.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var size = a.GetProperty("size").GetInt64(); var url = a.GetProperty("browser_download_url").GetString();
            if (size is <= 0 or > Limit || !Regex.IsMatch(digest ?? "", "^sha256:[a-fA-F0-9]{64}$")
                || url != $"https://github.com/{repo}/releases/download/{tag}/{name}") continue;
            return new(id, path, old, version, url!, digest![7..], size, id == "stability-matrix" ? "StabilityMatrix.exe" : null, hash);
        }
        return null;
    }
    internal async Task DownloadAsync(PortableUpdate update, string target, Action<ProgramDownload> report, CancellationToken token)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        using var response = update.Id == "gpu-z" ? await OpenGpuDownloadAsync(update, token) : await CatalogWeb.OpenAsync(new(update.Url), Hosts, token);
        long? size = update.Size ?? response.Content.Headers.ContentLength;
        if (size is <= 0 or > Limit || update.Size is { } expected && response.Content.Headers.ContentLength is { } length && length != expected) throw new IOException("Die Größe des Herstellerdownloads stimmt nicht überein.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await CopyDownloadAsync(source, file, size, report, token);
    }
    internal static async Task CopyDownloadAsync(Stream source, Stream file, long? size, Action<ProgramDownload> report, CancellationToken token, long limit = Limit)
    {
        if (size is <= 0 || size > limit) throw new IOException("Invalid download size");
        var buffer = new byte[81920]; long received = 0; int count;
        report(new(0, size));
        while ((count = await OnlineConnection.ReadAsync(source, buffer, token)) > 0)
        {
            received += count;
            if (received > limit || size is { } total && received > total) throw new IOException("Der Herstellerdownload ist zu groß.");
            await file.WriteAsync(buffer.AsMemory(0, count), token); report(new(received, size));
        }
        if (received == 0 || size is { } final && received != final) throw new IOException("Der Herstellerdownload ist unvollständig.");
    }
    internal static void ExtractPayload(PortableUpdate update, string download, string payload)
    {
        if (!Hash(download).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Die Prüfsumme des Herstellerdownloads stimmt nicht überein.");
        if (update.ZipMember is null) { File.Copy(download, payload); return; }
        using var zip = ZipFile.OpenRead(download);
        var matches = zip.Entries.Where(e => e.FullName == update.ZipMember).ToArray();
        if (matches.Length != 1 || matches[0].Length is <= 0 or > Limit || (matches[0].ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new IOException("Das portable Archiv wurde nicht bestätigt.");
        // Only the named executable is read. Archive paths are never used as filesystem paths.
        using var input = matches[0].Open(); using var output = File.Open(payload, FileMode.CreateNew);
        var buffer = new byte[81920]; long copied = 0; int count;
        while ((count = input.Read(buffer)) > 0)
        { copied += count; if (copied > Limit || copied > matches[0].Length) throw new IOException("Archive size exceeded"); output.Write(buffer, 0, count); }
        if (copied != matches[0].Length) throw new IOException("Incomplete archive member");
    }
    internal async Task ApplyAsync(PortableUpdate update, Action<ProgramStepEvent> step, Action<ProgramDownload> progress, CancellationToken token, bool reinstall = false)
    {
        if (update.Id == "obs-studio") { await _obs.ApplyAsync(update, step, progress, token, reinstall).ConfigureAwait(false); return; }
        if (update.Id == "vlc") { await _vlc.ApplyAsync(update, step, progress, token, reinstall).ConfigureAwait(false); return; }
        var proof = new ProgramInstallation(update.InstalledVersion, update.Id == "melonloader" ? "Portable-Setup" : "Portable", update.Path, false);
        var current = await ResolveAsync(update.Id, proof, token, fresh: true);
        if (!Equivalent(current, update) || !(ProgramVersions.IsNewer(update.InstalledVersion, update.Version)
            || reinstall && SameVersion(update.InstalledVersion, update.Version))) throw new IOException("Die portable Quelle oder Datei hat sich geändert. Bitte erneut prüfen.");
        string root = _backupRoot ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Portable Updates");
        Directory.CreateDirectory(root);
        string stage = System.IO.Path.Combine(root, "Download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            string download = System.IO.Path.Combine(stage, "download"), payload = System.IO.Path.Combine(stage, "program.exe");
            step(new(ProgramStep.Download, ProgramStepState.Active));
            if (update.Id == "tukui-client") await _tukui.CopyAsync(update, download, progress, token);
            else if (_download is not null) await _download(update, download, progress, token);
            else await DownloadAsync(update, download, progress, token);
            var parts = update.Parts ?? [new(update.Path, update.ZipMember ?? "", update.OriginalHash)];
            var ready = new List<(PortableUpdate Update, string Payload)>();
            foreach (var part in parts)
            {
                payload = System.IO.Path.Combine(stage, "program-" + ready.Count + ".exe");
                var fileUpdate = update with { Path = part.Path, ZipMember = part.Member.Length > 0 ? part.Member : null, OriginalHash = part.OriginalHash, Parts = null };
                await Task.Run(() => ExtractPayload(fileUpdate, download, payload), token);
                if (!SameVersion(FileVersion(update.Id, payload) ?? "", update.Version) || Machine(payload) == 0 || Machine(payload) != Machine(part.Path))
                    throw new IOException("Die Herstellerdatei hat eine andere Produktversion oder Architektur.");
                if (update.Id == "autoruns" && !ManufacturerSignature.Verify(payload, "Microsoft Corporation")) throw new IOException("Die Microsoft-Signatur wurde nicht bestätigt.");
                if (update.Id == "adwcleaner" && !ManufacturerSignature.Verify(payload, "Malwarebytes Inc")) throw new IOException("Die Malwarebytes-Signatur wurde nicht bestätigt.");
                if (update.Id == "tukui-client" && !ProgramTukuiUpdates.Signed(payload)) throw new IOException("Die Tukui-Datei oder Entwicklersignatur wurde nicht bestätigt.");
                ready.Add((fileUpdate, payload));
            }
            step(new(ProgramStep.Download, ProgramStepState.Done)); token.ThrowIfCancellationRequested();
            step(new(ProgramStep.Install, ProgramStepState.Active));
            await Task.Run(() => ReplaceGroup(ready, root), token);
            step(new(ProgramStep.Install, ProgramStepState.Done));
        }
        finally { if (update.Id == "tukui-client") _tukui.Clear(); try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    internal static bool Equivalent(PortableUpdate? a, PortableUpdate b) => a is not null && a with { Parts = null } == b with { Parts = null }
        && (a.Parts ?? []).SequenceEqual(b.Parts ?? []);
    internal static void ReplaceGroup(IReadOnlyList<(PortableUpdate Update, string Payload)> files, string root,
        Func<string, string?>? reader = null, Action<int>? beforePart = null)
    {
        if (files.Count == 0 || files.Select(f => f.Update.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count) throw new IOException("Invalid portable update group");
        // Validate the complete target set before the first commit.
        foreach (var f in files)
            if (!PlainPath(f.Update.Path) || Hash(f.Update.Path) != f.Update.OriginalHash) throw new IOException("Eine portable Programmdatei wurde zwischenzeitlich geändert.");
        var complete = new List<(PortableUpdate Update, string Backup)>();
        try
        {
            for (int n = 0; n < files.Count; n++)
            { beforePart?.Invoke(n); var f = files[n]; string backup = Replace(f.Update, f.Payload, root, reader); complete.Add((f.Update, System.IO.Path.Combine(backup, "previous.exe"))); }
        }
        catch (Exception error)
        {
            var errors = new List<Exception>();
            foreach (var part in complete.AsEnumerable().Reverse())
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(part.Update.Path)!, ".PowerLeitstand-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    if (Hash(part.Backup) != part.Update.OriginalHash) throw new IOException("Backup damaged: " + part.Backup);
                    File.Copy(part.Backup, temp); File.Replace(temp, part.Update.Path, null, true);
                    if (Hash(part.Update.Path) != part.Update.OriginalHash) throw new IOException("Group rollback failed: " + part.Backup);
                }
                catch (Exception e) { errors.Add(e); }
                finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
            if (errors.Count > 0) throw new IOException("Portable group restore failed. Backups: " + root, new AggregateException(errors.Prepend(error)));
            throw;
        }
    }
    internal static string Replace(PortableUpdate update, string payload, string backupRoot,
        Func<string, string?>? versionReader = null, Action<string>? postReplace = null)
    {
        versionReader ??= path => FileVersion(update.Id, path);
        if (!PlainPath(update.Path) || !Hash(update.Path).Equals(update.OriginalHash, StringComparison.OrdinalIgnoreCase)
            || !SameVersion(versionReader(update.Path) ?? "", update.InstalledVersion)
            || !SameVersion(versionReader(payload) ?? "", update.Version)) throw new IOException("Die portable Datei wurde nicht mehr bestätigt.");
        foreach (var process in Process.GetProcesses())
        {
            using (process) try
            { if (string.Equals(process.MainModule?.FileName, update.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Bitte die portable App zuerst schließen."); }
            catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { } catch (NotSupportedException) { }
        }
        Directory.CreateDirectory(backupRoot);
        string backupDirectory = System.IO.Path.Combine(backupRoot, update.Id + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(backupDirectory);
        string backup = System.IO.Path.Combine(backupDirectory, "previous.exe");
        string temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(update.Path)!, ".PowerLeitstand-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool replaced = false;
        try
        {
            // A locked file fails before the commit. Configuration and adjacent files are untouched.
            using (var old = new FileStream(update.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!Convert.ToHexString(SHA256.HashData(old)).Equals(update.OriginalHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Portable file changed");
                old.Position = 0; using var copy = File.Open(backup, FileMode.CreateNew); old.CopyTo(copy);
            }
            if (!Hash(backup).Equals(update.OriginalHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Backup verification failed");
            File.Copy(payload, temp, false);
            if (Hash(temp) != Hash(payload) || !PlainPath(update.Path) || Hash(update.Path) != update.OriginalHash) throw new IOException("Portable file changed before replacement");
            // Ignore nonessential metadata merge failures (for example compressed folders).
            // The atomic replacement still requires ordinary write/delete permissions.
            File.Replace(temp, update.Path, null, ignoreMetadataErrors: true); replaced = true;
            postReplace?.Invoke(update.Path);
            if (!SameVersion(versionReader(update.Path) ?? "", update.Version) || Hash(update.Path) != Hash(payload)) throw new IOException("Portable update verification failed");
            File.WriteAllText(System.IO.Path.Combine(backupDirectory, "backup.json"), JsonSerializer.Serialize(new { update.Id, update.Path, update.InstalledVersion, update.Version, update.Url, CreatedUtc = DateTime.UtcNow, update.OriginalHash }));
            return backupDirectory;
        }
        catch (Exception error)
        {
            if (replaced)
            {
                try { File.Copy(backup, temp, true); File.Replace(temp, update.Path, null, ignoreMetadataErrors: true); if (Hash(update.Path) != update.OriginalHash) throw new IOException("Rollback verification failed"); }
                catch (Exception rollback) { throw new IOException("Wiederherstellung fehlgeschlagen. Sicherung: / Restore failed. Backup: " + backup, new AggregateException(error, rollback)); }
            }
            throw;
        }
        finally { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
