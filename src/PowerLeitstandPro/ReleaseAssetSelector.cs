using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed record AvailableUpdate(Version Version, string DownloadUrl, string Sha256)
{
    internal UpdateChannel Channel { get; init; } = UpdateChannel.Stable;
    internal int Revision { get; init; }
    internal AppBuild Build => new(Version, Channel, Revision);
    internal bool IsArchive => Channel != UpdateChannel.Stable && SourceBackup is null;
    internal string Notes { get; init; } = "";
    internal AppBackup? SourceBackup { get; init; }
}

internal static class ReleaseAssetSelector
{
    internal static AvailableUpdate? Select(JsonElement release, Version installedVersion)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (AppBuild.Parse(tag) is not { Channel: UpdateChannel.Stable } build)
            throw new InvalidDataException("Ungültige Release-Version.");
        if (build.Version <= installedVersion) return null;
        var candidates = release.GetProperty("assets").EnumerateArray().Where(asset =>
            asset.GetProperty("name").GetString()?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (candidates.Length == 0) throw new FileNotFoundException("Im GitHub-Release fehlt die Windows-EXE.");
        if (candidates.Length != 1) throw new InvalidDataException("Das GitHub-Release enthält mehrere EXE-Dateien. Ein automatisches Update ist nicht eindeutig.");
        return ReadAsset(candidates[0], tag, build);
    }

    internal static string ArchiveName(AppBuild build) =>
        $"PowerLeitstand-PRO-{build.Version}-{(build.Channel == UpdateChannel.Beta ? "Beta" : "Hotfix")}-{build.Number}.zip";

    internal static AvailableUpdate ReadAsset(JsonElement asset, string tag, AppBuild build)
    {
        string? name = asset.GetProperty("name").GetString();
        var digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
        if (name is null || digest is null || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z"))
            throw new InvalidDataException("Für die Datei fehlt eine gültige SHA-256-Prüfsumme im GitHub-Release.");
        var url = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith($"/{ProjectLinks.RepositoryPath}/releases/download/", StringComparison.OrdinalIgnoreCase) ||
            Uri.UnescapeDataString(uri.AbsolutePath)[($"/{ProjectLinks.RepositoryPath}/releases/download/".Length)..] != tag + "/" + name)
            throw new InvalidDataException("Ungültige Release-Downloadadresse.");
        return new(build.Version, url, digest[7..]) { Channel = build.Channel, Revision = build.Number };
    }
}
