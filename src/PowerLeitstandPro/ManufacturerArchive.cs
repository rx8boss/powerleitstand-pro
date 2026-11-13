using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal static class ManufacturerArchive
{
    internal static bool InstallerName(string id, string name) => id switch
    {
        "asus" => name is "ArmouryCrateInstaller.exe" or "ArmouryCrateInstallTool.exe",
        "msi" => Regex.IsMatch(name, @"^MSI[ _-]Center(?:[ _-]\d+(?:\.\d+){1,3})?\.exe$", RegexOptions.IgnoreCase),
        "gigabyte" => Regex.IsMatch(name, @"^(?:GCC|GIGABYTE[ _-]Control[ _-]Center)(?:[ _-](?:Setup|\d+(?:\.\d+){1,3}))?\.exe$", RegexOptions.IgnoreCase),
        _ => false
    };

    internal static async Task<string> ExtractAsync(string id, string file, string destination, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(file);
        if (archive.Entries.Count is 0 or > 512) throw new IOException("Das Herstellerpaket ist nicht freigegeben.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var targets = new List<(ZipArchiveEntry Entry, string Path)>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (name.StartsWith('/') || parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase)) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0 || !names.Add(name.TrimEnd('/')))
                throw new IOException("Das Herstellerpaket ist nicht freigegeben.");
            var path = Path.GetFullPath(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.Length < 0 || entry.Length > 2L * 1024 * 1024 * 1024 - total)
                throw new IOException("Das Herstellerpaket ist nicht freigegeben.");
            total += entry.Length;
            targets.Add((entry, path));
        }
        var installers = targets.Where(x => InstallerName(id, Path.GetFileName(x.Path))).ToArray();
        if (installers.Length != 1) throw new IOException("Das Herstellerpaket ist nicht freigegeben.");
        foreach (var (entry, path) in targets)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            var buffer = new byte[65536]; long bytes = 0; int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                bytes += count;
                if (bytes > entry.Length) throw new IOException("Das Herstellerpaket ist nicht freigegeben.");
                await target.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (bytes != entry.Length) throw new IOException("Das Herstellerpaket ist nicht freigegeben.");
        }
        return installers[0].Path;
    }
}
