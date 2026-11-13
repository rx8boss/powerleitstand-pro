using System.Diagnostics;
using System.IO;

namespace PowerControlProWpf;

internal sealed partial class ProgramInstallService
{
    private readonly ProgramInspectionCache? _inspectionCache;
    private readonly Func<ProgramEntry[], CancellationToken, Task<Dictionary<string, ProgramInstallation>>>? _localReader;
    private readonly Action<string, TimeSpan>? _inspectionTiming;
    private readonly SemaphoreSlim _inspectionGate = new(1);
    private readonly object _flightGate = new();
    private readonly List<Task> _inspectionChildren = [];
    private Task<ProgramInventory>? _inspectionFlight;
    internal bool HasPersistentInspection => _inspectionCache is not null;
    internal async Task<T> TimedInspectionAsync<T>(string phase, Func<Task<T>> action)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var pending = action();
            if (phase != "Total") lock (_inspectionChildren) _inspectionChildren.Add(pending);
            return await pending.ConfigureAwait(false);
        }
        finally { _inspectionTiming?.Invoke(phase, clock.Elapsed); }
    }
    public Task<ProgramInventory> InspectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_flightGate)
        {
            if (_inspectionFlight is not { IsCompleted: false }) _inspectionFlight = InspectExclusiveAsync(token);
            return _inspectionFlight.WaitAsync(token);
        }
    }
    internal Task<ProgramInventory> InspectUpdatesAsync(ProgramEntry[] entries, CancellationToken token)
    {
        if (entries.Length == 0 || entries.Any(e => !Approved(e))) throw new InvalidOperationException("Unverified update selection");
        return InspectExclusiveAsync(token, entries.DistinctBy(e => e.Id).ToArray());
    }
    private async Task<ProgramInventory> InspectExclusiveAsync(CancellationToken token, ProgramEntry[]? selection = null)
    {
        await _inspectionGate.WaitAsync(token).ConfigureAwait(false);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (_inspectionChildren) _inspectionChildren.Clear();
        try
        {
            var result = await TimedInspectionAsync("Total", () => InspectCoreAsync(run.Token, selection)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (selection is null && _inspectionCache is not null) await Task.Run(() =>
            {
                if (!_inspectionCache.Save(result, KnownEntries)) _inspectionCache.SaveConfirmedParts(result, KnownEntries);
            }, token).ConfigureAwait(false);
            return result;
        }
        finally
        {
            run.Cancel(); Task[] children;
            lock (_inspectionChildren) children = _inspectionChildren.ToArray();
            // Do not release the next scan while canceled child readers can still change service state.
            try { await Task.WhenAll(children).ConfigureAwait(false); } catch (Exception) { }
            _inspectionGate.Release();
        }
    }
    internal Task<ProgramInventory?> ReadSavedAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var saved = _inspectionCache?.Read(KnownEntries, DateTime.UtcNow);
        return saved is null ? null : ProgramInspectionCache.Display(saved);
    }, token);
    internal void InvalidateSaved(string? id = null) => _inspectionCache?.Invalidate(id, KnownEntries);

    internal async Task<(ProgramInstallation? Installation, ProgramPackage? Package, DateTime CheckedUtc)> InspectEntryAsync(
        ProgramEntry entry, CancellationToken token)
    {
        if (!Approved(entry)) throw new InvalidOperationException("Program entry is not part of the approved catalog.");
        await _inspectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ProgramEntry[] entries = [entry];
            var local = _localReader is not null ? await _localReader(entries, token).ConfigureAwait(false)
                : await Task.Run(() => ProgramLocalInventory.Read(entries, token, includeManaged: false), token).ConfigureAwait(false);
            var managed = await Task.Run(() => ProgramManagedInventory.Read(entries, token), token).ConfigureAwait(false);
            if (_locationsFile is not null) await Task.Run(() => new ProgramFileAssociations(_locationsFile, _vlcCopies).Merge(entries, local, token), token).ConfigureAwait(false);
            ProgramManagedInventory.Merge(local, managed);
            if (_portableFile is not null && !local.ContainsKey(entry.Id)
                && new ProgramPortableStore(_portableFile).Read().TryGetValue(entry.Id, out var portable))
                local[entry.Id] = portable;
            if (_readStoreInventory && ProgramVersions.StoreFamilies.ContainsKey(entry.Id))
            {
                var store = await ReadStoreForInspectionAsync(token).ConfigureAwait(false);
                if (store.Complete && store.Entries.TryGetValue(entry.Id, out var app)) local[entry.Id] = app;
            }
            token.ThrowIfCancellationRequested();
            var evidence = _lastEvidence.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
            if (local.TryGetValue(entry.Id, out var proof)) evidence[entry.Id] = proof;
            else evidence.Remove(entry.Id);
            _lastEvidence = evidence;
            var package = await ResolveAsync(entry, token).ConfigureAwait(false);
            return (local.GetValueOrDefault(entry.Id), package, DateTime.UtcNow);
        }
        finally { _inspectionGate.Release(); }
    }

    // Reads local evidence only, and only for previously reported candidates. Never authorizes an update.
    internal async Task<ProgramInventory> RefreshKnownAsync(ProgramInventory previous, CancellationToken token)
    {
        await _inspectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var values in new[] { previous.Updates, previous.GitHubUpdates, previous.PendingUpdates })
                if (values is not null) foreach (var pair in values) targets.TryAdd(pair.Key, pair.Value);
            var entries = KnownEntries.Where(e => targets.ContainsKey(e.Id)).ToArray();
            if (entries.Length == 0) return previous;
            var saved = _inspectionCache?.Read(KnownEntries, DateTime.UtcNow);
            var evidence = _localReader is not null ? await _localReader(entries, token).ConfigureAwait(false)
                : await Task.Run(() => ProgramLocalInventory.Read(entries, token), token).ConfigureAwait(false);
            if (_locationsFile is not null) await Task.Run(() => new ProgramFileAssociations(_locationsFile, _vlcCopies).Merge(entries, evidence, token), token).ConfigureAwait(false);
            if (entries.Any(e => ProgramVersions.StoreFamilies.ContainsKey(e.Id)))
            {
                var store = await ReadStoreForInspectionAsync(token).ConfigureAwait(false);
                if (store.Complete) foreach (var pair in store.Entries.Where(p => targets.ContainsKey(p.Key))) evidence[pair.Key] = pair.Value;
            }
            token.ThrowIfCancellationRequested();
            var installed = previous.Installed.ToDictionary(p => p.Key, p => p.Value);
            var proofs = previous.Evidence?.ToDictionary(p => p.Key, p => p.Value) ?? [];
            var pending = new Dictionary<string, string>(targets);
            foreach (var entry in entries)
            {
                var old = previous.Evidence?.GetValueOrDefault(entry.Id);
                var row = saved?.Rows.SingleOrDefault(r => r.Id == entry.Id);
                if (!evidence.TryGetValue(entry.Id, out var fresh) || old is null) continue;
                // Scope, channel and managed roots must agree; paths for ordinary/portable copies must agree too.
                bool identity = (row?.Identity ?? ProgramInspectionCache.Identity(old)) == ProgramInspectionCache.Identity(fresh);
                bool path = old.Source is "Scoop" or "Chocolatey" || string.Equals(old.Path, fresh.Path, StringComparison.OrdinalIgnoreCase);
                bool multiple = fresh.ManagedCopies?.Count > 1 || fresh.LocationEvidence?.Alternatives?.Count > 1;
                if (!identity || !path || multiple) continue;
                installed[entry.Id] = fresh.Version; proofs[entry.Id] = fresh;
                var comparisonVersion = fresh.ManagedCopies is { Count: 1 } copies ? copies[0].PackageVersion : fresh.Version;
                if (CurrentEnough(comparisonVersion, targets[entry.Id])) pending.Remove(entry.Id);
            }
            // A local refresh cannot keep old executable offers valid. They need fresh online confirmation.
            var result = previous with { Installed = installed, Evidence = proofs, IsStale = true, FromCache = true,
                Complete = false, UpdateCheckComplete = false, PendingUpdates = pending,
                Updates = null, GitHubUpdates = null, GitHubPackages = null, PortableUpdates = null, ManagedPackages = null,
                InstalledSources = proofs.ToDictionary(p => p.Key, p => p.Value.Source) };
            if (saved is not null)
            {
                var rows = saved.Rows.Select(r => targets.ContainsKey(r.Id) ? r with { Version = installed.GetValueOrDefault(r.Id), Target = pending.GetValueOrDefault(r.Id) } : r).ToArray();
                await Task.Run(() => _inspectionCache!.WriteLocal(saved with { Rows = rows }), token).ConfigureAwait(false);
            }
            return result;
        }
        finally { _inspectionGate.Release(); }
    }
    internal static bool CurrentEnough(string? installed, string target) => installed is not null
        && (string.Equals(installed, target, StringComparison.Ordinal) || ProgramPortableUpdates.SameVersion(installed, target) || ProgramVersions.IsNewer(target, installed));
}
