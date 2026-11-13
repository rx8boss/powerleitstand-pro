using System.IO;
using System.Security;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PowerControlProWpf;

internal sealed record ProgramManagedCopy(string Manager, string PackageId, string PackageVersion, string PackageDirectory, string Scope,
    ProgramPathEvidence? Location = null, string? ManagerRoot = null);
internal sealed record ProgramManagerRoot(string Manager, string Directory, string Scope);
internal sealed record ProgramManagerPresence(string Manager, string Directory, string Scope, bool Present);
internal sealed record ProgramManagedSnapshot(IReadOnlyDictionary<string, IReadOnlyList<ProgramManagedCopy>> Matches, IReadOnlyList<ProgramManagerPresence> Managers);

// Data-only inventory. No shell, package-manager command, script import or network request.
internal static class ProgramManagedInventory
{
    private static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["7-zip"] = ["7zip", "7zip.install", "7zip.portable"], ["git"] = ["git", "git.install", "git.portable"],
        ["firefox"] = ["firefox"], ["google-chrome"] = ["googlechrome"], ["brave"] = ["brave"],
        ["opera"] = ["opera"], ["opera-gx"] = ["opera-gx"], ["vivaldi"] = ["vivaldi"],
        ["notepad"] = ["notepadplusplus", "notepadplusplus.install", "notepadplusplus.portable"],
        ["vs-code"] = ["vscode"], ["cursor"] = ["cursor"], ["winrar"] = ["winrar"],
        ["vlc"] = ["vlc"], ["obs-studio"] = ["obs-studio"], ["discord"] = ["discord"],
        ["steam"] = ["steam"], ["powertoys"] = ["powertoys"], ["putty"] = ["putty", "putty.install", "putty.portable"],
        ["rufus"] = ["rufus"], ["geek-uninstaller"] = ["geekuninstaller"], ["bcuninstaller"] = ["bulk-crap-uninstaller", "bulkcrapuninstaller"],
        ["gpu-z"] = ["gpu-z"], ["cpu-z"] = ["cpu-z", "cpu-z.install", "cpu-z.portable"],
        ["crystaldiskinfo"] = ["crystaldiskinfo", "crystaldiskinfo.portable"],
        ["node-js"] = ["nodejs", "nodejs.install", "nodejs-lts"], ["python"] = ["python"],
        ["powershell"] = ["pwsh", "powershell-core"], ["windows-terminal"] = ["windows-terminal", "microsoft-windows-terminal"],
        ["blender"] = ["blender"], ["audacity"] = ["audacity"], ["gimp"] = ["gimp"], ["handbrake"] = ["handbrake"],
        ["spotify"] = ["spotify"], ["mpv"] = ["mpv"], ["qbittorrent"] = ["qbittorrent"], ["everything"] = ["everything"],
        ["msi-afterburner"] = ["msiafterburner"], ["compactgui"] = ["compactgui"], ["autoruns"] = ["autoruns"],
        ["ollama"] = ["ollama"], ["lm-studio"] = ["lm-studio"], ["vortex"] = ["vortex"], ["epic-games-launcher"] = ["epicgameslauncher"]
    };
    internal static IEnumerable<ProgramManagerRoot> Roots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new[] { new ProgramManagerRoot("Scoop", Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(profile, "scoop"), "User"),
            new("Scoop", Environment.GetEnvironmentVariable("SCOOP_GLOBAL") ?? Path.Combine(common, "scoop"), "Machine"),
            new("Chocolatey", Environment.GetEnvironmentVariable("ChocolateyInstall") ?? Path.Combine(common, "chocolatey"), "Machine") }
            .Where(r => Path.IsPathFullyQualified(r.Directory) && Directory.Exists(r.Directory))
            .DistinctBy(r => Path.GetFullPath(r.Directory), StringComparer.OrdinalIgnoreCase);
    }
    internal static string[] PackageAliases(ProgramEntry entry) => Aliases.GetValueOrDefault(entry.Id) ?? [entry.Id];
    internal static bool Matches(ProgramEntry entry, string packageId, string? homepage)
    {
        if (!PackageAliases(entry).Contains(packageId, StringComparer.OrdinalIgnoreCase)) return false;
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || url.UserInfo.Length > 0 || !url.IsDefaultPort) return false;
        var expected = new Uri(entry.Website); static string Host(Uri uri) => uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..].ToLowerInvariant() : uri.IdnHost.ToLowerInvariant();
        string host = Host(url), official = Host(expected);
        if (host != official && !(entry.Id == "git" && host is "gitforwindows.org" or "git-scm.com")
            && !(entry.Id == "firefox" && host is "mozilla.org" or "firefox.com")) return false;
        if (host == "github.com") return url.AbsolutePath.Trim('/').Split('/').Take(2).SequenceEqual(expected.AbsolutePath.Trim('/').Split('/').Take(2), StringComparer.OrdinalIgnoreCase);
        return true;
    }
    private static string ReadBounded(string file)
    {
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked metadata");
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Package metadata too large");
        using var reader = new StreamReader(stream); var buffer = new char[1024 * 1024 + 1]; int count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count == buffer.Length) throw new InvalidDataException("Package metadata too large"); return new(buffer, 0, count);
    }
    private static string Metadata(string dir, string modern, string legacy) => File.Exists(Path.Combine(dir, modern)) ? Path.Combine(dir, modern) : Path.Combine(dir, legacy);
    private static string? Text(JsonElement json, string field) => json.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string? ScoopDirectory(string app)
    {
        var current = new DirectoryInfo(Path.Combine(app, "current"));
        if (current.Exists)
        {
            string target = current.LinkTarget is null ? current.FullName : current.ResolveLinkTarget(true)?.FullName ?? "";
            return target.StartsWith(Path.GetFullPath(app) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? target : null;
        }
        // Matches Scoop's Select-CurrentVersion fallback: installed metadata, not highest version name.
        var versions = new DirectoryInfo(app).EnumerateDirectories().Take(64)
            .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0 && ProgramVersions.ConfirmedVersion(d.Name) is not null)
            .Select(d => new { Directory = d.FullName, Marker = Metadata(d.FullName, "scoop-install.json", "install.json") })
            .Where(d => File.Exists(d.Marker)).Select(d => new { d.Directory, Time = File.GetLastWriteTimeUtc(d.Marker) }).OrderByDescending(d => d.Time).ToArray();
        if (versions.Length == 0 || versions.Length > 1 && versions[0].Time == versions[1].Time) return null;
        return versions[0].Directory;
    }
    internal static ProgramManagedSnapshot Read(IEnumerable<ProgramEntry> entries, CancellationToken token, IEnumerable<ProgramManagerRoot>? roots = null)
    {
        var catalog = entries.ToArray(); var matches = new Dictionary<string, List<ProgramManagedCopy>>(); var managers = new List<ProgramManagerPresence>();
        var files = new ProgramPathResolver();
        foreach (var root in roots ?? Roots())
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(root.Directory)) continue;
            bool present = root.Manager == "Scoop" ? File.Exists(Path.Combine(root.Directory, "shims", "scoop.ps1")) && Directory.Exists(Path.Combine(root.Directory, "apps", "scoop"))
                : root.Manager == "Chocolatey" && File.Exists(Path.Combine(root.Directory, "bin", "choco.exe"));
            managers.Add(new(root.Manager, root.Directory, root.Scope, present)); if (!present) continue;
            try
            {
                string container = Path.Combine(root.Directory, root.Manager == "Scoop" ? "apps" : "lib"); if (!Directory.Exists(container)) continue;
                foreach (var package in new DirectoryInfo(container).EnumerateDirectories().Take(2048))
                {
                    token.ThrowIfCancellationRequested();
                    if ((package.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var candidates = catalog.Where(e => PackageAliases(e).Contains(package.Name, StringComparer.OrdinalIgnoreCase)).ToArray(); if (candidates.Length == 0) continue;
                    try
                    {
                        string directory, id = package.Name; string? version, homepage;
                        if (root.Manager == "Scoop")
                        {
                            directory = ScoopDirectory(package.FullName) ?? ""; if (directory.Length == 0) continue;
                            using var install = JsonDocument.Parse(ReadBounded(Metadata(directory, "scoop-install.json", "install.json")), new() { MaxDepth = 32 });
                            using var manifest = JsonDocument.Parse(ReadBounded(Metadata(directory, "scoop-manifest.json", "manifest.json")), new() { MaxDepth = 32 });
                            if (install.RootElement.ValueKind != JsonValueKind.Object || manifest.RootElement.ValueKind != JsonValueKind.Object) continue;
                            version = Text(manifest.RootElement, "version");
                            if (Path.GetFileName(directory) != "current" && !string.Equals(Path.GetFileName(directory), version, StringComparison.OrdinalIgnoreCase)) continue;
                            homepage = Text(manifest.RootElement, "homepage");
                        }
                        else
                        {
                            directory = package.FullName;
                            if (File.Exists(Path.Combine(directory, ".chocolateyPending")) || File.Exists(Path.Combine(container, package.Name + ".chocolateyPending")) || !File.Exists(Path.Combine(directory, package.Name + ".nupkg"))) continue;
                            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
                            using var reader = XmlReader.Create(new StringReader(ReadBounded(Path.Combine(directory, package.Name + ".nuspec"))), settings);
                            var doc = XDocument.Load(reader); var metadata = doc.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata"); if (metadata is null) continue;
                            string? Field(string name) => metadata.Elements().SingleOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
                            if (!string.Equals(Field("id"), package.Name, StringComparison.OrdinalIgnoreCase)) continue;
                            version = Field("version"); homepage = Field("projectUrl");
                            // Meta packages do not prove a concrete desktop app was installed.
                            if (package.Name is "7zip" or "git" or "notepadplusplus" or "putty" or "cpu-z") continue;
                        }
                        version = ProgramVersions.ConfirmedVersion(version); if (version is null) continue;
                        foreach (var entry in candidates.Where(e => Matches(e, id, homepage)))
                        {
                            if (!matches.TryGetValue(entry.Id, out var copies)) matches[entry.Id] = copies = [];
                            var copy = new ProgramManagedCopy(root.Manager, id, version, directory, root.Scope, ManagerRoot: root.Directory);
                            copies.Add(copy with { Location = files.ResolveManaged(entry, copy, token) });
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException or JsonException or XmlException or InvalidOperationException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return new(matches.ToDictionary(p => p.Key, p => (IReadOnlyList<ProgramManagedCopy>)p.Value.DistinctBy(c => c.PackageDirectory, StringComparer.OrdinalIgnoreCase).ToArray()), managers);
    }
    internal static void Merge(IDictionary<string, ProgramInstallation> installed, ProgramManagedSnapshot snapshot)
    {
        foreach (var (id, copies) in snapshot.Matches)
        {
            if (copies.Count == 0) continue;
            // Keep existing product version/path; package versions have different semantics.
            if (installed.TryGetValue(id, out var proof)) installed[id] = proof with { WinGetCompatible = false, ManagedCopies = copies };
            // Never silently choose the first of multiple user/machine/package copies.
            // Only a checked executable supplies a product version; never the wrapper's version.
            else
            {
                var location = copies.Count == 1 ? copies[0].Location : null;
                installed[id] = new(location?.FileVersion, copies[0].Manager, location?.Path, false,
                    ManagedCopies: copies, LocationEvidence: location);
            }
        }
    }
    internal static IReadOnlyList<InstalledProgram> Rows(IEnumerable<ProgramEntry> entries, ProgramManagedSnapshot snapshot)
    {
        var catalog = entries.ToDictionary(e => e.Id);
        return snapshot.Matches.Where(p => catalog.ContainsKey(p.Key)).SelectMany(pair => pair.Value.Select(copy =>
        {
            var entry = catalog[pair.Key];
            return new InstalledProgram("managed|" + copy.Manager + "|" + copy.PackageDirectory, entry.Name, entry.Publisher,
                copy.Location?.FileVersion, copy.Location?.Path, copy.Manager, CatalogId: entry.Id, Protected: true,
                LocationEvidence: copy.Location, PackageVersion: copy.PackageVersion);
        })).ToArray();
    }
}
