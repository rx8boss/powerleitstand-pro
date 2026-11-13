using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerControlProWpf;

internal sealed partial class ObsPortableUpdates
{
    private const string MainFile = "bin/64bit/obs64.exe";
    private sealed record PackageFile(string Relative, string Path, string Sha256, long Size);
    private sealed class ObsRollbackException(string message, Exception error) : IOException(message, error) { }

    private static Dictionary<string, PackageFile> PackageFiles(string directory, string version, Func<string, ObsPortableBinary?> binary, CancellationToken token)
    {
        var result = new Dictionary<string, PackageFile>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (relative.Equals("portable_mode", StringComparison.OrdinalIgnoreCase) || relative.Equals("portable_mode.txt", StringComparison.OrdinalIgnoreCase))
            { if (new FileInfo(file).Length != 0) throw new IOException("Ungültige OBS-Portable-Markierung im Paket. / Invalid OBS package marker."); continue; }
            if (relative.Split('/')[0].Equals("config", StringComparison.OrdinalIgnoreCase) || relative.Split('/')[0].Equals("plugins", StringComparison.OrdinalIgnoreCase)
                || !ProgramPortableUpdates.PlainPath(file) || result.Count >= 12000)
                throw new IOException("OBS-Paket überschneidet sich mit Benutzerablagen. / OBS package overlaps user data.");
            long size = new FileInfo(file).Length; total += size;
            if (size < 0 || total > 4L * 1024 * 1024 * 1024 || !result.TryAdd(relative, new(relative, file, ProgramPortableUpdates.Hash(file), size)))
                throw new IOException("Ungültiger Umfang des OBS-Pakets. / Invalid OBS package contents.");
        }
        var main = result.GetValueOrDefault(MainFile);
        var identity = main is null ? null : binary(main.Path);
        if (identity is null || identity.Version != version || identity.Machine != 0x8664)
            throw new IOException("OBS-Produktversion oder Architektur des Archivs stimmt nicht. / OBS archive product version or architecture mismatch.");
        return result;
    }

    private static string Target(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("OBS-Pfad verlässt den bestätigten Ordner. / OBS path escapes confirmed directory.");
        CheckParents(path); return path;
    }

    private static void CheckParents(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Verknüpfte OBS-Ablagen werden nicht ersetzt. / Linked OBS paths cannot be replaced.");
    }

    private static void EnsureNotRunning(string root)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process) try
            {
                string? file = process.MainModule?.FileName;
                if (file is not null && file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Bitte diese OBS-Kopie und ihre Hilfsprozesse zuerst schließen. / Close this OBS copy and its helper processes first.");
            }
            catch (Win32Exception)
            {
                try { if (process.ProcessName.StartsWith("obs", StringComparison.OrdinalIgnoreCase)) throw new IOException("Ein laufender OBS-Prozess konnte nicht geprüft werden. / A running OBS process could not be verified."); }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }
        }
    }

    private void Commit(ObsPortableIdentity local, Dictionary<string, PackageFile> oldFiles, Dictionary<string, PackageFile> newFiles,
        string stage, PortableUpdate update, CancellationToken token)
    {
        var locks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var movedOld = new List<string>(); var movedNew = new List<string>();
        string? backupDirectory = null; bool mutationStarted = false;
        using var marker = new FileStream(Marker(local.Root) ?? throw new IOException("OBS-Portable-Markierung fehlt. / OBS portable marker missing."), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Deny other reads/writes while holding delete sharing for same-volume moves.
            // Removing the old main file first and publishing the new main file last
            // prevents launching a mixed program halfway through the transaction.
            foreach (var old in oldFiles.Values.OrderBy(p => p.Relative.Equals(MainFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                token.ThrowIfCancellationRequested(); string path = Target(local.Root, old.Relative);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) throw new IOException("Bisherige OBS-Paketdateien fehlen oder sind schreibgeschützt. / Previous OBS package files are missing or read-only.");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete); locks.Add(old.Relative, stream);
                if (stream.Length != old.Size || Convert.ToHexString(SHA256.HashData(stream)) != old.Sha256)
                    throw new IOException("Eine ursprüngliche OBS-Paketdatei wurde verändert; Benutzerdateien werden nicht überschrieben. / An original OBS package file changed; user files will not be overwritten.");
            }
            CheckCollisions();
            backupDirectory = Backup(local, oldFiles, locks, update, stage);
            _checkpoint?.Invoke("before-commit"); token.ThrowIfCancellationRequested();
            if (!SafeRoot(local.Root) || Marker(local.Root) is null) throw new IOException("OBS-Zielordner wurde geändert. / OBS target directory changed.");
            WriteState(backupDirectory, "prepared", local, update, stage, oldFiles, newFiles);
            CheckCollisions();
            foreach (var old in oldFiles.Values)
            {
                string path = Target(local.Root, old.Relative);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("OBS-Dateizuordnung wurde geändert. / OBS file identity changed.");
            }
            // From here cancellation waits for commit or rollback to finish.
            mutationStarted = true;
            foreach (var old in oldFiles.Values.OrderBy(p => p.Relative.Equals(MainFile, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                string previous = Target(Path.Combine(stage, "displaced"), old.Relative); Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                File.Move(Target(local.Root, old.Relative), previous); movedOld.Add(old.Relative); _checkpoint?.Invoke("old:" + old.Relative);
            }
            foreach (var next in newFiles.Values.OrderBy(p => p.Relative.Equals(MainFile, StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                string target = Target(local.Root, next.Relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!ProgramPortableUpdates.PlainPath(next.Path) || ProgramPortableUpdates.Hash(next.Path) != next.Sha256) throw new IOException("Die geprüfte OBS-Nutzlast wurde geändert. / Verified OBS payload changed.");
                File.Move(next.Path, target); movedNew.Add(next.Relative); _checkpoint?.Invoke("new:" + next.Relative);
            }
            _checkpoint?.Invoke("after-commit");
            foreach (var next in newFiles.Values)
            {
                string path = Target(local.Root, next.Relative);
                if (!ProgramPortableUpdates.PlainPath(path) || ProgramPortableUpdates.Hash(path) != next.Sha256) throw new IOException("OBS-Abschlussprüfung fehlgeschlagen. / OBS completion verification failed.");
            }
            foreach (var old in oldFiles.Keys.Except(newFiles.Keys, StringComparer.OrdinalIgnoreCase))
                if (File.Exists(Target(local.Root, old))) throw new IOException("Alte OBS-Paketdatei verblieben. / Obsolete OBS package file remains.");
            var installed = Identify(local.Executable, _binary);
            if (installed?.Version != update.Version || installed.Machine != local.Machine || installed.Root != local.Root)
                throw new IOException("OBS-Version nach Austausch nicht bestätigt. / OBS version not confirmed after replacement.");
            WriteState(backupDirectory, "complete", local, update, stage, oldFiles, newFiles);
        }
        catch (Exception failure)
        {
            if (!mutationStarted) throw;
            var rollbackErrors = new List<Exception>();
            foreach (var relative in movedNew.AsEnumerable().Reverse())
            {
                try
                {
                    string path = Target(local.Root, relative);
                    if (!File.Exists(path) || !ProgramPortableUpdates.PlainPath(path) || ProgramPortableUpdates.Hash(path) != newFiles[relative].Sha256)
                        throw new IOException("Eine neue OBS-Datei wurde während des Rückrollens geändert. / New OBS file changed during rollback.");
                    string failed = Target(Path.Combine(stage, "failed"), relative); Directory.CreateDirectory(Path.GetDirectoryName(failed)!); File.Move(path, failed);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rollbackErrors.Add(e); }
            }
            foreach (var relative in movedOld.AsEnumerable().Reverse())
            {
                try { string path = Target(local.Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Move(Target(Path.Combine(stage, "displaced"), relative), path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rollbackErrors.Add(e); }
            }
            foreach (var stream in locks.Values) stream.Dispose();
            if (rollbackErrors.Count == 0)
            {
                foreach (var old in oldFiles.Values)
                    if (!ProgramPortableUpdates.PlainPath(Target(local.Root, old.Relative)) || ProgramPortableUpdates.Hash(Target(local.Root, old.Relative)) != old.Sha256)
                        rollbackErrors.Add(new IOException("OBS-Rücksicherung konnte nicht bestätigt werden. / OBS rollback could not be verified."));
            }
            if (rollbackErrors.Count > 0)
                throw new ObsRollbackException("OBS-Rücksicherung benötigt eine manuelle Prüfung. Sicherung / backup: " + backupDirectory + "; staging: " + stage,
                    new AggregateException(rollbackErrors.Prepend(failure)));
            if (backupDirectory is not null) WriteState(backupDirectory, "rolled-back", local, update, stage, oldFiles, newFiles);
            throw;
        }
        finally { foreach (var stream in locks.Values) stream.Dispose(); }

        void CheckCollisions()
        {
            foreach (var next in newFiles.Values)
            {
                string path = Target(local.Root, next.Relative);
                if (Directory.Exists(path) || !oldFiles.ContainsKey(next.Relative) && File.Exists(path))
                    throw new IOException("Das neue OBS-Paket kollidiert mit einer nicht zugeordneten Datei. / New OBS package conflicts with an unowned file.");
            }
        }
    }

    private string Backup(ObsPortableIdentity local, Dictionary<string, PackageFile> files, Dictionary<string, FileStream> locks, PortableUpdate update, string stage)
    {
        if (!Path.IsPathFullyQualified(_backupRoot)) throw new IOException("Ungültiger OBS-Sicherungspfad. / Invalid OBS backup path.");
        CheckParents(_backupRoot); Directory.CreateDirectory(_backupRoot); CheckParents(_backupRoot);
        string directory = Path.Combine(_backupRoot, "obs-studio-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string zipPath = Path.Combine(directory, "previous-package.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var file in files.Values)
            {
                var input = locks[file.Relative]; input.Position = 0;
                using var output = zip.CreateEntry(file.Relative, CompressionLevel.Optimal).Open(); input.CopyTo(output);
            }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            if (zip.Entries.Count != files.Count) throw new IOException("OBS-Sicherungsumfang stimmt nicht. / OBS backup contents mismatch.");
            foreach (var entry in zip.Entries)
            {
                using var input = entry.Open();
                if (!files.TryGetValue(entry.FullName, out var file) || entry.Length != file.Size || Convert.ToHexString(SHA256.HashData(input)) != file.Sha256)
                    throw new IOException("OBS-Sicherung wurde nicht bestätigt. / OBS backup could not be verified.");
            }
        }
        WriteState(directory, "prepared", local, update, stage, files, []);
        return directory;
    }

    private static void WriteState(string directory, string state, ObsPortableIdentity local, PortableUpdate update, string stage,
        Dictionary<string, PackageFile> previous, Dictionary<string, PackageFile> next)
        => File.WriteAllText(Path.Combine(directory, "backup.json"), JsonSerializer.Serialize(new
        {
            Application = "PowerLeitstand PRO", Schema = 1, Program = "obs-studio", State = state, CreatedUtc = DateTime.UtcNow,
            Root = local.Root, update.InstalledVersion, update.Version, update.Url, update.Sha256, Stage = stage,
            Previous = previous.Values.Select(f => new { f.Relative, f.Sha256, f.Size }), Next = next.Values.Select(f => new { f.Relative, f.Sha256, f.Size })
        }));

    private static void DeleteStage(string stage, string parent)
    {
        var full = Path.GetFullPath(stage); var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(".PowerLeitstand-OBS-", StringComparison.Ordinal) || !Guid.TryParseExact(name[20..], "N", out _))
            throw new IOException("Temporärer OBS-Ordner nicht bestätigt. / Temporary OBS folder not verified.");
        CheckParents(full);
        Delete(full);
        static void Delete(string path)
        {
            if (!Directory.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked stage directory");
            foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) child.Delete();
                else if (child is DirectoryInfo) Delete(child.FullName);
                else child.Delete();
            }
            Directory.Delete(path);
        }
    }
}
