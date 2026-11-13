using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Brushes = System.Windows.Media.Brushes;

namespace PowerControlProWpf;

internal sealed class DeveloperPackagesPane : UserControl
{
    private readonly ProgramCatalogView _view;
    private readonly DeveloperPackages _service;
    private readonly Action _close;
    private readonly Action<bool> _work;
    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly StackPanel _rows = new(), _details = new();
    private readonly ScrollViewer _scroll, _infoScroll;
    private readonly TextBox _query = new();
    private readonly TextBlock _queryHint;
    private readonly TextBlock _title, _subtitle, _status, _hint;
    private readonly Button _back, _search, _check, _install, _update, _stop;
    private readonly List<Button> _tabs = [];
    private readonly AnimatedProgressBar _bar = new() { Height = 22, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cancel;
    private bool _busy, _installing, _stopRequested;
    private bool _setupReady;
    private bool _inventoryReady;
    private bool _onlineResult;
    private string _manager = "PowerShell";
    private IReadOnlyList<DeveloperPackage> _installed = [], _packages = [];
    private DeveloperPackage? _detail;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyList<DeveloperPackage> Packages => _packages;
    internal string Manager => _manager;
    internal Grid Frame => _root;
    private bool NuGet => _manager == "NuGet";
    private bool Existing => _manager is "Scoop" or "Chocolatey";
    private string L(string de, string en) => _view.English ? en : de;
    private TextBlock Text(string value, double size = 12, string brush = "TextBrush")
    { var t = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, brush); return t; }
    private Button Action(string caption, string icon, string name)
    {
        var b = _view.Action(caption, 31); b.Name = name; b.Margin = new(3, 0, 3, 0);
        ProgramButtonIcons.Set(b, icon, caption); return b;
    }
    private Border FrameAround(UIElement child)
    {
        var b = new Border { Child = child, Padding = new(10), BorderThickness = new(1), CornerRadius = new(8), ClipToBounds = true };
        b.SetResourceReference(Border.BackgroundProperty, "InputBrush"); b.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); return b;
    }
    internal DeveloperPackagesPane(ProgramCatalogView view, DeveloperPackages service, Action close, Action<bool> work)
    {
        _view = view; _service = service; _close = close; _work = work;
        Name = "DeveloperPackagesPane"; _root.SetResourceReference(Grid.BackgroundProperty, "CardBrush");
        // An opaque surface prevents neon effects from the underlying catalog shining through.
        _root.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(view.Palette.Colors[0]));
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) _root.RowDefinitions.Add(new() { Height = h });
        var head = new Grid { Margin = new(0, 0, 0, 8) }; head.ColumnDefinitions.Add(new() { Width = new(42) }); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = new(65) });
        _back = Action("", "back", "DeveloperPackagesBack"); _back.Width = 31; _back.Click += (_, _) => { if (!_busy) _close(); }; head.Children.Add(_back);
        var headings = new StackPanel { Margin = new(5, 0, 0, 0) }; _title = Text("", 20); _title.FontWeight = FontWeights.SemiBold; _subtitle = Text("", 10, "SubtleTextBrush"); headings.Children.Add(_title); headings.Children.Add(_subtitle); Grid.SetColumn(headings, 1); head.Children.Add(headings);
        var stripes = new System.Windows.Shapes.Path { Data = Geometry.Parse("M48,0 L50,0 L8,40 L6,40 Z M67,0 L69,0 L27,40 L25,40 Z M73,0 L87,0 L45,40 L31,40 Z"), Width = 60, Height = 34, Stretch = Stretch.Fill, Opacity = .2, IsHitTestVisible = false };
        stripes.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush"); Grid.SetColumn(stripes, 2); head.Children.Add(stripes); _root.Children.Add(FrameAround(head));
        var tabs = new Grid { Margin = new(0, 7, 0, 7) }; Grid.SetRow(tabs, 1); _root.Children.Add(tabs);
        foreach (var manager in new[] { "NuGet", "PowerShell", "Scoop", "Chocolatey" })
        {
            tabs.ColumnDefinitions.Add(new()); var b = Action(manager, "development", "DeveloperManager" + manager); _tabs.Add(b); Grid.SetColumn(b, _tabs.Count - 1); tabs.Children.Add(b);
            b.Click += async (_, _) => { if (_busy) return; _manager = manager; _inventoryReady = false; _selected.Clear(); _packages = []; _installed = []; _query.Clear(); RefreshLanguage(); await LoadAsync(); };
        }
        var search = new Grid { Margin = new(0, 0, 0, 7) }; search.ColumnDefinitions.Add(new()); search.ColumnDefinitions.Add(new() { Width = new(110) }); search.ColumnDefinitions.Add(new() { Width = new(100) });
        _query.Height = 31; _query.Padding = new(8, 4, 8, 4); _query.SetResourceReference(TextBox.BackgroundProperty, "InputBrush"); _query.SetResourceReference(TextBox.ForegroundProperty, "TextBrush"); _query.SetResourceReference(TextBox.BorderBrushProperty, "AccentBrush"); AutomationProperties.SetName(_query, L("Paketname suchen", "Search package name"));
        var input = new Grid { ClipToBounds = true }; input.Children.Add(_query); _queryHint = Text("", 11, "SubtleTextBrush"); _queryHint.IsHitTestVisible = false; _queryHint.Margin = new(8, 0, 0, 0); _queryHint.VerticalAlignment = VerticalAlignment.Center; input.Children.Add(_queryHint); search.Children.Add(input);
        _query.TextChanged += (_, _) => _queryHint.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _query.KeyDown += async (_, e) => { if (e.Key == Key.Enter && !_busy) { e.Handled = true; await SearchAsync(); } };
        _search = Action("", "search", "DeveloperSearch"); _search.Click += async (_, _) => await SearchAsync(); Grid.SetColumn(_search, 1); search.Children.Add(_search);
        _check = Action("", "check", "DeveloperCheck"); _check.Click += async (_, _) => await LoadAsync(); Grid.SetColumn(_check, 2); search.Children.Add(_check); Grid.SetRow(search, 2); _root.Children.Add(search);
        _body.ColumnDefinitions.Add(new()); _body.ColumnDefinitions.Add(new() { Width = new(10) }); _body.ColumnDefinitions.Add(new());
        _scroll = new() { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _scroll.ScrollChanged += (_, _) => UpdateHint(); _body.Children.Add(FrameAround(_scroll));
        _infoScroll = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _infoScroll.ScrollChanged += (_, _) => UpdateHint();
        var infoFrame = FrameAround(_infoScroll); Grid.SetColumn(infoFrame, 2); _body.Children.Add(infoFrame); Grid.SetRow(_body, 3); _root.Children.Add(_body);
        var footer = new StackPanel { Margin = new(0, 6, 0, 0) }; _status = Text("", 11, "SubtleTextBrush"); _status.MaxHeight = 45; _status.TextTrimming = TextTrimming.CharacterEllipsis; footer.Children.Add(_status); footer.Children.Add(_bar);
        var actions = new Grid { Margin = new(0, 6, 0, 0) }; actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _hint = Text("", 10, "AccentBrush"); _hint.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(_hint);
        _stop = Action("", "remove", "DeveloperStop"); _stop.Width = 120; _stop.Click += (_, _) => Stop(); Grid.SetColumn(_stop, 1); actions.Children.Add(_stop);
        _install = Action("", "install", "DeveloperInstall"); _install.Width = 140; _install.Click += async (_, _) => await ExecuteAsync(false); Grid.SetColumn(_install, 2); actions.Children.Add(_install);
        _update = Action("", "update", "DeveloperUpdate"); _update.Width = 170; _update.Click += async (_, _) => await ExecuteAsync(true); Grid.SetColumn(_update, 3); actions.Children.Add(_update); footer.Children.Add(actions); Grid.SetRow(footer, 4); _root.Children.Add(footer);
        Content = _root; RefreshLanguage(); Palette();
        Unloaded += (_, _) => { if (!_installing) _cancel?.Cancel(); };
    }
    internal void RefreshLanguage()
    {
        _title.Text = L("Paketmanager", "Package managers"); _subtitle.Text = L("Entwicklungspakete und Module · keine Desktop-Programme", "Development packages and modules · not desktop applications");
        _back.ToolTip = L("Zurück zu Programmen", "Back to programs");
        _queryHint.Text = L("Paketname online suchen …", "Search package name online …");
        ProgramButtonIcons.Set(_search, "search", L("Online suchen", "Search online")); ProgramButtonIcons.Set(_check, "check", L("Bestand", "Installed"));
        ProgramButtonIcons.Set(_stop, "remove", L("Stoppen", "Stop")); ProgramButtonIcons.Set(_install, "install", L("Installieren", "Install"));
        Render(); ShowDetails(_detail); Availability();
    }
    internal void Palette() { _root.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_view.Palette.Colors[0])); _bar.ApplyPalette(_view.Palette); }
    private void Availability()
    {
        foreach (var b in _tabs) { b.IsEnabled = !_busy; b.BorderThickness = new(b.Name.EndsWith(_manager, StringComparison.Ordinal) ? 2 : 1); }
        _query.IsEnabled = _search.IsEnabled = !_busy && !Existing; _check.IsEnabled = _back.IsEnabled = !_busy;
        bool ready = _inventoryReady && (!NuGet || _service.NuGetExecutable is not null && _service.NuGetDirectory is not null);
        _install.IsEnabled = !_busy && ready && !Existing && _selected.Any(id => !_installed.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        _update.IsEnabled = !_busy && (Existing || ready && _packages.Any(p => _selected.Contains(p.Id) && HasUpdate(p)));
        int updateCount = Existing ? ManagerUpdateIds().Count : 0;
        ProgramButtonIcons.Set(_update, Existing && updateCount == 0 ? "all" : "update", !Existing
            ? L("Updates installieren", "Install updates")
            : updateCount > 0 ? L("Updates auswählen", "Select updates") + $" ({updateCount})" : L("Programme öffnen", "Open programs"));
        _details.IsEnabled = !_busy;
        _stop.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool HasUpdate(DeveloperPackage package) => _installed.Where(p => p.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)).All(p => ProgramVersions.IsNewer(p.Version, package.Version)) && _installed.Any(p => p.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
    private HashSet<string> ManagerUpdateIds() => _view.UpdateEntries
        .Where(e => _view.Inventory.ManagedPackages?.GetValueOrDefault(e.Id)?.Any(p => p.Manager == _manager && p.IsUpdate) == true)
        .Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private void Busy(bool value, bool install = false)
    {
        _busy = value; _installing = value && install; _bar.Visibility = value ? Visibility.Visible : Visibility.Collapsed; _bar.IsRunning = value; _bar.IsIndeterminate = value;
        if (install || !value) _work(value && install); Availability(); Render();
    }
    internal void Stop()
    {
        _stopRequested = true; _cancel?.Cancel();
        if (_installing) _status.Text = L("Stop angefordert · gestartetes Paket zuerst beenden und prüfen", "Stop requested · finish and verify the active package first");
    }
    private async Task ReadOperation(Func<CancellationToken, Task> action)
    {
        if (_busy) return; _stopRequested = false; using var cancel = new CancellationTokenSource(); _cancel = cancel; Busy(true);
        try { await action(cancel.Token); }
        catch (OperationCanceledException) { _status.Text = L("Prüfung abgebrochen", "Check cancelled"); }
        catch (Exception e) { Error(e); }
        finally { _cancel = null; Busy(false); }
    }
    internal Task LoadAsync() => ReadOperation(async token =>
    {
        _status.Text = L("Paketbestand wird gelesen …", "Reading package inventory …");
        _setupReady = false;
        if (_manager != "PowerShell")
            _setupReady = await Task.Run(() => _service.LocalSetupReady(_manager), token);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush"); _inventoryReady = false; _onlineResult = false;
        if (Existing)
        {
            _packages = _view.Inventory.ManagedPackages?.Values.SelectMany(x => x).Where(p => p.Manager == _manager)
                .Select(p => new DeveloperPackage(p.Target.Id, p.Version, p.Description(_view.English), p.Manager, p.License, p.Dependencies, p.Target.OldDirectory)).DistinctBy(p => p.Id).ToArray() ?? [];
            _installed = _view.Inventory.ManagedPackages?.Values.SelectMany(x => x).Where(p => p.Manager == _manager && p.Target.OldVersion is not null)
                .Select(p => new DeveloperPackage(p.Target.Id, p.Target.OldVersion!, "", p.Manager, p.License, p.Dependencies, p.Target.OldDirectory)).ToArray() ?? [];
            _status.Text = _setupReady ? _manager + (ManagerUpdateIds().Count > 0
                    ? L(" ist verfügbar. Öffne unten die Updateauswahl.", " is available. Open the update selection below.")
                    : L(" ist verfügbar. Pakete über „Programme öffnen“ verwalten.", " is available. Manage packages through “Open programs”."))
                : _manager + L(" wurde nicht gefunden. Über „Einrichten“ kannst du die Installation starten.", " was not found. Use the setup button to install it.");
            _status.Text += L(" Geprüft um ", " Checked at ") + DateTime.Now.ToString("HH:mm:ss") + ".";
        }
        else
        {
            _installed = await _service.InstalledAsync(NuGet, token); _packages = _installed;
            if (!NuGet) _setupReady = true;
            _status.Text = NuGet && _service.NuGetDirectory is null ? L("NuGet: Paketordner in den Details auswählen.", "NuGet: choose a package directory in the details.") : _packages.Count + L(" Pakete gefunden · für neue Versionen online suchen", " packages found · search online for newer versions");
        }
        _inventoryReady = true; _selected.Clear(); Render(); ShowDetails(null);
    });
    internal Task SearchAsync() => ReadOperation(async token =>
    {
        _status.Text = L("Offizielle Paketquelle wird durchsucht …", "Searching the official package source …");
        var result = await _service.SearchAsync(NuGet, _query.Text.Trim(), token);
        token.ThrowIfCancellationRequested(); _packages = result; _onlineResult = true; _selected.Clear(); _status.Text = result.Count + L(" Treffer · Bestand separat erfasst", " results · local inventory checked separately"); Render(); _scroll.ScrollToTop(); ShowDetails(null);
    });
    private void Render()
    {
        _rows.Children.Clear();
        foreach (var package in _packages)
        {
            var row = new Grid { Margin = new(0, 0, 0, 6) };
            bool installed = _installed.Any(p => p.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            bool update = HasUpdate(package);
            if (!Existing)
            {
                row.ColumnDefinitions.Add(new() { Width = new(29) }); row.ColumnDefinitions.Add(new());
                var check = new CheckBox { IsChecked = _selected.Contains(package.Id), VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = !_busy && (!installed || update), Width = 22, Height = 22 };
                AutomationProperties.SetName(check, package.Id);
                check.Checked += (_, _) => { _selected.Add(package.Id); Availability(); };
                check.Unchecked += (_, _) => { _selected.Remove(package.Id); Availability(); };
                row.Children.Add(check);
            }
            var labels = new StackPanel(); labels.Children.Add(Text(package.Id, 13));
            var status = Text(update ? L("Update verfügbar", "Update available") : installed
                ? L("Installiert · ", "Installed · ") + package.Version : L("Verfügbar · ", "Available · ") + package.Version,
                10, Existing && update ? "WarningBrush" : "SubtleTextBrush");
            labels.Children.Add(status);
            var open = new Button { Content = labels, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(4), Background = Brushes.Transparent, BorderThickness = new(0), IsEnabled = !_busy };
            open.SetResourceReference(Button.ForegroundProperty, "TextBrush"); open.Click += (_, _) => ShowDetails(package);
            if (!Existing) Grid.SetColumn(open, 1);
            row.Children.Add(open); _rows.Children.Add(FrameAround(row));
        }
        if (_packages.Count == 0) _rows.Children.Add(Text(L("Keine Pakete angezeigt. Bestand prüfen oder online suchen.", "No packages shown. Read inventory or search online.")));
        UpdateHint();
    }
    private void UpdateHint() => _hint.Text = (_scroll.VerticalOffset > 1 || _infoScroll.VerticalOffset > 1 ? "↑ " : "")
        + (_scroll.ScrollableHeight > _scroll.VerticalOffset + 1 || _infoScroll.ScrollableHeight > _infoScroll.VerticalOffset + 1 ? "↓ " : "")
        + (_scroll.ScrollableHeight > 1 || _infoScroll.ScrollableHeight > 1 ? L("Pakete / Details scrollen", "Scroll packages / details") : L("Paketdetails per Klick", "Click for package details"));
    private void ShowDetails(DeveloperPackage? package)
    {
        _detail = package;
        _details.Children.Clear();
        var title = Text(package?.Id ?? _manager, 19); title.FontWeight = FontWeights.SemiBold; _details.Children.Add(title);
        void Add(string text, string brush = "TextBrush") { var t = Text(text, 11, brush); t.Margin = new(0, 9, 0, 0); _details.Children.Add(t); }
        var setup = Action(_setupReady ? L("Erneut prüfen", "Check again") : (_manager == "PowerShell" ? "PSResourceGet" : _manager) + L(" einrichten", " setup"),
            _setupReady ? "check" : "install", "DeveloperManagerSetup");
        setup.Margin = new(0, 6, 0, 0);
        setup.Click += async (_, _) => { if (_setupReady) await LoadAsync(); else await SetupManagerAsync(); };
        if (_setupReady)
        {
            var actions = new Grid();
            actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
            actions.Children.Add(setup);
            var remove = Action(L("Deinstallieren …", "Uninstall …"), "remove", "DeveloperManagerRemove");
            remove.Margin = new(7, 6, 0, 0);
            remove.SetResourceReference(Button.BackgroundProperty, "DangerBrush");
            remove.SetResourceReference(Button.BorderBrushProperty, "DangerBorderBrush");
            remove.Click += async (_, _) => await SetupManagerAsync(remove: true);
            Grid.SetColumn(remove, 1); actions.Children.Add(remove);
            _details.Children.Add(actions);
            if (_manager == "Chocolatey")
            {
                string requirement = L("Für die Deinstallation sind Administratorrechte erforderlich.",
                    "Administrator rights are required to uninstall Chocolatey.");
                remove.ToolTip = requirement;
                if (!ElevationService.IsElevated())
                    requirement += L(" Starte PowerLeitstand dazu als Administrator.",
                        " Start PowerLeitstand as administrator.");
                Add(requirement, "WarningBrush");
            }
        }
        else _details.Children.Add(setup);
        Add(Existing ? L("Programme behalten ihren erkannten Paketmanager. Die gemeinsame Updateauswahl verwendet die vorhandenen Prüfungen und Fortschrittsanzeigen.", "Applications retain their detected package manager. The shared update selection uses existing checks and progress displays.") : NuGet
            ? L("NuGet-Bibliotheken werden im gewählten Paketordner abgelegt. Über „NuGet einrichten“ erhältst du NuGet.exe.", "NuGet libraries are placed in the selected package directory. Use “NuGet setup” to get NuGet.exe.")
            : L("PowerShell-Module aus PSGallery für das aktuelle Konto. PSResourceGet muss vorhanden sein; Module werden nicht automatisch importiert.", "PowerShell modules from PSGallery for the current account. PSResourceGet is required; modules are not imported automatically."));
        if (package is not null)
        {
            Add(package.Description); var old = _installed.Where(p => p.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Version).ToArray();
            Add(L("Installierte Version: ", "Installed version: ") + (old.Length > 0 ? string.Join(", ", old) : L("Nicht gefunden", "Not found")), HasUpdate(package) ? "DangerBorderBrush" : "TextBrush");
            Add(L("Verfügbare Version: ", "Available version: ") + (_onlineResult ? package.Version : Existing ? package.Version + L(" · gespeicherter Prüfstand", " · saved inspection") : L("Noch nicht online geprüft", "Not checked online yet")), _onlineResult ? "ReadyBrush" : "SubtleTextBrush"); Add(L("Autor: ", "Author: ") + package.Authors);
            Add(L("Lizenz: ", "License: ") + (package.License.Length > 0 ? package.License : L("Beim Anbieter prüfen", "Check with the provider")));
            Add(L("Abhängigkeiten: ", "Dependencies: ") + (package.Dependencies.Length > 0 ? package.Dependencies : L("Vom Paketmanager aufgelöst", "Resolved by the package manager")));
            if (package.Location is { Length: > 0 } location) Add(L("Speicherort: ", "Location: ") + location);
        }
        if (NuGet)
        {
            Add(L("NuGet.exe: ", "NuGet.exe: ") + (_service.NuGetExecutable ?? L("Nicht ausgewählt", "Not selected")));
            var exe = Action(L("NuGet.exe auswählen", "Choose NuGet.exe"), "development", "DeveloperNuGetExecutable"); exe.Margin = new(0, 8, 0, 0); exe.IsEnabled = !_busy; exe.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "NuGet|nuget.exe", CheckFileExists = true }; if (d.ShowDialog(Window.GetWindow(this)) == true) { _service.NuGetExecutable = d.FileName; ShowDetails(package); Availability(); } }; _details.Children.Add(exe);
            Add(L("Paketordner: ", "Package directory: ") + (_service.NuGetDirectory ?? L("Nicht ausgewählt", "Not selected")));
            var folder = Action(L("Paketordner auswählen", "Choose package directory"), "install", "DeveloperNuGetDirectory"); folder.Margin = new(0, 8, 0, 0); folder.IsEnabled = !_busy; folder.Click += async (_, _) => { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog(Window.GetWindow(this)) == true) { _service.NuGetDirectory = d.FolderName; await LoadAsync(); } }; _details.Children.Add(folder);
        }
        var web = Action(L("Offizielle Quelle", "Official source"), "browser", "DeveloperSource"); web.Margin = new(0, 10, 0, 0);
        web.Click += (_, _) =>
        {
            try
            {
                string url = NuGet ? "https://www.nuget.org/downloads" : Existing ? _manager == "Scoop" ? "https://scoop.sh/" : "https://chocolatey.org/" : "https://learn.microsoft.com/powershell/gallery/powershellget/install-powershellget";
                if (package is not null && !Existing) url = (NuGet ? "https://www.nuget.org/packages/" : "https://www.powershellgallery.com/packages/") + Uri.EscapeDataString(package.Id) + "/" + Uri.EscapeDataString(package.Version);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e) { Error(e); }
        }; _details.Children.Add(web);
    }
    private void Error(Exception e)
    {
        _status.Text = e is MissingPSResourceGetException
            ? L("PSResourceGet fehlt. Klicke auf „PSResourceGet einrichten“.", "PSResourceGet is missing. Select “PSResourceGet setup”.")
            : OnlineConnection.Message(e, _view.English);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "DangerBorderBrush");
        _view.WriteLog?.Invoke(L("Paketmanager: ", "Package manager: ") + _status.Text);
        _view.WriteErrorLog?.Invoke(new("Paketmanager", _manager, e.Message));
        ShowDetails(null);
    }
    private async Task SetupManagerAsync(bool remove = false)
    {
        if (_busy || _view.HostBusy()) return;
        var manager = _manager;
        var (source, target) = _service.SetupDetails(manager);
        var name = manager == "PowerShell" ? "PSResourceGet" : manager;
        Guid? staleChocolateyEntry = null;
        if (!remove && manager == "Chocolatey")
        {
            try { staleChocolateyEntry = DeveloperPackages.ChocolateyInstallerProduct(); }
            catch (Exception e) { Error(e); return; }
        }
        if (remove)
        {
            try
            {
                string? recognized = _service.RemovalTarget(manager);
                if (recognized is null)
                    throw new IOException(L("Für diese Installation ist kein eindeutiger Deinstallationsweg verfügbar.",
                        "No verified removal route is available for this installation."));
                target = recognized;
            }
            catch (Exception e) { Error(e); return; }
        }
        string question = name + L(" jetzt einrichten?\n\nQuelle: ", " — set up now?\n\nSource: ") + source
            + L("\nZiel: ", "\nDestination: ") + target
            + (manager == "Chocolatey" ? L("\nWindows kann Administratorrechte anfordern.", "\nWindows may request administrator privileges.")
                : L("\nEinrichtung für das aktuelle Windows-Konto.", "\nSetup for the current Windows account."));
        if (manager == "Chocolatey")
            question += L("\nMit „Einrichten“ akzeptierst du die Bedingungen der WinGet-Quelle und des Chocolatey-Pakets.",
                "\nBy selecting “Set up”, you accept the WinGet source and Chocolatey package terms.");
        if (staleChocolateyEntry is not null)
            question += L("\nDer alte MSI-Eintrag wird vor der Einrichtung entfernt.", "\nThe old MSI entry will be removed before setup.");
        var owner = Window.GetWindow(this);
        if (owner?.FindName("Root") is not Grid host) return;
        UnifiedDialogAppearance.Resources(owner, _view.Palette);
        var layer = new Grid { Name = "ManagerSetupLayer", Background = new SolidColorBrush(Color.FromArgb(175, 0, 10, 20)) };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count));
        System.Windows.Controls.Panel.SetZIndex(layer, 6000);
        var card = new Border { Padding = new(17), Margin = new(14), MaxWidth = 540, CornerRadius = new(10),
            BorderThickness = new(1.2), VerticalAlignment = VerticalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "DialogSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var body = new StackPanel();
        var header = new Grid { Margin = new(0, 0, 0, 13) };
        header.ColumnDefinitions.Add(new() { Width = new(34) });
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = new(28) });
        header.Children.Add(ProgramFallbackIcon.Create(26, "UnifiedAccent"));
        var headings = new StackPanel { Margin = new(0, 0, 62, 0) };
        var title = Text(remove ? L("Deinstallation vorbereiten", "Prepare removal") : L("Einrichtung vorbereiten", "Prepare setup"), 20, "UnifiedText");
        title.FontWeight = FontWeights.SemiBold;
        headings.Children.Add(title);
        headings.Children.Add(Text(name + " · PowerLeitstand PRO", 11, "UnifiedMuted"));
        Grid.SetColumn(headings, 1); header.Children.Add(headings);
        var stripe = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M48,0 L50,0 L8,40 L6,40 Z M67,0 L69,0 L27,40 L25,40 Z M73,0 L87,0 L45,40 L31,40 Z"),
            Width = 62, Height = 32, Opacity = .25, Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false
        };
        stripe.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UnifiedAccent");
        Grid.SetColumn(stripe, 1); header.Children.Add(stripe);
        var close = new Button { Content = "×", FontSize = 19, Width = 28, Height = 28 };
        CrossButtonAppearance.PreserveCustomSurface(close);
        close.Style = (Style)owner.FindResource("ManagerDialogTextButton");
        close.SetResourceReference(Button.ForegroundProperty, "UnifiedDangerBorder");
        AutomationProperties.SetName(close, L("Schließen", "Close"));
        Grid.SetColumn(close, 2); header.Children.Add(close);
        body.Children.Add(header);
        var frame = new Border { Padding = new(12), CornerRadius = new(6), BorderThickness = new(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush");
        var frameBody = new StackPanel(); frame.Child = frameBody; body.Children.Add(frame);
        var fields = new Grid();
        fields.ColumnDefinitions.Add(new() { Width = new(119) }); fields.ColumnDefinitions.Add(new());
        void Field(string label, string value)
        {
            int row = fields.RowDefinitions.Count; fields.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var key = Text(label, 12, "UnifiedMuted"); key.Margin = new(0, 0, 8, 6);
            var content = Text(value, 12, "UnifiedText"); content.Margin = new(0, 0, 0, 6);
            Grid.SetRow(key, row); Grid.SetRow(content, row); Grid.SetColumn(content, 1);
            fields.Children.Add(key); fields.Children.Add(content);
        }
        if (!remove) Field(L("Quelle", "Source"), source);
        Field(L("Installationsort", "Destination"), target);
        Field(L("Berechtigung", "Permissions"), manager == "Chocolatey"
            ? remove ? L("PowerLeitstand muss als Administrator laufen.", "PowerLeitstand must run as administrator.")
                : L("Windows kann Administratorrechte anfordern.", "Windows may request administrator privileges.")
            : L("Aktuelles Windows-Konto", "Current Windows account"));
        var details = new StackPanel(); details.Children.Add(fields);
        if (remove)
            details.Children.Add(Text(manager switch
            {
                "Scoop" => L("Scoop entfernt auch die lokal installierten Scoop-Programme. Der Ordner „persist“ bleibt erhalten. Bestätige anschließend im Scoop-Fenster.",
                    "Scoop also removes locally installed Scoop apps. The persist folder is kept. Confirm again in the Scoop window."),
                "Chocolatey" => L("Der Chocolatey-Ordner wird gesichert. Programme, die aus diesem Ordner laufen, sind danach nicht mehr verfügbar.",
                    "The Chocolatey folder is backed up. Apps running from this folder will no longer be available."),
                "PowerShell" => L("Nur diese PSResourceGet-Version wird in einen Sicherungsordner verschoben.", "Only this PSResourceGet version is moved to a backup folder."),
                _ => L("Nur die NuGet.exe aus dem PowerLeitstand-Ordner wird entfernt. Der Paketordner bleibt erhalten.",
                    "Only NuGet.exe in the PowerLeitstand directory is removed. The package directory is kept.")
            }, 12, "UnifiedText"));
        else if (manager == "Chocolatey")
            details.Children.Add(Text(L("Mit „Einrichten“ akzeptierst du die Paket- und Quellenbedingungen.",
                "Selecting “Set up” accepts the package and source terms."), 11, "UnifiedMuted"));
        if (staleChocolateyEntry is not null)
            details.Children.Add(Text(L("Alter Windows-Eintrag gefunden. Mit „Bereinigen und einrichten“ wird er entfernt. Deine Sicherung bleibt erhalten.",
                "An old Windows entry was found. “Clean up and set up” removes it and keeps your backup."), 12, "WarningBrush"));
        var scroll = new ScrollViewer { Content = details, MaxHeight = 138, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        frameBody.Children.Add(scroll);
        var message = Text("", 14, "UnifiedText"); message.FontWeight = FontWeights.SemiBold;
        var resultScroll = new ScrollViewer { Content = message, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Visibility = Visibility.Collapsed };
        frameBody.Children.Add(resultScroll);
        var steps = new Grid { Margin = new(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        string[] labels = [L("Prüfen", "Check"), "Download", remove ? L("Entfernen", "Remove") : L("Installation", "Install"), L("Abschluss", "Finish")];
        var captions = new TextBlock[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            steps.ColumnDefinitions.Add(new());
            captions[i] = Text(labels[i] + " –", 11, "UnifiedMuted");
            Grid.SetColumn(captions[i], i); steps.Children.Add(captions[i]);
        }
        frameBody.Children.Add(steps);
        var completed = new HashSet<int>();
        int active = -1;
        void UpdateSteps(int next, bool completeActive)
        {
            if (completeActive && active >= 0) completed.Add(active);
            active = next;
            for (int i = 0; i < labels.Length; i++)
            {
                captions[i].Text = completed.Contains(i) ? "✓ " + labels[i] : labels[i] + (i == active ? " …" : " –");
                captions[i].SetResourceReference(TextBlock.ForegroundProperty,
                    completed.Contains(i) ? "ReadyBrush" : i == active ? "UnifiedAccent" : "UnifiedMuted");
            }
        }
        var progress = new AnimatedProgressBar { Height = 29, Margin = new(0, 10, 0, 0), Visibility = Visibility.Collapsed, IsIndeterminate = true };
        progress.ApplyPalette(_view.Palette);
        progress.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "UnifiedAccent");
        progress.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "UnifiedAccent");
        progress.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "InputBrush");
        frameBody.Children.Add(progress);
        var note = Text(!remove && manager == "Chocolatey" ? L("WinGet führt die Einrichtung automatisch aus.", "WinGet handles setup automatically.") : "",
            11, "UnifiedMuted");
        note.Margin = new(0, 10, 0, 0); body.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var back = new Button { Name = "ManagerSetupCancel", Content = L("Abbrechen", "Cancel"),
            Style = (Style)owner.FindResource("ManagerDialogTextButton"), Height = 34, Padding = new(10, 0, 10, 0) };
        CrossButtonAppearance.PreserveCustomSurface(back);
        var start = Action(remove ? L("Deinstallieren", "Uninstall") : staleChocolateyEntry is not null
            ? L("Bereinigen und einrichten", "Clean up and set up") : L("Einrichten", "Set up"), remove ? "remove" : "install", "ManagerSetupStart");
        UnifiedDialogAppearance.Button(owner, start, remove ? DialogButtonKind.Danger : DialogButtonKind.Primary);
        if (remove && manager == "Chocolatey") start.IsEnabled = ElevationService.IsElevated();
        buttons.Children.Add(back); buttons.Children.Add(start); body.Children.Add(buttons);
        card.Child = body; layer.Children.Add(card);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        bool running = false, finished = false;
        void Dismiss()
        {
            if (running && !remove) { cancel.Cancel(); message.Text = L("Stopp angefordert. Eine gestartete Installation wird abgewartet.", "Stop requested. An active installer is allowed to finish."); }
            else if (running) return;
            else if (finished) dismissed.TrySetResult();
            else decision.TrySetResult(false);
        }
        void Unload(object sender, RoutedEventArgs e) { Dismiss(); dismissed.TrySetResult(); }
        layer.Unloaded += Unload;
        Unloaded += Unload;
        close.Click += (_, _) => Dismiss();
        back.Click += (_, _) => Dismiss();
        start.Click += (_, _) => decision.TrySetResult(true);
        layer.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
        host.Children.Add(layer);
        back.Focus();

        try
        {
            var confirmed = _view.ConfirmForTests?.Invoke(question) ?? await decision.Task;
            if (!confirmed || _busy || _view.HostBusy() || !IsLoaded) return;
            start.Visibility = Visibility.Collapsed;
            close.Visibility = Visibility.Hidden;
            scroll.Visibility = Visibility.Collapsed;
            resultScroll.Visibility = steps.Visibility = Visibility.Visible;
            title.Text = remove ? L("Paketmanager wird entfernt", "Removing package manager") : L("Paketmanager wird eingerichtet", "Setting up package manager");
            back.Content = L("Vorgang stoppen", "Stop setup");
            if (remove) back.Visibility = Visibility.Collapsed;
            note.Text = L("Der Balken läuft, solange keine Prozentwerte vorliegen.", "The bar animates while percentage data is unavailable.");
            UpdateSteps(0, false);
            progress.Visibility = Visibility.Visible; progress.IsRunning = true;
            running = true; _cancel = cancel; _stopRequested = false; Busy(true, true);
            _bar.IsRunning = false; _bar.Visibility = Visibility.Collapsed;
            bool ready = false;
            try
            {
                string? backup = null;
                if (remove)
                {
                    message.Text = L("Deinstallation läuft …", "Removing package manager …");
                    UpdateSteps(2, true);
                    backup = await _service.RemoveManagerAsync(manager, target);
                    UpdateSteps(3, true);
                }
                else
                {
                    message.Text = L("Einrichtung wird vorbereitet …", "Preparing setup …");
                    await _service.SetupAsync(manager, cancel.Token, phase =>
                {
                    if (phase != "cleanup") UpdateSteps(phase switch { "download" => 1, "install" => 2, _ => 3 }, true);
                    message.Text = phase switch
                    {
                        "download" => L("Download läuft …", "Downloading …"),
                        "install" => L("Einrichtung läuft …", "Installing …"),
                        "cleanup" => L("Alter Chocolatey-MSI-Eintrag wird entfernt …", "Removing old Chocolatey MSI entry …"),
                        _ => L("Verfügbarkeit wird geprüft …", "Checking availability …")
                    };
                    }, staleChocolateyEntry);
                }
                ready = true;
                message.Text = "✓  " + name + (remove ? L(" wurde am gewählten Ort entfernt", " was removed from the selected location")
                    : L(" ist verfügbar", " is available"));
                if (backup is not null) message.Text += L("\nSicherung: ", "\nBackup: ") + backup;
                message.SetResourceReference(TextBlock.ForegroundProperty, "ReadyBrush");
                _view.WriteLog?.Invoke(message.Text);
            }
            catch (OperationCanceledException) { message.Text = L("Einrichtung abgebrochen", "Setup cancelled"); }
            catch (Exception e) { Error(e); message.Text = _status.Text; }
            finally
            {
                running = false; finished = true; progress.IsRunning = false;
                title.Text = remove ? L("Deinstallation beendet", "Removal ended")
                    : ready ? L("Einrichtung abgeschlossen", "Setup complete") : L("Einrichtung beendet", "Setup ended");
                note.Text = ready && !remove ? L("Die App hat den Paketmanager erkannt.", "The app detected the package manager.") : "";
                UpdateSteps(-1, ready);
                close.Visibility = Visibility.Visible;
                progress.IsIndeterminate = false; progress.Value = ready ? 100 : 0;
                progress.ShowPercentage = ready;
                if (!ready) progress.Visibility = Visibility.Collapsed;
                back.Visibility = Visibility.Visible; back.Content = L("Schließen", "Close");
                _cancel = null; Busy(false);
            }
            if (ready) await LoadAsync();
            await dismissed.Task;
        }
        finally
        {
            layer.Unloaded -= Unload;
            Unloaded -= Unload;
            host.Children.Remove(layer);
        }
    }
    internal async Task ExecuteAsync(bool update)
    {
        if (_busy || _view.HostBusy()) return;
        if (!Existing && (!_inventoryReady || NuGet && (_service.NuGetExecutable is null || _service.NuGetDirectory is null))) return;
        if (Existing)
        {
            var ids = ManagerUpdateIds();
            _view.CloseDeveloperPackages();
            if (ids.Count > 0) await _view.ChooseUpdatesAsync(ids);
            return;
        }
        var selected = _packages.Where(p => _selected.Contains(p.Id) && (update ? HasUpdate(p) : !_installed.Any(i => i.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase)))).ToArray();
        if (selected.Length == 0) return;
        string question = string.Join("\n", selected.Select(p => p.Id + " · " + p.Version + "\n" + L("Lizenz: ", "License: ") + p.License)) + "\n\n" + L("Quelle: ", "Source: ") + (NuGet ? DeveloperPackages.NuGetSource : DeveloperPackages.GallerySource) + "\n" + L("Pakete und benötigte Abhängigkeiten installieren und die angegebenen Lizenzen akzeptieren?", "Install packages and required dependencies and accept the listed licenses?");
        bool confirmed = _view.ConfirmForTests?.Invoke(question) ?? ThemedMessageBox.Show(Window.GetWindow(this), question, L("Paketinstallation bestätigen", "Confirm package installation"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        if (!confirmed) return;
        _stopRequested = false; using var cancel = new CancellationTokenSource(); _cancel = cancel; Busy(true, true);
        try
        {
            for (int n = 0; n < selected.Length; n++)
            {
                if (_stopRequested) break; var p = selected[n];
                await _service.InstallAsync(NuGet, p, cancel.Token, phase => _status.Text = (n + 1) + "/" + selected.Length + " · " + p.Id + " · " + (phase switch
                { "check" => L("Quelle und Version prüfen …", "Verifying source and version …"), "install" => L("Prüfen ✓ · Download und Installation laufen … (keine Prozentdaten vom Paketmanager)", "Check ✓ · Download and installation running … (no percentage data from manager)"), "verify" => L("Installation beendet · Zielversion prüfen …", "Installation ended · verifying target version …"), _ => L("Zielversion bestätigt ✓", "Target version verified ✓") }));
                _view.WriteLog?.Invoke(p.Id + " · " + p.Version + L(" · Paketinstallation bestätigt", " · Package installation verified"));
            }
            _installed = await _service.InstalledAsync(NuGet, CancellationToken.None); _selected.Clear(); _status.Text = _stopRequested ? L("Warteschlange gestoppt · gestartetes Paket geprüft", "Queue stopped · active package verified") : L("Auswahl abgeschlossen · Zielversionen bestätigt ✓", "Selection complete · target versions verified ✓");
        }
        catch (OperationCanceledException) { _status.Text = L("Vorbereitung abgebrochen", "Preparation cancelled"); }
        catch (Exception e) { Error(e); }
        finally { _cancel = null; Busy(false); }
    }
}
