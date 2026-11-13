using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal static class ProgramVersions
{
    // PFNs confirmed against Microsoft Store metadata; do not infer identities from display names.
    internal static readonly IReadOnlyDictionary<string, string> StoreFamilies = new Dictionary<string, string>
    {
        ["chatgpt"] = "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0",
        ["microsoft-copilot"] = "Microsoft.Copilot_8wekyb3d8bbwe",
        ["windows-terminal"] = "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
        ["powershell"] = "Microsoft.PowerShell_8wekyb3d8bbwe",
        ["python"] = "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0",
        ["roblox-client"] = "ROBLOXCORPORATION.ROBLOX_55nm5eh3cm0pr"
    };
    internal static string? ConfirmedVersion(string? value) => !string.IsNullOrWhiteSpace(value) && value.Any(char.IsDigit)
        && Regex.IsMatch(value, @"^[A-Za-z0-9._+\-]+$") ? value : null;
    internal static bool IsNewer(string? installed, string? available)
    {
        // Numeric stable releases only; opaque vendor versions need their source's update proof.
        static int[]? Parts(string? value) => value is not null && Regex.IsMatch(value, @"^\d+(?:\.\d+){0,5}$")
            && value.Split('.').All(x => int.TryParse(x, out _)) ? value.Split('.').Select(int.Parse).ToArray() : null;
        var a = Parts(installed); var b = Parts(available); if (a is null || b is null) return false;
        for (int n = 0; n < Math.Max(a.Length, b.Length); n++)
        { int c = (n < b.Length ? b[n] : 0).CompareTo(n < a.Length ? a[n] : 0); if (c != 0) return c > 0; }
        return false;
    }
    internal static Dictionary<string, string?> ParseStorePackages(string json)
        => ParseStoreInstallations(json).ToDictionary(x => x.Key, x => x.Value.Version);
    internal static Dictionary<string, ProgramInstallation> ParseStoreInstallations(string json)
    {
        using var document = JsonDocument.Parse(json); var result = new Dictionary<string, ProgramInstallation>();
        var rows = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray()
            : document.RootElement.ValueKind == JsonValueKind.Object ? new[] { document.RootElement } : [];
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("PackageFamilyName", out var family) || !row.TryGetProperty("Version", out var version)) continue;
            foreach (var item in StoreFamilies.Where(x => string.Equals(x.Value, family.GetString(), StringComparison.OrdinalIgnoreCase)))
                if (System.Version.TryParse(version.GetString(), out var parsed))
                {
                    string? path = row.TryGetProperty("InstallLocation", out var location) ? location.GetString() : null;
                    if (path is null || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) path = null;
                    if (!result.TryGetValue(item.Key, out var current) || !System.Version.TryParse(current.Version, out var old) || parsed > old)
                        result[item.Key] = new(parsed.ToString(), "Windows-App", path, false, item.Key == "roblox-client" ? "Roblox (Microsoft Store)" : null);
                }
        }
        return result;
    }
    internal static async Task<Dictionary<string, string?>> ReadStorePackagesAsync(CancellationToken token)
        => (await ReadStoreInstallationsAsync(token)).ToDictionary(x => x.Key, x => x.Value.Version);
    internal static async Task<Dictionary<string, ProgramInstallation>> ReadStoreInstallationsAsync(CancellationToken token)
    {
        var snapshot = await ProgramStoreInventory.ReadAsync(token).ConfigureAwait(false);
        if (!snapshot.Complete) throw new IOException(snapshot.Error);
        return snapshot.Entries.ToDictionary(x => x.Key, x => x.Value);
    }
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Time, ProgramPackage? Value)> Cache = [];
    private static readonly SemaphoreSlim Gate = new(3);
    internal static string? GithubRepository(ProgramEntry entry)
    {
        if (!ProgramCatalog.Contains(entry)) return null;
        if (entry.Id == "artcraft-launcher") return "storytold/craft-launcher";
        if (entry.Id == "extra-ventoy-ventoy") return "ventoy/Ventoy";
        if (entry.Id == "wowup-cf") return "WowUp/WowUp.CF";
        var uri = new Uri(entry.Website);
        if (uri.Host != "github.com" || uri.Scheme != "https") return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(x => !Regex.IsMatch(x, "^[A-Za-z0-9_.-]+$")) || parts[1].EndsWith(".git")) return null;
        return string.Join('/', parts);
    }
    internal static string? ParseReleaseVersion(string json)
    {
        using var data = JsonDocument.Parse(json); var root = data.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var match = Regex.Match(tag, @"^[vV]?(\d+(?:\.\d+){1,3})$");
        return match.Success ? match.Groups[1].Value : null;
    }
    internal static async Task<ProgramPackage?> GithubAsync(ProgramEntry entry, CancellationToken token)
    {
        var repo = GithubRepository(entry); if (repo is null) return null;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(repo, out var cached) && DateTime.UtcNow - cached.Time < TimeSpan.FromMinutes(10)) return cached.Value;
            using var network = new OnlineRequest(token); using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repo + "/releases/latest");
            request.Headers.UserAgent.ParseAdd(AppVersion.UserAgent); request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, network.Token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { Cache[repo] = (DateTime.UtcNow, null); return null; }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(network.Token); using var copy = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, network.Token)) > 0) { if (copy.Length + count > 1024 * 1024) throw new IOException("GitHub response too large"); copy.Write(buffer, 0, count); }
            var version = ParseReleaseVersion(System.Text.Encoding.UTF8.GetString(copy.ToArray()));
            var value = version is null ? null : new ProgramPackage(entry.Id, null, "https://github.com/" + repo + "/releases/latest", DateTime.UtcNow,
                OnlineVersion: version, VersionSource: "GitHub", CanInstall: false);
            Cache[repo] = (DateTime.UtcNow, value); return value;
        }
        finally { Gate.Release(); }
    }
}
