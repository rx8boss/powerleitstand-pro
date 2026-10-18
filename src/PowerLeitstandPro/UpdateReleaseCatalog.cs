using System.Net.Http;
using System.Text.Json;

namespace PowerControlProWpf;

internal sealed record UpdateRelease(AvailableUpdate Update, string Notes, DateTimeOffset Published)
{
    public string Label => $"Version {Update.Version}";
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
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) continue;
                var update = ReleaseAssetSelector.Select(release, new Version(0, 0, 0));
                // v1.1.1 is the earliest published WPF release whose update/download contract was verified.
                if (update is null || update.Version < new Version(1, 1, 1)) continue;
                var notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var published = release.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue;
                list.Add(new(update with { Notes = notes }, notes, published));
            }
            catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return list.GroupBy(r => r.Update.Version).Select(g => g.First()).OrderByDescending(r => r.Update.Version).ToArray();
    }

    internal static async Task<IReadOnlyList<UpdateRelease>> LoadAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppVersion.UserAgent);
        var result = new List<UpdateRelease>();
        // Follow all pages rather than silently hiding earlier compatible releases.
        for (var page = 1; ; page++)
        {
            using var response = await client.GetAsync($"https://api.github.com/repos/{ProjectLinks.RepositoryPath}/releases?per_page=100&page={page}", token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
            result.AddRange(Parse(json.RootElement));
            if (json.RootElement.GetArrayLength() < 100) break;
        }
        return result.OrderByDescending(r => r.Update.Version).ToArray();
    }
}
