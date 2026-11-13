using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Management.Deployment;

namespace PowerControlProWpf;

internal sealed record ProgramStoreSnapshot(IReadOnlyDictionary<string, ProgramInstallation> Entries, bool Complete,
    string? Error = null, string? Provider = null, bool PreviousConfirmed = false);

internal static class ProgramStoreInventory
{
    internal static async Task<ProgramStoreSnapshot> ReadAsync(CancellationToken token,
        Func<CancellationToken, Task<Dictionary<string, ProgramInstallation>>>? primary = null,
        Func<CancellationToken, Task<Dictionary<string, ProgramInstallation>>>? fallback = null)
    {
        string firstError;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            var entries = await (primary ?? ReadNativeAsync)(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return new(entries, true, Provider: "Windows PackageManager");
        }
        catch (Exception e) when (Failure(e)) { token.ThrowIfCancellationRequested(); firstError = e.Message; }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
            var entries = await (fallback ?? ReadPowerShellAsync)(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return new(entries, true, Provider: "Windows PowerShell");
        }
        catch (Exception e) when (Failure(e))
        {
            token.ThrowIfCancellationRequested();
            return new(new Dictionary<string, ProgramInstallation>(), false, "Store: " + Short(firstError) + "; " + Short(e.Message));
        }
    }
    private static bool Failure(Exception e) => e is IOException or JsonException or OperationCanceledException
        or COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or TypeLoadException or TypeInitializationException
        or System.Security.SecurityException or System.ComponentModel.Win32Exception;
    private static string Short(string value) => value.Length <= 300 ? value : value[..300];
    internal static Task<Dictionary<string, ProgramInstallation>> ReadNativeAsync(CancellationToken token) => Task.Run(() =>
    {
        var packages = new List<object>(); int inspected = 0;
        var manager = new PackageManager();
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            token.ThrowIfCancellationRequested();
            if (++inspected > 4096) throw new IOException("Store package enumeration exceeds limit.");
            if (package.IsFramework || package.IsResourcePackage) continue;
            var id = package.Id;
            if (!ProgramVersions.StoreFamilies.Values.Contains(id.FamilyName, StringComparer.OrdinalIgnoreCase)) continue;
            var version = id.Version; string? directory = null;
            try { directory = package.InstalledLocation?.Path; }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException) { }
            packages.Add(new { PackageFamilyName = id.FamilyName, InstallLocation = directory,
                Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}" });
        }
        token.ThrowIfCancellationRequested();
        return ProgramVersions.ParseStoreInstallations(JsonSerializer.Serialize(packages));
    }, token);
    internal static async Task<Dictionary<string, ProgramInstallation>> ReadPowerShellAsync(CancellationToken token)
    {
        var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        string families = "'" + string.Join("','", ProgramVersions.StoreFamilies.Values) + "'";
        string script = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $families=@(" + families + "); "
            + "@(Get-AppxPackage -PackageTypeFilter Main -ErrorAction Stop | Where-Object { $_.PackageFamilyName -in $families } | "
            + "Select-Object PackageFamilyName,InstallLocation,@{Name='Version';Expression={$_.Version.ToString()}}) | ConvertTo-Json -Compress";
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) command.ArgumentList.Add(arg);
        var result = await WinGetService.QueryAsync(command, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("Windows-App-Paketprüfung fehlgeschlagen. / Windows app package check failed.");
        return ProgramVersions.ParseStoreInstallations(string.IsNullOrWhiteSpace(result.Output) ? "[]" : result.Output.Trim());
    }
}
