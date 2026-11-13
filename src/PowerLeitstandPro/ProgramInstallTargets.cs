using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record ProgramInstallTargetProof(string PackageId, string Version, string ManifestHash, string Kind, string Source);

internal static class ProgramInstallTargets
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" };
    internal static Uri ManifestUri(string id, string version)
    {
        if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9.-]{1,159}$") || id.Contains("..")
            || !Regex.IsMatch(version, @"^[A-Za-z0-9][A-Za-z0-9.+-]{0,99}$") || version.Contains("..")) throw new IOException("Invalid package identity");
        return new("https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/" + char.ToLowerInvariant(id[0]) + "/"
            + id.Replace('.', '/') + "/" + Uri.EscapeDataString(version) + "/" + id + ".installer.yaml");
    }
    internal static async Task<ProgramInstallTargetProof?> ResolveAsync(ProgramPackage package, CancellationToken token)
    {
        if (package is not { CanInstall: true, VersionSource: "WinGet", Version: { } version, GitHubUpdate: null, PortableUpdate: null }) return null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(8));
            var url = ManifestUri(package.Id, version);
            var bytes = await CatalogWeb.ReadAsync(url, Hosts, 512 * 1024, limit.Token).ConfigureAwait(false);
            return Parse(package.Id, version, Encoding.UTF8.GetString(bytes), url.AbsoluteUri);
        }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or OperationCanceledException or ArgumentException)
        { token.ThrowIfCancellationRequested(); return null; }
    }
    // A conservative reader for the ordinary Microsoft installer-manifest layout, not a general YAML parser.
    // Unsupported structures disable custom paths. These values never become installer arguments.
    internal static ProgramInstallTargetProof? Parse(string id, string version, string yaml, string source = "fixture")
    {
        try
        {
            if (yaml.Length > 512 * 1024 || yaml.Contains('\t') || Regex.IsMatch(yaml, @"(?m)(?:^|\s)[&*!][A-Za-z_]|:\s*[|>]|^---|^\.\.\.")) return null;
            var lines = yaml.TrimStart('\uFEFF').Split('\n').Select(x => x.TrimEnd('\r'))
                .Where(x => x.Trim().Length > 0 && !x.TrimStart().StartsWith('#')).ToArray();
            // Even an unrecognized unsupported-arguments list is treated as a restriction.
            if (lines.Any(x => Regex.IsMatch(x, @"^\s*UnsupportedArguments\s*:"))) return null;
            int start = Array.FindIndex(lines, x => x == "Installers:");
            if (start < 0 || lines.Count(x => x == "Installers:") != 1 || start + 1 == lines.Length) return null;
            int itemIndent = lines[start + 1].TakeWhile(c => c == ' ').Count();
            if (itemIndent is not (0 or 2) || !lines[start + 1].TrimStart().StartsWith("- ")) return null;
            var roots = lines.Take(start).ToList(); var blocks = new List<List<string>>(); List<string>? current = null;
            for (int n = start + 1; n < lines.Length; n++)
            {
                string line = lines[n];
                if (Regex.IsMatch(line, "^" + new string(' ', itemIndent) + @"- [A-Za-z]+:"))
                { current = []; blocks.Add(current); current.Add(new string(' ', itemIndent + 2) + line[(itemIndent + 2)..]); }
                else if (line.Length > 0 && line[0] != ' ')
                { roots.AddRange(lines.Skip(n)); break; }
                else if (current is null || line.TakeWhile(c => c == ' ').Count() < itemIndent + 2) return null;
                else current.Add(line);
            }
            if (blocks.Count is 0 or > 100 || Value(roots, 0, "PackageIdentifier") != id || Value(roots, 0, "PackageVersion") != version
                || Value(roots, 0, "ManifestType") != "installer") return null;
            string? rootType = Value(roots, 0, "InstallerType"), rootNested = Value(roots, 0, "NestedInstallerType"), rootSwitch = Switch(roots, 0);
            var kinds = new HashSet<string>();
            foreach (var block in blocks)
            {
                int indent = itemIndent + 2;
                string? type = Value(block, indent, "InstallerType") ?? rootType;
                if (type == "zip") type = Value(block, indent, "NestedInstallerType") ?? rootNested;
                string? location = Switch(block, indent) ?? rootSwitch;
                if (type is null || type is "msix" or "appx" or "msstore" || location is not null && !location.Contains("<INSTALLPATH>", StringComparison.Ordinal)) return null;
                if (location is null && type is not ("inno" or "nullsoft" or "portable")) return null;
                kinds.Add(type);
            }
            return new(id, version, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yaml))), string.Join(", ", kinds.Order()), source);
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException) { return null; }
    }
    private static string? Value(IEnumerable<string> lines, int indent, string key)
    {
        string prefix = new string(' ', indent) + key + ":";
        var values = lines.Where(x => x.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (values.Length > 1) throw new FormatException("Duplicate manifest field");
        if (values.Length == 0) return null;
        string value = values[0][prefix.Length..].Trim();
        if (value.StartsWith('"')) return JsonSerializer.Deserialize<string>(value);
        if (value.StartsWith('\''))
        { if (!value.EndsWith('\'') || value.Length < 2) throw new FormatException(); return value[1..^1].Replace("''", "'"); }
        if (value.Contains(" #")) value = value[..value.IndexOf(" #", StringComparison.Ordinal)].TrimEnd();
        if (value.StartsWith('[') || value.StartsWith('{')) throw new FormatException();
        return value;
    }
    private static string? Switch(IReadOnlyList<string> lines, int indent)
    {
        string parent = new string(' ', indent) + "InstallerSwitches:";
        int n = -1; for (int i = 0; i < lines.Count; i++) if (lines[i].StartsWith(parent, StringComparison.Ordinal))
        { if (n >= 0 || lines[i] != parent) throw new FormatException(); n = i; }
        if (n < 0) return null;
        var children = lines.Skip(n + 1).TakeWhile(x => x.TakeWhile(c => c == ' ').Count() > indent).ToArray();
        return Value(children, indent + 2, "InstallLocation");
    }
    internal static string Suggest(ProgramEntry entry, string drive)
    {
        string name = new(entry.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return Path.Combine(drive, "Programme", name.TrimEnd(' ', '.'));
    }
    internal static string Validate(string value, bool probeWrite = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 230 || !Regex.IsMatch(value, @"^[A-Za-z]:\\")
            || value.Any(char.IsControl) || value.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0 || value[2..].Contains(':'))
            throw new IOException("Bitte einen vollständigen lokalen Zielordner wählen. / Choose a full local destination folder.");
        var parts = value[3..].Split('\\');
        if (parts.Length == 0 || parts.Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith(' ') || x.EndsWith('.')
            || Regex.IsMatch(x, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)))
            throw new IOException("Ungültiger Zielordner. / Invalid destination folder.");
        string path = Path.GetFullPath(value); var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) throw new IOException("Ziellaufwerk nicht verfügbar. / Destination drive unavailable.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.Equals(windows, StringComparison.OrdinalIgnoreCase) || path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows ist kein Programmzielordner. / Windows is not an application destination.");
        if (File.Exists(path) || Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("Bitte einen neuen oder leeren Programmordner wählen. / Choose a new or empty program folder.");
        string? parent = path;
        while (parent is not null)
        {
            if (File.Exists(parent)) throw new IOException("Ein Bestandteil des Zielpfads ist eine Datei. / A destination path component is a file.");
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("Verknüpfter Zielordner nicht unterstützt. / Linked destination folders are not supported.");
            parent = Path.GetDirectoryName(parent);
        }
        if (drive.AvailableFreeSpace <= 0) throw new IOException("Kein freier Speicher auf dem Ziellaufwerk. / No free space on the destination drive.");
        if (probeWrite)
        {
            string ancestor = path; while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor) ?? throw new IOException("Destination parent missing");
            using var probe = new FileStream(Path.Combine(ancestor, ".PowerLeitstand-write-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        return path;
    }
    internal static bool MatchesLocation(string? actual, string selected)
    {
        if (actual is null || !Path.IsPathFullyQualified(actual)) return false;
        string root = Path.GetFullPath(selected).TrimEnd('\\'), found = Path.GetFullPath(actual).TrimEnd('\\');
        return found.Equals(root, StringComparison.OrdinalIgnoreCase) || found.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
    internal static bool Overlapping(IEnumerable<string> destinations)
    {
        var paths = destinations.Select(p => p.TrimEnd('\\')).ToArray();
        return paths.Where((path, i) => paths.Where((_, n) => n != i).Any(other => path.Equals(other, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(other + "\\", StringComparison.OrdinalIgnoreCase))).Any();
    }
}
