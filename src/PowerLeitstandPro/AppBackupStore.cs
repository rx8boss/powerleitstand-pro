using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record AppBackup(string Folder, string Version, DateTimeOffset Created, string Sha256, string Notes)
{
    internal string BinaryPath => Path.Combine(Folder, "PowerLeitstand-PRO.exe");
    public string Label => $"Version {Version}   ·   {Created.ToLocalTime():dd.MM.yyyy HH:mm}";
}

internal sealed class AppBackupStore
{
    internal const int Limit = 4;
    internal static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "Backups");
    internal string Root { get; }
    private sealed record Manifest(string Application, int Schema, string Version, DateTimeOffset Created, string Sha256, string Notes);
    internal AppBackupStore(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);

    internal IReadOnlyList<AppBackup> List()
    {
        if (!Directory.Exists(Root)) return [];
        CheckRoot();
        var entries = new List<AppBackup>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try { entries.Add(Read(dir)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException) { }
        }
        return entries.OrderByDescending(b => b.Created).ToArray();
    }

    internal AppBackup Read(string folder)
    {
        CheckFolder(folder);
        var file = Path.Combine(folder, "backup.json");
        CheckFile(file);
        // A concurrent list refresh must not prevent deletion of this backup.
        // The open handle remains readable, while Windows can remove the directory entry.
        using var manifest = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var data = JsonSerializer.Deserialize<Manifest>(manifest) ?? throw new InvalidDataException("Ungültige Sicherung.");
        if (data.Application != "PowerLeitstand-PRO" || data.Schema != 1 || !Version.TryParse(data.Version, out _) ||
            !Regex.IsMatch(data.Sha256 ?? "", "^[0-9a-fA-F]{64}$")) throw new InvalidDataException("Unbekannte Sicherung.");
        return new(Path.GetFullPath(folder), data.Version, data.Created, data.Sha256!, data.Notes ?? "");
    }

    internal async Task<bool> VerifyAsync(AppBackup entry, CancellationToken token = default)
    {
        var current = Read(entry.Folder);
        CheckFile(current.BinaryPath);
        return string.Equals(await HashAsync(current.BinaryPath, token), current.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<AppBackup> CreateAsync(string source, string version, string notes, CancellationToken token = default)
    {
        if (!Version.TryParse(version, out _)) throw new InvalidDataException("Ungültige Sicherungsversion.");
        CheckRoot(); Directory.CreateDirectory(Root); CheckRoot();
        var created = DateTimeOffset.Now;
        var folder = Path.Combine(Root, $"PowerLeitstand-PRO_{version}_{created:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var committed = false;
        try
        {
            var binary = Path.Combine(folder, "PowerLeitstand-PRO.exe");
            await CopyAsync(source, binary, token);
            var hash = await HashAsync(binary, token);
            if (hash != await HashAsync(source, token)) throw new InvalidDataException("Sicherung konnte nicht verifiziert werden.");
            var data = new Manifest("PowerLeitstand-PRO", 1, version, created, hash, notes.Length > 50000 ? notes[..50000] : notes);
            await File.WriteAllTextAsync(Path.Combine(folder, "backup.json"), JsonSerializer.Serialize(data), token);
            var entry = Read(folder);
            if (!await VerifyAsync(entry, token)) throw new InvalidDataException("Sicherungsprüfung fehlgeschlagen.");
            token.ThrowIfCancellationRequested();
            foreach (var old in List().Where(b => b.Folder != entry.Folder).Skip(Limit - 1)) Delete(old);
            committed = true;
            return entry;
        }
        finally
        {
            if (!committed)
            {
                // This newly generated directory can contain only the two files written above.
                foreach (var name in new[] { "PowerLeitstand-PRO.exe", "backup.json" })
                    try { File.Delete(Path.Combine(folder, name)); } catch { }
                try { Directory.Delete(folder, false); } catch { }
            }
        }
    }

    internal void Delete(AppBackup entry)
    {
        var current = Read(entry.Folder);
        foreach (var item in Directory.EnumerateFileSystemEntries(current.Folder))
        {
            CheckFile(item);
            if (Path.GetFileName(item) is not ("PowerLeitstand-PRO.exe" or "backup.json"))
                throw new InvalidDataException("Unbekannter Inhalt im Sicherungsordner; nichts wird gelöscht.");
        }
        File.Delete(current.BinaryPath);
        File.Delete(Path.Combine(current.Folder, "backup.json"));
        Directory.Delete(current.Folder, false);
    }

    internal static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        await input.CopyToAsync(output, token); await output.FlushAsync(token);
    }
    internal static async Task<string> HashAsync(string file, CancellationToken token = default)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 131072, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token));
    }
    internal static string FileVersion(string file) => System.Version.TryParse(FileVersionInfo.GetVersionInfo(file).FileVersion, out var v)
        ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    private void CheckRoot()
    {
        foreach (var path in new[] { Root, Path.GetDirectoryName(Root)! })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Verknüpfte Sicherungsordner werden nicht verwendet.");
    }
    private void CheckFolder(string folder)
    {
        CheckRoot();
        var full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(full), @"^PowerLeitstand-PRO_[0-9.]+_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_[a-f0-9]{32}$") ||
            !Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Ungültiger Sicherungspfad.");
    }
    private static void CheckFile(string file)
    {
        if (!File.Exists(file) || (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("Ungültige Sicherungsdatei.");
    }
}
