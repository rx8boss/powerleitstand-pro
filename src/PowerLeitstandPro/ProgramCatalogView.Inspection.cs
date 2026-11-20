namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal async Task ApplyEntryCheckAsync(ProgramEntry entry, ProgramInstallation? proof, ProgramPackage? package, DateTime checkedUtc, CancellationToken token, ProgramInventory? checkedUpdates = null)
    {
        if (Service is not ProgramInstallService service) return;
        var latest = checkedUpdates?.Updates?.GetValueOrDefault(entry.Id)
            ?? (entry.Id == "windows-terminal" && proof?.Source == "Windows-App" ? null : checkedUpdates?.GitHubUpdates?.GetValueOrDefault(entry.Id));
        if (latest is not null) package = package is null
            ? new(entry.PackageId ?? entry.Id, null, "", checkedUtc, OnlineVersion: latest, CanInstall: false)
            : package with { OnlineVersion = latest };
        var previous = Inventory;
        var checkedInventory = await service.RecordEntryCheckAsync(previous, entry, proof, package, checkedUtc, token);
        if (!ReferenceEquals(previous, Inventory)) return;
        Inventory = checkedInventory;
        Selected.Remove(entry.Id); RenderTiles(); RefreshAvailability();
    }
    internal async Task RefreshRemovedProgramAsync(string id)
    {
        if (Service is not ProgramInstallService service || KnownEntries.FirstOrDefault(e => e.Id == id) is not { } entry) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (proof, _, _) = await service.InspectEntryAsync(entry, timeout.Token, localOnly: true);
        if (!ReferenceEquals(Service, service)) return;

        Dictionary<string, T>? WithoutEntry<T>(IReadOnlyDictionary<string, T>? values)
            => values?.Where(pair => pair.Key != id).ToDictionary(pair => pair.Key, pair => pair.Value);
        var installed = WithoutEntry(Inventory.Installed)!;
        var evidence = WithoutEntry(Inventory.Evidence) ?? [];
        var sources = WithoutEntry(Inventory.InstalledSources) ?? [];
        if (proof is not null)
        {
            evidence[id] = proof;
            sources[id] = proof.Source;
            if (!proof.MissingPortableFile) installed[id] = proof.Version;
        }
        if (!installed.ContainsKey(id)) ForgetInstallation(id);
        Inventory = Inventory with
        {
            Installed = installed, Evidence = evidence, InstalledSources = sources,
            Updates = WithoutEntry(Inventory.Updates), GitHubUpdates = WithoutEntry(Inventory.GitHubUpdates),
            GitHubPackages = WithoutEntry(Inventory.GitHubPackages), PortableUpdates = WithoutEntry(Inventory.PortableUpdates),
            ManagedPackages = WithoutEntry(Inventory.ManagedPackages), PendingUpdates = WithoutEntry(Inventory.PendingUpdates),
            UnverifiedInstalledIds = Inventory.UnverifiedInstalledIds?.Where(value => value != id).ToArray(),
            UnverifiedUpdateIds = Inventory.UnverifiedUpdateIds?.Where(value => value != id).ToArray(),
            TerminalMsixUpdate = id == "windows-terminal" ? null : Inventory.TerminalMsixUpdate
        };
        Selected.Remove(id);
        RenderTiles();
    }

    private async Task<ProgramInventory> InspectInventoryAsync(CancellationToken token, bool reuseRecent)
    {
        var service = Service; var previous = Inventory; var progress = service as IProgramInspectionProgress; bool listening = true;
        void Changed(ProgramInventory inventory) => Dispatcher.BeginInvoke(() =>
        {
            if (!listening || token.IsCancellationRequested || !ReferenceEquals(Service, service) || IsWorking || (!_refreshing && !_searching)) return;
            Inventory = PreserveLastCandidates(previous, inventory);
            ObserveCheckStatus(inventory);
            // Local records can be read while the online check continues; selection remains locked.
            RenderTiles();
        });
        if (progress is not null) progress.InventoryChanged += Changed;
        try
        {
            return reuseRecent && service is IProgramSearchInventory cached
                ? await cached.InspectForSearchAsync(token) : await service.InspectAsync(token);
        }
        finally { listening = false; if (progress is not null) progress.InventoryChanged -= Changed; }
    }
}
