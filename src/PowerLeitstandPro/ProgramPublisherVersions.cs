using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

internal static class ProgramPublisherVersions
{
    private static readonly Dictionary<string, (DateTime Time, ProgramPackage Value)> Cache = [];
    private static readonly SemaphoreSlim Gate = new(1);
    internal static bool SupportsOnlineVersion(string id) => id == "nvidia-broadcast";

    internal static Version? SwarmReleaseNoteVersion(Stream releaseNotes)
    {
        if (releaseNotes.Length > 1024 * 1024) return null;
        using var json = JsonDocument.Parse(releaseNotes);
        if (json.RootElement.ValueKind != JsonValueKind.Object
            || !json.RootElement.TryGetProperty("Software", out var software) || software.ValueKind != JsonValueKind.Object
            || !software.TryGetProperty("Swarm II", out var swarm) || swarm.ValueKind != JsonValueKind.Object) return null;

        var minimum = new Version(0, 0, 0, 0);
        Version? newest = null;
        foreach (var note in swarm.EnumerateObject())
            if (note.Value.ValueKind == JsonValueKind.Object && Version.TryParse(note.Name, out var version)
                && version.Revision >= 0 && version > minimum && (newest is null || version > newest))
                newest = version;
        return newest;
    }

    internal static Version? ReadSwarmReleaseNoteVersion()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (appData.Length == 0) return null;
        try
        {
            using var file = File.OpenRead(Path.Combine(appData, "Turtle Beach", "Swarm II", "ChangeLog", "ReleaseNotes.json"));
            return SwarmReleaseNoteVersion(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    internal static string? BroadcastVersion(string html)
    {
        var matches = Regex.Matches(html, @"https://international\.download\.nvidia\.com/Windows/broadcast/(?<version>\d+(?:\.\d+){1,3})/NVIDIA_Broadcast_v\k<version>\.\d+\.exe", RegexOptions.CultureInvariant);
        var versions = matches.Select(x => x.Groups["version"].Value).Distinct().ToArray();
        return versions.Length == 1 ? versions[0] : null;
    }
    internal static async Task<ProgramPackage?> ReadAsync(ProgramEntry entry, CancellationToken token)
    {
        if (!ProgramCatalog.Contains(entry) || !SupportsOnlineVersion(entry.Id)) return null;
        await Gate.WaitAsync(token);
        try
        {
            if (Cache.TryGetValue(entry.Id, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(10)) return cached.Value;
            var uri = new Uri(entry.Website);
            var html = Encoding.UTF8.GetString(await CatalogWeb.ReadAsync(uri, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uri.IdnHost }, 2 * 1024 * 1024, token));
            string? version = entry.Id == "nvidia-broadcast" ? BroadcastVersion(html) : null;
            var value = new ProgramPackage(entry.Id, null, entry.Website, DateTime.UtcNow, OnlineVersion: version, VersionSource: "Publisher", CanInstall: false);
            Cache[entry.Id] = (DateTime.UtcNow, value); return value;
        }
        finally { Gate.Release(); }
    }
}
