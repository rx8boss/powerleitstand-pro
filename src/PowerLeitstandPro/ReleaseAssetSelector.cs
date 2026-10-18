using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record AvailableUpdate(Version Version, string DownloadUrl, string Sha256)
{
    internal string Notes { get; init; } = "";
    internal AppBackup? SourceBackup { get; init; }
}

internal static class ReleaseAssetSelector
{
    internal static AvailableUpdate? Select(JsonElement release, Version installedVersion)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
        var match = Regex.Match(tag, @"\d+\.\d+\.\d+");
        if (!match.Success || !Version.TryParse(match.Value, out var version))
            throw new InvalidDataException("Ungültige Release-Version.");
        if (version <= installedVersion) return null;

        JsonElement selected = default;
        var executableCount = 0;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            executableCount++;
            selected = asset;
        }
        if (executableCount == 0)
            throw new FileNotFoundException("Im GitHub-Release fehlt die Windows-EXE.");
        if (executableCount != 1)
            throw new InvalidDataException("Das GitHub-Release enthält mehrere EXE-Dateien. Ein automatisches Update ist nicht eindeutig.");

        var digest = selected.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
        if (digest is null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("Für die EXE fehlt eine gültige SHA-256-Prüfsumme im GitHub-Release.");
        var url = selected.GetProperty("browser_download_url").GetString() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "github.com" ||
            !uri.AbsolutePath.StartsWith($"/{ProjectLinks.RepositoryPath}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ungültige Release-Downloadadresse.");
        return new AvailableUpdate(version, url, digest[7..]);
    }
}
