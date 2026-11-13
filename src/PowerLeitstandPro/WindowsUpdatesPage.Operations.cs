using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Control = System.Windows.Controls.Control;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerControlProWpf;

public partial class WindowsUpdatesPage
{
    private readonly List<WindowsUpdateItem> _software = [], _drivers = [];
    private readonly HashSet<string> _selected = [], _stale = [];
    private readonly Dictionary<string, (int Code, string Error)> _results = [];
    private readonly List<CheckBox> _selectionBoxes = [];
    private readonly List<(CheckBox Box, List<WindowsUpdateItem> Items)> _groupSelectors = [];
    private readonly Dictionary<string, TextBlock> _installRows = [];
    private int _installStage;
    private bool _softwareChecked, _driversChecked, _working, _stopping, _partial, _rebootRequired;
    private bool _installOperation, _terminalReceived, _searchOperation;
    private long _operationVersion;
    private string _phase = "", _failure = "", _version = "Windows";
    private DateTime? _lastCheck;
    private CancellationTokenSource? _operationCancellation;
    internal IWindowsUpdateService Service { get; set; } = new WindowsUpdateService();
    internal Func<bool> HostBusy { get; set; } = () => false;
    internal Action<bool>? WorkStateChanged { get; set; }
    internal Action<string>? WriteLog { get; set; }
    internal Action<AppLogError>? WriteErrorLog { get; set; }
    internal Func<string, bool>? ConfirmForTests { get; set; }
    internal bool IsWorking => _working;
    internal IReadOnlyCollection<string> SelectedKeys => _selected;
    internal IReadOnlyList<WindowsUpdateItem> SoftwareItems => _software;

    private string L(string de, string en) => _english ? en : de;
    private void InitializeOperations()
    {
        SearchUpdatesButton.Click += async (_, _) => await SearchAsync(false);
        CheckDriversButton.Click += async (_, _) => await SearchAsync(true);
        InstallUpdatesButton.Click += async (_, _) => await InstallAsync();
        CancelOperationButton.Click += (_, _) => CancelOperation();
        MinimizeOperationButton.Click += (_, _) => CollapseInstallDialog();
        CollapseOperationButton.Click += (_, _) => CollapseInstallDialog();
        ReopenOperationButton.Click += (_, _) => SetInstallDialog(open: true);
        OpenWindowsUpdateButton.Click += (_, _) =>
        {
            try { OpenExternalLink(new Uri("ms-settings:windowsupdate")); }
            catch (Exception error) { WriteLog?.Invoke(UiText.Error(error, _english)); }
        };
        OperationSurface.IsVisibleChanged += (_, _) => ProgramOverlayChanged?.Invoke();
        OperationListScroll.ScrollChanged += (_, _) => RefreshInstallScrollHint();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = Convert.ToInt32(key?.GetValue("CurrentBuildNumber") ?? "0");
            if (build > 0) _version = ((build >= 22000 ? "Windows 11" : "Windows 10") + " " + key?.GetValue("DisplayVersion")).TrimEnd();
        }
        catch { _version = "Windows"; }
        RefreshOperationLanguage();
    }

    internal void ApplyProgressPalette(ThemePalette palette)
    {
        OperationProgress.ApplyPalette(palette); SearchProgress.ApplyPalette(palette);
        _manufacturerPalette = palette; _manufacturerView?.SetPalette(palette); _programView?.SetPalette(palette);
    }

    private void RefreshOperationLanguage()
    {
        if (SoftwareRows is null) return;
        UpdatesWindowsVersion.Text = _version;
        UpdatesLastCheck.Text = L("Letzte Prüfung: ", "Last check: ") + (_lastCheck?.ToString("g") ?? "—");
        UpdatesEmptyState.Text = _working && (_phase is "search" or "search-all") ? L("Windows-Updates werden gesucht …", "Checking Windows updates …")
            : _softwareChecked ? L("Keine verfügbaren Windows-Updates gefunden.", "No available Windows updates found.") : L("Noch keine Prüfung durchgeführt.", "No check has been performed yet.");
        DriversEmptyState.Text = _working && (_phase is "drivers" or "search-all") ? L("Angebotene Treiber werden gesucht …", "Checking offered drivers …")
            : _driversChecked ? L("Keine angebotenen Treiber gefunden.", "No offered drivers found.") : L("Noch keine Prüfung durchgeführt.", "No check has been performed yet.");
        UpdatesEmptyState.Visibility = _software.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DriversEmptyState.Visibility = _drivers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ManufacturersButton.ToolTip = L("Hersteller-Software anzeigen", "Show manufacturer software");
        ProgramsButton.ToolTip = L("Programme auswählen und installieren", "Select and install programs");
        PrivacyButton.ToolTip = L("O&O: Lizenz bestätigen, herunterladen und öffnen", "O&O: accept license, download and open");
        _selectionBoxes.Clear(); _groupSelectors.Clear(); BuildRows(SoftwareRows, _software); BuildRows(DriverRows, _drivers);
        RefreshProgressText(); RefreshAvailability();
        System.Windows.Automation.AutomationProperties.SetName(OperationProgress, L("Windows-Update-Fortschritt", "Windows update progress"));
    }

    private void BuildRows(StackPanel panel, List<WindowsUpdateItem> items)
    {
        panel.Children.Clear(); if (items.Count == 0) return;
        Grid Row()
        {
            var row = new Grid { MinHeight = IsCompactLayout ? 24 : 34 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(38) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(5, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1.5, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) }); return row;
        }
        void Text(Grid grid, string value, int column, bool heading = false)
        {
            var text = new TextBlock { Text = value, FontSize = IsCompactLayout ? 11.5 : 12, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, IsCompactLayout ? 2 : 3, 6, IsCompactLayout ? 2 : 3), TextWrapping = TextWrapping.Wrap, ToolTip = value,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            Grid.SetColumn(text, column); grid.Children.Add(text);
        }
        var header = Row(); header.SetResourceReference(Panel.BackgroundProperty, "StandardActionButtonBrush");
        var eligible = items.Where(x => !x.RequiresUserInput && !_stale.Contains(x.Key)).ToArray();
        var all = new CheckBox { Style = (Style)FindResource("UpdateSelection"), IsThreeState = true,
            IsChecked = eligible.Length == 0 || eligible.All(x => !_selected.Contains(x.Key)) ? false : eligible.All(x => _selected.Contains(x.Key)) ? true : null,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !_working && !HostBusy() && eligible.Length > 0, ToolTip = L("Alle verfügbaren Einträge auswählen", "Select all available items") };
        System.Windows.Automation.AutomationProperties.SetName(all, L("Alle verfügbaren Einträge auswählen", "Select all available items"));
        all.Click += (_, _) => { bool select = eligible.Any(x => !_selected.Contains(x.Key)); foreach (var item in eligible) { if (select) _selected.Add(item.Key); else _selected.Remove(item.Key); } RefreshOperationLanguage(); };
        _groupSelectors.Add((all, items));
        header.Children.Add(all);
        Text(header, items[0].Driver ? L("Treiber", "Driver") : "Update", 1, true);
        Text(header, L("Typ", "Type"), 2, true); Text(header, L("Größe", "Size"), 3, true); Text(header, "Status", 4, true); panel.Children.Add(header);
        foreach (var item in items)
        {
            var row = Row(); var box = new CheckBox { IsChecked = _selected.Contains(item.Key), Tag = item.Key,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = item.Detail };
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.Style = (Style)FindResource("UpdateSelection");
            System.Windows.Automation.AutomationProperties.SetName(box, item.Title);
            box.Checked += (_, _) => { _selected.Add(item.Key); RefreshAvailability(); };
            box.Unchecked += (_, _) => { _selected.Remove(item.Key); RefreshAvailability(); };
            _selectionBoxes.Add(box); row.Children.Add(box);
            Text(row, item.Title, 1); Text(row, item.Category, 2); Text(row, item.SizeBytes > 0 ? (item.SizeBytes / 1024 / 1024).ToString("0.#") + " MB" : "—", 3);
            Text(row, ItemStatus(item), 4);
            var border = new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) };
            border.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush"); panel.Children.Add(border);
        }
    }

    private string ItemStatus(WindowsUpdateItem item)
    {
        if (_results.TryGetValue(item.Key, out var result))
            return result.Code == 2 ? L("Installiert", "Installed") : L("Nicht vollständig installiert", "Not fully installed") + " · " + result.Error;
        if (_stale.Contains(item.Key)) return L("Erneut prüfen", "Check again");
        if (item.RequiresUserInput) return L("Windows-Einstellungen verwenden", "Use Windows Settings");
        if (item.Optional) return L("Optional", "Optional");
        return item.Downloaded ? L("Heruntergeladen", "Downloaded") : L("Verfügbar", "Available");
    }

    internal void RefreshAvailability()
    {
        if (SearchUpdatesButton is null) return;
        var free = !_working && !HostBusy();
        SearchUpdatesButton.IsEnabled = free;
        CheckDriversButton.IsEnabled = free;
        OpenStartupButton.IsEnabled = free;
        ManufacturersButton.IsEnabled = ProgramsButton.IsEnabled = true; PrivacyButton.IsEnabled = free;
        _programView?.RefreshAvailability();
        UpdateWinGetCaption();
        _manufacturerView?.RefreshAvailability();
        var activeItems = _software.Concat(_drivers);
        InstallUpdatesButton.IsEnabled = free && activeItems.Any(x => _selected.Contains(x.Key) && !_stale.Contains(x.Key) && !x.RequiresUserInput);
        CancelOperationButton.IsEnabled = _installOperation && !_stopping && !_terminalReceived;
        SetAction(CancelOperationButton, _stopping ? L("Beenden angefordert…", "Stop requested…") : L("Vorgang beenden", "Stop operation"), "M 5,5 L 19,19 M 19,5 L 5,19");
        CancelSearchButton.IsEnabled = _working && _searchOperation && !_stopping;
        CancelSearchButton.Content = _stopping ? L("Suche wird beendet …", "Stopping search …") : L("Suche beenden", "Stop search");
        foreach (var box in _selectionBoxes)
        {
            var item = _software.Concat(_drivers).FirstOrDefault(x => x.Key == (string)box.Tag);
            box.IsEnabled = free && item is not null && !item.RequiresUserInput && !_stale.Contains(item.Key);
        }
        foreach (var (box, items) in _groupSelectors)
        {
            var eligible = items.Where(x => !x.RequiresUserInput && !_stale.Contains(x.Key)).ToArray();
            box.IsEnabled = free && eligible.Length > 0;
            box.IsChecked = eligible.Length == 0 || eligible.All(x => !_selected.Contains(x.Key)) ? false : eligible.All(x => _selected.Contains(x.Key)) ? true : null;
        }
        QueueFirstOpenCheck();
    }

    private bool Begin(string phase, WindowsUpdateItem[]? installItems = null)
    {
        if (_working || HostBusy()) return false;
        bool install = installItems is not null;
        _working = true; _stopping = false; _failure = ""; _phase = phase;
        _installOperation = install; _searchOperation = !install; _terminalReceived = false; _operationVersion++;
        _operationCancellation = new CancellationTokenSource();
        _installRows.Clear();
        OperationRows.Children.Clear();
        SetInstallDialog(open: install);
        SearchSurface.Visibility = install ? Visibility.Collapsed : Visibility.Visible;
        if (installItems is not null)
        {
            _installStage = 0;
            OperationListHint.Visibility = Visibility.Collapsed;
            OperationListScroll.ScrollToTop();
            foreach (var item in installItems)
            {
                _results.Remove(item.Key);
                var row = new DockPanel { Margin = new Thickness(7, 4, 7, 4) };
                var status = new TextBlock { Margin = new Thickness(10, 0, 0, 0), MaxWidth = 145, TextWrapping = TextWrapping.Wrap,
                    FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(status, Dock.Right); row.Children.Add(status);
                var title = new TextBlock { Text = item.Title, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                row.Children.Add(title);
                var surface = new Border { Child = row, Margin = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(3) };
                if (OperationRows.Children.Count % 2 != 0)
                    surface.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                OperationRows.Children.Add(surface);
                _installRows.Add(item.Key, status);
            }
            OperationProgress.IsIndeterminate = true; OperationProgress.ShowPercentage = false;
            OperationProgress.Value = 0; OperationProgress.IsRunning = true;
            OperationProgress.Visibility = CancelOperationButton.Visibility = Visibility.Visible;
        }
        else
        {
            SoftwareSearchStatus.Visibility = phase == "drivers" ? Visibility.Collapsed : Visibility.Visible;
            DriverSearchStatus.Visibility = phase == "search" ? Visibility.Collapsed : Visibility.Visible;
            SearchStatuses.Columns = phase == "search-all" ? 2 : 1;
            DriverSearchStatus.Margin = phase == "search-all" ? new Thickness(4, 0, 0, 0) : new Thickness(0);
            SoftwareSearchStatus.Margin = phase == "search-all" ? new Thickness(0, 0, 4, 0) : new Thickness(0);
            SearchRing.Visibility = SearchStatuses.Visibility = SearchProgress.Visibility = CancelSearchButton.Visibility = Visibility.Visible;
            SearchProgress.IsRunning = true;
        }
        WorkStateChanged?.Invoke(true); RefreshOperationLanguage();
        if (!install)
            Dispatcher.BeginInvoke(() =>
            {
                if (!SearchSurface.IsVisible) return;
                if (phase == "search-all") PageScroll.ScrollToTop();
                else SearchSurface.BringIntoView();
            });
        return true;
    }

    private void End()
    {
        bool completedInstall = _installOperation;
        OperationProgress.IsRunning = SearchProgress.IsRunning = false;
        _working = false; _stopping = false; _installOperation = false;
        OperationProgress.Visibility = CancelOperationButton.Visibility = Visibility.Collapsed;
        if (completedInstall) SetInstallDialog(open: true);
        SearchRing.Visibility = SearchStatuses.Visibility = SearchProgress.Visibility = CancelSearchButton.Visibility = Visibility.Collapsed;
        if (_phase == "searched" && !_partial) SearchSurface.Visibility = Visibility.Collapsed;
        _operationCancellation?.Dispose(); _operationCancellation = null;
        WorkStateChanged?.Invoke(false); RefreshOperationLanguage();
    }

    internal void CancelOperation()
    {
        if (!_working || _stopping || _terminalReceived) return;
        _stopping = true; _operationCancellation?.Cancel(); RefreshAvailability(); RefreshProgressText();
    }

    private void RefreshProgressText()
    {
        OperationText.Text = _stopping ? L("Beenden angefordert · auf Windows-Abschluss warten… Bereits installierte Updates bleiben erhalten.", "Stop requested · waiting for Windows to finish… Installed updates are retained.") : _phase switch
        {
            "prepare" => L("Installationsauftrag wird vorbereitet…", "Preparing update installation…"),
            "validate" => L("Ausgewählte Updates werden vor der Installation geprüft…", "Checking selected updates before installation…"),
            "download" => L("Ausgewählte Updates werden heruntergeladen…", "Downloading selected updates…"),
            "install" => L("Ausgewählte Updates werden installiert…", "Installing selected updates…"),
            "searched" => _partial ? L("Prüfung unvollständig. Vor Installation erneut prüfen.", "Partial check. Check again before installing.") : L("Prüfung abgeschlossen · gewünschte Einträge auswählen.", "Check complete · select the desired entries."),
            "done" => L("Vorgang abgeschlossen.", "Operation completed."),
            "partial" => L("Nicht alle Updates wurden installiert. Einzelergebnisse prüfen.", "Not all updates were installed. Check individual results."),
            "cancelled" => L("Vorgang beendet. Bereits installierte Updates bleiben erhalten.", "Operation stopped. Installed updates are retained."),
            "error" => L("Vorgang fehlgeschlagen: ", "Operation failed: ") + _failure,
            _ => ""
        };
        if (_rebootRequired && _phase is "done" or "partial" or "cancelled" or "error")
            OperationText.Text += L(" Windows benötigt einen Neustart. Es erfolgt kein automatischer Neustart.", " Windows requires a restart. There will be no automatic restart.");
        RefreshInstallPresentation();
        RefreshSearchPresentation();
    }

    private void SetInstallDialog(bool open, bool reopen = false)
    {
        OperationSurface.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ReopenOperationButton.Visibility = reopen ? Visibility.Visible : Visibility.Collapsed;
        ScrollHint.Visibility = reopen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CollapseInstallDialog()
    {
        SetInstallDialog(open: false, reopen: _installOperation);
        if (_installOperation) ReopenOperationButton.Focus();
    }

    private void RefreshInstallPresentation()
    {
        if (_installRows.Count == 0) return;
        bool ended = !_installOperation;
        bool final = ended || _terminalReceived;
        bool success = _phase == "done";
        bool failed = _phase is "error" or "partial";
        bool windowsAborted = _phase == "error" && _failure.Contains("0x80242008", StringComparison.OrdinalIgnoreCase);
        bool hasIndividualResults = _installRows.Keys.Any(_results.ContainsKey);
        bool missingResults = final && !hasIndividualResults;
        string[] names = [L("Prüfen", "Check"), "Download", L("Installation", "Install"), L("Abschluss", "Finish")];
        TextBlock[] steps = [ValidateOperationStep, DownloadOperationStep, InstallOperationStep, FinishOperationStep];
        int activeStep = Math.Max(0, _installStage - 1);
        if (_phase == "partial") activeStep = 3;
        for (int index = 0; index < steps.Length; index++)
        {
            bool done = success || index < activeStep;
            bool failure = failed && index == activeStep;
            bool active = !final && index == activeStep;
            steps[index].Text = names[index] + (done ? " ✓" : failure ? " ×" : active ? " …" : " ·");
            steps[index].SetResourceReference(TextBlock.ForegroundProperty,
                done ? "ReadyBrush" : failure ? "DangerBorderBrush" : active ? "AccentBrush" : "SubtleTextBrush");
            if (steps[index].Parent is Border tile)
            {
                tile.SetResourceReference(Border.BackgroundProperty, active ? "StandardActionButtonBrush" : "InputBrush");
                tile.SetResourceReference(Border.BorderBrushProperty,
                    done ? "ReadyBrush" : failure ? "DangerBorderBrush" : active ? "AccentBrush" : "InputBorderBrush");
            }
        }
        OperationTitle.Text = windowsAborted || _phase == "cancelled" ? L("Installation abgebrochen", "Installation cancelled")
            : final ? L("Windows-Update-Ergebnis", "Windows update result")
            : L("Windows-Updates installieren", "Install Windows updates");
        OperationSummary.Text = _installRows.Count + L(" ausgewählt", " selected") + " · " +
            (missingResults ? L("Einzelstatus offen", "Individual status unknown")
                : final ? L("Einzelergebnisse", "Individual results") : names[activeStep]);
        if (windowsAborted)
        {
            OperationText.Text = L("Windows hat die Installation abgebrochen.", "Windows cancelled the installation.");
            if (_rebootRequired) OperationText.Text += L(" Ein Neustart ist erforderlich.", " A restart is required.");
        }
        if (missingResults)
            OperationText.Text += "\n" + L("Windows hat keine Einzelergebnisse gemeldet.", "Windows did not report individual results.");
        OperationMessage.BorderThickness = failed ? new Thickness(1) : new Thickness(0);
        OperationMessage.Padding = failed ? new Thickness(9, 6, 9, 6) : new Thickness(0);
        OperationMessage.CornerRadius = new CornerRadius(6);
        OperationMessage.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        OperationMessage.SetResourceReference(Border.BackgroundProperty, failed ? "InputBrush" : "StandardReferenceStatusBrush");
        OperationList.Visibility = missingResults ? Visibility.Collapsed : Visibility.Visible;
        RefreshInstallScrollHint();
        OperationSteps.Visibility = missingResults ? Visibility.Collapsed : Visibility.Visible;
        OperationDetails.Visibility = windowsAborted ? Visibility.Visible : Visibility.Collapsed;
        OperationErrorLabel.Text = L("Fehlercode", "Error code");
        OperationErrorCode.Text = "0x80242008";
        OpenWindowsUpdateButton.Content = L("Windows Update öffnen", "Open Windows Update");
        OpenWindowsUpdateButton.Visibility = ended && (failed || _phase == "cancelled") ? Visibility.Visible : Visibility.Collapsed;
        CollapseOperationButton.Content = ended ? L("Schließen", "Close") : L("Einklappen", "Collapse");
        CollapseOperationButton.IsEnabled = true;
        MinimizeOperationButton.Visibility = ended ? Visibility.Collapsed : Visibility.Visible;
        MinimizeOperationButton.ToolTip = L("Einklappen", "Collapse");
        System.Windows.Automation.AutomationProperties.SetName(MinimizeOperationButton, (string)MinimizeOperationButton.ToolTip);
        ReopenOperationButton.Content = L("Vorgang anzeigen", "Show operation");
        string activeRow = _phase switch
        {
            "validate" => L("Wird geprüft", "Checking"),
            "download" => L("Download läuft", "Downloading"),
            "install" => L("Wird installiert", "Installing"),
            _ => L("Wartet auf Prüfung", "Waiting for check")
        };
        foreach (var (key, status) in _installRows)
        {
            bool reported = _results.TryGetValue(key, out var result);
            status.Text = reported ? result.Code == 2 ? L("Installiert", "Installed")
                : L("Nicht vollständig installiert", "Not fully installed") + (string.IsNullOrWhiteSpace(result.Error) ? "" : " · " + result.Error)
                : final ? L("Kein Einzelergebnis", "No individual result")
                : _stopping ? L("Warte auf Windows …", "Waiting for Windows …")
                : activeRow;
            status.SetResourceReference(TextBlock.ForegroundProperty, reported
                ? result.Code == 2 ? "ReadyBrush" : "DangerBorderBrush"
                : final ? "SubtleTextBrush" : "AccentBrush");
        }
    }

    private void RefreshInstallScrollHint()
    {
        OperationListHint.Visibility = OperationListScroll.ScrollableHeight > 1 ? Visibility.Visible : Visibility.Collapsed;
        OperationListHint.Text = OperationListScroll.VerticalOffset >= OperationListScroll.ScrollableHeight - 1
            ? L("↑ Für vorherige Updates scrollen", "↑ Scroll for previous updates")
            : L("↓ Für weitere Updates scrollen", "↓ Scroll for more updates");
    }

    internal async Task SearchAsync(bool? drivers)
    {
        if (!Begin(drivers is null ? "search-all" : drivers == true ? "drivers" : "search")) return;
        try
        {
            var result = await Service.SearchAsync(drivers, _operationCancellation!.Token);
            _operationCancellation.Token.ThrowIfCancellationRequested();
            if (drivers != true)
            {
                ReplaceSearchResults(_software, result, drivers: false);
                _softwareChecked = true; _lastCheck = DateTime.Now;
            }
            if (drivers != false)
            {
                ReplaceSearchResults(_drivers, result, drivers: true);
                _driversChecked = true;
            }
            _partial = result.Partial || _stale.Count > 0; _phase = "searched";
        }
        catch (OperationCanceledException e) { if (_operationCancellation?.IsCancellationRequested == true) _phase = "cancelled"; else Failure(e); }
        catch (Exception exception) { Failure(exception); }
        finally { End(); }
    }

    private void ReplaceSearchResults(List<WindowsUpdateItem> list, WindowsUpdateSearch result, bool drivers)
    {
        foreach (var old in list) { _selected.Remove(old.Key); _stale.Remove(old.Key); _results.Remove(old.Key); }
        list.Clear(); list.AddRange(result.Items.Where(item => item.Driver == drivers));
        if (result.Partial) foreach (var item in list) _stale.Add(item.Key);
        WriteLog?.Invoke((drivers ? L("Treiberprüfung: ", "Driver check: ") : L("Windows-Update-Prüfung: ", "Windows update check: "))
            + list.Count + L(" Einträge", " entries"));
    }

    internal void CancelSearchOnClose()
    {
        _firstOpenCheckPending = false;
        if (_searchOperation) _operationCancellation?.Cancel();
    }

    internal async Task InstallAsync()
    {
        if (_working || HostBusy()) return;
        var activeItems = _software.Concat(_drivers);
        var chosen = activeItems.Where(x => _selected.Contains(x.Key) && !_stale.Contains(x.Key) && !x.RequiresUserInput).ToArray();
        if (chosen.Length == 0) return;
        if (!ConfirmInstall(chosen) || !Begin("prepare", chosen)) return;
        foreach (var item in _software.Concat(_drivers)) _stale.Add(item.Key);
        foreach (var item in chosen) _selected.Remove(item.Key);
        await RunAsync(new("install", chosen.Select(x => new WindowsUpdateSelection(x.Id, x.Revision, x.EulaHash)).ToArray()));
    }

    private async Task RunAsync(WindowsUpdateRequest request)
    {
        var cancellation = _operationCancellation!;
        long version = _operationVersion;
        try
        {
            await Service.RunElevatedAsync(request, value => Dispatcher.Invoke(() =>
            {
                if (_working && version == _operationVersion && ReferenceEquals(_operationCancellation, cancellation)) Receive(value);
            }), cancellation.Token);
        }
        catch (OperationCanceledException e) { if (_operationCancellation?.IsCancellationRequested == true) _phase = "cancelled"; else Failure(e); }
        catch (Exception exception) { Failure(exception); }
        finally { End(); }
    }

    private void Receive(WindowsUpdateEvent value)
    {
        if (!_installOperation || _terminalReceived) return;
        if (value.Phase == "item" && value.Key is not null)
        {
            _results[value.Key] = (value.Code ?? 0, value.Text);
            string title = _software.Concat(_drivers).FirstOrDefault(x => x.Key == value.Key)?.Title ?? value.Key;
            if (value.Code == 4 && WriteErrorLog is not null) WriteErrorLog(new(title, "Windows Update", value.Text, "WUA.ResultCode=4"));
            else WriteLog?.Invoke(title + ": " + value.Code + " · " + value.Text);
            if (value.Code == 2)
            {
                _software.RemoveAll(x => x.Key == value.Key); _drivers.RemoveAll(x => x.Key == value.Key);
                _selected.Remove(value.Key); _stale.Remove(value.Key);
            }
        }
        else if (value.Phase == "error")
        { _terminalReceived = true; _phase = "error"; _failure = value.Text; if (WriteErrorLog is not null) WriteErrorLog(new("Windows Update", "Installation", value.Text, value.Code?.ToString())); else WriteLog?.Invoke(value.Text); }
        else if (value.Phase == "done") { _terminalReceived = true; _phase = value.Code is null or 2 ? "done" : "partial"; }
        else if (value.Phase == "cancelled") { _terminalReceived = true; _phase = "cancelled"; }
        else ApplyInstallProgress(value);
        _rebootRequired |= value.RebootRequired;
        if (_terminalReceived) OperationProgress.IsRunning = false;
        RefreshProgressText();
    }

    private void ApplyInstallProgress(WindowsUpdateEvent value)
    {
        // "search" is accepted from an older helper, but during an installation it
        // means revalidating the approved selection, not a new user-requested search.
        string phase = value.Phase == "search" ? "validate" : value.Phase;
        static int Rank(string phase) => phase switch { "prepare" => 0, "validate" => 1, "download" => 2, "install" => 3, _ => -1 };
        int rank = Rank(phase);
        if (rank < 0 || rank < Rank(_phase)) return;
        _installStage = rank;
        if (_phase != phase)
        {
            _phase = phase; OperationProgress.IsIndeterminate = true;
            OperationProgress.ShowPercentage = false; OperationProgress.Value = 0;
        }
        bool measured = rank >= 2 && value.Percent is { } percent && double.IsFinite(percent);
        OperationProgress.IsIndeterminate = !measured; OperationProgress.ShowPercentage = measured;
        if (measured) OperationProgress.Value = Math.Clamp(value.Percent!.Value, 0, 100);
    }

    private void Failure(Exception exception)
    {
        _phase = "error"; _failure = UiText.Error(exception, _english);
        if (unchecked((uint)exception.HResult) == 0x80242008) _failure = "0x80242008";
        if (WriteErrorLog is not null) WriteErrorLog(new("Windows Update", L("Prüfung/Installation", "Check/install"), _failure, "0x" + exception.HResult.ToString("X8")));
        else WriteLog?.Invoke(L("Windows-Update-Fehler: ", "Windows update error: ") + _failure);
    }
}
