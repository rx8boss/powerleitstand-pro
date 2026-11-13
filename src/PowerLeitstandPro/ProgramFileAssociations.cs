using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

// Stores only explicit user choices, never scans a drive or executes a selected file.
internal sealed class ProgramFileAssociations(string file, Func<IReadOnlyList<VlcRegisteredCopy>>? vlcCopies = null)
{
    private Dictionary<string, string> ReadPaths()
    {
        if (!File.Exists(file)) return new(StringComparer.OrdinalIgnoreCase);
        if (new FileInfo(file).Length > 256 * 1024 || !ProgramPortableUpdates.PlainPath(file)) throw new IOException("Programmzuordnungen können nicht gelesen werden.");
        var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
        if (paths.Count > 512) throw new IOException("Zu viele Programmzuordnungen gespeichert.");
        return new(paths, StringComparer.OrdinalIgnoreCase);
    }

    internal static ProgramInstallation Validate(ProgramEntry entry, string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)
            || !ProgramPortableUpdates.PlainPath(path)) throw new IOException("Bitte eine vorhandene lokale Programm-EXE auswählen.");
        path = Path.GetFullPath(path);
        var machine = ProgramPortableUpdates.Machine(path);
        if (machine is not (0x014c or 0x8664 or 0xaa64)) throw new IOException("Keine unterstützte Windows-Programmdatei.");
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            stream.Position = 0x3c; int header = reader.ReadInt32();
            if (header > stream.Length - 24) throw new IOException("Der Dateikopf der Programmdatei ist ungültig.");
            stream.Position = header + 22; ushort flags = reader.ReadUInt16();
            if ((flags & 0x2000) != 0 || (flags & 2) == 0) throw new IOException("Bitte eine Programm-EXE statt einer Bibliothek auswählen.");
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        if (entry.Id == "vlc" && VlcPortableUpdates.Identify(path) is { } vlc)
            return new(vlc.Version, "Portable", vlc.Executable, false, VlcPortableUpdates.Variant);
        if (entry.Id == "obs-studio" && ObsPortableUpdates.Identify(path) is { } obs)
            return new(obs.Version, "Portable", obs.Executable, false, "OBS Studio Portable");
        bool portable = ProgramLocalInventory.MatchesPortable(entry.Id, info.ProductName, info.CompanyName);
        if (!portable && !ProgramLocalInventory.Matches(entry, info.ProductName, info.CompanyName))
            throw new IOException("Produkt und Hersteller passen nicht zum gewählten Programm.");
        if (entry.Id != "melonloader" && Regex.IsMatch(Path.GetFileNameWithoutExtension(path) + " " + Path.GetFileNameWithoutExtension(info.OriginalFilename ?? ""),
            @"(?:^|[ ._-])(?:setup|unins\w*|uninstall\w*|installer|update\w*|helper|crashpad\w*|squirrel)(?:$|[ ._-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new IOException("Bitte die Hauptdatei statt Installer oder Hilfsprogramm auswählen.");
        // A selected file is evidence of a file, not of an installation channel or portable distribution.
        var version = portable ? ProgramLocalInventory.PortableVersion(entry.Id, path, info)
            : ProgramVersions.ConfirmedVersion(info.ProductVersion?.Split('+')[0]) ?? ProgramVersions.ConfirmedVersion(info.FileVersion);
        if (entry.Id == "melonloader") version = null; // Setup version is not the per-game loader version.
        string architecture = machine == 0x014c ? "x86" : machine == 0x8664 ? "x64" : "ARM64";
        return new(version, "SelectedFile", path, false, "EXE · " + architecture);
    }

    internal ProgramInstallation Save(ProgramEntry entry, string path)
    {
        var proof = Validate(entry, path);
        var paths = ReadPaths();
        if (paths.Count >= 512 && !paths.ContainsKey(entry.Id)) throw new IOException("Zu viele Programmzuordnungen gespeichert.");
        paths[entry.Id] = proof.Path!;
        for (string? part = Path.GetFullPath(file); part is not null; part = Path.GetDirectoryName(part))
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verknüpfte Ablagen für Programmzuordnungen werden nicht verwendet.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(paths)); File.Move(temporary, file, true); }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return proof;
    }

    internal void Merge(IEnumerable<ProgramEntry> entries, Dictionary<string, ProgramInstallation> evidence, CancellationToken token)
    {
        Dictionary<string, string> paths;
        try { paths = ReadPaths(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return; }
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (!paths.TryGetValue(entry.Id, out var path)) continue;
            try
            {
                var selected = Validate(entry, path);
                if (entry.Id == "vlc" && selected.Source != "Portable" && evidence.GetValueOrDefault(entry.Id)?.Source == "Registry")
                {
                    var copy = ProgramVlcReinstallation.FindPath(path, (vlcCopies ?? ProgramVlcReinstallation.Read)());
                    if (copy is not null) evidence[entry.Id] = new(copy.Version, "Registry", copy.Executable);
                    continue;
                }
                if (!evidence.TryGetValue(entry.Id, out var known)) evidence[entry.Id] = selected;
                else if (entry.Id == "vlc" && selected.Variant == VlcPortableUpdates.Variant)
                { if (VlcPortableUpdates.CanAdopt(known, selected.Path!)) evidence[entry.Id] = selected; }
                else if (entry.Id == "obs-studio" && selected.Source == "Portable" && known.ManagedCopies is not { Count: > 0 })
                    // An explicit portable copy keeps its own version and route;
                    // never attach it to an unrelated registered installer copy.
                    evidence[entry.Id] = selected;
                else if (known.Path is null || !File.Exists(known.Path) && !Directory.Exists(known.Path))
                    // Keep independently detected package versions; EXE resources can use a different numbering scheme.
                    evidence[entry.Id] = known with { Path = selected.Path, Version = known.Version ?? (known.Source == "Registry" ? selected.Version : null), LocationEvidence = null };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { }
        }
    }
}
