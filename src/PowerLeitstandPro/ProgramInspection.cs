using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

internal sealed partial class ProgramInstallService
{
    public event Action<ProgramInventory>? InventoryChanged;
    private ProgramInventory? _recentInventory;
    private HashSet<string> _recentIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string?> _recentExport = new Dictionary<string, string?>();
    private string _recentUpdatesText = "";
    private readonly bool _readStoreInventory;
    private readonly Func<CancellationToken, Task<ProgramStoreSnapshot>> _storeReader;
    private Dictionary<string, ProgramInstallation> _lastGoodStore = new();
    internal void ClearTukuiPackage() => _portableUpdates.ClearTukuiPackage();
    internal async Task<ProgramPackage?> ResolveCheckedUpdateAsync(ProgramEntry entry, ProgramInventory inventory, CancellationToken token)
    {
        if (!Approved(entry) || !inventory.HasUpdate(entry.Id) || inventory.Updates?.GetValueOrDefault(entry.Id) is not { } version) return null;
        if (inventory.ManagedPackages?.GetValueOrDefault(entry.Id)?.FirstOrDefault(p => p.IsUpdate && p.Version == version) is { } managed)
            return managed.Package();
        if (inventory.PortableUpdates?.GetValueOrDefault(entry.Id) is { } portable && portable.Version == version)
            return new(entry.PackageId ?? entry.Id, version, portable.Url, DateTime.UtcNow, VersionSource: "Publisher", PortableUpdate: portable);
        if (inventory.GitHubPackages?.GetValueOrDefault(entry.Id) is { } github && github.Version == version)
            return new(entry.Id, version, github.Url, DateTime.UtcNow, VersionSource: "GitHub", GitHubUpdate: github);
        if (entry.PackageId is null || inventory.Evidence?.GetValueOrDefault(entry.Id) is not { WinGetCompatible: true }) return null;
        var package = await ResolveAsync(entry, token);
        if (package is { VersionSource: "WinGet" } && package.Version == version) return package.CanInstall ? package : null;
        // The default package can be newer than the exact upgrade offered to this installation.
        var exact = await ReadReinstallWinGetAsync(entry, version, token);
        if (exact is null) return null;
        exact = await BindVlcUpdateAsync(entry, exact, token);
        return exact.CanInstall ? exact : null;
    }

    internal static bool CheckedUpdateMatches(ProgramEntry entry, ProgramPackage? package, ProgramInventory inventory)
    {
        if (!inventory.HasUpdate(entry.Id) || package is not { CanInstall: true, Version: { } version }) return false;
        if (package.ManagedPackage is { } managed)
            return inventory.ManagedPackages?.GetValueOrDefault(entry.Id)?.Any(p => p == managed && p.IsUpdate) == true;
        if (version != inventory.Updates?.GetValueOrDefault(entry.Id)) return false;
        return package.GitHubUpdate == inventory.GitHubPackages?.GetValueOrDefault(entry.Id)
            && package.PortableUpdate == inventory.PortableUpdates?.GetValueOrDefault(entry.Id)
            && inventory.ManagedPackages?.GetValueOrDefault(entry.Id)?.Any(p => p.IsUpdate) != true
            && (package.GitHubUpdate is not null || package.PortableUpdate is not null || PackageMatches(entry, package.Id));
    }
    internal async Task<ProgramInventory> ProbeTukuiAsync(ProgramInventory inventory, Action<ProgramDownload> report, CancellationToken token)
    {
        if (inventory.Evidence?.GetValueOrDefault("tukui-client") is not { } proof || !ProgramTukuiUpdates.Eligible(proof)) return inventory;
        var offer = await _portableUpdates.ProbeTukuiAsync(proof, report, token).ConfigureAwait(false);
        var updates = inventory.Updates?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        var portable = inventory.PortableUpdates?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        updates.Remove("tukui-client"); portable.Remove("tukui-client");
        if (offer is not null && ProgramVersions.IsNewer(offer.InstalledVersion, offer.Version))
        { updates[offer.Id] = offer.Version; portable[offer.Id] = offer; }
        return inventory with { Updates = updates, PortableUpdates = portable, CheckedUtc = DateTime.UtcNow };
    }
    internal async Task<Dictionary<string, string>> ReadGithubNoticesAsync(IEnumerable<ProgramEntry> entries,
        IReadOnlyDictionary<string, string?> installed, CancellationToken token, Action<string>? failed = null)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(entries.Where(entry => installed.ContainsKey(entry.Id) && ProgramVersions.GithubRepository(entry) is not null).Select(async entry =>
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
                var release = await _github(entry, limit.Token).ConfigureAwait(false);
                var available = ProgramVersions.ConfirmedVersion(release?.OnlineVersion);
                if (available is not null && ProgramVersions.IsNewer(installed.GetValueOrDefault(entry.Id), available))
                    lock (results) results[entry.Id] = available;
            }
            catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or JsonException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
            { token.ThrowIfCancellationRequested(); failed?.Invoke(entry.Id); }
        })).ConfigureAwait(false);
        return results;
    }
    private async Task<Dictionary<string, PortableUpdate>> ReadPortableForInspectionAsync(IReadOnlyDictionary<string, ProgramInstallation> evidence, CancellationToken token, Action<string>? failed = null)
    {
        var results = new Dictionary<string, PortableUpdate>();
        await Task.WhenAll(evidence.Where(p => ProgramPortableUpdates.Eligible(p.Key, p.Value)).Select(async pair =>
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(10));
                var update = await _portableUpdates.ResolveAsync(pair.Key, pair.Value, limit.Token).ConfigureAwait(false);
                if (update is not null) lock (results) results[pair.Key] = update;
            }
            catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or JsonException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
            { token.ThrowIfCancellationRequested(); failed?.Invoke(pair.Key); }
        })).ConfigureAwait(false);
        return results;
    }
    private async Task<ProgramStoreSnapshot> ReadStoreForInspectionAsync(CancellationToken token)
    {
        if (!_readStoreInventory) return new(new Dictionary<string, ProgramInstallation>(), true);
        ProgramStoreSnapshot result;
        try { result = await _storeReader(token).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { token.ThrowIfCancellationRequested(); result = new(new Dictionary<string, ProgramInstallation>(), false, e.Message); }
        token.ThrowIfCancellationRequested();
        if (result.Complete) _lastGoodStore = result.Entries.ToDictionary(x => x.Key, x => x.Value);
        else if (_lastGoodStore.Count > 0) result = result with { Entries = new Dictionary<string, ProgramInstallation>(_lastGoodStore), PreviousConfirmed = true };
        return result;
    }
    // Only online searches reuse a recent complete scan. The explicit Check button always scans afresh.
    public async Task<ProgramInventory> InspectForSearchAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_realInventory) OnlineConnection.RequireNetwork();
        var recent = _recentInventory;
        if (recent is not { Complete: true, UpdateCheckComplete: true, UpdateSourcesComplete: true, OnlineError: null, CheckedUtc: { } stamp }
            || DateTime.UtcNow - stamp > TimeSpan.FromSeconds(45)) return await InspectAsync(token);
        var added = KnownEntries.Where(e => !_recentIds.Contains(e.Id)).ToArray();
        if (added.Length == 0) return recent with { FromCache = true };
        var localTask = Task.Run(() => ProgramLocalInventory.Read(added, token, includeManaged: false), token);
        var managedTask = Task.Run(() => ProgramManagedInventory.Read(added, token), token);
        await Task.WhenAll(localTask, managedTask);
        var local = await localTask; var managed = await managedTask;
        ProgramManagedInventory.Merge(local, managed);
        if (_locationsFile is not null) await Task.Run(() => new ProgramFileAssociations(_locationsFile).Merge(added, local, token), token);
        var evidence = recent.Evidence?.ToDictionary(x => x.Key, x => x.Value) ?? [];
        var installed = recent.Installed.ToDictionary(x => x.Key, x => x.Value);
        foreach (var pair in local) { evidence[pair.Key] = pair.Value; installed[pair.Key] = pair.Value.Version; }
        foreach (var entry in added)
        foreach (var pair in _recentExport.Where(p => PackageMatches(entry, p.Key)))
        {
            if (evidence.GetValueOrDefault(entry.Id) is { WinGetCompatible: false }) continue;
            if (_installedPackageIds.TryGetValue(entry.Id, out var previousId) && !string.Equals(previousId, pair.Key, StringComparison.OrdinalIgnoreCase)) _ambiguousReinstallIds.Add(entry.Id);
            _installedPackageIds[entry.Id] = pair.Key;
            if (!installed.ContainsKey(entry.Id))
            { installed[entry.Id] = ProgramVersions.ConfirmedVersion(pair.Value); evidence[entry.Id] = new(installed[entry.Id], "WinGet"); }
        }
        var updates = recent.Updates?.ToDictionary(x => x.Key, x => x.Value) ?? [];
        var parsedInstalled = new Dictionary<string, string?>(installed);
        var foundUpdates = ParseUpdates(_recentUpdatesText, parsedInstalled,
            added.Where(e => evidence.GetValueOrDefault(e.Id) is not { WinGetCompatible: false }), _installedPackageIds);
        foreach (var pair in foundUpdates)
        {
            // A newly discovered local version may already differ from the cached WinGet row.
            if (installed.GetValueOrDefault(pair.Key) is { } current && current != parsedInstalled.GetValueOrDefault(pair.Key)) continue;
            updates[pair.Key] = pair.Value; installed[pair.Key] = parsedInstalled[pair.Key];
            if (_installedPackageIds.TryGetValue(pair.Key, out var packageId)) _installedPackageVersions[packageId] = parsedInstalled[pair.Key];
            evidence.TryAdd(pair.Key, new(installed[pair.Key], "WinGet"));
        }
        _lastEvidence = evidence;
        var result = recent with { Installed = installed, Evidence = evidence, Updates = updates,
            InstalledSources = evidence.ToDictionary(x => x.Key, x => x.Value.Source), FromCache = true };
        _recentInventory = result with { FromCache = false };
        foreach (var entry in added) _recentIds.Add(entry.Id);
        return result;
    }
}
