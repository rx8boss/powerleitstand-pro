using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record TeamSpeakCopy(int Edition, string? Version, string? Path, string Source, string? ProductCode = null, string? Scope = null);
internal sealed record TeamSpeakSnapshot(IReadOnlyList<TeamSpeakCopy> Copies, bool Complete = true);
internal interface ITeamSpeakInfoService
{
    Task<TeamSpeakSnapshot> ReadLocalAsync(CancellationToken token);
    Task<IReadOnlyDictionary<int, string>> ReadAvailableAsync(CancellationToken token);
}

internal sealed class TeamSpeakInfoService : ITeamSpeakInfoService
{
    internal const string Website = "https://www.teamspeak.com/en/downloads";
    private static readonly Regex Number = new(@"^(?<major>[356])\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<build>\d+))?(?:-beta(?<beta>\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static string? Normalize(string? version)
    {
        if (version is null) return null;
        string value = Regex.Replace(version.Trim(), @",\s*", ".");
        return Number.IsMatch(value) ? value : null;
    }
    internal static int Edition(string? version) => Normalize(version) is { } valid ? valid[0] - '0' : 0;
    internal static Version? FixedFileVersion(FileVersionInfo file) => FixedFileVersion(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart, file.FilePrivatePart);
    internal static Version? FixedFileVersion(int major, int minor, int build, int revision)
        => major > 0 && minor >= 0 && build >= 0 && revision >= 0 ? new Version(major, minor, build, revision) : null;
    internal static bool Newer(string? installed, string? available)
    {
        if (Normalize(installed) is not { } local || Normalize(available) is not { } online || Edition(local) != Edition(online)) return false;
        var a = Number.Match(local); var b = Number.Match(online);
        // A stable installation must not be presented with an automatic move to a beta channel.
        if (!a.Groups["beta"].Success && b.Groups["beta"].Success) return false;
        foreach (string part in new[] { "minor", "patch", "build" })
        {
            if (!int.TryParse(a.Groups[part].Success ? a.Groups[part].Value : "0", out int x)
                || !int.TryParse(b.Groups[part].Success ? b.Groups[part].Value : "0", out int y)) return false;
            if (x != y) return y > x;
        }
        if (a.Groups["beta"].Success != b.Groups["beta"].Success) return a.Groups["beta"].Success;
        return a.Groups["beta"].Success && int.TryParse(a.Groups["beta"].Value, out int previous)
            && int.TryParse(b.Groups["beta"].Value, out int next) && next > previous;
    }
    internal static bool Client(string? name, string? publisher) => Regex.IsMatch(name ?? "", @"^TeamSpeak(?: ?[356])?(?: ?Client)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && Regex.IsMatch(publisher?.Trim() ?? "", @"^TeamSpeak(?: Systems(?: GmbH|,? Inc\.?)?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static TeamSpeakCopy? ReadExecutable(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(path) || !ProgramPortableUpdates.PlainPath(path)) return null;
        string name = System.IO.Path.GetFileName(path);
        if (!new[] { "TeamSpeak.exe", "ts3client_win64.exe", "ts3client_win32.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return null;
        var file = FileVersionInfo.GetVersionInfo(path);
        if (!Client(file.ProductName, file.CompanyName) || ProgramPortableUpdates.Machine(path) is not (0x014c or 0x8664 or 0xaa64)) return null;
        string? version = Normalize(file.ProductVersion) ?? Normalize(file.FileVersion) ?? Normalize(FixedFileVersion(file)?.ToString());
        int edition = Edition(version);
        // The explicit TS3 executable identifies its generation even when version resources are absent.
        if (edition == 0 && name.StartsWith("ts3client_", StringComparison.OrdinalIgnoreCase)) edition = 3;
        if (edition == 0 || name.StartsWith("ts3client_", StringComparison.OrdinalIgnoreCase) && edition != 3) return null;
        return new(edition, version, System.IO.Path.GetFullPath(path), "File");
    }
    public Task<TeamSpeakSnapshot> ReadLocalAsync(CancellationToken token) => Task.Run(() => ReadLocal(token), token);
    private static TeamSpeakSnapshot ReadLocal(CancellationToken token)
    {
        var copies = new List<TeamSpeakCopy>(); bool complete = true;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view); using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var key in uninstall?.GetSubKeyNames() ?? [])
                {
                    token.ThrowIfCancellationRequested(); using var item = uninstall!.OpenSubKey(key);
                    if (!Client(item?.GetValue("DisplayName") as string, item?.GetValue("Publisher") as string)) continue;
                    var version = Normalize(item?.GetValue("DisplayVersion") as string); int edition = Edition(version);
                    if (edition == 0)
                    {
                        var named = Regex.Match(item?.GetValue("DisplayName") as string ?? "", @"^TeamSpeak ?([356])(?: ?Client)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        if (named.Success) edition = int.Parse(named.Groups[1].Value);
                        else { complete = false; continue; }
                    }
                    var path = ProgramLocalInventory.ExistingIcon(item?.GetValue("DisplayIcon") as string);
                    if (path is not null)
                    {
                        var executable = ReadExecutable(path);
                        if (executable is null || executable.Edition != edition) path = null;
                        else version = executable.Version ?? version;
                    }
                    if (path is null && item?.GetValue("InstallLocation") is string folder && !folder.StartsWith(@"\\", StringComparison.Ordinal) && System.IO.Path.IsPathFullyQualified(folder) && Directory.Exists(folder))
                        foreach (string file in edition == 3 ? new[] { "ts3client_win64.exe", "ts3client_win32.exe" } : new[] { "TeamSpeak.exe" })
                            if (ReadExecutable(System.IO.Path.Combine(folder, file)) is { } proof && proof.Edition == edition) { path = proof.Path; version = proof.Version ?? version; break; }
                    if (path is null && edition == 6 && Guid.TryParse(key, out _))
                    {
                        try { if (TeamSpeakInstallerMetadata.RegisteredExecutable(key) is { } registered && ReadExecutable(registered) is { Edition: 6 } proof) { path = proof.Path; version = proof.Version ?? version; } }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    }
                    string? productCode = Guid.TryParse(key, out _) ? key : null;
                    string scope = productCode is not null ? TeamSpeakInstallerMetadata.RegisteredScope(productCode) ?? (hive == RegistryHive.CurrentUser ? "user" : "machine") : hive == RegistryHive.CurrentUser ? "user" : "machine";
                    copies.Add(new(edition, version, path, "Registry", productCode, scope));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception) { complete = false; }
        }
        copies = copies.DistinctBy(x => x.Edition + "|" + x.Version + "|" + x.Path, StringComparer.OrdinalIgnoreCase).Select(TeamSpeakInstallReceipts.Apply).ToList();
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")];
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        foreach (var relative in new[] { @"TeamSpeak\TeamSpeak.exe", @"TeamSpeak 6\TeamSpeak.exe", @"TeamSpeak 5\TeamSpeak.exe", @"TeamSpeak 3 Client\ts3client_win64.exe", @"TeamSpeak 3 Client\ts3client_win32.exe" })
        {
            token.ThrowIfCancellationRequested();
            try { if (ReadExecutable(System.IO.Path.Combine(root, relative)) is { } proof) AddFile(copies, proof); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { complete = false; }
        }
        return new(copies.DistinctBy(x => x.Edition + "|" + x.Version + "|" + x.Path, StringComparer.OrdinalIgnoreCase).Select(TeamSpeakInstallReceipts.Apply).ToArray(), complete);
    }
    internal static void AddFile(List<TeamSpeakCopy> copies, TeamSpeakCopy proof)
    {
        if (copies.Any(x => string.Equals(x.Path, proof.Path, StringComparison.OrdinalIgnoreCase))) return;
        int registered = copies.FindIndex(x => x.Edition == proof.Edition && x.Path is null && Normalize(proof.Version) is not null && Normalize(x.Version) is { } version
            && !Newer(version, proof.Version) && !Newer(proof.Version, version));
        if (registered >= 0) copies[registered] = copies[registered] with { Path = proof.Path };
        else copies.Add(proof);
    }
    internal static IReadOnlyDictionary<int, string> ParseAvailable(string html)
    {
        var matches = new Dictionary<int, HashSet<string>>();
        foreach (Match match in Regex.Matches(WebUtility.HtmlDecode(html), @"https://files\.teamspeak-services\.com/[^\s\""'<>]+", RegexOptions.CultureInvariant))
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var url) || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Host != "files.teamspeak-services.com") continue;
            var path = Regex.Match(url.AbsolutePath, @"^/(?:pre_releases|releases)/client/(?<version>[0-9.]+(?:-beta\d+)?)/(?<file>[^/]+)$", RegexOptions.CultureInvariant);
            if (!path.Success || Normalize(path.Groups["version"].Value) is not { } version) continue;
            int edition = Edition(version); string filename = path.Groups["file"].Value;
            if (!(edition == 3 && filename == "TeamSpeak3-Client-win64-" + version + ".exe" || edition == 6 && filename == "teamspeak-client.msi")) continue;
            if (!matches.TryGetValue(edition, out var values)) matches[edition] = values = [];
            values.Add(version);
        }
        return matches.Where(x => x.Value.Count == 1).ToDictionary(x => x.Key, x => x.Value.Single());
    }
    public async Task<IReadOnlyDictionary<int, string>> ReadAvailableAsync(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
        var html = await CatalogWeb.ReadAsync(new Uri(Website), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "www.teamspeak.com", "teamspeak.com" }, 2 * 1024 * 1024, limit.Token);
        return ParseAvailable(Encoding.UTF8.GetString(html));
    }
}
