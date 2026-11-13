using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal enum TeamSpeakInstallKind { Install, Update, Reinstall, Switch }
internal sealed record TeamSpeakInstallRequest(int Edition, string Version, TeamSpeakInstallKind Kind, TeamSpeakSnapshot Snapshot, TeamSpeakCopy? Copy, bool ReinstallRequested = false);
internal sealed record TeamSpeakInstallPlan(TeamSpeakInstallRequest Request, string Url, string? Sha256, string? OriginalHash);
internal sealed record TeamSpeakInstallResult(int ExitCode, bool Verified, string? Path, string? Reason = null);
internal interface ITeamSpeakInstaller
{
    Task<TeamSpeakInstallPlan> PrepareAsync(TeamSpeakInstallRequest request, CancellationToken token);
    Task<TeamSpeakInstallResult> ApplyAsync(TeamSpeakInstallPlan plan, Action<ProgramStepEvent> step, Action<ProgramDownload> download, CancellationToken token);
}

internal static class TeamSpeakInstallPolicy
{
    internal static bool Equal(string? a, string? b)
    {
        if (TeamSpeakInfoService.Normalize(a) is not { } x || TeamSpeakInfoService.Normalize(b) is not { } y) return false;
        var left = x.Split("-beta", StringSplitOptions.None); var right = y.Split("-beta", StringSplitOptions.None);
        if (left.Length != right.Length || left.Length == 2 && left[1] != right[1]) return false;
        static string Numeric(string value) => string.Join('.', value.Split('.').Select(s => s.TrimStart('0') is { Length: > 0 } digits ? digits : "0").Reverse().SkipWhile(n => n == "0").Reverse());
        return Numeric(left[0]) == Numeric(right[0]);
    }
    internal static TeamSpeakInstallKind? Decide(int edition, string? target, TeamSpeakSnapshot snapshot, out string? reason)
    {
        reason = null;
        if (edition is not (3 or 6) || TeamSpeakInfoService.Edition(target) != edition) { reason = "source"; return null; }
        if (!snapshot.Complete) { reason = "incomplete"; return null; }
        var copies = snapshot.Copies.Where(x => x.Edition == edition).ToArray();
        if (copies.Length > 1) { reason = "multiple"; return null; }
        var copy = copies.SingleOrDefault();
        var replaced = copy is null && edition == 6 ? snapshot.Copies.Where(x => x.Edition == 5).ToArray() : [];
        if (replaced.Length > 1) { reason = "multiple"; return null; }
        var existing = copy ?? replaced.SingleOrDefault();
        if (existing is not null && (existing.Source != "Registry" || existing.Path is null || !File.Exists(existing.Path) || existing.Version is null)) { reason = "variant"; return null; }
        if (edition == 6 && existing?.Scope == "machine") { reason = "variant"; return null; }
        if (copy is null) return replaced.Length == 1 ? TeamSpeakInstallKind.Switch : TeamSpeakInstallKind.Install;
        if (Equal(copy.Version, target)) return TeamSpeakInstallKind.Reinstall;
        if (TeamSpeakInfoService.Newer(copy.Version, target)) return TeamSpeakInstallKind.Update;
        // A resource build does not prove the beta channel. It requires an explicit channel-switch confirmation.
        if (!copy.Version!.Contains("-beta", StringComparison.OrdinalIgnoreCase) && target!.Contains("-beta", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(copy.Version, out var local) && Version.TryParse(target.Split('-')[0], out var remote)
            && local.Major == remote.Major && local.Minor == remote.Minor && local.Build == remote.Build) return TeamSpeakInstallKind.Switch;
        reason = "older"; return null;
    }
    internal static string Fingerprint(TeamSpeakSnapshot snapshot) => snapshot.Complete + "|" + string.Join(";", snapshot.Copies.Select(x => $"{x.Edition}|{x.Version}|{x.Path}|{x.Source}|{x.ProductCode}|{x.Scope}").Order(StringComparer.OrdinalIgnoreCase));
}

internal sealed class TeamSpeakInstaller : ITeamSpeakInstaller
{
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase) { "files.teamspeak-services.com" };
    private static readonly HashSet<string> ManifestHosts = new(StringComparer.OrdinalIgnoreCase) { "raw.githubusercontent.com" };
    private readonly ITeamSpeakInfoService _info;
    private readonly Func<TeamSpeakInstallPlan, string, Action<ProgramDownload>, CancellationToken, Task> _download;
    private readonly Func<string, TeamSpeakInstallPlan, TeamSpeakMsiInfo?> _validate;
    private readonly Func<ProcessStartInfo, Task<int>> _run;
    private readonly Func<string, CancellationToken, Task<string>> _manifest;
    private readonly Func<string, TeamSpeakCopy?> _executable;
    private readonly string _root;
    internal TeamSpeakInstaller(ITeamSpeakInfoService? info = null,
        Func<TeamSpeakInstallPlan, string, Action<ProgramDownload>, CancellationToken, Task>? download = null,
        Func<string, TeamSpeakInstallPlan, TeamSpeakMsiInfo?>? validate = null, Func<ProcessStartInfo, Task<int>>? run = null,
        Func<string, CancellationToken, Task<string>>? manifest = null, string? root = null, Func<string, TeamSpeakCopy?>? executable = null)
    {
        _info = info ?? new TeamSpeakInfoService(); _download = download ?? DownloadAsync; _validate = validate ?? Validate;
        _run = run ?? RunAsync; _manifest = manifest ?? ReadManifestAsync;
        _executable = executable ?? TeamSpeakInfoService.ReadExecutable;
        _root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "TeamSpeak Downloads");
    }
    internal static string Url(int edition, string version)
    {
        if (TeamSpeakInfoService.Edition(version) != edition || edition is not (3 or 6)) throw new InvalidDataException("Unconfirmed TeamSpeak edition/version.");
        return edition == 3 ? $"https://files.teamspeak-services.com/releases/client/{version}/TeamSpeak3-Client-win64-{version}.exe"
            : $"https://files.teamspeak-services.com/{(version.Contains("-beta", StringComparison.Ordinal) ? "pre_releases" : "releases")}/client/{version}/teamspeak-client.msi";
    }
    internal static string? ManifestHash(string manifest, string version, string url)
    {
        if (ProgramPortableUpdates.ManifestField(manifest, "PackageIdentifier") != "TeamSpeakSystems.TeamSpeakClient"
            || ProgramPortableUpdates.ManifestField(manifest, "PackageVersion") != version || ProgramPortableUpdates.ManifestField(manifest, "InstallerType") != "nullsoft") return null;
        var hashes = Regex.Matches(manifest, @"(?m)^\s*InstallerUrl:\s*([^\r\n]+)\r?\n\s*InstallerSha256:\s*([A-Fa-f0-9]{64})\s*$")
            .Where(m => m.Groups[1].Value.Trim() == url).Select(m => m.Groups[2].Value.ToUpperInvariant()).Distinct().ToArray();
        return hashes.Length == 1 ? hashes[0] : null;
    }
    public async Task<TeamSpeakInstallPlan> PrepareAsync(TeamSpeakInstallRequest request, CancellationToken token)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64) throw new IOException("Dieser TeamSpeak-Installer benötigt Windows x64. / This TeamSpeak installer requires Windows x64.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(40));
        var current = await _info.ReadLocalAsync(limit.Token);
        if (TeamSpeakInstallPolicy.Fingerprint(current) != TeamSpeakInstallPolicy.Fingerprint(request.Snapshot)
            || TeamSpeakInstallPolicy.Decide(request.Edition, request.Version, current, out _) != request.Kind
            || current.Copies.SingleOrDefault(x => x.Edition == request.Edition) != request.Copy) throw new IOException("Installationsstand geändert. Bitte erneut prüfen. / Installation changed. Please check again.");
        var online = await _info.ReadAvailableAsync(limit.Token);
        if (online.GetValueOrDefault(request.Edition) != request.Version) throw new IOException("Herstellerversion geändert. Bitte erneut prüfen. / Publisher version changed. Please check again.");
        if (request.Copy?.Path is { } existing)
        {
            var file = _executable(existing);
            if (file?.Edition != request.Edition) throw new IOException("TeamSpeak-Programmdatei nicht bestätigt. / TeamSpeak executable not verified.");
            if (request.Edition == 3 && ProgramPortableUpdates.Machine(existing) != 0x8664) throw new IOException("32-Bit-Installation bleibt getrennt. / A 32-bit installation stays separate.");
            if (request.Edition == 6 && (request.Copy.Scope == "machine" || !UserPath(existing))) throw new IOException("Der Hersteller bietet hier eine Benutzerinstallation. Vorhandene Variante bleibt getrennt. / The publisher offers a per-user installer; existing variant stays separate.");
        }
        if (request.Kind == TeamSpeakInstallKind.Switch && request.Copy is null)
        {
            var legacy = current.Copies.Single(x => x.Edition == 5);
            if (legacy.Scope == "machine" || !UserPath(legacy.Path!) || _executable(legacy.Path!)?.Edition != 5) throw new IOException("TeamSpeak-5-Ziel nicht eindeutig bestätigt. / TeamSpeak 5 target not verified.");
        }
        string url = Url(request.Edition, request.Version);
        string? sha = request.Edition == 3 ? ManifestHash(await _manifest(request.Version, limit.Token), request.Version, url) : null;
        if (request.Edition == 3 && sha is null) throw new IOException("Offizieller Installer nicht durch WinGet-Prüfsumme bestätigt. / Official installer checksum not confirmed by WinGet.");
        var previous = request.Copy ?? current.Copies.SingleOrDefault(x => request.Edition == 6 && x.Edition == 5);
        return new(request, url, sha, previous?.Path is { } path ? ProgramPortableUpdates.Hash(path) : null);
    }
    private static bool UserPath(string path) => Path.GetFullPath(path).StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static async Task<string> ReadManifestAsync(string version, CancellationToken token) => Encoding.UTF8.GetString(await CatalogWeb.ReadAsync(new($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/t/TeamSpeakSystems/TeamSpeakClient/{version}/TeamSpeakSystems.TeamSpeakClient.installer.yaml"), ManifestHosts, 512 * 1024, token));
    private static async Task DownloadAsync(TeamSpeakInstallPlan plan, string destination, Action<ProgramDownload> progress, CancellationToken token)
    {
        using var connection = new OnlineRequest(token);
        using var response = await CatalogWeb.OpenAsync(new(plan.Url), DownloadHosts, connection.Token);
        await using var source = await response.Content.ReadAsStreamAsync(connection.Token);
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await ProgramPortableUpdates.CopyDownloadAsync(source, file, response.Content.Headers.ContentLength, progress, connection.Token);
    }
    internal static TeamSpeakMsiInfo? Validate(string file, TeamSpeakInstallPlan plan)
    {
        if (plan.Url != Url(plan.Request.Edition, plan.Request.Version)) throw new InvalidDataException("TeamSpeak download identity mismatch.");
        if (!ManufacturerSignature.Verify(file, "TeamSpeak Systems GmbH")) throw new IOException("TeamSpeak-Herstellersignatur nicht bestätigt. / TeamSpeak publisher signature not verified.");
        if (plan.Request.Edition == 3)
        {
            if (plan.Sha256 is null || !ProgramPortableUpdates.Hash(file).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("TeamSpeak checksum mismatch.");
            return null;
        }
        var metadata = TeamSpeakInstallerMetadata.Read(file);
        if (metadata.Name != "TeamSpeak" || metadata.Manufacturer != "TeamSpeak" || metadata.Architecture != "x64" || metadata.PerUser != "1" || metadata.AllUsers.Length != 0
            || metadata.Version != plan.Request.Version.Split('-')[0] || !Guid.TryParse(metadata.ProductCode, out _)
            || !Version.TryParse(metadata.FileVersion, out var target) || !Version.TryParse(metadata.Version, out var core)
            || target.Major != 6 || target.Minor != core.Minor || target.Build != core.Build) throw new IOException("TeamSpeak-Clientpaket nicht bestätigt. / TeamSpeak client package not verified.");
        if (plan.Request.Copy is { Path: { } oldPath })
        {
            var installed = TeamSpeakInfoService.FixedFileVersion(FileVersionInfo.GetVersionInfo(oldPath));
            if (installed is null || installed > target) throw new IOException("Keine bestätigte Installation ohne Downgrade. / Installation without downgrade not verified.");
        }
        return metadata;
    }
    internal static ProcessStartInfo StartInfo(TeamSpeakInstallPlan plan, string file)
    {
        if (file.IndexOfAny(['"', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(file)) throw new IOException("Invalid installer path.");
        var start = new ProcessStartInfo { UseShellExecute = true };
        if (plan.Request.Edition == 6)
        { start.FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"); start.Arguments = (plan.Request.Kind == TeamSpeakInstallKind.Reinstall ? "/famus " : "/i ") + "\"" + file + "\" /qn /norestart"; }
        else
        {
            start.FileName = file; string? directory = ProgramLocation.DirectoryOf(plan.Request.Copy?.Path);
            bool machine = plan.Request.Copy?.Scope == "machine" || plan.Request.Copy?.Scope is null && directory is not null && new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(x => x.Length > 0).Any(x => directory.StartsWith(x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            start.Arguments = "/S " + (machine ? "/ALLUSERS" : "/CURRENTUSER"); if (machine) start.Verb = "runas";
            if (directory is not null) { if (directory.IndexOfAny(['"','\r','\n']) >= 0) throw new IOException("Invalid destination."); start.Arguments += " /D=" + directory; }
        }
        return start;
    }
    private static async Task<int> RunAsync(ProcessStartInfo start)
    { using var process = Process.Start(start) ?? throw new IOException("TeamSpeak installer could not start."); await process.WaitForExitAsync(); return process.ExitCode; }
    internal static TeamSpeakCopy? VerifiedCopy(TeamSpeakInstallRequest request, TeamSpeakMsiInfo? metadata, TeamSpeakSnapshot after, out string? reason,
        Func<string, Version?>? fixedVersion = null, Func<string, bool>? trustedFile = null)
    {
        reason = null;
        if (!after.Complete) { reason = "Lokaler Bestand nicht vollständig. / Local inventory incomplete."; return null; }
        var found = after.Copies.Where(x => x.Edition == request.Edition && x.Source == "Registry" && x.Path is not null).ToArray();
        if (found.Length != 1) { reason = "Installiertes Ziel nicht eindeutig gefunden. / Installed target not uniquely identified."; return null; }
        var copy = found[0];
        if (request.Copy is not null && !string.Equals(copy.Path, request.Copy.Path, StringComparison.OrdinalIgnoreCase)) { reason = "Installationspfad hat sich geändert. / Installation path changed."; return null; }
        if (metadata is null)
        { if (TeamSpeakInstallPolicy.Equal(copy.Version, request.Version)) return copy; reason = "Installierte Zielversion weicht ab. / Installed version differs from target."; return null; }
        if (copy.ProductCode?.Equals(metadata.ProductCode, StringComparison.OrdinalIgnoreCase) != true) { reason = "MSI-Produktkennung weicht ab. / MSI product identity differs."; return null; }
        var actual = (fixedVersion ?? (path => TeamSpeakInfoService.FixedFileVersion(FileVersionInfo.GetVersionInfo(path))))(copy.Path!);
        if (!Version.TryParse(metadata.FileVersion, out var expected) || actual != expected) { reason = "Numerische Dateiversion stimmt nicht mit dem Paket überein. / Numeric file version does not match the package."; return null; }
        if (!(trustedFile ?? (path => ManufacturerSignature.Verify(path, "TeamSpeak Systems GmbH")))(copy.Path!)) { reason = "Signatur der installierten Programmdatei nicht bestätigt. / Installed executable signature not verified."; return null; }
        return copy;
    }
    internal static bool PlainDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(path)) return false;
        for (string? part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    public async Task<TeamSpeakInstallResult> ApplyAsync(TeamSpeakInstallPlan plan, Action<ProgramStepEvent> step, Action<ProgramDownload> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(_root); Directory.CreateDirectory(root);
        if (!PlainDirectory(root)) throw new IOException("Invalid staging directory.");
        var stage = Path.GetFullPath(Path.Combine(root, "Download-" + Guid.NewGuid().ToString("N")));
        if (!stage.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid staging path.");
        Directory.CreateDirectory(stage);
        try
        {
            if (plan.Url != Url(plan.Request.Edition, plan.Request.Version)) throw new IOException("Installer source changed.");
            step(new(ProgramStep.Check, ProgramStepState.Done)); step(new(ProgramStep.Download, ProgramStepState.Active));
            string file = Path.Combine(stage, plan.Request.Edition == 6 ? "TeamSpeak.msi" : "TeamSpeak.exe");
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token)) { limit.CancelAfter(TimeSpan.FromMinutes(15)); await _download(plan, file, progress, limit.Token); }
            var metadata = await Task.Run(() => _validate(file, plan), token);
            step(new(ProgramStep.Download, ProgramStepState.Done));
            var revalidated = await PrepareAsync(plan.Request, token);
            if (revalidated != plan) throw new IOException("TeamSpeak target/source changed.");
            var previous = plan.Request.Copy ?? plan.Request.Snapshot.Copies.SingleOrDefault(x => plan.Request.Edition == 6 && x.Edition == 5);
            if (previous?.Path is { } oldPath) ProgramGitHubUpdates.EnsureClosed(oldPath);
            token.ThrowIfCancellationRequested(); step(new(ProgramStep.Install, ProgramStepState.Active));
            int code = await _run(StartInfo(plan, file)); // An active installer must finish; never kill it on queue stop.
            if (code is not (0 or 3010)) return new(code, false, null);
            step(new(ProgramStep.Install, ProgramStepState.Done)); step(new(ProgramStep.Finish, ProgramStepState.Active));
            var after = await _info.ReadLocalAsync(CancellationToken.None);
            var result = VerifiedCopy(plan.Request, metadata, after, out var reason);
            if (result is not null && metadata is not null) TeamSpeakInstallReceipts.Save(result, plan.Request.Version);
            return new(code, result is not null, result?.Path, reason);
        }
        finally { try { if (PlainDirectory(stage)) Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
