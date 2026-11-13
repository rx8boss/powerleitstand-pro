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
