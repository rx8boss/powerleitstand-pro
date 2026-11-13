using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace PowerControlProWpf;

internal sealed partial class ProgramInfoCard
{
    private readonly Grid _locationField;
    private string? _locationKey;
    private bool _metadataLoaded, _openingPublisher;
    private CancellationTokenSource? _publisherCancellation;
    private string _packageDetails = "";
    private ProgramInstallation? _eaEligibilityProof;
    private ProgramInstallation? _singleEntryEvidence;
    private ProgramInventory? _singleEntryUpdates;
    private DateTime? _singleEntryCheckedUtc;
    private bool _singleEntryChecking;
    private bool _eaEligible;
    private string PortableSourceDetails(PortableUpdate portable) => (portable.Id == "tukui-client"
        ? L("\nOffizieller Standalone-Download mit geprüfter Entwicklersignatur. Lokale Prüfsumme bindet genau dieses Paket; keine separat veröffentlichte Herstellerprüfsumme.", "\nOfficial standalone download with verified developer signature. A local checksum binds this exact package; no separately published publisher checksum.")
        : L("\nHerstellerquelle mit bestätigter Prüfsumme.", "\nPublisher source with verified checksum."))
        + L(" Austausch nur am erkannten Speicherort. Vorherige Programmdateien werden unter AppData gesichert; Einstellungen bleiben erhalten.", " Replacement only at the detected location. Previous executables are backed up under AppData; settings are preserved.") + "\n" + new Uri(portable.Url).Host;
    private ProgramPackage? ForDisplayedVariant(ProgramPackage? package, ProgramInstallation? proof = null)
    {
        proof ??= _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id);
        // WinGet describes Tukui's MSI; it cannot confirm a standalone ZIP release.
        return package is not null && package.PortableUpdate is null && package.VersionSource != "Tukui-Standalone" && _entry.Id == "tukui-client" && proof is { Source: "Portable" or "Portable-Setup" or "SelectedFile" }
            ? package with { Version = null, OnlineVersion = null, CanInstall = false, VersionSource = "WinGet-MSI" } : package;
    }
    private bool StorePending => !_view.Inventory.IsStale && ProgramVersions.StoreFamilies.ContainsKey(_entry.Id) && !_view.Inventory.StoreCheckComplete;
    private bool StoreKnown => _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id)?.Source == "Windows-App";
    private bool EaOpenAvailable
    {
        get
        {
            if (_singleEntryCheckedUtc is null && (_view.Inventory.IsStale || _view.Inventory.UnverifiedInstalledIds?.Contains(_entry.Id) == true)
                || _entry.Id != "extra-electronicarts-eadesktop" || CanUpdate()
                || !ProgramVersions.IsNewer(_singleEntryEvidence?.Version ?? _view.Inventory.Installed.GetValueOrDefault(_entry.Id), _confirmedOnline)) return false;
            var proof = _singleEntryEvidence ?? _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id);
            if (!ReferenceEquals(proof, _eaEligibilityProof))
            { _eaEligibilityProof = proof; _eaEligible = ProgramPublisherLauncher.CanOpenEa(_entry, proof); }
            return _eaEligible && System.IO.File.Exists(proof?.Path);
        }
    }

    internal void RefreshInventory()
    {
        if (_closed || _choosingFile || _teamSpeak is not null) return;
        if (_singleEntryCheckedUtc is { } entryCheck && !_view.Inventory.IsStale && _view.Inventory.CheckedUtc > entryCheck)
        { _singleEntryCheckedUtc = null; _singleEntryEvidence = null; _singleEntryUpdates = null; }
        RefreshVersionPresentation(); RefreshLocation();
        _sourceSummary.Text = SourceSummary(); _details.Text = Details() + _packageDetails + _descriptionDetails;
        if (_metadataLoaded)
        {
            if (_view.Inventory.HasUpdateNotice(_entry.Id) && !_view.Inventory.IsStale)
                _details.Text += L("\nGitHub-Version erkannt; automatischer Updateweg nicht bestätigt.", "\nGitHub version detected; automatic update route is not verified.");
            if (_resolvedPackage?.GitHubUpdate is null && _resolvedPackage?.PortableUpdate is null && _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id)?.Source is "Portable" or "Portable-Setup")
                _details.Text += L("\nPortable-Update nicht automatisch bestätigt. Herstellerseite verwenden; vorhandene Dateien bleiben erhalten.", "\nPortable update not automatically verified. Use the publisher website; existing files are preserved.");
        }
        if (StorePending) _details.Text += _view.Inventory.StoreError is { } error
            ? L("\nStore-Prüfung fehlgeschlagen: ", "\nStore check failed: ") + error
                + (StoreKnown ? L("\nLetzter bestätigter Paketstand wird angezeigt.", "\nShowing the last confirmed package state.") : "")
            : L("\nStore-Prüfung läuft noch.", "\nStore check is still running.");
        _availabilityNote.Text = AvailabilityNote();
        _availabilityNote.Visibility = _availabilityNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_metadataLoaded && StoreKnown && _confirmedOnline is null) _available.Text = TerminalStore
            ? L("Kein Update bestätigt", "No update verified") : L("Im Microsoft Store prüfen", "Check in Microsoft Store");
        if (_manualRow is not null) _manualRow.Visibility = _view.ShowManualMark(_entry.Id) ? Visibility.Visible : Visibility.Collapsed;
        if (_manualSwitch is not null) _manualSwitch.IsEnabled = _view.CanMark(_entry.Id);
    }
    private async Task RecheckSavedEntryAsync(ProgramInstallService service)
    {
        if (_closed || _singleEntryChecking) return;
        _singleEntryChecking = true; _leave.Stop(); _updateAction.IsEnabled = false;
        _singleEntryCheckedUtc = null; _singleEntryEvidence = null; _singleEntryUpdates = null;
        RefreshVersionPresentation();
        _updateAction.Content = new TextBlock { Text = L("Prüfe …", "Checking …") };
        _availabilityNote.Text = L("Dieses Programm wird einzeln geprüft …", "Checking this program …");
        _availabilityNote.Visibility = Visibility.Visible;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token); limit.CancelAfter(TimeSpan.FromSeconds(60));
            await _view.PauseBackgroundInspectionAsync();
            var checkedUpdates = await service.InspectUpdatesAsync([_entry], limit.Token);
            if (!checkedUpdates.HasUpdate(_entry.Id) && (checkedUpdates.Error is not null || checkedUpdates.OnlineError is not null))
                throw new System.IO.IOException(checkedUpdates.Error ?? checkedUpdates.OnlineError);
            if (_entry.Id == "tukui-client" && checkedUpdates.Evidence?.GetValueOrDefault(_entry.Id) is { } tukui && ProgramTukuiUpdates.Eligible(tukui))
            {
                _availabilityNote.Text = L("Tukui-Herstellerpaket wird geprüft …", "Checking Tukui publisher package …");
                IProgress<ProgramDownload> progress = new Progress<ProgramDownload>(p =>
                {
                    if (!_closed && _singleEntryChecking) _availabilityNote.Text = L("Tukui-Download zur Prüfung: ", "Tukui download for verification: ")
                        + (p.Percent is { } percent ? percent.ToString("0") + " %" : L("läuft …", "in progress …"));
                });
                checkedUpdates = await service.ProbeTukuiAsync(checkedUpdates, progress.Report, limit.Token);
            }
            var offer = await service.ResolveCheckedUpdateAsync(_entry, checkedUpdates, limit.Token);
            var installation = checkedUpdates.Evidence?.GetValueOrDefault(_entry.Id);
            bool terminalStore = _entry.Id == "windows-terminal" && installation?.Source == "Windows-App";
            var package = ForDisplayedVariant(offer ?? (terminalStore ? null : await service.ResolveAsync(_entry, limit.Token)), installation);
            if (offer is null && package is not null) package = package with { CanInstall = false };
            var result = (Installation: checkedUpdates.Evidence?.GetValueOrDefault(_entry.Id), Package: package, CheckedUtc: checkedUpdates.CheckedUtc ?? DateTime.UtcNow);
            if (_closed) return;
            await _view.ApplyEntryCheckAsync(_entry, result.Installation, result.Package, result.CheckedUtc, _cancel.Token, checkedUpdates);
            if (_closed) return;
            _singleEntryEvidence = result.Installation; _singleEntryCheckedUtc = result.CheckedUtc; _singleEntryUpdates = checkedUpdates;
            _resolvedPackage = result.Package;
            _confirmedOnline = ProgramVersions.ConfirmedVersion(offer?.Version ?? checkedUpdates.Updates?.GetValueOrDefault(_entry.Id)
                ?? (terminalStore ? null : checkedUpdates.GitHubUpdates?.GetValueOrDefault(_entry.Id) ?? result.Package?.OnlineVersion ?? result.Package?.Version));
            _available.Text = _confirmedOnline ?? L("Nicht ermittelbar", "Unavailable");
            _error.Visibility = Visibility.Collapsed;
            _view.VersionChecked(_entry.Id, _confirmedOnline);
            _view.Assets.RegisterIcon(_entry, result.Package?.IconUrl, result.Package?.IconWebsite);
            _ = _view.Assets.EnsureIconAsync(_entry);
            _packageDetails = L("\nEinzelprüfung: ", "\nSingle check: ") + result.CheckedUtc.ToLocalTime().ToString("g")
                + (result.Package?.VersionSource is { } source ? L("\nVersionsquelle: ", "\nVersion source: ") + source : "")
                + (result.Package?.CanInstall == false ? L("\nVersionsinformation · ausführbarer Updateweg nicht bestätigt", "\nVersion information · executable update route unconfirmed") : "");
            if (result.Package?.PortableUpdate is { } portable) _packageDetails += PortableSourceDetails(portable);
            RefreshInventory();
            _availabilityNote.Text = AvailabilityNote();
            _availabilityNote.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closed)
            {
                if (TukuiStandalone && _resolvedPackage is not null) _resolvedPackage = _resolvedPackage with { CanInstall = false, PortableUpdate = null };
                _availabilityNote.Text = L("Einzelprüfung fehlgeschlagen", "Single-program check failed");
                _error.Text = UiText.Error(error, _view.English); _error.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _singleEntryChecking = false;
            if (_closed && _entry.Id == "tukui-client") service.ClearTukuiPackage();
            if (!_closed) { RefreshVersionPresentation(); ResetIdle(); }
        }
    }
    private string AvailabilityNote()
    {
        if (TukuiStandalone && _resolvedPackage?.PortableUpdate is null)
            return L("„Erneut prüfen“ lädt das offizielle Standalone-Paket und prüft EXE-Version und Entwicklersignatur. Kein Wechsel auf MSI.", "“Check again” downloads the official standalone package and checks its EXE version and developer signature. No switch to MSI.");
        if (_singleEntryCheckedUtc is { } checkedUtc)
            return L("Einzelprüfung abgeschlossen", "Single check complete")
                + (SingleUpdateAvailable ? L(" · Update für dieses Programm bestätigt", " · update verified for this application")
                    : TerminalStore ? L(" · WinGet hat kein passendes Update bestätigt", " · WinGet did not verify a matching update")
                    : _singleEntryEvidence?.Source == "Windows-App" ? L(" · Updates im Microsoft Store prüfen", " · Check for updates in Microsoft Store")
                    : ProgramVersions.IsNewer(_singleEntryEvidence?.Version, _confirmedOnline)
                        ? L(" · Kein passender Updateweg bestätigt. Herstellerseite verwenden.", " · No matching update route verified. Use the publisher website.") : "")
                + " · " + checkedUtc.ToLocalTime().ToString("dd.MM. HH:mm");
        if (_view.Inventory.IsStale || _view.Inventory.PendingUpdates?.ContainsKey(_entry.Id) == true) return L("Gespeicherter Stand · vor Aktionen erneut prüfen", "Saved state · verify again before actions")
            + (_view.Inventory.CheckedUtc is { } stamp ? " · " + stamp.ToLocalTime().ToString("g") : "");
        string note = "";
        if (StorePending) note = _view.Inventory.StoreError is null ? L("Store-Prüfung läuft …", "Checking Store apps …")
            : StoreKnown ? L("Store-Prüfung fehlgeschlagen · letzter bestätigter Stand", "Store check failed · last confirmed state")
                : L("Store-Prüfung fehlgeschlagen · bitte erneut prüfen", "Store check failed · please check again");
        else if (TerminalStore) note = L("Paketversion aus Windows · Prüfung über WinGet", "Package version from Windows · checked through WinGet");
        else if (StoreKnown) note = L("Paketversion aus Windows · Updates über Microsoft Store", "Package version from Windows · updates through Microsoft Store");
        else if (_resolvedPackage?.ManagedPackage is { } managed) note = L("Paketversion · ", "Package version · ") + managed.Manager
            + (managed.NeedsPreparation ? L(" · Quellen zuerst vorbereiten", " · prepare sources first") : managed.IsUpdate ? L(" · Update auswählbar", " · update can be selected") : "");
        else if (_entry.Id == "vlc" && _resolvedPackage?.CanInstall == false && ProgramVersions.IsNewer(_view.Inventory.Installed.GetValueOrDefault(_entry.Id), _confirmedOnline))
            note = L("Passender VLC-Installer noch nicht bestätigt · erneut prüfen oder Herstellerseite öffnen", "Matching VLC installer is not yet verified · check again or open the publisher website");
        else if (_view.Inventory.HasUpdate(_entry.Id)) note = L("Update auswählbar", "Update can be selected");
        else if (EaOpenAvailable) note = L("Update über EA App · dort bei Bedarf neu starten", "Update through EA app · restart it there if needed");
        else if (_view.Inventory.HasUpdateNotice(_entry.Id)) note = L("Neue Version · Herstellerseite öffnen", "New version · open publisher page");
        else if (ProgramVersions.IsNewer(_view.Inventory.Installed.GetValueOrDefault(_entry.Id), _confirmedOnline))
            note = _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id)?.Source is "Portable" or "Portable-Setup"
                ? L("Neuere Version · portabler Austausch nicht bestätigt", "Newer version · portable replacement not verified")
                : L("Neuere Version · Updateweg noch nicht bestätigt. Bitte erneut prüfen.", "Newer version · update route not yet confirmed. Please check again.");
        if (_resolvedPackage?.PortableUpdate is not null)
        {
            note += L("\nHersteller-Download · Portable", "\nPublisher download · Portable");
            if (_entry.Id == "melonloader") note += L("\nVersion des Einrichtungsprogramms", "\nSetup program version");
        }
        else if (!StoreKnown && _resolvedPackage?.GitHubUpdate is null && _resolvedPackage?.ManagedPackage is null && _confirmedOnline is not null
            && _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id) is { WinGetCompatible: false } proof)
            note += proof.Source is "Portable" or "Portable-Setup"
                ? L("\nAutomatischer Austausch nicht bestätigt · Herstellerseite verwenden", "\nAutomatic replacement not verified · use publisher website")
                : L("\nAnderer Bezugsweg / andere Variante", "\nDifferent source / variant");
        return note.Trim();
    }
    private void RefreshLocation()
    {
        var locationProof = _singleEntryCheckedUtc is null ? _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id) : _singleEntryEvidence;
        string? location = locationProof?.Path;
        string? directory = ProgramLocation.DirectoryOf(location);
        string key = location + "|" + directory + "|" + StorePending + "|" + (_view.Inventory.StoreError is not null) + "|" + _view.CanChooseProgramFile;
        if (_locationKey == key) return; _locationKey = key;
        var value = (Grid)_locationField.Children[1]; value.Children.Clear();
        value.ColumnDefinitions.Clear(); value.ColumnDefinitions.Add(new()); value.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ((TextBlock)_locationField.Children[0]).Text = directory is not null ? L("Speicherort:", "Location:") : L("Programmdatei:", "Program executable:");
        var label = Text(directory is not null ? ProgramLocation.Shorten(directory)
            : StorePending ? _view.Inventory.StoreError is null ? L("Store wird geprüft …", "Checking Store …") : L("Prüfung fehlgeschlagen", "Check failed")
            : L("Noch nicht zugeordnet", "Not associated yet"), _view.Compact ? 13 : 14);
        label.VerticalAlignment = VerticalAlignment.Center; label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (directory is null) label.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedMuted"); else label.ToolTip = location;
        value.Children.Add(label);
        var button = Action(""); button.Name = directory is null ? "ProgramInfoChooseFile" : "ProgramInfoOpenFolder";
        button.Width = 34; button.Height = button.MinHeight = 28; button.Padding = new(0); button.Margin = new(7, 0, 0, 0);
        button.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,3 L1,1 L6,1 L8,3 L14,3 L14,11 L1,11 Z M1,4 L14,4"),
            Width = 13, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 1.2, Stroke = (Brush)_owner.FindResource("UnifiedText") };
        button.ToolTip = directory is not null ? L("Programmordner öffnen", "Open program folder") : L("Vorhandene Programmdatei auswählen und prüfen", "Select and verify an existing program executable");
        AutomationProperties.SetName(button, button.ToolTip.ToString());
        button.IsEnabled = directory is not null || !StorePending && _view.CanChooseProgramFile;
        if (directory is not null) button.Click += (_, _) =>
        {
            try
            {
                string current = ProgramLocation.DirectoryOf((_singleEntryEvidence ?? _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id))?.Path)
                    ?? throw new System.IO.DirectoryNotFoundException(L("Programmordner nicht mehr vorhanden.", "Program folder no longer exists."));
                Process.Start(DrivePathLauncher.CreateStartInfo(current));
            }
            catch (Exception e) { _error.Text = UiText.Error(e, _view.English); _error.Visibility = Visibility.Visible; }
        };
        else button.Click += async (_, _) => await ChooseProgramFileAsync(button);
        Grid.SetColumn(button, 1); value.Children.Add(button);
    }
    private async Task OpenEaAsync()
    {
        if (_closed || _openingPublisher || !_view.CanChooseProgramFile) return;
        using var action = new CancellationTokenSource(TimeSpan.FromSeconds(12)); _publisherCancellation = action;
        _openingPublisher = true; _versionAction.IsEnabled = false;
        try
        {
            var proof = await Task.Run(() => ProgramLocalInventory.Read([_entry], action.Token, includeManaged: false).GetValueOrDefault(_entry.Id), action.Token);
            if (_closed || !_view.CanChooseProgramFile) return;
            var start = ProgramPublisherLauncher.CreateEaStart(_entry, proof) ?? throw new System.IO.IOException(L("EA-Programmdatei konnte nicht bestätigt werden. Bitte erneut prüfen.", "EA executable could not be verified. Please check again."));
            ProgramPublisherLauncher.Launch(start); Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (ProgramPathResolver.ReadFailure(e) || e is InvalidOperationException)
        { if (!_closed) { _error.Text = UiText.Error(e, _view.English); _error.Visibility = Visibility.Visible; } }
        finally { _publisherCancellation = null; _openingPublisher = false; if (!_closed) RefreshVersionPresentation(); }
    }
}
