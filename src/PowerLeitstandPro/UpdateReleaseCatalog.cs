using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record UpdateRelease(AvailableUpdate Update, string Notes, DateTimeOffset Published)
{
    public string Label => $"Version {Update.Build.Display}";
}

internal static class UpdateReleaseCatalog
{
    internal static IReadOnlyList<UpdateRelease> Parse(JsonElement array)
    {
        var list = new List<UpdateRelease>();
        foreach (var release in array.EnumerateArray())
        {
            try
            {
                if (release.GetProperty("draft").GetBoolean()) continue;
                bool prerelease = release.GetProperty("prerelease").GetBoolean();
                string tag = release.GetProperty("tag_name").GetString() ?? "";
                if (AppBuild.Parse(tag) is not { } build || build.Version < new Version(1, 1, 1)
                    || prerelease != (build.Channel != UpdateChannel.Stable)) continue;
                var notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var published = release.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue;
                void Add(AvailableUpdate update, string description) => list.Add(new(update with { Notes = description }, description, published));
                if (build.Channel == UpdateChannel.Stable)
                {
                    try
                    {
                        if (ReleaseAssetSelector.Select(release, new Version(0, 0, 0)) is { } stable) Add(stable, notes);
                    }
                    catch (Exception e) when (InvalidRelease(e)) { }
                }

                var assets = release.GetProperty("assets").EnumerateArray()
                    .Where(a => a.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    .GroupBy(a => a.GetProperty("name").GetString(), StringComparer.Ordinal);
                var prefix = $"PowerLeitstand-PRO-{build.Version}-Hotfix-";
                foreach (var group in assets)
                {
                    string? name = group.Key;
                    var candidate = build;
                    if (build.Channel == UpdateChannel.Stable)
                    {
                        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".zip", StringComparison.Ordinal) ||
                            AppBuild.Parse($"{build.Version}-hotfix.{name[prefix.Length..^4]}") is not { } hotfix) continue;
                        candidate = hotfix;
                    }
                    if (name != ReleaseAssetSelector.ArchiveName(candidate) || group.Count() != 1) continue;
                    try
                    {
                        string description = build.Channel == UpdateChannel.Stable
                            ? Regex.Match(notes, $@"(?im)^##[ \t]+Hotfix[ \t]+{candidate.Number}[ \t]*\r?\n([\s\S]*?)(?=^##[ \t]|\z)").Groups[1].Value.Trim()
                            : notes;
                        Add(ReleaseAssetSelector.ReadAsset(group.Single(), tag, candidate), description);
                    }
                    catch (Exception e) when (InvalidRelease(e)) { }
                }
            }
            catch (Exception e) when (InvalidRelease(e)) { }
        }
        return Distinct(list);
    }

    private static bool InvalidRelease(Exception e) =>
        e is IOException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException;

    private static IReadOnlyList<UpdateRelease> Distinct(IEnumerable<UpdateRelease> releases) => releases
        .GroupBy(r => r.Update.Build).Where(g => g.Select(r => (r.Update.DownloadUrl, r.Update.Sha256)).Distinct().Count() == 1)
        .Select(g => g.First()).OrderByDescending(r => r.Update.Build).ToArray();

    internal static IReadOnlyList<UpdateRelease> Visible(IReadOnlyList<UpdateRelease> releases, AppBuild installed, UpdatePreference preference)
    {
        var stable = releases.Where(r => r.Update.Channel == UpdateChannel.Stable).ToArray();
        Version? newestStable = stable.MaxBy(r => r.Update.Version)?.Update.Version;
        var optional = releases.Where(r => r.Update.Build.CompareTo(installed) > 0 && (r.Update.Channel switch
        {
            UpdateChannel.Hotfix => preference == UpdatePreference.Hotfixes && r.Update.Version == newestStable,
            UpdateChannel.Beta => preference == UpdatePreference.Beta && (newestStable is null || r.Update.Version > newestStable),
            _ => false
        })).MaxBy(r => r.Update.Build);
        if (preference == UpdatePreference.Beta) return optional is null ? [] : [optional];
        return (optional is null ? stable : stable.Append(optional)).OrderByDescending(r => r.Update.Build).ToArray();
    }

    internal static async Task<IReadOnlyList<UpdateRelease>> LoadAsync(CancellationToken token)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        var result = new List<UpdateRelease>();
        for (var page = 1; ; page++)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{ProjectLinks.RepositoryPath}/releases?per_page=100&page={page}", token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            result.AddRange(Parse(json.RootElement));
            if (json.RootElement.GetArrayLength() < 100) break;
        }
        return Distinct(result);
    }
}
