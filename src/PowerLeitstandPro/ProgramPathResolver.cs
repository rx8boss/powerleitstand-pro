using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record ProgramPathRegistration(string Name, string Publisher, string? Version, string? InstallLocation,
    string? DisplayIcon, string? UninstallString, string ProductKey, bool WindowsInstaller, RegistryHive Hive, RegistryView View);
internal sealed record ProgramPathCandidate(string Path, string Source, int Priority, bool Anchored = false);
internal sealed record ProgramPathFile(string Product, string Publisher, string? Version, string Description);
internal sealed record ProgramPathEvidence(string? Directory, string? Executable, string Source, string Scope,
    string? FileVersion = null, string? Note = null, IReadOnlyList<string>? Alternatives = null)
{
    internal string? Path => Executable ?? Directory;
}

// One short-lived resolver per inventory pass. Reads existing hints only; never launches
// a target, repairs MSI products, scans a drive or writes registry/shortcut/cache data.
internal sealed class ProgramPathResolver
{
    private readonly Action<string, TimeSpan>? _timing;
    internal ProgramPathResolver(Action<string, TimeSpan>? timing = null) => _timing = timing;
    private sealed record Rule(string[] Files, string Products, string Publishers, string[] Folders,
        bool SeparateFileVersion = false, string? SignedPublisher = null);
    private static readonly Dictionary<string, Rule> Rules = new()
    {
        ["extra-electronicarts-eadesktop"] = new(["EADesktop.exe"], "EA|EA Desktop|EA App", "Electronic Arts", [@"Electronic Arts\EA Desktop\EA Desktop"]),
        ["adwcleaner"] = new(["adwcleaner.exe"], "AdwCleaner", @"Malwarebytes(?: Inc\.?)?", []),
        ["node-js"] = new(["node.exe"], @"Node\.js", @"Node\.js|OpenJS Foundation|Node\.js Foundation", ["nodejs"]),
        ["putty"] = new(["putty.exe"], "PuTTY suite|PuTTY", "Simon Tatham", ["PuTTY"]),
        ["steam"] = new(["steam.exe"], "Steam", "Valve Corporation", ["Steam"], SeparateFileVersion: true),
        ["msi-afterburner"] = new(["MSIAfterburner.exe"], "MSIAfterburner|MSI Afterburner", @"Micro-Star International(?: Co\.,? Ltd\.?)?|MSI Co\.,? LTD\.?", ["MSI Afterburner"], SignedPublisher: "MICRO-STAR INTERNATIONAL CO., LTD."),
        ["turtle-beach-swarm-ii"] = new(["Turtle Beach Swarm II.exe"], "Turtle Beach Swarm II", @"Turtle Beach|Voyetra Turtle Beach, Inc\.", ["Turtle Beach Swarm II"], SignedPublisher: "Voyetra Turtle Beach, Inc."),
        ["7-zip"] = new(["7zFM.exe"], "7-Zip", "Igor Pavlov", ["7-Zip"]),
        ["git"] = new(["git.exe"], "Git", "The Git Development Community", [@"Git\cmd"]),
        ["firefox"] = new(["firefox.exe"], "Firefox", "Mozilla Corporation", ["Mozilla Firefox"]),
        ["vlc"] = new(["vlc.exe"], "VLC media player", "VideoLAN", [@"VideoLAN\VLC"]),
        ["teamspeak"] = new(["TeamSpeak.exe", "ts3client_win64.exe", "ts3client_win32.exe"], "TeamSpeak(?: Client| 3 Client)?", "TeamSpeak(?: Systems(?: GmbH)?)?", ["TeamSpeak", "TeamSpeak 3 Client"])
        , ["brave"] = new(["brave.exe"], "Brave(?: Browser)?", @"Brave Software(?:,? Inc\.?)?|BraveSoftware(?: Inc\.?)?", [@"BraveSoftware\Brave-Browser\Application"])
        , ["opera"] = new(["opera.exe"], "Opera(?: Internet Browser)?", "Opera(?: Software)?", ["Opera"])
        , ["opera-gx"] = new(["opera.exe"], "Opera GX(?: Internet Browser)?", "Opera(?: Software)?", ["Opera GX"])
        , ["notepad"] = new(["notepad++.exe"], @"Notepad\+\+", @"Don HO don\.h@free\.fr|Notepad\+\+(?: Team)?", ["Notepad++"])
        , ["winrar"] = new(["WinRAR.exe"], "WinRAR", @"Alexander Roshal|win\.rar GmbH", ["WinRAR"])
        , ["ubisoft-connect"] = new(["UbisoftConnect.exe"], "Ubisoft Connect", "Ubisoft(?: Entertainment)?", [@"Ubisoft\Ubisoft Game Launcher"], SeparateFileVersion: true)
        , ["ultraplus-manager"] = new(["UltraPlusManager.exe"], "UltraPlusManager|UltraPlus Manager", "UltraPlusManager|UltraPlus Manager|UltraPlus", ["UltraPlus Manager"], SeparateFileVersion: true)
        , ["bcuninstaller"] = new(["BCUninstaller.exe"], "Bulk Crap Uninstaller|BCUninstaller", @"https://github\.com/Klocman/Bulk-Crap-Uninstaller|Klocman software|Marcin Szeniak", ["BCUninstaller"])
        , ["dlss-swapper"] = new(["DLSS Swapper.exe"], "DLSS Swapper", "DLSS Swapper|beeradmoore", ["DLSS Swapper"])
    };
    private static readonly Regex Helper = new(@"(?:^|[ ._-])(?:unins\w*|uninstall\w*|setup\w*|install(?:er)?|updat(?:e|er)\w*|helper\w*|crashpad\w*|service\w*|maintenancetool)(?:$|[ ._-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HelperDescription = new(@"(?:^|[ ._-])(?:setup|installer|updater|helper|crashpad|maintenance service)(?:$|[ ._-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VersionTail = new(@"\s+(?:(?:version|release)\s+)?v?\d+(?:\.\d+){1,4}(?:[-+][\w.]+)?(?=\s*(?:\(|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, ProgramPathFile?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);
    private List<(RegistryHive Hive, string Path)>? _appPaths;
    private List<string>? _shortcuts;
    private readonly Dictionary<(string Path, string Publisher), bool> _signatures = new();

    internal static ProgramPathRegistration Registration(RegistryKey key, string productKey, string name, string publisher,
        string? version, RegistryHive hive, RegistryView view) => new(name, publisher, version,
        key.GetValue("InstallLocation") as string, key.GetValue("DisplayIcon") as string, key.GetValue("UninstallString") as string,
        productKey, key.GetValue("WindowsInstaller") is int msi && msi == 1, hive, view);

    internal ProgramPathEvidence? Resolve(ProgramPathRegistration registration, ProgramEntry? entry, CancellationToken token)
    {
        if (_timing is null) return ResolveCore(registration, entry, token);
        var watch = Stopwatch.StartNew();
        try { return ResolveCore(registration, entry, token); }
        finally { _timing(registration.Name, watch.Elapsed); }
    }
    // Package folders are hints, not product-version evidence. This bounded local
    // read never imports a package script, runs the manager or searches another installation.
    internal ProgramPathEvidence? ResolveManaged(ProgramEntry entry, ProgramManagedCopy copy, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? root = ExistingDirectory(copy.PackageDirectory);
        if (root is null || !UsefulDirectory(root) || !PlainPackageDirectory(root)) return null;
        var candidates = new List<ProgramPathCandidate>();
        var folders = new Queue<(string Path, int Depth)>(); folders.Enqueue((root, 0)); int visited = 0;
        while (folders.Count > 0 && visited++ < 24)
        {
            token.ThrowIfCancellationRequested(); var folder = folders.Dequeue();
            try
            {
                if (!PlainPackageDirectory(folder.Path) || !InDirectory(root, folder.Path)) continue;
                // cmd/git.exe is Git for Windows' public entry point. Keep other
                // equally plausible copies ambiguous instead of relying on enumeration order.
                int priority = entry.Id == "git" && System.IO.Path.GetFileName(folder.Path).Equals("cmd", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                AddFolder(candidates, folder.Path, copy.Manager + " package file", priority, entry, true, token);
                if (folder.Depth < 2)
                    foreach (string child in Directory.EnumerateDirectories(folder.Path).Take(24 - visited))
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) folders.Enqueue((child, folder.Depth + 1));
            }
            catch (Exception e) when (ReadFailure(e)) { }
        }
        var registration = new ProgramPathRegistration(entry.Name, entry.Publisher, null, root, null, null,
            copy.PackageId, false, copy.Scope == "Machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        var result = Select(registration, entry, candidates.Where(c => InDirectory(root, c.Path)
            && File.Exists(c.Path) && ProgramPortableUpdates.PlainPath(c.Path)), Inspect, root, path => TrustedEmptyPublisher(entry, path));
        return result?.Executable is not null ? result with { Source = copy.Manager + " package file", Scope = copy.Scope }
            : result?.Alternatives is { Count: > 0 } ? result with { Directory = null, Source = copy.Manager + " ambiguous package files", Scope = copy.Scope } : null;
    }
    private static bool PlainPackageDirectory(string path)
    {
        if (!Directory.Exists(path)) return false;
        for (string? part = System.IO.Path.GetFullPath(path); part is not null; part = System.IO.Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private ProgramPathEvidence? Choose(ProgramPathRegistration r, ProgramEntry? entry, IEnumerable<ProgramPathCandidate> candidates, string? directory)
        => Select(r, entry, candidates.Select(c => (Candidate: c, Target: FileTarget(c.Path)))
            .Where(x => x.Target is not null).Select(x => x.Candidate with { Path = x.Target! }), Inspect, directory, path => TrustedEmptyPublisher(entry, path));
    private string? FileTarget(string path)
    {
        if (!_targets.TryGetValue(path, out string? target)) _targets[path] = target = LocalTarget(path, file: true);
        return target;
    }
    internal ProgramPathEvidence? ResolveWinGetPortable(ProgramPathRegistration registration, ProgramEntry entry, string? target)
    {
        if (target is null || !Path.IsPathFullyQualified(target) || ExistingDirectory(registration.InstallLocation) is not { } directory
            || !UsefulDirectory(directory) || !InDirectory(directory, target)) return null;
        return Choose(registration, entry, [new(target, "WinGet portable", 0, true)], directory) is { Executable: not null } evidence ? evidence : null;
    }

    private ProgramPathEvidence? ResolveCore(ProgramPathRegistration registration, ProgramEntry? entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string? directory = ExistingDirectory(registration.InstallLocation);
        if (directory is not null && !UsefulDirectory(directory)) directory = null;
        if (entry?.Id == "net-sdk") return directory is null ? null : new(directory, null, "Registry directory", Scope(registration), Note: "SDK; no single main executable");
        var candidates = new List<ProgramPathCandidate>();
        string? icon = ExecutableToken(registration.DisplayIcon, icon: true);
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (directory is not null && UsefulDirectory(directory)) anchors.Add(directory);
        if (icon is not null)
        {
            string? parent = ExistingDirectory(System.IO.Path.GetDirectoryName(icon));
            if (parent is not null && UsefulDirectory(parent)) anchors.Add(parent);
            candidates.Add(new(icon, "Registry icon", directory is not null && !SameDirectory(directory, parent) ? 2 : 0, true));
        }
        string? uninstall = ExecutableToken(registration.UninstallString);
        if (uninstall is not null && !System.IO.Path.GetFileName(uninstall).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = ExistingDirectory(System.IO.Path.GetDirectoryName(uninstall));
            if (parent is not null && UsefulDirectory(parent) && File.Exists(uninstall)) anchors.Add(parent);
        }
        // A registered containing folder remains useful even if its icon is an
        // uninstaller or its executable metadata cannot identify the main program.
        directory ??= anchors.Count == 1 ? anchors.Single() : null;
        foreach (string folder in anchors) AddFolder(candidates, folder, "Registry folder", 2, entry, true, token);
        var direct = Choose(registration, entry, candidates, directory);
        if (direct?.Executable is not null) return direct;

        // A component/extension is not a standalone client. In particular, do not map
        // an Overwolf CurseForge extension to the separately installed CurseForge client.
        if (TryExtension(registration, entry) is { } extension) return extension;
        if (Component(registration.Name)) return direct ?? (directory is null ? null : new(directory, null, "Registry directory", Scope(registration), Note: "Component; no main executable inferred"));

        _appPaths ??= ReadAppPaths(token);
        foreach (var item in _appPaths.Where(p => p.Hive == registration.Hive && ExpectedFile(entry, registration, p.Path)))
        {
            string? target = FileTarget(item.Path);
            if (target is not null && (anchors.Count == 0 || anchors.Any(a => InDirectory(a, target))))
                candidates.Add(new(target, "App Paths", 3, anchors.Count > 0));
        }
        var pathsResult = Choose(registration, entry, candidates, directory);
        if (pathsResult?.Executable is not null) return pathsResult;

        _shortcuts ??= ReadShortcuts(token);
        foreach (var link in _shortcuts.Where(l => ShortcutLabel(entry, registration, System.IO.Path.GetFileNameWithoutExtension(l))).Take(12))
        {
            token.ThrowIfCancellationRequested();
            if (ReadShortcut(link) is { } linked && FileTarget(linked) is { } target && (anchors.Count == 0 || anchors.Any(a => InDirectory(a, target))))
                candidates.Add(new(target, "Start menu shortcut", 4, anchors.Count > 0));
        }
        var shortcutResult = Choose(registration, entry, candidates, directory);
        if (shortcutResult?.Executable is not null) return shortcutResult;

        if (entry is not null && Rules.TryGetValue(entry.Id, out var rule))
        {
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") }.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string relative in rule.Folders)
            {
                token.ThrowIfCancellationRequested();
                string folder = System.IO.Path.Combine(root, relative);
                // An explicit different installation folder must never be replaced by
                // an arbitrary same-product copy in the standard location.
                if (anchors.Count > 0 && !anchors.Any(a => InDirectory(a, folder))) continue;
                AddFolder(candidates, folder, "Known product folder", 5, entry, anchors.Contains(folder), token);
            }
            foreach (string file in rule.Files)
            {
                token.ThrowIfCancellationRequested();
                foreach (var process in Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(file)).Take(16))
                {
                    using (process) try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path is not null && FileTarget(path) is { } target && (anchors.Count == 0 || anchors.Any(a => InDirectory(a, target))))
                            candidates.Add(new(target, "Running process path", 6, anchors.Count > 0));
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
        }
        var inexpensive = Choose(registration, entry, candidates, directory);
        if (inexpensive?.Executable is not null) return inexpensive;
        if (registration.WindowsInstaller && Guid.TryParse(registration.ProductKey, out _))
            candidates.AddRange(ProgramMsiPaths.Read(registration, token));
        return Choose(registration, entry, candidates, directory);
    }

    // Kept independent of Windows enumeration so incorrect/ambiguous evidence can be
    // exercised with isolated metadata without executing or modifying an installed app.
    internal static ProgramPathEvidence? Select(ProgramPathRegistration registration, ProgramEntry? entry,
        IEnumerable<ProgramPathCandidate> candidates, Func<string, ProgramPathFile?> inspect, string? directory = null,
        Func<string, bool>? trustedEmptyPublisher = null)
    {
        var accepted = new List<(ProgramPathCandidate Candidate, ProgramPathFile File)>();
        foreach (var candidate in candidates.OrderBy(c => c.Priority).DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (Helper.IsMatch(System.IO.Path.GetFileNameWithoutExtension(candidate.Path)) || !candidate.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var file = inspect(candidate.Path); if (file is null) continue;
            if (entry?.Id == "turtle-beach-swarm-ii" && file.Version == "0.0.0.0") file = file with { Version = null };
            if (!IdentityMatches(registration, entry, candidate.Path, file, candidate.Anchored, trustedEmptyPublisher)) continue;
            if (!VersionFits(registration.Version, file.Version) && !(entry is not null && Rules.TryGetValue(entry.Id, out var special) && special.SeparateFileVersion && candidate.Anchored)) continue;
            accepted.Add((candidate, file));
        }
        if (accepted.Count == 0) return directory is null ? null : new(directory, null, "Registry directory", Scope(registration));
        int priority = accepted[0].Candidate.Priority;
        var best = accepted.Where(x => x.Candidate.Priority == priority).ToArray();
        if (entry is not null && Rules.TryGetValue(entry.Id, out var rule))
        {
            int Rank(string path) { int index = Array.FindIndex(rule.Files, f => f.Equals(System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)); return index < 0 ? int.MaxValue : index; }
            int rank = best.Min(x => Rank(x.Candidate.Path)); best = best.Where(x => Rank(x.Candidate.Path) == rank).ToArray();
        }
        if (best.Length != 1) return new(directory, null, "Ambiguous paths", Scope(registration), Note: "Multiple matching executable copies; no automatic selection", Alternatives: best.Select(x => x.Candidate.Path).ToArray());
        var chosen = best[0];
        return new(System.IO.Path.GetDirectoryName(chosen.Candidate.Path), chosen.Candidate.Path, chosen.Candidate.Source, Scope(registration), chosen.File.Version);
    }

    internal static bool IdentityMatches(ProgramPathRegistration registration, ProgramEntry? entry, string path,
        ProgramPathFile file, bool anchored, Func<string, bool>? trustedEmptyPublisher = null)
    {
        if (HelperDescription.IsMatch(file.Description) || Helper.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path))) return false;
        if (entry?.Id == "adwcleaner" && anchored)
            return Regex.IsMatch(System.IO.Path.GetFileName(path), @"\Aadwcleaner(?:_[\d.]+)?\.exe\z", RegexOptions.IgnoreCase)
                && Exact(file.Product, "AdwCleaner") && Exact(file.Publisher, @"Malwarebytes(?: Inc\.?)?");
        if (entry is not null && Rules.TryGetValue(entry.Id, out var rule))
        {
            if (!rule.Files.Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) || !Exact(file.Product, rule.Products)) return false;
            return Exact(file.Publisher, rule.Publishers) || string.IsNullOrWhiteSpace(file.Publisher) && anchored
                && rule.SignedPublisher is not null && trustedEmptyPublisher?.Invoke(path) == true;
        }
        return entry is not null ? ProgramLocalInventory.Matches(entry, file.Product, file.Publisher)
            : Clean(registration.Name) == Clean(file.Product) && Clean(registration.Publisher).Length > 0
                && Clean(registration.Publisher) == Clean(file.Publisher);
    }
    private bool TrustedEmptyPublisher(ProgramEntry? entry, string path)
    {
        if (entry is null || !Rules.TryGetValue(entry.Id, out var rule) || rule.SignedPublisher is not { } publisher) return false;
        var key = (path, publisher);
        if (!_signatures.TryGetValue(key, out bool valid))
            _signatures[key] = valid = ManufacturerSignature.Verify(path, publisher);
        return valid;
    }
    private ProgramPathFile? Inspect(string path)
    {
        if (_files.TryGetValue(path, out var existing)) return existing;
        ProgramPathFile? result = null;
        try
        {
            if (LocalFile(path) && ProgramPortableUpdates.PlainPath(path) && ProgramPortableUpdates.Machine(path) is 0x014c or 0x8664 or 0xaa64)
            {
                using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream);
                stream.Position = 0x3c; int pe = reader.ReadInt32();
                if (pe < 0 || pe > stream.Length - 24) return null;
                stream.Position = pe + 22; ushort flags = reader.ReadUInt16();
                if ((flags & 0x2000) != 0 || (flags & 2) == 0) return null;
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!Helper.IsMatch(System.IO.Path.GetFileNameWithoutExtension(info.OriginalFilename ?? "")))
                    result = new(info.ProductName ?? "", info.CompanyName ?? "", NumericVersion(info.ProductVersion) ?? NumericVersion(info.FileVersion), info.FileDescription ?? "");
            }
        }
        catch (Exception e) when (ReadFailure(e)) { }
        _files[path] = result; return result;
    }
    internal static string? NumericVersion(string? value)
    {
        var match = Regex.Match(value?.Trim() ?? "", @"^(?:v|Release\s+)?(\d+(?:[.,]\s*\d+){1,4})(?:$|[ +(-]|\.[a-zA-Z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? Regex.Replace(match.Groups[1].Value, @",\s*", ".") : null;
    }
    internal static bool VersionFits(string? registered, string? file)
    {
        string? a = NumericVersion(registered), b = NumericVersion(file);
        if (a is null) return true;
        if (b is null) return false;
        string Number(string value) => value.TrimStart('0') is { Length: > 0 } n ? n : "0";
        var left = a.Split('.').Select(Number).ToArray(); var right = b.Split('.').Select(Number).ToArray();
        // Registry versions commonly omit build components; a different major/minor
        // must never be grafted onto that registered copy.
        int count = Math.Min(left.Length, right.Length);
        return left.Take(count).SequenceEqual(right.Take(count)) && left.Skip(count).All(n => n == "0");
    }
    internal static string? ExistingDirectory(string? value)
        => LocalTarget(value, file: false);
    // Existing installation hints may contain legitimate versioned directory
    // junctions (EA Desktop does). Resolve at most eight local links and validate
    // the final target; never follow a network/device link or enumerate its drive.
    internal static string? LocalTarget(string? value, bool file)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int hop = 0; hop < 9; hop++)
            {
                if (!seen.Add(path)) return null;
                string root = System.IO.Path.GetPathRoot(path)!; string current = root; bool changed = false;
                string[] parts = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
                for (int index = 0; index < parts.Length; index++)
                {
                    current = System.IO.Path.Combine(current, parts[index]);
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
                    FileSystemInfo link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new System.IO.FileInfo(current);
                    string? target = link.ResolveLinkTarget(returnFinalTarget: false)?.FullName;
                    if (target is null || !System.IO.Path.IsPathFullyQualified(target) || target.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                    path = System.IO.Path.GetFullPath(System.IO.Path.Combine(new[] { target }.Concat(parts.Skip(index + 1)).ToArray()));
                    changed = true; break;
                }
                if (!changed) return (file ? File.Exists(path) : Directory.Exists(path)) ? path : null;
            }
            return null;
        }
        catch (Exception e) when (ReadFailure(e)) { return null; }
    }
    internal static string? ExecutableToken(string? value, bool icon = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\r', '\n', '\0']) >= 0) return null;
        string text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (icon) text = Regex.Replace(text, @",\s*-?\d+$", "");
        var match = text.StartsWith('"') ? Regex.Match(text, "^\"([^\"]+\\.exe)\"(?:\\s|$)", RegexOptions.IgnoreCase)
            : Regex.Match(text, @"^([a-zA-Z]:\\[^""\r\n]*?\.exe)(?:\s|$)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        try { string path = System.IO.Path.GetFullPath(match.Groups[1].Value); return LocalFilePath(path) ? path : null; }
        catch (Exception e) when (ReadFailure(e)) { return null; }
    }
    private static bool LocalFilePath(string path) => System.IO.Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && !path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) && !path.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase);
    private static bool LocalFile(string path) => LocalFilePath(path) && File.Exists(path);
    private static bool SameDirectory(string? left, string? right) => string.Equals(left?.TrimEnd('\\'), right?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    internal static bool InDirectory(string directory, string path)
    {
        try
        {
            string root = System.IO.Path.GetFullPath(directory).TrimEnd('\\'), target = System.IO.Path.GetFullPath(path);
            return SameDirectory(root, target) || target.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (ReadFailure(e)) { return false; }
    }
    private static bool UsefulDirectory(string path) => !new[] { System.IO.Path.GetPathRoot(path), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp") }.Any(p => SameDirectory(p, path));
    private static bool Component(string name) => Regex.IsMatch(name, @"(?:\bSDK\b|\bRuntime\b|\bRedistributable\b|\bWebView\b|\bDriver\b|\bService\b|\bUpdate for\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Scope(ProgramPathRegistration r) => r.Hive == RegistryHive.CurrentUser ? "User registry" : "Machine registry; MSI context checked separately";
    private static bool Exact(string value, string expression) => Regex.IsMatch(value.Trim(), "^(?:" + expression + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Clean(string value) => new(Regex.Replace(VersionTail.Replace(value.Trim(), ""), @"\s*\((?:x64|x86|arm64|64-bit|32-bit|User|System)\)\s*$", "", RegexOptions.IgnoreCase).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    internal static bool ReadFailure(Exception e) => e is IOException or UnauthorizedAccessException or SecurityException or ArgumentException
        or NotSupportedException or System.ComponentModel.Win32Exception or COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
    private static bool ExpectedFile(ProgramEntry? entry, ProgramPathRegistration r, string path) => entry is not null && Rules.TryGetValue(entry.Id, out var rule)
        ? rule.Files.Contains(System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) : Clean(System.IO.Path.GetFileNameWithoutExtension(path)) == Clean(r.Name);
    private static bool ShortcutLabel(ProgramEntry? entry, ProgramPathRegistration r, string label) => Clean(label) == Clean(r.Name)
        || entry is not null && (Clean(label) == Clean(entry.Name) || entry.Id == "extra-electronicarts-eadesktop" && Clean(label) == "ea");
    private static void AddFolder(List<ProgramPathCandidate> found, string folder, string source, int priority, ProgramEntry? entry, bool anchored, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            if (!UsefulDirectory(folder) || ExistingDirectory(folder) is null) return;
            if (entry is not null && entry.Id != "adwcleaner" && Rules.TryGetValue(entry.Id, out var rule))
                foreach (string name in rule.Files) found.Add(new(System.IO.Path.Combine(folder, name), source, priority, anchored));
            else foreach (string path in Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).Take(64))
                found.Add(new(path, source, priority, anchored));
        }
        catch (Exception e) when (ReadFailure(e)) { }
    }
    private static List<(RegistryHive Hive, string Path)> ReadAppPaths(CancellationToken token)
    {
        var result = new List<(RegistryHive, string)>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view); using var parent = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            foreach (string child in (parent?.GetSubKeyNames() ?? []).Take(1024))
            { token.ThrowIfCancellationRequested(); using var key = parent!.OpenSubKey(child); if (ExecutableToken(key?.GetValue(null) as string) is { } path) result.Add((hive, path)); }
        }
        catch (Exception e) when (ReadFailure(e)) { }
        return result.Distinct().ToList();
    }
    private static List<string> ReadShortcuts(CancellationToken token)
    {
        var result = new List<string>(); int visited = 0;
        var folders = new Queue<(string Path, int Depth)>();
        foreach (var root in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }) folders.Enqueue((Environment.GetFolderPath(root), 0));
        while (folders.Count > 0 && visited++ < 160 && result.Count < 768)
        {
            token.ThrowIfCancellationRequested(); var folder = folders.Dequeue();
            try
            {
                if (ExistingDirectory(folder.Path) is null) continue;
                result.AddRange(Directory.EnumerateFiles(folder.Path, "*.lnk").Take(768 - result.Count));
                if (folder.Depth < 3) foreach (string child in Directory.EnumerateDirectories(folder.Path).Take(48)) folders.Enqueue((child, folder.Depth + 1));
            }
            catch (Exception e) when (ReadFailure(e)) { }
        }
        return result;
    }
    internal static string? ReadShortcut(string path)
    {
        object? shell = null, link = null;
        try
        {
            if (!ProgramPortableUpdates.PlainPath(path)) return null;
            var type = Type.GetTypeFromProgID("WScript.Shell"); if (type is null) return null;
            shell = Activator.CreateInstance(type); link = ((dynamic)shell!).CreateShortcut(path);
            string target = ((dynamic)link).TargetPath;
            return ExecutableToken('"' + target + '"');
        }
        catch (Exception e) when (ReadFailure(e)) { return null; }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
    private static ProgramPathEvidence? TryExtension(ProgramPathRegistration r, ProgramEntry? entry)
    {
        if (entry?.Id != "curseforge" || r.DisplayIcon is null) return null;
        string path = Environment.ExpandEnvironmentVariables(r.DisplayIcon.Trim().Trim('"')).Replace('/', '\\');
        var match = Regex.Match(path, @"^(.*\\Overwolf\\Extensions\\cchhcaiapeikjbdbpfplgmpobbcdkdaphclbmkbj\\\d+(?:\.\d+)+)\\assets\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && ExistingDirectory(match.Groups[1].Value) is { } directory
            ? new(directory, null, "Registry Overwolf extension", Scope(r), Note: "Extension directory; no standalone executable") : null;
    }
}
