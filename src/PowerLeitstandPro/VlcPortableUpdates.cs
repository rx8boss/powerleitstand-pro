using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record VlcZipBinary(string Version, ushort Machine);
internal sealed record VlcZipIdentity(string Root, string Executable, string Version, ushort Machine);

// Explicit official ZIP distribution; never relocates VLC's configuration or converts installers.
internal sealed partial class VlcPortableUpdates
{
    internal const string Variant = "VLC ZIP";
    internal const string ReceiptName = "PowerLeitstand.vlc-zip.json";
    internal const long ArchiveLimit = 1024L * 1024 * 1024;
    private const string BaseUrl = "https://download.videolan.org/pub/videolan/vlc/";
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "download.videolan.org" };
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _read;
    private readonly Func<PortableUpdate, string, Action<ProgramDownload>, CancellationToken, Task>? _download;
    private readonly Func<string, VlcZipBinary?> _binary;
    private readonly Action<string>? _checkpoint;
    private readonly string _backupRoot;
    internal VlcPortableUpdates(Func<Uri, CancellationToken, Task<byte[]>>? read = null,
        Func<PortableUpdate, string, Action<ProgramDownload>, CancellationToken, Task>? download = null,
        string? backupRoot = null, Func<string, VlcZipBinary?>? binary = null, Action<string>? checkpoint = null)
    {
        _read = read ?? ((uri, token) => CatalogWeb.ReadAsync(uri, Hosts, 2 * 1024 * 1024, token));
        _download = download; _binary = binary ?? ReadBinary; _checkpoint = checkpoint;
        _backupRoot = backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Portable Updates");
    }
    internal static VlcZipBinary? ReadBinary(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!ProgramLocalInventory.MatchesPortable("vlc", info.ProductName, info.CompanyName)
                || !string.Equals(info.OriginalFilename, "vlc.exe", StringComparison.OrdinalIgnoreCase)) return null;
            var version = Regex.Match(info.FileVersion ?? "", @"^(\d+\.\d+\.\d+)(?:\.0)?$");
            ushort machine = ProgramPortableUpdates.Machine(path);
            return version.Success && machine == 0x8664 ? new(version.Groups[1].Value, machine) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { return null; }
    }
    internal static bool SafePath(string root)
    {
        if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length <= (Path.GetPathRoot(root)?.Length ?? 0)) return false;
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.UserProfile })
        {
            string special = Environment.GetFolderPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (special.Length == 0) continue;
            if (root.Equals(special, StringComparison.OrdinalIgnoreCase) || folder is Environment.SpecialFolder.Windows or Environment.SpecialFolder.ProgramFiles or Environment.SpecialFolder.ProgramFilesX86
                && root.StartsWith(special + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        }
        string userPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (root.Equals(userPrograms, StringComparison.OrdinalIgnoreCase) || root.StartsWith(userPrograms + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        try { CheckParents(root); return true; } catch (IOException) { return false; }
    }
    internal static bool SafeRoot(string root) => Directory.Exists(root) && SafePath(root);
    internal static string? Marker(string root)
    {
        string marker = Path.Combine(root, ReceiptName);
        if (!ProgramPortableUpdates.PlainPath(marker) || new FileInfo(marker).Length > 4096) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(marker)); var value = json.RootElement;
            return value.GetProperty("Schema").GetInt32() == 1 && value.GetProperty("Variant").GetString() == Variant
                && value.GetProperty("MainFile").GetString() == "vlc.exe" ? marker : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    internal static VlcZipIdentity? Identify(string path, Func<string, VlcZipBinary?>? reader = null, bool requireMarker = true)
    {
        if (!ProgramPortableUpdates.PlainPath(path) || !Path.GetFileName(path).Equals("vlc.exe", StringComparison.OrdinalIgnoreCase)) return null;
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (!SafeRoot(root) || !ProgramPortableUpdates.PlainPath(Path.Combine(root, "libvlc.dll")) || !ProgramPortableUpdates.PlainPath(Path.Combine(root, "libvlccore.dll"))
            || !Directory.Exists(Path.Combine(root, "plugins")) || requireMarker && Marker(root) is null
            || Directory.EnumerateFiles(root, "*.exe").Any(p => Regex.IsMatch(Path.GetFileName(p), @"^(?:uninstall|unins.*|setup)\.exe$", RegexOptions.IgnoreCase))) return null;
        var identity = (reader ?? ReadBinary)(path);
        return identity is { Machine: 0x8664 } ? new(root, Path.GetFullPath(path), identity.Version, identity.Machine) : null;
    }
    internal static void ConfirmZip(string path, Func<string, VlcZipBinary?>? reader = null)
    {
        var identity = Identify(path, reader, requireMarker: false) ?? throw new IOException("VLC-ZIP-Kopie nicht bestätigt. / VLC ZIP copy is unconfirmed.");
        if (Marker(identity.Root) is not null) return;
        string marker = Path.Combine(identity.Root, ReceiptName); CheckParents(marker);
        using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new { Schema = 1, Variant, MainFile = "vlc.exe" });
        File.SetAttributes(marker, File.GetAttributes(marker) | FileAttributes.Hidden);
    }
    internal static bool CanAdopt(ProgramInstallation? known, string path)
    {
        if (known is null) return true;
        if (known.ManagedCopies is { Count: > 0 } || known.LocationEvidence?.Alternatives is { Count: > 1 } || known.Source == "Windows-App") return false;
        if (known.Source is "SelectedFile" or "Portable") return true;
        // A new ZIP destination has no executable yet; compare its future directory.
        string? registered = ProgramLocation.DirectoryOf(known.Path), selected = Path.IsPathFullyQualified(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;
        return registered is not null && selected is not null && !ProgramInstallTargets.Overlapping([registered, selected]);
    }
    internal static bool ProofEligible(ProgramInstallation proof) => proof.Source == "Portable" && proof.Variant == Variant && proof.Path is not null
        && !proof.WinGetCompatible && proof.ManagedCopies is not { Count: > 0 } && proof.LocationEvidence?.Alternatives is not { Count: > 1 };
    internal static string Destination(PortablePackage package, string parent)
    {
        if (!Regex.IsMatch(package.Version, @"^\d+\.\d+\.\d+$") || !Directory.Exists(parent)) throw new IOException("Ungültiger VLC-Zielordner. / Invalid VLC destination.");
        string target = Path.Combine(Path.GetFullPath(parent), "VLC-" + package.Version + "-ZIP");
        if (!SafePath(target)) throw new IOException("Geschützter oder verknüpfter VLC-Zielordner. / Protected or linked VLC destination.");
        return target;
    }
    private static string PackageRoot(string directory, string version)
    {
        string root = Path.Combine(directory, "vlc-" + version);
        if (!Directory.Exists(root) || Directory.EnumerateFileSystemEntries(directory).Count() != 1) throw new IOException("VLC-ZIP-Wurzel nicht bestätigt. / VLC ZIP root is unconfirmed.");
        foreach (string name in new[] { "vlc.exe", "libvlc.dll", "libvlccore.dll" })
            if (!ProgramPortableUpdates.PlainPath(Path.Combine(root, name))) throw new IOException("VLC-Paketdateien fehlen. / VLC package files are missing.");
        if (!Directory.Exists(Path.Combine(root, "plugins"))) throw new IOException("VLC-Plugins fehlen. / VLC plugins are missing.");
        return root;
    }
}
