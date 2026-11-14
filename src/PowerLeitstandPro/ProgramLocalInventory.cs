using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record ProgramInstallation(string? Version, string Source, string? Path = null,
    bool WinGetCompatible = true, string? Variant = null, IReadOnlyList<ProgramManagedCopy>? ManagedCopies = null,
    ProgramPathEvidence? LocationEvidence = null);

internal static class ProgramLocalInventory
{
    // Explicit product aliases only. Version suffixes may not turn a helper, SDK component,
    // documentation link or another product edition into an installation of this entry.
    private sealed record Rule(string Names, string Publishers);
    private static readonly Dictionary<string, Rule> Rules = new()
    {
        ["firefox"] = new(@"(?:Mozilla )?Firefox(?: \(x(?:64|86) [a-zA-Z-]+\))?", @"Mozilla(?: Corporation)?"),
        ["brave"] = new("Brave", @"Brave Software(?:,? Inc\.?)?|BraveSoftware(?: Inc\.?)?|Die Brave-Autoren"),
        ["opera"] = new(@"Opera(?: Stable)?", "Opera(?: Software)?"),
        ["opera-gx"] = new(@"Opera GX(?: Stable)?", "Opera(?: Software)?"),
        ["notepad"] = new(@"Notepad\+\+(?: \((?:64-bit x64|32-bit x86|64-bit|32-bit)\))?", @"Notepad\+\+(?: Team)?"),
        ["vs-code"] = new(@"(?:Microsoft )?Visual Studio Code(?: \((?:User|System)\))?", @"Microsoft(?: Corporation)?"),
        ["node-js"] = new(@"Node\.js", @"(?:OpenJS|Node\.js) Foundation"),
        ["vlc"] = new(@"VLC(?: media player)?", "VideoLAN"),
        ["cursor"] = new(@"Cursor(?: \((?:User|System)\))?", @"Anysphere(?:,? Inc\.?)?"),
        ["winrar"] = new(@"WinRAR(?: \((?:64|32)-(?:Bit|bit)\))?", @"win\.rar GmbH"),
        ["putty"] = new(@"PuTTY(?: release)?(?: \((?:64|32)-bit\))?", "Simon Tatham"),
        ["crystaldiskinfo"] = new("CrystalDiskInfo", "Crystal Dew World"),
        ["net-sdk"] = new(@"(?:Microsoft )?\.NET SDK(?: \((?:x64|x86|arm64)\))?", @"Microsoft(?: Corporation)?"),
        ["vortex"] = new("Vortex", @"Nexus Mods|Black Tree Gaming Ltd\.?"),
        ["wowup-cf"] = new("WowUp-CF", @"WowUp(?: LLC)?"),
        ["tukui-client"] = new(@"Tukui(?:\s*Client)?", @"Tukui(?:\.org| Community)?"),
        ["satisfactory-mod-manager"] = new(@"Satisfactory Mod Manager(?: \((?:current user|all users)\))?", "Satisfactory Modding(?: Community)?"),
        ["ultraplus-manager"] = new("UltraPlus Manager", "UltraPlus(?: Manager)?"),
        ["turtle-beach-swarm-ii"] = new("(?:Turtle Beach )?Swarm II", @"Turtle Beach|Voyetra Turtle Beach,? Inc\.?"),
        ["bcuninstaller"] = new(@"BCUninstaller|Bulk Crap Uninstaller", @"Klocman software|Marcin Szeniak"),
        ["msi-afterburner"] = new("MSI Afterburner", @"Micro-Star International(?: Co\.,? Ltd\.?)?|MSI Co\.,? LTD\.?"),
        ["teamspeak"] = new(@"TeamSpeak(?: [356](?: Client)?)?", "TeamSpeak(?: Systems(?: GmbH)?)?"),
        ["artcraft-launcher"] = new("ArtCraft Launcher", @"ArtCraft|Learning Machines LLC")
        , ["lightshot"] = new(@"Lightshot(?: \d+(?:\.\d+)*)?", "Skillbrains(?: LLC)?")
    };
    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static readonly Regex VersionSuffix = new(@"\s+(?:version\s+|release\s+)?v?\d+(?:\.\d+){1,4}(?:[-+][A-Za-z0-9.]+)?(?=\s*(?:\(|$))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, (Regex Name, Regex Publisher)> Matchers = Rules.ToDictionary(x => x.Key,
        x => (new Regex("^(?:" + x.Value.Names + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new Regex("^(?:" + x.Value.Publishers + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)));
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Name, string Publisher)> Keys = new();
    internal static bool Matches(ProgramEntry entry, string? name, string? publisher)
        => MatchesPrepared(entry, name, publisher, name is null ? "" : Key(name), publisher is null ? "" : Key(publisher));
    private static bool MatchesPrepared(ProgramEntry entry, string? name, string? publisher, string nameKey, string publisherKey)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher)) return false;
        if (Matchers.TryGetValue(entry.Id, out var rule))
        {
            // Remove only a trailing numeric version and architecture, not arbitrary suffix words.
            string clean = VersionSuffix.Replace(name.Trim(), "");
            return rule.Name.IsMatch(clean) && rule.Publisher.IsMatch(publisher.Trim());
        }
        var keys = Keys.GetOrAdd(entry.Id + "\n" + entry.Name + "\n" + entry.Publisher, _ => (Key(entry.Name), Key(entry.Publisher)));
        if (entry.InstalledNames is { Length: > 0 })
        {
            var cleanKey = Key(VersionSuffix.Replace(name.Trim(), ""));
            return entry.InstalledNames.Any(alias => Key(alias) == nameKey || Key(alias) == cleanKey)
                && keys.Publisher.Length > 0 && publisherKey.Contains(keys.Publisher, StringComparison.Ordinal);
        }
        return nameKey == keys.Name
            && keys.Publisher.Length > 0 && publisherKey.Contains(keys.Publisher, StringComparison.Ordinal);
    }
    internal static bool Compatible(ProgramEntry entry, string? version)
        => entry.Id != "teamspeak" || version?.StartsWith("3.", StringComparison.Ordinal) == true;

    internal static Dictionary<string, ProgramInstallation> Read(IEnumerable<ProgramEntry> entries, CancellationToken token, bool includeManaged = true, ProgramPathResolver? pathResolver = null)
    {
        var catalog = entries.ToArray(); var result = new Dictionary<string, ProgramInstallation>();
        var locations = pathResolver ?? new ProgramPathResolver();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var child in uninstall?.GetSubKeyNames() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    using var item = uninstall!.OpenSubKey(child);
                    string? name = item?.GetValue("DisplayName") as string, publisher = item?.GetValue("Publisher") as string;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher)) continue;
                    string nameKey = Key(name), publisherKey = Key(publisher);
                    foreach (var entry in catalog.Where(e => MatchesPrepared(e, name, publisher, nameKey, publisherKey)))
                    {
                        var version = ProgramVersions.ConfirmedVersion(item?.GetValue("DisplayVersion") as string);
                        if (entry.Id == "turtle-beach-swarm-ii" && version == "0.0.0.0") version = null;
                        if (entry.Id == "vlc" && version is not null) version = ProgramVlcReinstallation.Version(version);
                        if (entry.Id == "net-sdk")
                        {
                            var sdk = Regex.Match(item?.GetValue("DisplayName") as string ?? "", @"\.NET SDK (\d+\.\d+\.\d+)");
                            if (sdk.Success) version = sdk.Groups[1].Value;
                        }
                        var registration = ProgramPathResolver.Registration(item!, child, name, publisher!, version, hive, view);
                        var location = locations.Resolve(registration, entry, token);
                        if (entry.PackageId is not null
                            && string.Equals(item!.GetValue("WinGetPackageIdentifier") as string, entry.PackageId, StringComparison.OrdinalIgnoreCase)
                            && item.GetValue("WinGetSourceIdentifier") as string == "Microsoft.Winget.Source_8wekyb3d8bbwe"
                            && item.GetValue("WinGetInstallerType") as string == "portable")
                        {
                            var target = item.GetValue("PortableTargetFullPath") as string;
                            if (locations.ResolveWinGetPortable(registration, entry, target) is { } registeredPortable)
                                location = registeredPortable;
                            else if (entry.Id == "adwcleaner" && AdwCleanerWinGetExecutable(version, registration.InstallLocation, target) is { } executable)
                                location = new(Path.GetDirectoryName(executable), executable, "WinGet portable", hive == RegistryHive.CurrentUser ? "User" : "Machine", version);
                        }
                        var path = location?.Path;
                        if (version is null && location?.Executable is not null) version = location.FileVersion;
                        if (entry.Id == "net-sdk") path = ExistingDirectory(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "sdk", version ?? ""));
                        bool extension = location?.Source == "Registry Overwolf extension";
                        var evidence = new ProgramInstallation(version, "Registry", path, Compatible(entry, version) && !extension,
                            extension ? "CurseForge · Overwolf" : entry.Id == "teamspeak" && !Compatible(entry, version) ? "TeamSpeak " + (version ?? "?") : null,
                            LocationEvidence: location);
                        if (!result.TryGetValue(entry.Id, out var old) || Prefer(evidence, old)) result[entry.Id] = evidence;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        ReadPortable(catalog, result, token);
        if (includeManaged) ProgramManagedInventory.Merge(result, ProgramManagedInventory.Read(catalog, token));
        return result;
    }
    private static bool Prefer(ProgramInstallation next, ProgramInstallation old)
    {
        if (System.Version.TryParse(next.Version, out var n) && System.Version.TryParse(old.Version, out var o)) return n > o;
        return old.Version is null && next.Version is not null || old.Path is null && next.Path is not null;
    }
    internal static string? ExistingIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string path = Environment.ExpandEnvironmentVariables(Regex.Replace(value.Trim(), @",\s*-?\d+$", "").Trim('"'));
        return System.IO.Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !System.IO.Path.GetFileName(path).StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            && !path.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }
    private static string? ExistingDirectory(string? value) => value is { Length: > 0 } && System.IO.Path.IsPathFullyQualified(value) && Directory.Exists(value) ? value : null;
    private sealed record PortableRule(string[] Files, string Products, string Companies, bool EmptyCompany = false);
    private static readonly Dictionary<string, PortableRule> Portable = new()
    {
        // OBS is recognized through its explicit path and portable marker, not a
        // bare executable found in a conventional installation directory.
        ["obs-studio"] = new([], "OBS Studio|OBS", @"OBS|OBS Project|Wizards of OBS(?: LLC)?"),
        ["compactgui"] = new(["CompactGUI.exe"], "CompactGUI", "IridiumIO"),
        ["stability-matrix"] = new(["StabilityMatrix.exe"], @"StabilityMatrix\.Avalonia|Stability ?Matrix", @"StabilityMatrix\.Avalonia|Lykos(?: ?AI)?"),
        ["autoruns"] = new(["Autoruns64.exe", "Autoruns.exe", "Autoruns64a.exe"], "Sysinternals autoruns", @"Sysinternals(?: - www\.sysinternals\.com)?|Microsoft Corporation"),
        ["melonloader"] = new(["MelonLoader.Installer.exe"], @"MelonLoader\.Installer", @"discord\.gg/2Wn3N2P|LavaGang"),
        ["geek-uninstaller"] = new(["geek.exe"], "Geek[- ]Uninstaller", "Geek Uninstaller"),
        ["adwcleaner"] = new(["adwcleaner.exe", "adwcleaner_*.exe"], "AdwCleaner", @"Malwarebytes(?: Inc\.?)?"),
        ["rufus"] = new(["rufus.exe", "rufus-*.exe"], "Rufus", "Akeo(?: Consulting)?"),
        ["extra-ventoy-ventoy"] = new([@"ventoy\Ventoy2Disk.exe", "Ventoy2Disk.exe"], "Ventoy", "Ventoy", true),
        ["tukui-client"] = new(["Tukui Client.exe", "TukuiClient.exe"], "Tukui Client", "Tukui(?:.org)?", true),
        ["teamspeak"] = new([@"TeamSpeak\TeamSpeak.exe"], "TeamSpeak Client", "TeamSpeak Systems GmbH"),
        ["artcraft-launcher"] = new([@"ArtCraft Launcher\artcraft-launcher.exe"], "ArtCraft Launcher", "Learning Machines LLC"),
        ["gpu-z"] = new([@"GPU-Z\GPU-Z.exe", "GPU-Z.exe", "GPU-Z.*.exe"], "GPU-Z - Video card Information Utility", @"TechPowerUp(?: \(www\.techpowerup\.com\))?"),
        ["cpu-z"] = new([@"CPUID\CPU-Z\cpuz.exe", "cpuz.exe", "cpuz_x64.exe", "cpuz_x32.exe"], "CPUID CPU-Z|CPU-Z Application", "CPUID"),
        ["firefox"] = new([@"Mozilla Firefox\firefox.exe"], "Firefox", "Mozilla Corporation"),
        ["vlc"] = new([@"VideoLAN\VLC\vlc.exe"], "VLC media player", "VideoLAN")
    };
    internal static bool MatchesPortable(string id, string? product, string? company) => Portable.TryGetValue(id, out var rule)
        && Regex.IsMatch(product ?? "", "^(?:" + rule.Products + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && (rule.EmptyCompany && string.IsNullOrWhiteSpace(company) || Regex.IsMatch(company?.Trim() ?? "", "^(?:" + rule.Companies + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    internal static string? AdwCleanerWinGetExecutable(string? version, string? registered, string? exactTarget = null)
    {
        const string folder = "Malwarebytes.AdwCleaner_Microsoft.Winget.Source_8wekyb3d8bbwe";
        var directories = new[]
        {
            registered,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages", folder),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages", folder),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinGet", "Packages", folder)
        };
        return AdwCleanerExecutable(version, Directory.Exists(registered) ? [registered] : directories, path =>
        {
            var file = FileVersionInfo.GetVersionInfo(path);
            return (file.ProductName, file.CompanyName, PortableVersion("adwcleaner", path, file));
        }, exactTarget);
    }

    internal static string? AdwCleanerExecutable(string? version, IEnumerable<string?> directories,
        Func<string, (string? Product, string? Publisher, string? Version)> inspect, string? exactTarget = null)
    {
        if (version is null) return null;
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) continue;
            try
            {
                var files = exactTarget is not null && Path.IsPathFullyQualified(exactTarget)
                    && ProgramPathResolver.InDirectory(directory, exactTarget) && File.Exists(exactTarget)
                    ? new[] { exactTarget } : Directory.EnumerateFiles(directory, "adwcleaner*.exe").Take(32);
                foreach (var file in files)
                {
                    if (!Regex.IsMatch(Path.GetFileName(file), @"\Aadwcleaner(?:_[\d.]+)?\.exe\z", RegexOptions.IgnoreCase)
                        || !ProgramPortableUpdates.PlainPath(file)) continue;
                    var metadata = inspect(file);
                    if (MatchesPortable("adwcleaner", metadata.Product, metadata.Publisher)
                        && metadata.Version is not null && ProgramPathResolver.VersionFits(version, metadata.Version))
                        matches.Add(file);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return matches.Count == 1 ? matches.Single() : null;
    }

    internal static IEnumerable<string> PortableRoots()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            System.IO.Path.Combine(profile, "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), AppContext.BaseDirectory,
            System.IO.Path.Combine(profile, "Applications"), System.IO.Path.Combine(profile, "PortableApps") };
        foreach (string key in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } drive && System.IO.Path.IsPathFullyQualified(drive)) roots.Add(System.IO.Path.Combine(drive, "Desktop"));
        foreach (var desktop in roots.Take(3).Concat(roots.Where(r => r.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            try { if (Directory.Exists(desktop)) roots.AddRange(new DirectoryInfo(desktop).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Take(40).Select(d => d.FullName)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }
    internal static void ReadPortable(ProgramEntry[] catalog, Dictionary<string, ProgramInstallation> result, CancellationToken token, IEnumerable<string>? searchRoots = null)
    {
        // Bounded known locations only; never execute an EXE or crawl whole drives.
        var roots = (searchRoots ?? PortableRoots()).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();
        foreach (var entry in catalog.Where(e => (!result.TryGetValue(e.Id, out var known) || known.Path is null) && Portable.ContainsKey(e.Id)))
        foreach (var root in roots)
        foreach (var relative in Portable[entry.Id].Files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var paths = relative.Contains('*') ? Directory.EnumerateFiles(root, relative, SearchOption.TopDirectoryOnly).Take(20)
                    : new[] { System.IO.Path.Combine(root, relative) };
                foreach (var path in paths.Where(File.Exists))
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (MatchesPortable(entry.Id, info.ProductName, info.CompanyName))
                    {
                        if (result.TryGetValue(entry.Id, out var registered))
                        {
                            string? fileVersion = PortableVersion(entry.Id, path, info);
                            if (registered.LocationEvidence?.Alternatives is { Count: > 1 } || !ProgramPathResolver.VersionFits(registered.Version, fileVersion)) continue;
                            result[entry.Id] = registered with { Path = path, Version = registered.Version ?? fileVersion, LocationEvidence = null };
                        }
                        else
                        {
                            string? version = PortableVersion(entry.Id, path, info);
                            // The MelonLoader installer version is not the per-game loader version.
                            result.TryAdd(entry.Id, entry.Id == "melonloader"
                                ? new(null, "Portable-Setup", path, false, "MelonLoader Installer " + (version ?? "?"))
                                : new(version, "Portable", path, false));
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception) { }
        }
    }

    internal static string? PortableVersion(string id, string path, FileVersionInfo? info = null)
    {
        info ??= FileVersionInfo.GetVersionInfo(path);
        if (!MatchesPortable(id, info.ProductName, info.CompanyName)) return null;
        if (id == "extra-ventoy-ventoy")
        {
            // Ventoy2Disk's resource version is not the distributed Ventoy package version.
            var file = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "ventoy", "version");
            if (!File.Exists(file) || new FileInfo(file).Length > 64 || !ProgramPortableUpdates.PlainPath(file)) return null;
            var value = File.ReadAllText(file).Trim();
            return Regex.IsMatch(value, @"^\d+\.\d+\.\d+$") ? value : null;
        }
        return ProgramVersions.ConfirmedVersion(Regex.Replace((info.ProductVersion ?? "").Split('+')[0], @",\s*", "."))
            ?? ProgramVersions.ConfirmedVersion(Regex.Replace((info.FileVersion ?? "").Split('+')[0], @",\s*", "."));
    }
}
