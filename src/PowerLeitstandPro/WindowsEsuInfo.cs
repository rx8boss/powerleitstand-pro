using Microsoft.Win32;
using System.Security.Principal;

namespace PowerControlProWpf;

internal sealed record WindowsEsuInfo(int Build, string Edition, bool? Administrator)
{
    internal static readonly DateOnly EndDate = new(2027, 10, 12);

    internal bool EligibleEdition => Edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase)
        || Edition.StartsWith("Professional", StringComparison.OrdinalIgnoreCase);

    internal bool ShowOn(DateOnly today) => today <= EndDate && Build == 19045 && EligibleEdition;

    internal static WindowsEsuInfo Read()
    {
        var build = Environment.OSVersion.Version.Build;
        var edition = "";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (int.TryParse(key?.GetValue("CurrentBuildNumber")?.ToString(), out var found)) build = found;
            edition = key?.GetValue("EditionID")?.ToString() ?? "";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        bool? administrator = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException) { }
        return new(build, edition, administrator);
    }
}
