using System.IO;
using System.Runtime.InteropServices;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal string CreateDesktopShortcut(ProgramEntry entry, string? executable, bool portable = false)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outcome = SaveDesktopShortcut(entry.Name, executable, desktop, portable);
            return Report(outcome switch
            {
                true => L("Desktop-Verknüpfung erstellt.", "Desktop shortcut created."),
                false => L("Desktop-Verknüpfung bereits vorhanden.", "Desktop shortcut already exists."),
                null => L("Keine eindeutige Programmdatei für Desktop-Verknüpfung gefunden.", "No unambiguous executable found for the desktop shortcut.")
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException)
        {
            return Report(L("Desktop-Verknüpfung fehlgeschlagen: ", "Desktop shortcut failed: ") + UiText.Error(e, _english));
        }
        string Report(string message) { WriteLog?.Invoke(entry.Name + ": " + message); return message; }
    }

    internal static string? DesktopExecutable(ProgramEntry entry, ProgramInstallation? proof)
    {
        if (proof?.LocationEvidence?.Alternatives is { Count: > 0 } || proof?.ManagedCopies is { Count: > 1 }) return null;
        var path = proof?.LocationEvidence?.Executable ?? proof?.Path;
        if (path is not null && File.Exists(path)) return path;
        if (entry.Id != "adwcleaner" || proof?.Source is not ("Registry" or "WinGet")) return null;
        return ProgramLocalInventory.AdwCleanerWinGetExecutable(proof.Version, path);
    }

    internal static bool? SaveDesktopShortcut(string name, string? executable, string desktop, bool portable = false)
    {
        if (executable is null || !Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\", StringComparison.Ordinal)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            return null;
        if (name.Length is < 1 or > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new ArgumentException("Invalid shortcut name.");
        if (!Path.IsPathFullyQualified(desktop) || !Directory.Exists(desktop)) throw new IOException("Desktop folder unavailable.");
        string destination = Path.Combine(desktop, name + ".lnk");
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string? commonLink = common.Length == 0 ? null : Path.Combine(common, name + ".lnk");
        if (File.Exists(destination) || File.Exists(commonLink))
        {
            if (!portable || PointsToProgram(destination) || PointsToProgram(commonLink)) return false;
            destination = Path.Combine(desktop, name + " (Portable).lnk");
            if (File.Exists(destination))
            {
                if (PointsToProgram(destination)) return false;
                throw new IOException("Der portable Desktop-Link ist bereits anderweitig belegt. / The portable desktop shortcut name is already in use.");
            }
        }
        bool PointsToProgram(string? path) => File.Exists(path)
            && string.Equals(ProgramPathResolver.ReadShortcut(path!), executable, StringComparison.OrdinalIgnoreCase);
        string stage = Path.Combine(desktop, "." + Guid.NewGuid().ToString("N") + ".lnk");
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut service unavailable.");
            shell = Activator.CreateInstance(type);
            link = ((dynamic)shell!).CreateShortcut(stage);
            ((dynamic)link).TargetPath = executable;
            ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(executable);
            ((dynamic)link).Save();
            if (!PointsToProgram(stage)) throw new IOException("Das Ziel der Desktop-Verknüpfung konnte nicht bestätigt werden. / The desktop shortcut target could not be verified.");
            File.Move(stage, destination);
            return true;
        }
        catch (IOException) when (File.Exists(destination)) { return false; }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            File.Delete(stage);
        }
    }
}
