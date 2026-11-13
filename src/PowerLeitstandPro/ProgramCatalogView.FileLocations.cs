namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal bool CanChooseProgramFile => !IsWorking && !HostBusy() && !_refreshing && !_searching && !SelectionOpen;
    internal async Task RememberProgramFileAsync(ProgramEntry entry, string path)
    {
        if (!CanChooseProgramFile || !KnownEntries.Any(x => ReferenceEquals(x, entry)))
            throw new InvalidOperationException(L("Programmzuordnung derzeit gesperrt.", "Program association is currently locked."));
        SelectionOpen = true;
        await PauseBackgroundInspectionAsync();
        try
        {
        if (entry.Id == "vlc" && VlcPortableUpdates.Identify(path, requireMarker: false) is not null
            && VlcPortableUpdates.Marker(System.IO.Path.GetDirectoryName(path)!) is null
            && VlcPortableUpdates.CanAdopt(Inventory.Evidence?.GetValueOrDefault(entry.Id), path))
        {
            string question = L("Diese eigenständige VLC-Kopie ausdrücklich als VLC-ZIP zuordnen? Vor einem Update wird der vollständige Bestand gegen das alte Originalpaket geprüft. VLC-Einstellungen werden nicht verschoben.",
                "Explicitly associate this standalone copy as VLC ZIP? Before updating, its complete contents are checked against the old official package. VLC settings will not be relocated.");
            bool accepted = ConfirmForTests?.Invoke(question) ?? ThemedMessageBox.Show(System.Windows.Window.GetWindow(this), question, "PowerLeitstand PRO", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
            if (!accepted) return;
            await Task.Run(() => VlcPortableUpdates.ConfirmZip(path));
        }
        var selected = await Task.Run(() => _fileAssociations.Save(entry, path));
        var evidence = Inventory.Evidence?.ToDictionary(x => x.Key, x => x.Value) ?? [];
        var installed = Inventory.Installed.ToDictionary(x => x.Key, x => x.Value);
        var known = evidence.GetValueOrDefault(entry.Id);
        var registeredVlc = entry.Id == "vlc" && Service is ProgramInstallService vlcService
            ? ProgramVlcReinstallation.FindPath(path, await vlcService.VlcCopiesAsync(CancellationToken.None)) : null;
        // Registered/Store package versions and installation channels keep their independent evidence.
        var proof = registeredVlc is not null ? new ProgramInstallation(registeredVlc.Version, "Registry", registeredVlc.Executable)
            : selected.Variant == VlcPortableUpdates.Variant ? VlcPortableUpdates.CanAdopt(known, selected.Path!) ? selected : known!
            : known is not null && known.Source != "SelectedFile" ? known with { Path = selected.Path, LocationEvidence = null,
            Version = known.Version ?? (known.Source == "Registry" ? selected.Version : null) } : selected;
        evidence[entry.Id] = proof; installed[entry.Id] = proof.Version;
        static Dictionary<string, T>? Without<T>(IReadOnlyDictionary<string, T>? values, string id)
            => values?.Where(x => !x.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => x.Value);
        Inventory = Inventory with { Installed = installed, Evidence = evidence,
            InstalledSources = evidence.ToDictionary(x => x.Key, x => x.Value.Source),
            Updates = Without(Inventory.Updates, entry.Id), PortableUpdates = Without(Inventory.PortableUpdates, entry.Id),
            GitHubPackages = Without(Inventory.GitHubPackages, entry.Id), GitHubUpdates = Without(Inventory.GitHubUpdates, entry.Id),
            PendingUpdates = Without(Inventory.PendingUpdates, entry.Id), UpdateCheckComplete = false, FromCache = false };
        if (Service is ProgramInstallService service) service.SelectedFileChanged(entry, proof);
        Selected.Remove(entry.Id); RenderTiles();
        }
        finally { SelectionOpen = false; RefreshAvailability(); }
    }
    internal void ReopenInfo(ProgramEntry entry, System.Windows.FrameworkElement anchor)
    {
        CloseInfo();
        if (IsVisible) _info = ProgramInfoCard.Open(this, entry, anchor, false);
    }
}

internal sealed partial class ProgramInstallService
{
    internal void SelectedFileChanged(ProgramEntry entry, ProgramInstallation proof)
    {
        InvalidateSaved(entry.Id);
        _recentInventory = null;
        var evidence = _lastEvidence.ToDictionary(x => x.Key, x => x.Value); evidence[entry.Id] = proof; _lastEvidence = evidence;
        if (!proof.WinGetCompatible) _installedPackageIds.Remove(entry.Id);
    }
}
