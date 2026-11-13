using System.Windows;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal IReadOnlyList<ProgramEntry> UpdateEntries => KnownEntries
        .Where(x => Inventory.Installed.ContainsKey(x.Id) && (Inventory.HasUpdate(x.Id) || UpdateTarget(x.Id) is not null)).ToArray();

    internal string? UpdateTarget(string id)
    {
        var known = Inventory.Updates?.GetValueOrDefault(id) ?? Inventory.PendingUpdates?.GetValueOrDefault(id) ?? Inventory.GitHubUpdates?.GetValueOrDefault(id);
        if (known is not null) return known;
        var proof = Inventory.Evidence?.GetValueOrDefault(id);
        if (proof?.Source == "Windows-App" || proof?.Source is "Scoop" or "Chocolatey" && proof.ManagedCopies is not { Count: 1 }) return null;
        var installed = proof?.ManagedCopies is { Count: 1 } copies ? copies[0].PackageVersion : Inventory.Installed.GetValueOrDefault(id);
        return _versionChecks.TryGetValue(id, out var checkedVersion) && checkedVersion.Complete
            && ProgramVersions.IsNewer(installed, checkedVersion.Version) ? checkedVersion.Version : null;
    }

    private void UpdateCategoryVisibility()
    {
        _tabs.Visibility = _search.Text.Trim().Length > 0 || HasLoadingOverlay ? Visibility.Collapsed : Visibility.Visible;
        if (_tabs.Visibility != Visibility.Visible) CloseAdditionalCategories();
    }

    internal async Task ChooseUpdatesAsync(IReadOnlySet<string>? scope = null)
    {
        if (IsWorking || HostBusy() || SelectionOpen || _refreshing || _searching) return;
        SelectionOpen = true; RefreshAvailability();
        try
        {
            await PauseBackgroundInspectionAsync();
            if (Service is not ProgramInstallService && (Inventory.IsStale || Inventory.PendingUpdates?.Count > 0)) await RefreshAsync();
            if ((Service is not ProgramInstallService && Inventory.IsStale) || HostBusy() || IsWorking || _refreshing || _searching) return;
        }
        finally { SelectionOpen = false; RefreshAvailability(); }
        var entries = UpdateEntries.Where(e => scope is null || scope.Contains(e.Id)).ToArray();
        if (entries.Length == 0) return;
        CloseInfo();
        var chosen = await ProgramUpdatesDialog.ShowAsync(this, entries);
        if (chosen is not { Length: > 0 }) return;
        await InstallEntriesAsync(chosen, updatesOnly: true);
    }
}
