using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record ObsPortableBinary(string Version, ushort Machine);
internal sealed record ObsPortableIdentity(string Root, string Executable, string Version, ushort Machine);

// A package updater, not a single-EXE recipe. Both official archives establish
// ownership; only those program files are changed. User directories stay in place.
internal sealed partial class ObsPortableUpdates
{
    internal const long ArchiveLimit = 1024L * 1024 * 1024;
    private const string Releases = "https://api.github.com/repos/obsproject/obs-studio/releases/";
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _read;
    private readonly Func<PortableUpdate, string, Action<ProgramDownload>, CancellationToken, Task>? _download;
    private readonly Func<string, ObsPortableBinary?> _binary;
    private readonly Action<string>? _checkpoint;
    private readonly string _backupRoot;

    internal ObsPortableUpdates(Func<Uri, CancellationToken, Task<byte[]>> read,
        Func<PortableUpdate, string, Action<ProgramDownload>, CancellationToken, Task>? download = null,
        string? backupRoot = null, Func<string, ObsPortableBinary?>? binary = null, Action<string>? checkpoint = null)
    {
        _read = read; _download = download; _binary = binary ?? ReadBinary; _checkpoint = checkpoint;
        _backupRoot = backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Portable Updates");
    }

    internal static ObsPortableBinary? ReadBinary(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!ProgramLocalInventory.MatchesPortable("obs-studio", info.ProductName, info.CompanyName)) return null;
            var match = Regex.Match(info.ProductVersion ?? info.FileVersion ?? "", @"^(\d+\.\d+\.\d+)(?:\.0)?(?:\+[^\s]+)?$");
            ushort machine = ProgramPortableUpdates.Machine(path);
            return match.Success && machine is 0x8664 or 0xaa64 ? new(match.Groups[1].Value, machine) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        { return null; }
    }

    internal static ObsPortableIdentity? Identify(string path, Func<string, ObsPortableBinary?>? reader = null)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || !ProgramPortableUpdates.PlainPath(path)
            || !Path.GetFileName(path).Equals("obs64.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var architecture = Path.GetDirectoryName(path)!; var bin = Path.GetDirectoryName(architecture); var root = bin is null ? null : Path.GetDirectoryName(bin);
        if (root is null || !Path.GetFileName(architecture).Equals("64bit", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase) || Marker(root) is null) return null;
        var identity = (reader ?? ReadBinary)(path);
        return identity is null ? null : new(Path.GetFullPath(root), Path.GetFullPath(path), identity.Version, identity.Machine);
    }

    internal static string? Marker(string root)
    {
        foreach (var name in new[] { "portable_mode.txt", "portable_mode" })
        {
            string path = Path.Combine(root, name);
            if (File.Exists(path) && ProgramPortableUpdates.PlainPath(path)) return path;
        }
        return null;
    }

    internal static bool SafeRoot(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length <= (Path.GetPathRoot(root)?.Length ?? 0) || !Directory.Exists(root)) return false;
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.UserProfile })
        {
            var special = Environment.GetFolderPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (special.Length == 0) continue;
            if (root.Equals(special, StringComparison.OrdinalIgnoreCase)) return false;
            if (folder is Environment.SpecialFolder.Windows or Environment.SpecialFolder.ProgramFiles or Environment.SpecialFolder.ProgramFilesX86
                && root.StartsWith(special + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private async Task<PortablePackage?> ReleaseAsync(string tag, CancellationToken token)
    {
        if (tag != "latest" && !Regex.IsMatch(tag, @"^\d+\.\d+\.\d+$")) return null;
        return ProgramPortable.ParseRelease(Encoding.UTF8.GetString(await _read(new(Releases + (tag == "latest" ? tag : "tags/" + tag)), token).ConfigureAwait(false)));
    }

    internal async Task<PortableUpdate?> ResolveAsync(ProgramInstallation proof, CancellationToken token)
    {
        if (proof.Source != "Portable" || proof.ManagedCopies is { Count: > 0 } || proof.Path is null
            || proof.LocationEvidence?.Alternatives is { Count: > 1 } || RuntimeInformation.OSArchitecture != Architecture.X64) return null;
        var local = Identify(proof.Path, _binary);
        if (local is null || local.Machine != 0x8664 || !SafeRoot(local.Root)
            || proof.Version is not null && !ProgramPortableUpdates.SameVersion(proof.Version, local.Version)) return null;
        var release = await ReleaseAsync("latest", token).ConfigureAwait(false);
        if (release is null || ProgramVersions.IsNewer(release.Version, local.Version)) return null;
        return new("obs-studio", local.Executable, local.Version, release.Version, release.Url, release.Sha256, release.Size, null,
            await Task.Run(() => ProgramPortableUpdates.Hash(local.Executable), token).ConfigureAwait(false));
    }

    internal Task ApplyAsync(PortableUpdate update, Action<ProgramStepEvent> step, Action<ProgramDownload> progress, CancellationToken token, bool reinstall = false)
        => Task.Run(() => ApplyCoreAsync(update, step, progress, token, reinstall), token);

    private async Task ApplyCoreAsync(PortableUpdate update, Action<ProgramStepEvent> step, Action<ProgramDownload> progress, CancellationToken token, bool reinstall)
    {
        if (update.Id != "obs-studio" || update.Parts is not null || update.ZipMember is not null) throw new IOException("OBS-Paketzuordnung ungültig. / Invalid OBS package identity.");
        var proof = new ProgramInstallation(update.InstalledVersion, "Portable", update.Path, false);
        var current = await ResolveAsync(proof, token).ConfigureAwait(false);
        if (current != update || !(ProgramVersions.IsNewer(update.InstalledVersion, update.Version)
            || reinstall && ProgramPortableUpdates.SameVersion(update.InstalledVersion, update.Version)))
            throw new IOException("OBS-Kopie oder Updateangebot wurde geändert. Bitte erneut prüfen. / OBS copy or update offer changed; check again.");
        var local = Identify(update.Path, _binary)!;
        EnsureNotRunning(local.Root);
        var previous = await ReleaseAsync(update.InstalledVersion, token).ConfigureAwait(false);
        if (previous is null || previous.Version != update.InstalledVersion) throw new IOException("Das bisherige OBS-Originalpaket konnte nicht bestätigt werden. / Previous official OBS package could not be verified.");
        var next = new PortablePackage(update.Version, update.Url, update.Sha256, update.Size ?? 0);
        string parent = Path.GetDirectoryName(local.Root)!;
        string stage = Path.Combine(parent, ".PowerLeitstand-OBS-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool retainStage = false;
        try
        {
            step(new(ProgramStep.Download, ProgramStepState.Active));
            long total = checked(previous.Size + next.Size);
            var oldRequest = update with { Version = previous.Version, Url = previous.Url, Sha256 = previous.Sha256, Size = previous.Size };
            await DownloadAsync(oldRequest, Path.Combine(stage, "previous.zip"), p => progress(new(Math.Min(p.Received, previous.Size), total)), token).ConfigureAwait(false);
            _checkpoint?.Invoke("downloaded-old");
            await DownloadAsync(update, Path.Combine(stage, "next.zip"), p => progress(new(previous.Size + Math.Min(p.Received, next.Size), total)), token).ConfigureAwait(false);
            _checkpoint?.Invoke("downloaded-new");
            step(new(ProgramStep.Download, ProgramStepState.Done)); step(new(ProgramStep.Install, ProgramStepState.Active));
            string oldDirectory = Path.Combine(stage, "old-package"), newDirectory = Path.Combine(stage, "new-package");
            await ProgramPortable.ExtractAsync(Path.Combine(stage, "previous.zip"), oldDirectory, token).ConfigureAwait(false);
            await ProgramPortable.ExtractAsync(Path.Combine(stage, "next.zip"), newDirectory, token).ConfigureAwait(false);
            var oldFiles = PackageFiles(oldDirectory, previous.Version, _binary, token);
            var newFiles = PackageFiles(newDirectory, next.Version, _binary, token);
            _checkpoint?.Invoke("prepared");
            // Re-confirm the selected immutable release, without silently changing versions.
            if (await ReleaseAsync(next.Version, token).ConfigureAwait(false) != next) throw new IOException("OBS-Downloadquelle wurde geändert. / OBS download source changed.");
            var repeated = Identify(update.Path, _binary);
            if (repeated != local || ProgramPortableUpdates.Hash(update.Path) != update.OriginalHash) throw new IOException("Die gewählte OBS-Kopie wurde zwischenzeitlich geändert. / Selected OBS copy changed.");
            EnsureNotRunning(local.Root); token.ThrowIfCancellationRequested();
            try { Commit(local, oldFiles, newFiles, stage, update, token); }
            catch (ObsRollbackException) { retainStage = true; throw; }
            step(new(ProgramStep.Install, ProgramStepState.Done));
        }
        finally
        {
            if (!retainStage) try { DeleteStage(stage, parent); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(PortableUpdate request, string path, Action<ProgramDownload> report, CancellationToken token)
    {
        if (request.Size is not (> 0 and <= ArchiveLimit) || request.Url != $"https://github.com/obsproject/obs-studio/releases/download/{request.Version}/OBS-Studio-{request.Version}-Windows-x64.zip"
            || !Regex.IsMatch(request.Sha256, "^[a-fA-F0-9]{64}$")) throw new IOException("Ungültiges offizielles OBS-Archiv. / Invalid official OBS archive.");
        if (_download is not null) await _download(request, path, report, token).ConfigureAwait(false);
        else
        {
            using var response = await CatalogWeb.OpenAsync(new(request.Url), ProgramGitHubUpdates.Hosts, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != request.Size) throw new IOException("OBS-Downloadgröße stimmt nicht überein. / OBS download size mismatch.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await ProgramPortableUpdates.CopyDownloadAsync(source, file, request.Size, report, token, ArchiveLimit).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (!ProgramPortableUpdates.PlainPath(path) || new FileInfo(path).Length != request.Size || ProgramPortableUpdates.Hash(path) != request.Sha256.ToUpperInvariant())
            throw new IOException("OBS-Archivgröße oder Prüfsumme wurde nicht bestätigt. / OBS archive size or checksum was not verified.");
    }
}
