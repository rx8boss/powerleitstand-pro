using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed partial class ProgramPortableUpdates
{
    internal const string AdwCleanerDownload = "https://adwcleaner.malwarebytes.com/adwcleaner?channel=release";
    internal const string AdwCleanerIndex = "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/m/Malwarebytes/AdwCleaner";

    internal static string? LatestAdwCleanerVersion(string json)
    {
        if (json.Length > 128 * 1024) return null;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 512) return null;
        return document.RootElement.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object
                && r.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "dir")
            .Select(r => r.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
            .Where(v => v is not null && Regex.IsMatch(v, @"^\d+(?:\.\d+){1,3}$") && Version.TryParse(v, out _))
            .OrderByDescending(v => Version.Parse(v!)).FirstOrDefault();
    }

    internal static PortableUpdate? ParseAdwCleaner(string manifest, string path, string old, string hash, string version)
    {
        var artifact = ManifestArtifact(manifest, "Malwarebytes.AdwCleaner", version, AdwCleanerDownload);
        return artifact is not null && Regex.IsMatch(version, @"^\d+(?:\.\d+){1,3}$") && Version.TryParse(version, out _)
            && ManifestField(manifest, "InstallerType") == "portable" && ManifestField(manifest, "- Architecture") == "x86"
            ? new("adwcleaner", path, old, version, artifact.Value.Url, artifact.Value.Sha, null, null, hash) : null;
    }

    private async Task<PortableUpdate?> ResolveAdwCleanerAsync(string path, string old, string hash, CancellationToken token)
    {
        if (Machine(path) != 0x14c || !ManufacturerSignature.Verify(path, "Malwarebytes Inc")) return null;
        var index = Encoding.UTF8.GetString(await _read(new(AdwCleanerIndex), token));
        var version = LatestAdwCleanerVersion(index);
        if (version is null) return null;
        var manifest = Encoding.UTF8.GetString(await _read(ProgramInstallTargets.ManifestUri("Malwarebytes.AdwCleaner", version), token));
        return ParseAdwCleaner(manifest, path, old, hash, version);
    }
}
