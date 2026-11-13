using System.IO;
using System.Runtime.InteropServices;

namespace PowerControlProWpf;

internal static class NavigationDiagnostics
{
    // Local bounded evidence only. Failure to write diagnostics must never affect navigation.
    internal static void Write(string directory, string stage, Exception exception)
    {
        try
        {
            TryWrite(directory, "windows-updates-error.txt", EnvironmentDetails(stage) + "\n" + exception);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
    }

    private static string EnvironmentDetails(string phase) =>
        $"{DateTimeOffset.Now:O}\n{phase}\nApp: {AppVersion.Installed}\nRuntime: {RuntimeInformation.FrameworkDescription}\nOS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\n";

    private static void TryWrite(string directory, string name, string details)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name), details.Length <= 65536 ? details : details[..65536]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
    }
}
