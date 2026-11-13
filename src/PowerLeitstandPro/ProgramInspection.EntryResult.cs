namespace PowerControlProWpf;

internal sealed partial class ProgramInstallService
{
    // Store display data only; a single-program check never authorizes an executable offer.
    internal async Task<ProgramInventory> RecordEntryCheckAsync(ProgramInventory previous, ProgramEntry entry, ProgramInstallation? proof, ProgramPackage? package, DateTime checkedUtc, CancellationToken token)
    {
        if (!Approved(entry) || proof is null || proof.ManagedCopies is { Count: > 1 } || proof.LocationEvidence?.Alternatives is { Count: > 1 }) return previous;
        await _inspectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var installed = previous.Installed.ToDictionary(p => p.Key, p => p.Value);
            var evidence = previous.Evidence?.ToDictionary(p => p.Key, p => p.Value) ?? [];
            var pending = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hints in new[] { previous.Updates, previous.GitHubUpdates, previous.PendingUpdates })
                if (hints is not null) foreach (var pair in hints) pending[pair.Key] = pair.Value;
            installed[entry.Id] = proof.Version; evidence[entry.Id] = proof;
            string? version = proof.ManagedCopies is { Count: 1 } copies ? copies[0].PackageVersion : proof.Version;
            string? target = package?.OnlineVersion ?? package?.Version;
            if (ProgramVersions.IsNewer(version, target) || package?.ManagedPackage?.IsUpdate == true && target is not null) pending[entry.Id] = target!;
            else if (pending.TryGetValue(entry.Id, out var old) && CurrentEnough(version, old)) pending.Remove(entry.Id);
            var saved = _inspectionCache?.Read(KnownEntries, DateTime.UtcNow);
            if (saved is not null && saved.CheckedUtc <= checkedUtc)
            {
                var rows = saved.Rows.Select(r => r.Id == entry.Id ? r with { Version = proof.Version, Source = proof.Source, Path = proof.Path,
                    Variant = proof.Variant, Identity = ProgramInspectionCache.Identity(proof), Target = pending.GetValueOrDefault(entry.Id) } : r).ToArray();
                if (!await Task.Run(() => _inspectionCache!.WriteLocal(saved with { Rows = rows }), token).ConfigureAwait(false))
                    throw new System.IO.IOException("Prüfstand konnte nicht gespeichert werden. / Could not save the checked state.");
            }
            return previous with { Installed = installed, Evidence = evidence, InstalledSources = evidence.ToDictionary(p => p.Key, p => p.Value.Source),
                Complete = false, IsStale = true, FromCache = true, UpdateCheckComplete = false, PendingUpdates = pending,
                Updates = null, GitHubUpdates = null, GitHubPackages = null, PortableUpdates = null, ManagedPackages = null };
        }
        finally { _inspectionGate.Release(); }
    }
}
