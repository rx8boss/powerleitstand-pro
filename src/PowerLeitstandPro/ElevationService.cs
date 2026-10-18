using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace PowerControlProWpf;

internal static class ElevationService
{
    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool TryRestartAsAdministrator(bool openUsersPage)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die veröffentlichte App-EXE wurde nicht gefunden.");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--elevation-attempted");
        start.ArgumentList.Add("--instance-handoff");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (openUsersPage) start.ArgumentList.Add("--page-users");
        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // UAC was cancelled; keep the standard-rights instance running.
            return false;
        }
    }
}
