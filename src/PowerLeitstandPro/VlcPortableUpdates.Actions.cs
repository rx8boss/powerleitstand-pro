using System.IO;
using System.Text.RegularExpressions;
namespace PowerControlProWpf;
internal sealed partial class VlcPortableUpdates
{
    internal Task ApplyAsync(PortableUpdate update, Action<ProgramStepEvent> step, Action<ProgramDownload> progress, CancellationToken token, bool reinstall = false)
        => Task.Run(() => ApplyCoreAsync(update, step, progress, token, reinstall), token);

    private async Task ApplyCoreAsync(PortableUpdate update, Action<ProgramStepEvent> step, Action<ProgramDownload> progress, CancellationToken token, bool reinstall)
    {
        if (update.Id != "vlc" || update.Parts is not null || update.ZipMember is not null) throw new IOException("VLC-Paketzuordnung ungültig. / Invalid VLC package identity.");
        var proof = new ProgramInstallation(update.InstalledVersion, "Portable", update.Path, false, Variant);
        var current = await ResolveAsync(proof, token).ConfigureAwait(false);
        if (current != update || !(ProgramVersions.IsNewer(update.InstalledVersion, update.Version)
            || reinstall && ProgramPortableUpdates.SameVersion(update.InstalledVersion, update.Version)))
            throw new IOException("VLC-Kopie oder Updateangebot wurde geändert. Bitte erneut prüfen. / VLC copy or update offer changed; check again.");
        var local = Identify(update.Path, _binary)!;
        EnsureNotRunning(local.Root);
        var previous = await ReleaseAsync(update.InstalledVersion, token).ConfigureAwait(false);
        if (previous is null || previous.Version != update.InstalledVersion) throw new IOException("Das bisherige VLC-Originalpaket konnte nicht bestätigt werden. / Previous official VLC package could not be verified.");
        var next = new PortablePackage(update.Version, update.Url, update.Sha256, update.Size ?? 0);
        string parent = Path.GetDirectoryName(local.Root)!;
        string stage = Path.Combine(parent, ".PowerLeitstand-VLC-" + Guid.NewGuid().ToString("N"));
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
            if (await ReleaseAsync(next.Version, token).ConfigureAwait(false) != next) throw new IOException("VLC-Downloadquelle wurde geändert. / VLC download source changed.");
            var repeated = Identify(update.Path, _binary);
            if (repeated != local || ProgramPortableUpdates.Hash(update.Path) != update.OriginalHash) throw new IOException("Die gewählte VLC-Kopie wurde zwischenzeitlich geändert. / Selected VLC copy changed.");
            EnsureNotRunning(local.Root); token.ThrowIfCancellationRequested();
            try { Commit(local, oldFiles, newFiles, stage, update, token); }
            catch (VlcRollbackException) { retainStage = true; throw; }
            step(new(ProgramStep.Install, ProgramStepState.Done));
        }
        finally
        {
            if (!retainStage) try { DeleteStage(stage, parent); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(PortableUpdate request, string path, Action<ProgramDownload> report, CancellationToken token)
    {
        if (!Regex.IsMatch(request.Version, @"^\d+\.\d+\.\d+$") || request.Size is not (> 0 and <= ArchiveLimit) || request.Url != BaseUrl + request.Version + "/win64/vlc-" + request.Version + "-win64.zip"
            || !Regex.IsMatch(request.Sha256, "^[a-fA-F0-9]{64}$")) throw new IOException("Ungültiges offizielles VLC-Archiv. / Invalid official VLC archive.");
        if (_download is not null) await _download(request, path, report, token).ConfigureAwait(false);
        else
        {
            using var response = await CatalogWeb.OpenAsync(new(request.Url), Hosts, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != request.Size) throw new IOException("VLC-Downloadgröße stimmt nicht überein. / VLC download size mismatch.");
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await ProgramPortableUpdates.CopyDownloadAsync(source, file, request.Size, report, token, ArchiveLimit).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (!ProgramPortableUpdates.PlainPath(path) || new FileInfo(path).Length != request.Size || ProgramPortableUpdates.Hash(path) != request.Sha256.ToUpperInvariant())
            throw new IOException("VLC-Archivgröße oder Prüfsumme wurde nicht bestätigt. / VLC archive size or checksum was not verified.");
    }

    internal Task<string> InstallNewAsync(PortablePackage package, string parent, Action<ProgramStepEvent> step, IProgress<double?> progress, CancellationToken token)
        => Task.Run(async () =>
    {
        string destination = Destination(package, parent);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("VLC-Ziel bereits vorhanden. / VLC destination already exists.");
        if (await LatestAsync(token).ConfigureAwait(false) != package) throw new IOException("VLC-Version wurde geändert. / VLC version changed.");
        string stage = Path.Combine(Path.GetFullPath(parent), ".PowerLeitstand-VLC-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            await DownloadAsync(new("vlc", "", "", package.Version, package.Url, package.Sha256, package.Size, null, ""), Path.Combine(stage, "next.zip"), p => progress.Report(p.Percent), token).ConfigureAwait(false);
            step(new(ProgramStep.Download, ProgramStepState.Done)); step(new(ProgramStep.Install, ProgramStepState.Active)); progress.Report(null);
            string unpacked = Path.Combine(stage, "package"); await ProgramPortable.ExtractAsync(Path.Combine(stage, "next.zip"), unpacked, token).ConfigureAwait(false);
            _ = PackageFiles(unpacked, package.Version, _binary, token);
            string payload = PackageRoot(unpacked, package.Version); ConfirmZip(Path.Combine(payload, "vlc.exe"), _binary);
            if (await ReleaseAsync(package.Version, token).ConfigureAwait(false) != package) throw new IOException("VLC-Quelle wurde geändert. / VLC source changed.");
            token.ThrowIfCancellationRequested(); CheckParents(destination); Directory.Move(payload, destination);
            return Path.Combine(destination, "vlc.exe");
        }
        finally { try { DeleteStage(stage, Path.GetFullPath(parent)); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }, token);
}
