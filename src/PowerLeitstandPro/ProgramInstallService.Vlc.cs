namespace PowerControlProWpf;

internal sealed partial class ProgramInstallService
{
    internal Task<IReadOnlyList<VlcRegisteredCopy>> VlcCopiesAsync(CancellationToken token)
        => Task.Run(() => _vlcCopies?.Invoke() ?? Array.Empty<VlcRegisteredCopy>(), token);

    private ProgramInventory VlcInventory() => new(true, _lastEvidence.ToDictionary(p => p.Key, p => p.Value.Version), Evidence: _lastEvidence);

    private async Task<ProgramPackage> BindVlcUpdateAsync(ProgramEntry entry, ProgramPackage package, CancellationToken token)
    {
        if (entry.Id != "vlc" || _vlcCopies is null || _lastEvidence.GetValueOrDefault(entry.Id) is not { Source: "Registry" } installed
            || !ProgramVersions.IsNewer(installed.Version, package.Version)) return package;
        var target = ReinstallCapability(entry, VlcInventory());
        string? hash;
        try { hash = target.VlcCopy is { } copy ? await _vlcManifest(package, copy, token) : null; }
        catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { token.ThrowIfCancellationRequested(); hash = null; }
        return hash is null ? package with { CanInstall = false } : package with { ReinstallProof = new(target, false, hash) };
    }

    private async Task<ProgramPackage?> VlcPublisherVersionAsync(ProgramEntry entry, CancellationToken token)
    {
        if (entry.Id != "vlc" || !Approved(entry)) return null;
        var selected = _lastEvidence.GetValueOrDefault(entry.Id);
        string? architecture = selected?.Source == "Registry" && _vlcCopies is not null
            ? ProgramVlcReinstallation.Select(selected, await VlcCopiesAsync(token))?.Architecture
            : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            { System.Runtime.InteropServices.Architecture.X64 => "x64", System.Runtime.InteropServices.Architecture.X86 => "x86", _ => null };
        string? folder = architecture switch { "x64" => "win64", "x86" => "win32", _ => null };
        if (folder is null) return null;
        var index = await CatalogWeb.ReadAsync(new("https://download.videolan.org/pub/videolan/vlc/last/" + folder + "/"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "download.videolan.org" }, 2 * 1024 * 1024, token);
        string? version = VlcPublisherVersion(System.Text.Encoding.UTF8.GetString(index), folder);
        return version is null ? null : new(entry.PackageId!, null, entry.Website, DateTime.UtcNow,
            OnlineVersion: version, VersionSource: "Publisher", CanInstall: false);
    }

    internal static string? VlcPublisherVersion(string index, string folder)
    {
        if (folder is not ("win64" or "win32")) return null;
        var versions = System.Text.RegularExpressions.Regex.Matches(index, "<a href=\"vlc-(?<v>\\d+\\.\\d+\\.\\d+)-" + folder + "\\.(?:exe|msi)\">")
            .Select(m => m.Groups["v"].Value).Distinct().ToArray();
        return versions.Length == 1 ? versions[0] : null;
    }
}
