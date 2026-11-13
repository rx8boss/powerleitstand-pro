using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record ProgramManagedTarget(string Manager, string Root, string Engine, string Id, string Scope,
    string Source, string Architecture, string? OldVersion, string? OldDirectory, string Stamp, string? File);
internal sealed record ProgramManagedPackage(ProgramManagedTarget Target, string Version, string SourceUrl,
    string MetadataHash, string? LocalManifest, bool NeedsPreparation, string License, string Dependencies)
{
    internal string Manager => Target.Manager;
    internal bool IsUpdate => Target.OldVersion is { } old && ProgramVersions.IsNewer(old, Version);
    internal string Description(bool english) => Manager + " · " + Target.Id + " · " + Target.Source + "\n"
        + (Target.Scope == "User" ? english ? "Current Windows account" : "Aktuelles Windows-Konto" : english ? "All users · administrator rights required" : "Alle Benutzer · Administratorrechte erforderlich")
        + "\n" + (english ? "License: " : "Lizenz: ") + License + "\n"
        + (english ? "Dependencies: " : "Abhängigkeiten: ") + Dependencies
        + "\n" + (english ? "Destination: " : "Ziel: ") + (Target.OldDirectory ?? Path.Combine(Target.Root, Manager == "Scoop" ? "apps" : "lib", Target.Id))
        + (NeedsPreparation ? english ? "\nRefresh package sources first; separate confirmation required." : "\nPaketquellen vorher aktualisieren; eigene Bestätigung erforderlich." : "");
    internal ProgramPackage Package() => new(Target.Id, Version, Description(false) + "\n\n" + Description(true), DateTime.UtcNow,
        VersionSource: Manager, ManagedPackage: this);
}

// Source-bound adapters. Inventory/metadata queries never execute package scripts.
// All mutations run only after the existing selection/confirmation flow.
internal sealed class ProgramManagedPackages
{
    private static readonly ISet<string> WebHosts = new HashSet<string>(["raw.githubusercontent.com", "community.chocolatey.org"], StringComparer.OrdinalIgnoreCase);
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _read;
    private readonly Func<ProcessStartInfo, Action, Action<string>, Task<WinGetResult>> _run;
    private readonly Func<IEnumerable<ProgramManagerRoot>> _roots;
    private readonly Func<bool> _elevated;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>> _query;
    private readonly Func<ProgramManagerRoot, string> _engine;
    private readonly Func<ProgramManagedTarget, string?> _git;
    private readonly Dictionary<string, (DateTime When, ProgramManagedPackage? Package)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(3);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _problems = new();
    internal string? Problem(string id) => _problems.GetValueOrDefault(id);
    internal ProgramManagedPackages(Func<Uri, CancellationToken, Task<byte[]>>? read = null,
        Func<ProcessStartInfo, Action, Action<string>, Task<WinGetResult>>? run = null,
        Func<IEnumerable<ProgramManagerRoot>>? roots = null, Func<bool>? elevated = null,
        Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>>? query = null,
        Func<ProgramManagerRoot, string>? engine = null, Func<ProgramManagedTarget, string?>? git = null)
    {
        _read = read ?? ((url, token) => CatalogWeb.ReadAsync(url, WebHosts, 1024 * 1024, token));
        _run = run ?? ProgramInstallService.RunHiddenAsync;
        _roots = roots ?? ProgramManagedInventory.Roots; _elevated = elevated ?? ElevationService.IsElevated; _query = query ?? WinGetService.QueryAsync;
        _engine = engine ?? Engine;
        _git = git ?? (target => ProgramScoopGit.Find(target, _roots()));
    }
    internal static bool Identifier(string value) => Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,99}$")
        && value is not ("all" or "scoop" or "chocolatey");
    internal static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    private static byte[] Local(string path)
    {
        if (!ProgramPortableUpdates.PlainPath(path) || new FileInfo(path).Length > 1024 * 1024) throw new IOException("Paketdaten nicht bestätigt. / Package metadata not verified.");
        return System.IO.File.ReadAllBytes(path);
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    private static JsonDocument Document(byte[] bytes) => JsonDocument.Parse(bytes.AsMemory(bytes.Length >= 3
        && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0), new() { MaxDepth = 32 });
    internal static string ManifestHash(byte[] bytes)
    {
        using var data = Document(bytes);
        return Hash(JsonSerializer.SerializeToUtf8Bytes(data.RootElement));
    }
    private static string FileHash(string file)
    {
        if (!ProgramPortableUpdates.PlainPath(file) || new FileInfo(file).Length > 512L * 1024 * 1024) throw new IOException("Paketdatei nicht bestätigt. / Package file not verified.");
        using var input = System.IO.File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(input));
    }
    private static string ArchiveHash(string file)
    {
        FileHash(file); using var input = System.IO.File.OpenRead(file); return Convert.ToBase64String(SHA512.HashData(input));
    }
    private static string Fingerprint(params string[] files) => string.Join("|", files.Select(FileHash));
    private static string Metadata(string directory, string modern, string legacy) => System.IO.File.Exists(Path.Combine(directory, modern)) ? Path.Combine(directory, modern) : Path.Combine(directory, legacy);
    internal static string Engine(ProgramManagerRoot root)
    {
        string? file = ProgramPathResolver.LocalTarget(Path.Combine(root.Directory, root.Manager == "Scoop" ? @"apps\scoop\current\bin\scoop.ps1" : @"bin\choco.exe"), true);
        if (file is null || !ProgramPathResolver.InDirectory(root.Directory, file) || !ProgramPortableUpdates.PlainPath(file)) throw new IOException("Paketmanager nicht bestätigt. / Package manager not verified.");
        if (root.Manager == "Chocolatey")
        {
            var info = FileVersionInfo.GetVersionInfo(file);
            if (info.ProductName?.Contains("Chocolatey", StringComparison.OrdinalIgnoreCase) != true || info.CompanyName?.Contains("Chocolatey", StringComparison.OrdinalIgnoreCase) != true)
                throw new IOException("Chocolatey-Datei nicht bestätigt. / Chocolatey executable not verified.");
        }
        return file;
    }
    private static string Origin(string root, string bucket)
    {
        if (bucket is not ("main" or "extras")) throw new IOException("Scoop-Bucket nicht bestätigt. / Scoop bucket not verified.");
        string config = Decode(Local(Path.Combine(root, "buckets", bucket, ".git", "config")));
        var section = Regex.Match(config, @"(?ms)^\[remote ""origin""\]\s*$(.*?)(?=^\[|\z)");
        var urls = Regex.Matches(section.Groups[1].Value, @"(?m)^\s*url\s*=\s*(\S+)\s*$");
        string expected = "https://github.com/ScoopInstaller/" + (bucket == "main" ? "Main" : "Extras");
        if (urls.Count != 1 || !urls[0].Groups[1].Value.TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Scoop-Ursprung nicht bestätigt. / Scoop origin not verified.");
        return expected;
    }
    private static bool Held(JsonElement install) => install.TryGetProperty("hold", out var held)
        && (held.ValueKind == JsonValueKind.True || held.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(held.GetString()));
    internal static string ScoopConfig(string root)
    {
        var portable = Path.Combine(root, "config.json");
        if (System.IO.File.Exists(portable)) return portable;
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(configHome, "scoop", "config.json");
    }
    internal static bool ScoopFresh(string root)
    {
        try
        {
            using var config = Document(Local(ScoopConfig(root)));
            return DateTimeOffset.TryParse(Text(config.RootElement, "last_update"), out var time)
                && time <= DateTimeOffset.UtcNow.AddMinutes(5) && DateTimeOffset.UtcNow - time < TimeSpan.FromHours(2);
        }
        catch (Exception e) when (Failure(e)) { return false; }
    }
    internal static string ScoopEngineOrigin(string root, string engine)
    {
        string home = Path.GetDirectoryName(Path.GetDirectoryName(engine))!;
        string config = Decode(Local(Path.Combine(home, ".git", "config")));
        var origin = Regex.Match(config, @"(?ms)^\[remote ""origin""\]\s*$(.*?)(?=^\[|\z)").Groups[1].Value;
        if (Regex.Matches(origin, @"(?m)^\s*url\s*=").Count != 1 || !Regex.IsMatch(origin, @"(?mi)^\s*url\s*=\s*https://github\.com/ScoopInstaller/Scoop(?:\.git)?/?\s*$"))
            throw new IOException("Scoop-Programmquelle nicht bestätigt. / Scoop engine source not verified.");
        if (System.IO.File.Exists(ScoopConfig(root)))
        {
            using var settings = Document(Local(ScoopConfig(root)));
            string repository = Text(settings.RootElement, "scoop_repo"), branch = Text(settings.RootElement, "scoop_branch");
            if (repository.Length > 0 && !repository.TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase).Equals("https://github.com/ScoopInstaller/Scoop", StringComparison.OrdinalIgnoreCase)
                || branch.Length > 0 && branch != "master") throw new IOException("Abweichende Scoop-Programmquelle; automatisch nicht bestätigt. / Different Scoop engine source; automatic execution not verified.");
        }
        return home;
    }
    private static string ChocoSource(string root)
    {
        using var reader = XmlReader.Create(new StringReader(Decode(Local(Path.Combine(root, "config", "chocolatey.config")))), new()
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var config = XDocument.Load(reader);
        var active = config.Descendants("source").Where(e => !string.Equals((string?)e.Attribute("disabled"), "true", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length != 1 || ((string?)active[0].Attribute("value"))?.TrimEnd('/') != "https://community.chocolatey.org/api/v2"
            || active[0].Attributes().Any(a => a.Name.LocalName is "user" or "password" && !string.IsNullOrEmpty(a.Value)))
            throw new IOException("Chocolatey-Quelle nicht eindeutig bestätigt. / Chocolatey source not unambiguously verified.");
        return "https://community.chocolatey.org/api/v2";
    }
    internal ProgramManagedTarget Target(ProgramEntry entry, ProgramManagedCopy copy)
    {
        if (copy.ManagerRoot is null || !Identifier(copy.PackageId) || !ProgramManagedInventory.PackageAliases(entry).Contains(copy.PackageId, StringComparer.OrdinalIgnoreCase))
            throw new IOException("Paketkennung nicht bestätigt. / Package identity not verified.");
        var root = _roots().SingleOrDefault(r => r.Manager == copy.Manager && string.Equals(Path.GetFullPath(r.Directory), Path.GetFullPath(copy.ManagerRoot), StringComparison.OrdinalIgnoreCase) && r.Scope == copy.Scope)
            ?? throw new IOException("Paketmanager-Ziel geändert. / Package-manager target changed.");
        string engine = _engine(root);
        if (!ProgramPathResolver.InDirectory(Path.Combine(root.Directory, root.Manager == "Scoop" ? "apps" : "lib", copy.PackageId), copy.PackageDirectory))
            throw new IOException("Paketordner nicht bestätigt. / Package directory not verified.");
        if (root.Manager == "Scoop")
        {
            ScoopEngineOrigin(root.Directory, engine);
            string installFile = Metadata(copy.PackageDirectory, "scoop-install.json", "install.json"), manifestFile = Metadata(copy.PackageDirectory, "scoop-manifest.json", "manifest.json");
            using var install = Document(Local(installFile)); using var manifest = Document(Local(manifestFile));
            string bucket = Text(install.RootElement, "bucket"), arch = Text(install.RootElement, "architecture");
            if (Held(install.RootElement) || Text(install.RootElement, "url").Length > 0 || arch is not ("64bit" or "32bit" or "arm64")
                || Text(manifest.RootElement, "version") != copy.PackageVersion || !ProgramManagedInventory.Matches(entry, copy.PackageId, Text(manifest.RootElement, "homepage")))
                throw new IOException("Paket gehalten oder Quelle unbestätigt. / Package held or source unverified.");
            Origin(root.Directory, bucket);
            return new("Scoop", root.Directory, engine, copy.PackageId, copy.Scope, bucket, arch, copy.PackageVersion, copy.PackageDirectory,
                Fingerprint(installFile, manifestFile, engine), copy.Location?.Executable);
        }
        string source = ChocoSource(root.Directory);
        string? application = copy.Location?.Executable;
        string architecture = application is null ? "native" : ProgramPortableUpdates.Machine(application) switch { 0x014c => "x86", 0x8664 => "x64", 0xaa64 => "ARM64", _ => "unknown" };
        return new("Chocolatey", root.Directory, engine, copy.PackageId, copy.Scope, source, architecture, copy.PackageVersion, copy.PackageDirectory,
            Fingerprint(Path.Combine(copy.PackageDirectory, copy.PackageId + ".nuspec"), Path.Combine(copy.PackageDirectory, copy.PackageId + ".nupkg"), Path.Combine(root.Directory, "config", "chocolatey.config"), engine)
                + (application is null ? "" : "|" + FileHash(application)), application);
    }
    private async Task<ProgramManagedPackage?> ResolveCore(ProgramEntry entry, ProgramManagedTarget target, CancellationToken token)
    {
        if (target.Manager == "Scoop")
        {
            Origin(target.Root, target.Source);
            string url = "https://raw.githubusercontent.com/ScoopInstaller/" + (target.Source == "main" ? "Main" : "Extras") + "/master/bucket/" + target.Id + ".json";
            var bytes = await _read(new(url), token).ConfigureAwait(false);
            using var data = Document(bytes); var manifest = data.RootElement;
            string version = Text(manifest, "version");
            if (ProgramVersions.ConfirmedVersion(version) is null || version is "nightly" or "latest" || !ProgramManagedInventory.Matches(entry, target.Id, Text(manifest, "homepage"))) return null;
            JsonElement download = manifest;
            if (manifest.TryGetProperty("architecture", out var architecture) && (!architecture.TryGetProperty(target.Architecture, out download) || download.ValueKind != JsonValueKind.Object)) return null;
            if (!HasHashes(download) || !HasUrls(download)) return null;
            string license = Text(manifest, "license"); if (license.Length == 0 && manifest.TryGetProperty("license", out var licensing)) license = Text(licensing, "identifier") + " " + Text(licensing, "url");
            string local = Path.Combine(target.Root, "buckets", target.Source, "bucket", target.Id + ".json");
            string? localHash = System.IO.File.Exists(local) ? ManifestHash(Local(local)) : null;
            string dependencies = manifest.TryGetProperty("depends", out var depends) ? depends.ToString() : "—";
            return new(target, version, url, ManifestHash(bytes), localHash, localHash != ManifestHash(bytes) || !ScoopFresh(target.Root), license, dependencies);
        }
        string source = ChocoSource(target.Root);
        var pins = new ProcessStartInfo(target.Engine) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "pin", "list", "--limit-output" }) pins.ArgumentList.Add(arg);
        var pinResult = await _query(pins, token).ConfigureAwait(false);
        if (pinResult.ExitCode != 0 || pinResult.Output.Split('\n').Any(line => line.Trim().Split('|')[0].Equals(target.Id, StringComparison.OrdinalIgnoreCase))) return null;
        if (target.OldVersion is not null)
        {
            var old = ParseChoco(await _read(new(source + "/Packages(Id='" + target.Id + "',Version='" + Uri.EscapeDataString(target.OldVersion) + "')"), token), entry, target.Id);
            if (old is null || old.Version != target.OldVersion || !old.Sha512.Equals(ArchiveHash(Path.Combine(target.OldDirectory!, target.Id + ".nupkg")), StringComparison.Ordinal)) return null;
        }
        var latest = ParseChoco(await _read(new(source + "/Packages?$filter=Id%20eq%20'" + target.Id + "'%20and%20IsLatestVersion%20eq%20true"), token), entry, target.Id);
        return latest is null ? null : new(target, latest.Version, source, latest.Sha512, null, false, latest.License, latest.Dependencies);
    }
    private static bool HasHashes(JsonElement element) => element.TryGetProperty("hash", out var hash)
        && (hash.ValueKind == JsonValueKind.String ? Regex.IsMatch(hash.GetString() ?? "", @"^[0-9a-fA-F]{64}$")
            : hash.ValueKind == JsonValueKind.Array && hash.GetArrayLength() > 0 && hash.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && Regex.IsMatch(v.GetString()!, @"^[0-9a-fA-F]{64}$")));
    private static bool HasUrls(JsonElement element) => element.TryGetProperty("url", out var url)
        && (url.ValueKind == JsonValueKind.String ? DownloadUrl(url.GetString()) : url.ValueKind == JsonValueKind.Array && url.GetArrayLength() > 0 && url.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && DownloadUrl(v.GetString())));
    private static bool DownloadUrl(string? url) => Uri.TryCreate(url?.Split('#')[0], UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort;
    internal sealed record ChocoMetadata(string Version, string Sha512, string License, string Dependencies);
    internal static ChocoMetadata? ParseChoco(byte[] bytes, ProgramEntry entry, string id)
    {
        if (bytes.Length > 1024 * 1024) return null;
        using var reader = XmlReader.Create(new MemoryStream(bytes), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var doc = XDocument.Load(reader); var entries = doc.Root?.Name.LocalName == "entry" ? new[] { doc.Root } : doc.Root?.Elements().Where(e => e.Name.LocalName == "entry").ToArray() ?? [];
        if (entries.Length != 1) return null;
        var props = entries[0].Descendants().SingleOrDefault(e => e.Name.LocalName == "properties"); if (props is null) return null;
        string Field(string name) => props.Elements().SingleOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
        if (!Field("Id").Equals(id, StringComparison.OrdinalIgnoreCase) || !ProgramManagedInventory.Matches(entry, id, Field("ProjectUrl"))
            || ProgramVersions.ConfirmedVersion(Field("Version")) is null || Field("IsPrerelease").Equals("true", StringComparison.OrdinalIgnoreCase)
            || !Field("PackageHashAlgorithm").Equals("SHA512", StringComparison.OrdinalIgnoreCase)) return null;
        try { if (Convert.FromBase64String(Field("PackageHash")).Length != 64) return null; } catch (FormatException) { return null; }
        return new(Field("Version"), Field("PackageHash"), Field("LicenseUrl"), Field("Dependencies"));
    }
    internal async Task<ProgramManagedPackage?> ResolveAsync(ProgramEntry entry, ProgramManagedTarget target, CancellationToken token, bool fresh = false)
    {
        string key = entry.Id + "|" + JsonSerializer.Serialize(target);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_cache) if (!fresh && _cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.When < TimeSpan.FromSeconds(45)) return cached.Package;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
            var package = await ResolveCore(entry, target, limit.Token).ConfigureAwait(false);
            lock (_cache) _cache[key] = (DateTime.UtcNow, package); return package;
        }
        finally { _gate.Release(); }
    }
    internal async Task<IReadOnlyList<ProgramManagedPackage>> ExistingAsync(ProgramEntry entry, ProgramInstallation proof, CancellationToken token, bool fresh = false, Action<string>? failed = null)
    {
        _problems.TryRemove(entry.Id, out _);
        var results = new List<ProgramManagedPackage>();
        foreach (var original in proof.ManagedCopies ?? [])
        {
            token.ThrowIfCancellationRequested();
            var copy = AttachRegisteredFile(entry, proof, original);
            if (copy.Location?.Executable is null) continue;
            if (proof.Source is not ("Scoop" or "Chocolatey") && (proof.Source != "Registry"
                || !string.Equals(proof.Path, copy.Location.Executable, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var target = Target(entry, copy); var package = await ResolveAsync(entry, target, token, fresh).ConfigureAwait(false);
                if (package is not null && (package.Version == target.OldVersion || ProgramVersions.IsNewer(target.OldVersion, package.Version))) results.Add(package);
                else _problems[entry.Id] = "Keine passende lieferbare Paketversion bestätigt. / No compatible available package version verified.";
            }
            catch (Exception e) when (Failure(e)) { token.ThrowIfCancellationRequested(); _problems[entry.Id] = e.Message; failed?.Invoke(entry.Id); }
        }
        return results;
    }
    internal async Task<ProgramManagedPackage?> NewAsync(ProgramEntry entry, CancellationToken token)
    {
        if (!ProgramCatalog.Contains(entry)) return null;
        foreach (var root in _roots().Where(r => r.Manager == "Scoop" && r.Scope == "User" || r.Manager == "Chocolatey"))
        {
            foreach (string id in ProgramManagedInventory.PackageAliases(entry).Where(Identifier))
            {
                if (root.Manager == "Chocolatey" && id is "7zip" or "git" or "notepadplusplus" or "putty" or "cpu-z") continue;
                foreach (string source in root.Manager == "Scoop" ? new[] { "main", "extras" } : new[] { "https://community.chocolatey.org/api/v2" })
                try
                {
                    string engine = _engine(root); if (root.Manager == "Scoop") { Origin(root.Directory, source); ScoopEngineOrigin(root.Directory, engine); }
                    else ChocoSource(root.Directory);
                    string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : Environment.Is64BitOperatingSystem ? "64bit" : "32bit";
                    var target = new ProgramManagedTarget(root.Manager, root.Directory, engine, id, root.Scope, source, architecture, null, null, Fingerprint(engine), null);
                    if (await ResolveAsync(entry, target, token) is { } package) return package;
                }
                catch (Exception e) when (Failure(e)) { token.ThrowIfCancellationRequested(); }
            }
        }
        return null;
    }
    internal static bool SameCopy(ProgramManagedTarget target, ProgramManagedCopy copy) => target.Manager == copy.Manager && target.Id == copy.PackageId
        && target.OldVersion == copy.PackageVersion && string.Equals(target.OldDirectory, copy.PackageDirectory, StringComparison.OrdinalIgnoreCase)
        && target.Scope == copy.Scope && string.Equals(target.Root, copy.ManagerRoot, StringComparison.OrdinalIgnoreCase);
    internal bool Matches(ProgramEntry entry, ProgramManagedPackage package, IReadOnlyDictionary<string, ProgramInstallation>? evidence)
    {
        if (!ProgramCatalog.Contains(entry) || !Identifier(package.Target.Id)) return false;
        if (package.Target.OldVersion is null) return evidence?.ContainsKey(entry.Id) != true;
        var proof = evidence?.GetValueOrDefault(entry.Id);
        return proof?.ManagedCopies?.Any(copy => SameCopy(package.Target, copy)) == true
            && (proof.Source is "Scoop" or "Chocolatey" || proof.Source == "Registry" && string.Equals(proof.Path, package.Target.File, StringComparison.OrdinalIgnoreCase));
    }
    internal static bool ResultVerified(ProgramManagedPackage package, ProgramInventory inventory, string id)
    {
        var proof = inventory.Evidence?.GetValueOrDefault(id); if (proof is null) return false;
        var entry = ProgramCatalog.Entries.FirstOrDefault(e => e.Id == id);
        return proof.ManagedCopies?.Any(original =>
        {
            var copy = entry is null ? original : AttachRegisteredFile(entry, proof, original);
            return copy.Manager == package.Manager && string.Equals(copy.ManagerRoot, package.Target.Root, StringComparison.OrdinalIgnoreCase)
                && copy.Scope == package.Target.Scope && copy.PackageId == package.Target.Id && copy.PackageVersion == package.Version
                && copy.Location?.Executable is { } file && System.IO.File.Exists(file) && copy.Location.FileVersion is not null;
        }) == true;
    }
    internal ProcessStartInfo Command(ProgramManagedPackage package, bool reinstall)
    {
        var target = package.Target;
        if (!Identifier(target.Id) || ProgramVersions.ConfirmedVersion(package.Version) is null) throw new IOException("Unbestätigter Paketauftrag. / Unverified package operation.");
        if (target.Manager == "Scoop")
        {
            var start = ScoopCommand(target);
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", target.Engine, target.OldVersion is null ? "install" : "update",
                target.OldVersion is null ? target.Source + "/" + target.Id : target.Id }) start.ArgumentList.Add(arg);
            if (target.OldVersion is null) { start.ArgumentList.Add("--no-update-scoop"); start.ArgumentList.Add("--arch"); start.ArgumentList.Add(target.Architecture); }
            if (target.Scope == "Machine") start.ArgumentList.Add("--global");
            if (reinstall) start.ArgumentList.Add("--force");
            return start;
        }
        var choco = new ProcessStartInfo(target.Engine) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { target.OldVersion is null ? "install" : "upgrade", target.Id, "--version=" + package.Version, "--source=" + target.Source,
            "--yes", "--no-color", "--use-package-exit-codes", "--use-remembered-arguments", "--stop-on-first-package-failure" }) choco.ArgumentList.Add(arg);
        if (target.OldVersion is not null) choco.ArgumentList.Add("--skip-if-not-installed");
        if (target.Architecture == "x86") choco.ArgumentList.Add("--force-x86");
        if (reinstall) choco.ArgumentList.Add("--force");
        return choco;
    }
    private ProcessStartInfo ScoopCommand(ProgramManagedTarget target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["SCOOP"] = target.Root;
        if (target.Scope == "Machine") start.Environment["SCOOP_GLOBAL"] = target.Root;
        ProgramScoopGit.Configure(start, _git(target));
        return start;
    }
    internal async Task PrepareAsync(ProgramManagedPackage package, Action started, Action<string> output)
    {
        if (package.Manager != "Scoop" || !package.NeedsPreparation) return;
        ScoopEngineOrigin(package.Target.Root, package.Target.Engine); Origin(package.Target.Root, package.Target.Source);
        var command = ScoopCommand(package.Target);
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-File", package.Target.Engine, "update" }) command.ArgumentList.Add(arg);
        var result = await _run(command, started, output).ConfigureAwait(false);
        lock (_cache) _cache.Clear();
        if (result.ExitCode != 0 || !ScoopFresh(package.Target.Root)) throw new IOException(
            result.Output.Contains("Scoop uses Git", StringComparison.OrdinalIgnoreCase) ? ProgramScoopGit.Unusable : ProgramScoopGit.PreparationFailed);
    }
    internal async Task<WinGetResult> ApplyAsync(ProgramEntry entry, ProgramManagedPackage selected, bool reinstall,
        Action started, Action<ProgramStepEvent> step, Action<ProgramDownload> download, Action<string> diagnostic, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (selected.Target.Scope == "Machine" && !_elevated()) throw new IOException("Für dieses Paket die App als Administrator starten und erneut auswählen. / Run the app as administrator and select this package again.");
        ProgramManagedTarget current;
        if (selected.Target.OldVersion is not null)
        {
            var copies = ProgramManagedInventory.Read([entry], token, _roots()).Matches.GetValueOrDefault(entry.Id) ?? [];
            var matching = copies.Where(c => SameCopy(selected.Target, c)).ToArray();
            if (matching.Length != 1) throw new IOException("Paketinstallation geändert. / Package installation changed.");
            var matched = matching[0];
            if (matched.Location?.Executable is null && matched.Manager == "Chocolatey")
                matched = AttachRegisteredFile(entry, ProgramLocalInventory.Read([entry], token, includeManaged: false).GetValueOrDefault(entry.Id) ?? new(null, "Unknown"), matched);
            current = Target(entry, matched);
            if (current != selected.Target) throw new IOException("Paketquelle geändert. / Package source changed.");
        }
        else
        {
            if (ProgramLocalInventory.Read([entry], token).ContainsKey(entry.Id)) throw new IOException("Programm inzwischen installiert. / Program was installed in the meantime.");
            current = selected.Target;
            if (Fingerprint(current.Engine) != current.Stamp) throw new IOException("Paketmanager geändert. / Package manager changed.");
            var root = _roots().SingleOrDefault(r => r.Manager == current.Manager && r.Directory == current.Root && r.Scope == current.Scope)
                ?? throw new IOException("Paketmanager-Ziel nicht bestätigt. / Package-manager target not verified.");
            if (_engine(root) != current.Engine) throw new IOException("Paketmanager geändert. / Package manager changed.");
            if (current.Manager == "Scoop") ScoopEngineOrigin(current.Root, current.Engine);
        }
        var fresh = await ResolveAsync(entry, current, token, true).ConfigureAwait(false);
        if (fresh is null || fresh.Version != selected.Version || fresh.MetadataHash != selected.MetadataHash || fresh.NeedsPreparation)
            throw new IOException("Paketquelle oder Version geändert; bitte erneut auswählen. / Package source or version changed; select it again.");
        if (!reinstall && current.OldVersion is not null && !fresh.IsUpdate) throw new IOException("Kein bestätigtes Update. / No verified update.");
        if (current.File is { } executable) ProgramGitHubUpdates.EnsureClosed(executable);
        token.ThrowIfCancellationRequested();
        var reports = new ProgramManagedSteps(current.Id, step, download);
        var command = Command(fresh, reinstall);
        diagnostic(current.Manager + " · " + current.Id + " · " + current.Source);
        step(new(ProgramStep.Check, ProgramStepState.Done));
        var result = await _run(command, started, reports.Line).ConfigureAwait(false);
        reports.End(result.ExitCode); diagnostic(result.Output);
        lock (_cache) _cache.Clear();
        return result;
    }
    internal static bool Failure(Exception e) => ProgramPathResolver.ReadFailure(e) || e is System.Net.Http.HttpRequestException
        or JsonException or XmlException or InvalidOperationException or OperationCanceledException or FormatException;
    private static ProgramManagedCopy AttachRegisteredFile(ProgramEntry entry, ProgramInstallation proof, ProgramManagedCopy copy)
    {
        if (copy.Location?.Executable is not null || copy.Manager != "Chocolatey" || proof.Source != "Registry"
            || proof.LocationEvidence?.Executable is null || proof.Version is null || !ProgramPathResolver.VersionFits(proof.Version, copy.PackageVersion)
            || proof.ManagedCopies is { Count: > 1 }) return copy;
        var registrations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view); using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string key in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var row = uninstall!.OpenSubKey(key);
                    if (ProgramLocalInventory.Matches(entry, row?.GetValue("DisplayName") as string, row?.GetValue("Publisher") as string))
                        registrations.Add(hive + "|" + key + "|" + row?.GetValue("InstallLocation") + "|" + row?.GetValue("UninstallString") + "|" + row?.GetValue("DisplayVersion"));
                }
            }
        }
        catch (Exception e) when (Failure(e)) { return copy; }
        return registrations.Count == 1 ? copy with { Location = proof.LocationEvidence } : copy;
    }
}

internal sealed class ProgramManagedSteps(string id, Action<ProgramStepEvent> step, Action<ProgramDownload> download)
{
    private bool _download, _install, _selected, _hash; private readonly object _gate = new();
    internal void Line(string text)
    {
        lock (_gate)
        {
            string line = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();
            // Dependency output never proves the selected application's completion.
            var scoop = Regex.Match(line, @"^(?:Installing|Updating) '([^']+)'", RegexOptions.IgnoreCase);
            var choco = Regex.Match(line, @"^([a-zA-Z0-9._-]+) v\d+(?:\.\d+)", RegexOptions.IgnoreCase);
            if (scoop.Success || choco.Success)
            {
                _selected = (scoop.Success ? scoop.Groups[1].Value : choco.Groups[1].Value).Equals(id, StringComparison.OrdinalIgnoreCase);
                _download = false;
            }
            if (!_selected) return;
            if (line.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase)) { _download = true; step(new(ProgramStep.Download, ProgramStepState.Active)); }
            if (_download && ProgramWinGetSteps.TryBytes(line) is { } bytes) download(bytes);
            if (line.StartsWith("Checking hash of ", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(line, @"\bok\.?$", RegexOptions.IgnoreCase))
            { _hash = true; _download = false; step(new(ProgramStep.Download, ProgramStepState.Done)); }
            if (line.StartsWith("Extracting ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Running ", StringComparison.OrdinalIgnoreCase)
                || line.Equals("Installing " + id + "...", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Linking ", StringComparison.OrdinalIgnoreCase))
            { _install = true; step(new(ProgramStep.Install, ProgramStepState.Active)); }
        }
    }
    internal void End(int code)
    {
        lock (_gate)
        {
            // A manager can use cached archives or delegate a silent downloader.
            // Do not turn its process exit into fabricated download/hash proof.
            if (!_hash) step(new(ProgramStep.Download, ProgramStepState.Unknown));
            if (_install) step(new(ProgramStep.Install, code is 0 or 3010 ? ProgramStepState.Unknown : ProgramStepState.Failed));
        }
    }
}
