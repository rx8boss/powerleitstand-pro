using System.IO;
using System.Runtime.InteropServices;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    private void CreateDesktopShortcut(ProgramEntry entry, string? executable)
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var outcome = SaveDesktopShortcut(entry.Name, executable, desktop);
            WriteLog?.Invoke(entry.Name + ": " + (outcome switch
            {
                true => L("Desktop-Verknüpfung erstellt.", "Desktop shortcut created."),
                false => L("Desktop-Verknüpfung bereits vorhanden.", "Desktop shortcut already exists."),
                null => L("Keine eindeutige Programmdatei für Desktop-Verknüpfung gefunden.", "No unambiguous executable found for the desktop shortcut.")
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or ArgumentException or InvalidOperationException)
        {
            WriteLog?.Invoke(entry.Name + L(": Desktop-Verknüpfung fehlgeschlagen: ", ": Desktop shortcut failed: ") + UiText.Error(e, _english));
        }
    }

    internal static bool? SaveDesktopShortcut(string name, string? executable, string desktop)
    {
        if (executable is null || !Path.IsPathFullyQualified(executable) || executable.StartsWith(@"\\", StringComparison.Ordinal)
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            return null;
        if (name.Length is < 1 or > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new ArgumentException("Invalid shortcut name.");
        if (!Path.IsPathFullyQualified(desktop) || !Directory.Exists(desktop)) throw new IOException("Desktop folder unavailable.");
        string destination = Path.Combine(desktop, name + ".lnk");
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (File.Exists(destination) || common.Length != 0 && File.Exists(Path.Combine(common, name + ".lnk"))) return false;
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
