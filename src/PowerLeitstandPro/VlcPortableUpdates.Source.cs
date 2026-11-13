using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal sealed partial class VlcPortableUpdates
{
    internal static (string Version, long Size)? ParseIndex(string html, string? expected = null)
    {
        var results = new List<(string Version, long Size)>();
        foreach (Match link in Regex.Matches(html, "<a href=\"vlc-(?<v>\\d+\\.\\d+\\.\\d+)-win64\\.zip\">[^<]*</a>(?<tail>[^<\\r\\n]*)"))
        {
            var size = Regex.Match(link.Groups["tail"].Value, @"^\s+\S+\s+\S+\s+(?<size>\d+)\s*$");
            if (!size.Success || !long.TryParse(size.Groups["size"].Value, out long bytes) || bytes is <= 0 or > ArchiveLimit) return null;
            results.Add((link.Groups["v"].Value, bytes));
        }
        return results.Count == 1 && (expected is null || results[0].Version == expected) ? results[0] : null;
    }
    internal static PortablePackage? Package((string Version, long Size) descriptor, string checksum)
    {
        string filename = "vlc-" + descriptor.Version + "-win64.zip";
        var match = Regex.Match(checksum, @"\A([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(filename) + @"\s*\z");
        return match.Success ? new(descriptor.Version, BaseUrl + descriptor.Version + "/win64/" + filename, match.Groups[1].Value.ToUpperInvariant(), descriptor.Size) : null;
    }
    private async Task<PortablePackage?> ReleaseAsync(string version, CancellationToken token)
    {
        if (version != "last" && !Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) return null;
        var descriptor = ParseIndex(Encoding.UTF8.GetString(await _read(new(BaseUrl + version + "/win64/"), token).ConfigureAwait(false)), version == "last" ? null : version);
        if (descriptor is not { } item) return null;
        string checksum = Encoding.UTF8.GetString(await _read(new(BaseUrl + item.Version + "/win64/vlc-" + item.Version + "-win64.zip.sha256"), token).ConfigureAwait(false));
        return Package(item, checksum);
    }
    internal Task<PortablePackage?> LatestAsync(CancellationToken token) => RuntimeInformation.OSArchitecture == Architecture.X64
        ? ReleaseAsync("last", token) : Task.FromResult<PortablePackage?>(null);
    internal async Task<PortableUpdate?> ResolveAsync(ProgramInstallation proof, CancellationToken token)
    {
        if (!ProofEligible(proof) || RuntimeInformation.OSArchitecture != Architecture.X64) return null;
        var local = await Task.Run(() => Identify(proof.Path!, _binary), token).ConfigureAwait(false);
        if (local is null || proof.Version is not null && !ProgramPortableUpdates.SameVersion(proof.Version, local.Version)) return null;
        var release = await LatestAsync(token).ConfigureAwait(false);
        if (release is null || ProgramVersions.IsNewer(release.Version, local.Version)) return null;
        return new("vlc", local.Executable, local.Version, release.Version, release.Url, release.Sha256, release.Size, null,
            await Task.Run(() => ProgramPortableUpdates.Hash(local.Executable), token).ConfigureAwait(false));
    }
}
