using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace PowerControlProWpf;

internal static class ProgramApplicationCloser
{
    internal sealed record Instance(int Id, long Started, string Path);
    [StructLayout(LayoutKind.Sequential)]
    private struct UniqueProcess { internal uint Id; internal System.Runtime.InteropServices.ComTypes.FILETIME Started; }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint files, IntPtr names, uint apps, [In] UniqueProcess[] processes, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll")] private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
    [DllImport("rstrtmgr.dll")] private static extern int RmCancelCurrentTask(uint session);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    private static string Key(string text) => Regex.Replace(text, @"[^\p{L}\p{N}]", "").ToUpperInvariant();
    internal static bool MatchesPath(ProgramEntry entry, ProgramInstallation? proof, string path)
    {
        if (proof?.Path is not { } location || !Path.IsPathFullyQualified(location) || !Path.IsPathFullyQualified(path)) return false;
        location = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar); path = Path.GetFullPath(path);
        if (location.Equals(path, StringComparison.OrdinalIgnoreCase)) return true;
        if (proof.Source is not ("Registry" or "WinGet")) return false;
        string? root = Directory.Exists(location) ? location : Path.GetDirectoryName(location);
        // Squirrel can keep an old app-* process alive next to the selected newer copy.
        if (root is not null && Regex.IsMatch(Path.GetFileName(root), @"^app-\d+(?:\.\d+)+$")) root = Path.GetDirectoryName(root);
        if (root is null || !Directory.Exists(root) || root.Equals(Path.GetPathRoot(root)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        string rootKey = Key(Path.GetFileName(root));
        if (rootKey != Key(entry.Name) && rootKey != Key(Path.GetFileNameWithoutExtension(path))) return false;
        foreach (var common in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows })
            if (root.Equals(Environment.GetFolderPath(common).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
        if (root.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), StringComparison.OrdinalIgnoreCase)
            || root.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), StringComparison.OrdinalIgnoreCase)) return false;
        string relative = Path.GetRelativePath(root, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative) && relative.Split(Path.DirectorySeparatorChar).Length <= 3;
    }
    private static bool Identity(ProgramEntry entry, string path)
    {
        try { ProgramFileAssociations.Validate(entry, path); return true; }
        catch (Exception error) when (ProgramPathResolver.ReadFailure(error)) { return false; }
    }
    internal static IReadOnlyList<Instance> Find(ProgramEntry entry, ProgramInstallation? proof, CancellationToken token)
    {
        var found = new List<Instance>(); var identities = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using var current = Process.GetCurrentProcess(); using var owner = WindowsIdentity.GetCurrent(); string? sid = owner.User?.Value;
        bool anchored = proof?.Path is { } location && (File.Exists(location) || Directory.Exists(location)
            && Key(Path.GetFileName(location.TrimEnd('\\'))) == Key(entry.Name) && MatchesPath(entry, proof, Path.Combine(location, "_scope_check_.exe")));
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId) continue;
                string? path = process.MainModule?.FileName;
                if (path is null || path.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                bool scoped = MatchesPath(entry, proof, path);
                if (!scoped && anchored) continue;
                if (!identities.TryGetValue(path, out bool matches)) identities[path] = matches = Identity(entry, path);
                if (!matches)
                {
                    if (scoped) throw new IOException("Das laufende Programm konnte nicht sicher zugeordnet werden. Bitte das Programm selbst schließen.");
                    continue;
                }
                if (!OpenProcessToken(process.Handle, 8, out var access)) throw new IOException("Das laufende Programm konnte nicht sicher zugeordnet werden. Bitte das Programm selbst schließen.");
                using (access)
                {
                    using var identity = new WindowsIdentity(access.DangerousGetHandle());
                    if (sid is null || identity.User?.Value != sid)
                    {
                        if (scoped) throw new IOException("Das laufende Programm konnte nicht sicher zugeordnet werden. Bitte das Programm selbst schließen.");
                        continue;
                    }
                }
                if (!scoped) throw new IOException("Der Speicherort des laufenden Programms ist nicht eindeutig bestätigt. Bitte die Programmdatei zuordnen oder das Programm selbst schließen.");
                found.Add(new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(), path));
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { }
        }
        return found;
    }
    internal static Task<bool> CloseAsync(ProgramEntry entry, ProgramInstallation? proof, CancellationToken token)
        => Task.Run(() => CloseCore(entry, proof, token), token);
    private static bool CloseCore(ProgramEntry entry, ProgramInstallation? proof, CancellationToken token)
    {
        if (proof?.Path is { } own && own.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("PowerLeitstand kann sich hier nicht selbst schließen. Bitte die eigene Updatefunktion verwenden.");
        var instances = Find(entry, proof, token); if (instances.Count == 0) return false;
        int code = RmStartSession(out uint session, 0, new StringBuilder(33));
        if (code != 0) throw new IOException("Windows konnte das Programm nicht geordnet schließen.\nCode: " + code);
        try
        {
            var processes = instances.Select(p => new UniqueProcess { Id = (uint)p.Id, Started = new() { dwLowDateTime = (int)p.Started, dwHighDateTime = (int)(p.Started >> 32) } }).ToArray();
            code = RmRegisterResources(session, 0, IntPtr.Zero, (uint)processes.Length, processes, 0, IntPtr.Zero);
            if (code != 0) throw new IOException("Windows konnte das Programm nicht geordnet schließen.\nCode: " + code);
            using (token.Register(() => RmCancelCurrentTask(session)))
            {
                token.ThrowIfCancellationRequested();
                code = RmShutdown(session, 0, IntPtr.Zero); // Never force a save dialog or an unresponsive app to exit.
                token.ThrowIfCancellationRequested();
            }
            var wait = Stopwatch.StartNew();
            while (code == 0 && wait.Elapsed < TimeSpan.FromSeconds(5) && instances.Any(Alive))
            { token.ThrowIfCancellationRequested(); Thread.Sleep(100); }
            if (code != 0 || Find(entry, proof, token).Count > 0)
                throw new IOException("Das Programm ist noch geöffnet oder konnte nicht geordnet beendet werden. Bitte Arbeit speichern und das Programm schließen; danach erneut versuchen.\nCode: " + code);
            return true;
        }
        finally { RmEndSession(session); }
    }
    internal static bool Alive(Instance instance)
    {
        try { using var process = Process.GetProcessById(instance.Id); return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == instance.Started; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
