using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerControlProWpf;

internal enum UpdatePhase { Downloading, Verifying, Restarting }
internal sealed record UpdateProgress(UpdatePhase Phase, long ReceivedBytes, long? TotalBytes);

internal static class UpdateService
{
    private const long MaxArchiveSize = 512L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static async Task PrepareAndLaunchAsync(AvailableUpdate update, Func<bool> isBusy,
        CancellationToken cancellationToken = default, IProgress<UpdateProgress>? progress = null)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("EXE-Pfad nicht ermittelbar.");
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Updates benötigen die veröffentlichte Windows-EXE.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Installationsordner nicht ermittelbar.");
        var staged = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.new");
        var helper = Path.Combine(Path.GetTempPath(), $"PowerLeitstand-update-helper-{Guid.NewGuid():N}.exe");
        var archive = staged + ".zip";
        bool archiveReady = false;
        string? backup = null;
        try
        {
            if (update.SourceBackup is { } local)
            {
                var store = new AppBackupStore();
                if (!await store.VerifyAsync(local, cancellationToken)) throw new InvalidDataException("Sicherung ist beschädigt.");
                progress?.Report(new(UpdatePhase.Verifying, 0, null));
                await AppBackupStore.CopyAsync(local.BinaryPath, staged, cancellationToken);
                if (!string.Equals(await AppBackupStore.HashAsync(staged, cancellationToken), local.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Wiederherstellungsdatei konnte nicht geprüft werden.");
            }
            else if (update.IsArchive)
            {
                await DownloadAndVerifyAsync(Client, update, archive, progress, cancellationToken);
                archiveReady = true;
                await ExtractUpdateAsync(archive, staged, update.Sha256, cancellationToken);
            }
            else await DownloadAndVerifyAsync(Client, update, staged, progress, cancellationToken);
            if (AppVersion.ReadBuild(staged) != update.Build)
                throw new InvalidDataException("Dateiversion passt nicht zur ausgewählten Version.");
            var stagedHash = update.IsArchive ? await AppBackupStore.HashAsync(staged, cancellationToken) : update.SourceBackup?.Sha256 ?? update.Sha256;
            if (isBusy()) throw new InvalidOperationException("Ein Timer oder eine Reparatur wurde während des Downloads gestartet.");
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await new AppBackupStore().CreateAsync(target, AppVersion.Build.ToString(), AppReleaseNotes.Current, cancellationToken);
            backup = snapshot.BinaryPath;
            await Task.Run(() => File.Copy(target, helper), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(UpdatePhase.Restarting, 0, null));
            // Give the UI its queued status notification before starting the helper.
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (isBusy()) throw new InvalidOperationException("Ein Timer oder eine Reparatur verhindert den Neustart.");
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--update-helper");
            start.ArgumentList.Add(target);
            start.ArgumentList.Add(staged);
            start.ArgumentList.Add(backup);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(stagedHash);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Update-Helfer konnte nicht gestartet werden.");
        }
        catch
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            try { if (File.Exists(helper)) File.Delete(helper); } catch { }
            throw;
        }
        finally
        {
            if (archiveReady) try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal static async Task ExtractUpdateAsync(string archivePath, string destination, string sha256, CancellationToken token)
    {
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (!digest.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA-256-Prüfsumme des Update-ZIP stimmt nicht überein.");
        file.Position = 0;
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(e => e.FullName.Equals("PowerLeitstand-PRO.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length is <= 0 or > MaxArchiveSize)
            throw new InvalidDataException("Das ZIP enthält keine eindeutige PowerLeitstand-PRO.exe.");
        await using var source = entries[0].Open();
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            total += read;
            if (total > entries[0].Length) throw new InvalidDataException("Ungültige Dateigröße im Update-ZIP.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (total != entries[0].Length) throw new InvalidDataException("Die EXE im Update-ZIP ist unvollständig.");
    }

    internal static async Task DownloadAndVerifyAsync(HttpClient client, AvailableUpdate update, string staged,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        using var connection = new OnlineRequest(cancellationToken); cancellationToken = connection.Token;
        var created = false;
        try
        {
            using var response = await client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength is > 0 ? response.Content.Headers.ContentLength : null;
            if (update.IsArchive && total > MaxArchiveSize) throw new InvalidDataException("Das Update-ZIP ist zu groß.");
            long received = 0;
            progress?.Report(new(UpdatePhase.Downloading, 0, total));
            var watch = Stopwatch.StartNew();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                created = true;
                var buffer = new byte[65536];
                int count;
                while ((count = await OnlineConnection.ReadAsync(input, buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (update.IsArchive && received > MaxArchiveSize) throw new InvalidDataException("Das Update-ZIP ist zu groß.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    if (watch.ElapsedMilliseconds >= 80)
                    {
                        progress?.Report(new(UpdatePhase.Downloading, received, total));
                        watch.Restart();
                    }
                }
            }
            if (total is not null && received != total.Value)
                throw new InvalidDataException("Der Download ist unvollständig. Die bisherige EXE bleibt erhalten.");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(UpdatePhase.Downloading, received, total));
            progress?.Report(new(UpdatePhase.Verifying, received, total));
            await using var file = File.OpenRead(staged);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256-Prüfsumme stimmt nicht überein. Das Update wurde verworfen.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            try { if (created && File.Exists(staged)) File.Delete(staged); } catch { }
            throw;
        }
    }

    internal static async Task RunHelperAsync(string[] args)
    {
        if (args.Length != 6 || !int.TryParse(args[4], out var parentId) || args[5].Length != 64 || !args[5].All(Uri.IsHexDigit)) return;
        var expectedHash = args[5];
        var english = UiText.SavedEnglish();
        var target = Path.GetFullPath(args[1]);
        var staged = Path.GetFullPath(args[2]);
        var backup = Path.GetFullPath(args[3]);
        var directory = Path.GetDirectoryName(target);
        var persistent = !string.Equals(Path.GetDirectoryName(backup), directory, StringComparison.OrdinalIgnoreCase);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(staged) ||
            !string.Equals(Path.GetDirectoryName(staged), directory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(staged).StartsWith("." + Path.GetFileName(target) + ".", StringComparison.Ordinal) ||
            !staged.EndsWith(".new", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
        if (persistent)
        {
            var store = new AppBackupStore();
            var entry = store.Read(Path.GetDirectoryName(backup)!);
            if (!string.Equals(entry.BinaryPath, backup, StringComparison.OrdinalIgnoreCase) || !await store.VerifyAsync(entry) ||
                !string.Equals(await AppBackupStore.HashAsync(target), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Sicherung passt nicht zur bisherigen EXE.");
        }
        else if (!Path.GetFileName(backup).StartsWith("." + Path.GetFileName(target) + ".", StringComparison.Ordinal) ||
            !backup.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || File.Exists(backup)) return;
        await using var verified = await OpenVerifiedExecutableAsync(staged, expectedHash);
        }
        catch (Exception exception)
        {
            ThemedMessageBox.ShowInLanguage(english, (english ? "Update stopped before replacement: " : "Update wurde vor dem Austausch gestoppt: ") + UiText.Error(exception, english), "PowerLeitstand PRO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return;
        }
        try
        {
            using var parent = Process.GetProcessById(parentId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
        catch (OperationCanceledException) { return; }

        Process? candidate = null;
        var pipeName = "PowerLeitstand-ready-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var result = await UpdateRecovery.InstallAsync(target, staged, backup, async () =>
            {
                var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = directory! };
                // Do not re-elevate during the monitored start: the helper must own the exact candidate PID.
                start.ArgumentList.Add("--elevation-attempted");
                start.ArgumentList.Add("--update-ready");
                start.ArgumentList.Add(pipeName);
                Version candidateVersion;
                // Pin the verified target against writes and renames until Windows has opened it.
                await using (var verified = await OpenVerifiedExecutableAsync(target, expectedHash))
                {
                    candidateVersion = Version.Parse(AppBackupStore.FileVersion(target));
                    candidate = Process.Start(start) ?? throw new InvalidOperationException("Neue EXE konnte nicht gestartet werden.");
                }
                return candidateVersion >= new Version(1, 1, 3)
                    ? await UpdateStartupConfirmation.WaitAsync(pipe, candidate, TimeSpan.FromSeconds(90))
                    : await WaitForLegacyWindowAsync(candidate);
            }, async () =>
            {
                if (candidate is not null && !candidate.HasExited)
                {
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync();
                }
            }, () =>
            {
                var restored = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = directory! };
                // The helper already has the original rights. Avoid another UAC restart during recovery.
                restored.ArgumentList.Add("--elevation-attempted");
                Process.Start(restored);
            }, retainBackup: persistent);

            var autoStart = AutoStartService.Current.Check(target);
            if (autoStart.Error is not null) Trace.TraceWarning("Autostart nach Update: " + autoStart.Error);
            if (result.Restored)
                ThemedMessageBox.ShowInLanguage(english, (english ? "The new version could not be started. The previous EXE was restored and restarted.\n\n" : "Die neue Version konnte nicht gestartet werden. Die vorherige EXE wurde wiederhergestellt und gestartet.\n\n") + UiText.Translate(result.Warning ?? "", english),
                    "PowerLeitstand PRO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            else if (result.Warning is not null)
                ThemedMessageBox.ShowInLanguage(english, UiText.Translate(result.Warning, english), "PowerLeitstand PRO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            // Never delete a backup after a failed restoration.
            ThemedMessageBox.ShowInLanguage(english, (english ? "Update failed. Any existing backup is preserved:\n" : "Update fehlgeschlagen. Eine vorhandene Sicherung bleibt erhalten:\n") + backup + "\n\n" + UiText.Error(exception, english),
                "PowerLeitstand PRO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            candidate?.Dispose();
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    internal static async Task<FileStream> OpenVerifiedExecutableAsync(string path, string expectedHash)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        try
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256-Prüfsumme stimmt nicht überein. Das Update wurde verworfen.");
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> WaitForLegacyWindowAsync(Process process)
    {
        // v1.1.1/v1.1.2 have updates but predate the explicit startup acknowledgement.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return false;
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero && process.Responding)
            {
                await Task.Delay(2000);
                process.Refresh();
                return !process.HasExited && process.MainWindowHandle != IntPtr.Zero && process.Responding;
            }
            await Task.Delay(300);
        }
        return false;
    }
}
