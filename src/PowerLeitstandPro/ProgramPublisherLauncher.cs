using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal static class ProgramPublisherLauncher
{
    internal static Action<ProcessStartInfo> Launch = start => Process.Start(start);
    internal static bool RequiresPublisherUpdate(string id, ProgramInstallation? proof)
        => id == "extra-electronicarts-eadesktop" && (proof?.Source is "Registry" or "WinGet");
    internal static bool CanOpenEa(ProgramEntry entry, ProgramInstallation? proof)
    {
        try { return CreateEaStart(entry, proof) is not null; }
        catch (Exception e) when (ProgramPathResolver.ReadFailure(e)) { return false; }
    }
    internal static ProcessStartInfo? CreateEaStart(ProgramEntry entry, ProgramInstallation? proof)
    {
        if (entry.Id != "extra-electronicarts-eadesktop" || proof is not { Source: "Registry", Path: { } path }
            || !File.Exists(path) || !ProgramPortableUpdates.PlainPath(path)
            || !Path.GetFileName(path).Equals("EADesktop.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        var file = new ProgramPathFile(info.ProductName ?? "", info.CompanyName ?? "", ProgramPathResolver.NumericVersion(info.ProductVersion), info.FileDescription ?? "");
        var registered = new ProgramPathRegistration(entry.Name, entry.Publisher, proof.Version, null, null, null, "", false, RegistryHive.LocalMachine, RegistryView.Registry64);
        if (!ProgramPathResolver.IdentityMatches(registered, entry, path, file, true) || !ProgramPathResolver.VersionFits(proof.Version, file.Version)) return null;
        return new(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) };
    }
}
