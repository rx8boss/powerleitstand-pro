using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record PortablePackage(string Version, string Url, string Sha256, long Size);
internal interface IProgramPortableService
{
    Task<PortablePackage?> ResolveAsync(ProgramEntry entry, CancellationToken token);
    Task<string> InstallAsync(ProgramEntry entry, PortablePackage package, string parent, IProgress<double?> progress, CancellationToken token);
}

internal sealed class ProgramPortable : IProgramPortableService, IProgramStepSource
{
    public event Action<ProgramStepEvent>? StepChanged;
    private readonly VlcPortableUpdates _vlc;
    internal ProgramPortable(VlcPortableUpdates? vlc = null) { _vlc = vlc ?? new(); }
    // Deliberately one verified portable recipe, not a generic conversion of installer packages.
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
        { "api.github.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com" };
    public async Task<PortablePackage?> ResolveAsync(ProgramEntry entry, CancellationToken token)
    {
        if (entry.Id == "vlc" && ProgramCatalog.Contains(entry)) return await _vlc.LatestAsync(token);
        if (entry.Id != "obs-studio" || !ProgramCatalog.Contains(entry) || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64) return null;
        var bytes = await CatalogWeb.ReadAsync(new("https://api.github.com/repos/obsproject/obs-studio/releases/latest"), Hosts, 2 * 1024 * 1024, token);
        return ParseRelease(System.Text.Encoding.UTF8.GetString(bytes));
    }
    internal static PortablePackage? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json); var release = doc.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var version = release.GetProperty("tag_name").GetString()?.TrimStart('v');
        if (version is null || !Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}$")) return null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != $"OBS-Studio-{version}-Windows-x64.zip") continue;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            var url = asset.GetProperty("browser_download_url").GetString();
            var size = asset.GetProperty("size").GetInt64();
            if (digest is null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") || size is <= 0 or > 1024L * 1024 * 1024
                || url != $"https://github.com/obsproject/obs-studio/releases/download/{release.GetProperty("tag_name").GetString()}/OBS-Studio-{version}-Windows-x64.zip") continue;
            return new(version, url, digest[7..], size);
        }
        return null;
    }
    internal static string Destination(ProgramEntry entry, PortablePackage package, string parent)
    {
        if (entry.Id == "vlc" && ProgramCatalog.Contains(entry)) return VlcPortableUpdates.Destination(package, parent);
        if (entry.Id != "obs-studio" || !Regex.IsMatch(package.Version, @"^\d+(?:\.\d+){1,3}$") || !Path.IsPathFullyQualified(parent)
            || !Directory.Exists(parent) || (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Ungültiger portabler Zielordner.");
        return Path.Combine(Path.GetFullPath(parent), "OBS-Studio-" + package.Version + "-Portable");
    }
    public async Task<string> InstallAsync(ProgramEntry entry, PortablePackage package, string parent, IProgress<double?> progress, CancellationToken token)
    {
        if (entry.Id == "vlc" && ProgramCatalog.Contains(entry)) return await _vlc.InstallNewAsync(package, parent, e => StepChanged?.Invoke(e), progress, token);
        var destination = Destination(entry, package, parent);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Ziel ist bereits vorhanden; es wird nichts überschrieben.");
        // Revalidate source recipe immediately before downloading, including the immutable digest.
        var current = await ResolveAsync(entry, token);
        if (current != package) throw new IOException("Portable Version hat sich geändert; bitte erneut prüfen.");
        string stage = Path.Combine(parent, ".PowerLeitstand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var archive = Path.Combine(stage, "download.zip");
            using (var response = await CatalogWeb.OpenAsync(new(package.Url), Hosts, token))
            {
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                var buffer = new byte[65536]; long count = 0; int n;
                while ((n = await OnlineConnection.ReadAsync(input, buffer, token)) > 0)
                { count += n; if (count > package.Size) throw new IOException("Die Größe des portablen Downloads stimmt nicht überein."); await output.WriteAsync(buffer.AsMemory(0, n), token); progress.Report(100d * count / package.Size); }
                if (count != package.Size) throw new IOException("Der portable Download ist unvollständig.");
            }
            await using (var input = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("Die portable Paketprüfsumme stimmt nicht überein.");
            StepChanged?.Invoke(new(ProgramStep.Download, ProgramStepState.Done));
            StepChanged?.Invoke(new(ProgramStep.Install, ProgramStepState.Active));
            progress.Report(null);
            var unpacked = Path.Combine(stage, "App");
            await ExtractAsync(archive, unpacked, token);
            var executable = Path.Combine(unpacked, "bin", "64bit", "obs64.exe");
            if (!File.Exists(executable)) throw new IOException("Die OBS-Programmdatei fehlt im portablen Paket.");
            await File.WriteAllTextAsync(Path.Combine(unpacked, "portable_mode.txt"), "", token);
            if (ObsPortableUpdates.Identify(executable) is not { Machine: 0x8664 } identity || identity.Version != package.Version)
                throw new IOException("OBS-Produktversion oder Architektur wurde nicht bestätigt. / OBS product version or architecture was not verified.");
            token.ThrowIfCancellationRequested();
            Directory.Move(unpacked, destination); // Atomic move, no overwrite of user configuration.
            return Path.Combine(destination, "bin", "64bit", "obs64.exe");
        }
        finally
        {
            // Stage is a freshly generated child of the explicitly selected parent; do not follow links.
            try { DeleteStage(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static void DeleteStage(string stage)
    {
        if (!Directory.Exists(stage)) return;
        foreach (var child in new DirectoryInfo(stage).EnumerateFileSystemInfos())
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) { child.Delete(); continue; }
            if (child is DirectoryInfo) DeleteStage(child.FullName); else child.Delete();
        }
        Directory.Delete(stage);
    }
    internal static async Task ExtractAsync(string archive, string target, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 12000) throw new IOException("Ungültiges portables Archiv.");
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var rows = new List<(ZipArchiveEntry Entry, string Path)>(); long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/'); var parts = name.TrimEnd('/').Split('/');
            if (name.StartsWith('/') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0 || !names.Add(name.TrimEnd('/')))
                throw new IOException("Das portable Archiv enthält ungültige Pfade.");
            var path = Path.GetFullPath(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.Length < 0 || entry.Length > 4L * 1024 * 1024 * 1024 - total) throw new IOException("Das portable Archiv ist zu groß.");
            total += entry.Length; rows.Add((entry, path));
        }
        foreach (var (entry, path) in rows)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open(); await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536]; long count = 0; int n;
            while ((n = await input.ReadAsync(buffer, token)) > 0)
            { count += n; if (count > entry.Length) throw new IOException("Die entpackte Dateigröße stimmt nicht überein."); await output.WriteAsync(buffer.AsMemory(0, n), token); }
            if (count != entry.Length) throw new IOException("Eine portable Datei ist unvollständig.");
        }
    }
}

internal sealed class ProgramPortableStore
{
    private readonly string _file;
    internal ProgramPortableStore(string file) => _file = file;
    private Dictionary<string, string> Paths()
    {
        try { return File.Exists(_file) && new FileInfo(_file).Length < 1024 * 1024 ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? [] : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    internal Dictionary<string, ProgramInstallation> Read()
    {
        var result = new Dictionary<string, ProgramInstallation>();
        try
        {
            if (Paths().GetValueOrDefault("obs-studio") is { } path && ObsPortableUpdates.Identify(path) is { } identity)
            {
                result["obs-studio"] = new(identity.Version, "Portable", identity.Executable, false, "OBS Studio Portable");
            }
            if (Paths().GetValueOrDefault("vlc") is { } vlcPath && VlcPortableUpdates.Identify(vlcPath) is { } vlc)
                result["vlc"] = new(vlc.Version, "Portable", vlc.Executable, false, VlcPortableUpdates.Variant);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception) { }
        return result;
    }
    internal void Remember(string id, string executable)
    {
        if (id is not ("obs-studio" or "vlc") || !File.Exists(executable)
            || id == "vlc" && VlcPortableUpdates.Identify(executable) is null) throw new IOException("Portable Installation nicht bestätigt.");
        var paths = Paths(); paths[id] = Path.GetFullPath(executable); Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(paths)); File.Move(temp, _file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
