using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

namespace PowerControlProWpf;

internal sealed record ProgramStoreApplication(string Family, string Name, string Publisher, string Version, string? Location, bool Protected);
internal sealed record ProgramStoreApplicationsSnapshot(IReadOnlyList<ProgramStoreApplication> Applications, bool Complete,
    string? Error = null, string? Provider = null);

// Only the explicit installed-programs view requests the complete Main-app list.
// Catalog/startup scans keep their small, identity-verified family set.
internal static class ProgramStoreApplications
{
    internal static async Task<ProgramStoreApplicationsSnapshot> ReadAsync(CancellationToken token,
        Func<CancellationToken, Task<IReadOnlyList<ProgramStoreApplication>>>? primary = null,
        Func<CancellationToken, Task<IReadOnlyList<ProgramStoreApplication>>>? fallback = null)
    {
        string first;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(5));
            var items = await (primary ?? ReadNativeAsync)(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return new(items, true, Provider: "Windows PackageManager");
        }
        catch (Exception e) when (Failure(e)) { token.ThrowIfCancellationRequested(); first = Short(e.Message); }
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
            var items = await (fallback ?? ReadPowerShellAsync)(limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); return new(items, true, Provider: "Windows PowerShell");
        }
        catch (Exception e) when (Failure(e))
        { token.ThrowIfCancellationRequested(); return new([], false, first + "; " + Short(e.Message)); }
    }
    private static string Short(string value) => value.Length > 300 ? value[..300] : value;
    private static bool Failure(Exception e) => ProgramPathResolver.ReadFailure(e) || e is JsonException or OperationCanceledException
        or InvalidOperationException or TypeLoadException or TypeInitializationException;

    internal static Task<IReadOnlyList<ProgramStoreApplication>> ReadNativeAsync(CancellationToken token) => Task.Run<IReadOnlyList<ProgramStoreApplication>>(() =>
    {
        var rows = new List<object>(); int count = 0;
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
        {
            token.ThrowIfCancellationRequested(); if (++count > 4096) throw new IOException("Store package enumeration exceeds limit.");
            if (package.IsFramework || package.IsResourcePackage || package.IsBundle || package.IsOptional) continue;
            var id = package.Id; var version = id.Version;
            string name = id.Name, publisher = id.Publisher, directory = "";
            try { if (!string.IsNullOrWhiteSpace(package.DisplayName)) name = package.DisplayName; }
            catch (Exception e) when (Failure(e)) { }
            try { if (!string.IsNullOrWhiteSpace(package.PublisherDisplayName)) publisher = package.PublisherDisplayName; }
            catch (Exception e) when (Failure(e)) { }
            try { directory = package.InstalledLocation?.Path ?? ""; }
            catch (Exception e) when (Failure(e)) { }
            rows.Add(new { PackageFamilyName = id.FamilyName, Name = name, Publisher = publisher,
                Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}", InstallLocation = directory,
                NonRemovable = package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System });
        }
        token.ThrowIfCancellationRequested(); return Parse(JsonSerializer.Serialize(rows));
    }, token);

    internal static async Task<IReadOnlyList<ProgramStoreApplication>> ReadPowerShellAsync(CancellationToken token)
    {
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        string script = "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); "
            + "@(Get-AppxPackage -PackageTypeFilter Main -ErrorAction Stop | Select-Object PackageFamilyName,Name,"
            + "@{Name='Publisher';Expression={$_.Publisher}},@{Name='Version';Expression={$_.Version.ToString()}},"
            + "InstallLocation,NonRemovable) | ConvertTo-Json -Compress";
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) command.ArgumentList.Add(arg);
        var result = await WinGetService.QueryAsync(command, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException("Windows-App-Paketprüfung fehlgeschlagen. / Windows app package check failed.");
        return Parse(string.IsNullOrWhiteSpace(result.Output) ? "[]" : result.Output.Trim());
    }
    internal static IReadOnlyList<ProgramStoreApplication> Parse(string json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new IOException("Store metadata exceeds limit.");
        using var data = JsonDocument.Parse(json, new() { MaxDepth = 16 });
        var rows = data.RootElement.ValueKind == JsonValueKind.Array ? data.RootElement.EnumerateArray().ToArray()
            : data.RootElement.ValueKind == JsonValueKind.Object ? [data.RootElement] : [];
        if (rows.Length > 4096) throw new IOException("Store package enumeration exceeds limit.");
        var found = new List<ProgramStoreApplication>();
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string? Text(string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Flag(string field) => row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
            string? family = Text("PackageFamilyName"), version = Text("Version"), name = Text("Name");
            if (family is null || family.Length > 256 || !Regex.IsMatch(family, @"^[A-Za-z0-9.-]+_[A-Za-z0-9]+$")
                || name is null || name.Length is 0 or > 250 || !System.Version.TryParse(version, out var parsed)
                || Flag("IsFramework") || Flag("IsResourcePackage") || Flag("IsBundle") || Flag("IsOptional")) continue;
            string? directory = Text("InstallLocation");
            if (directory is null || !Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)
                || !Directory.Exists(directory)) directory = null;
            found.Add(new(family, name, Text("Publisher") ?? "", parsed.ToString(), directory, Flag("NonRemovable")));
        }
        // The same family may have architecture/staging rows. Keep one deterministic
        // current-user version per family; never combine identities by display name.
        return found.GroupBy(p => p.Family, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(p => System.Version.Parse(p.Version)).ThenBy(p => p.Location, StringComparer.OrdinalIgnoreCase).First()
                with { Protected = group.Any(p => p.Protected) })
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    internal static IReadOnlyList<InstalledProgram> Rows(IEnumerable<ProgramStoreApplication> apps)
        => apps.Select(app =>
        {
            string? id = ProgramVersions.StoreFamilies.FirstOrDefault(p => p.Value.Equals(app.Family, StringComparison.OrdinalIgnoreCase)).Key;
            var entry = ProgramCatalog.Entries.FirstOrDefault(e => e.Id == id);
            // New families are listed, but cannot inherit another app's uninstaller
            // or WinGet route merely because their display names look similar.
            return new InstalledProgram("store|" + app.Family, entry?.Name ?? app.Name, entry?.Publisher ?? app.Publisher,
                app.Version, app.Location, "Windows-App", CatalogId: id, Protected: app.Protected || entry is null);
        }).ToArray();
}
