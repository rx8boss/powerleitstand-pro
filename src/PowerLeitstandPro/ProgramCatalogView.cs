using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = PowerControlProWpf.ThemedMessageBox;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView : UserControl
{
    private readonly WindowsUpdatesPage _page;
    internal ProgramAssets Assets { get; }
    internal IProgramInstallService Service { get; set; } = new ProgramInstallService();
    internal ProgramDescriptions Descriptions { get; set; } = ProgramDescriptions.Shared;
    internal ITeamSpeakInfoService TeamSpeakService { get; set; } = new TeamSpeakInfoService();
    internal ITeamSpeakInstaller TeamSpeakInstaller { get; set; } = new TeamSpeakInstaller();
    internal Func<bool> HostBusy { get; set; } = () => false;
    internal Action<bool>? WorkStateChanged { get; set; }
    internal Action? Back { get; set; }
    internal Action? PresentationChanged { get; set; }
    internal Action<string>? WriteLog { get; set; }
    internal Action<AppLogError>? WriteErrorLog { get; set; }
    internal Func<string, bool>? ConfirmForTests { get; set; }
    internal bool IsWorking { get; private set; }
    internal IProgramPortableService PortableService { get; set; } = new ProgramPortable();
    internal Func<string?>? PortableFolderSelector { get; set; }
    private bool _selectionOpen;
    internal bool SelectionOpen { get => _selectionOpen; set { _selectionOpen = value; if (value) CancelBackgroundInspection(); NotifyOverlayChanged(); } }
    internal bool Compact => _compact;
    internal int Columns => _compact ? 3 : 6;
    internal ProgramInventory Inventory { get; private set; } = new(false, new Dictionary<string, string?>());
    internal ThemePalette Palette { get; private set; } = ThemePalette.For("Standard");
    internal bool English => _english;
    internal readonly HashSet<string> Selected = [];
    internal IReadOnlyList<Border> CardSurfaces => _surfaces;
    internal IReadOnlyList<Border> Tiles => _tiles;
    internal IReadOnlyList<ProgramEntry> VisibleEntries => _visible;
    internal Button InstallButton => _install;
    private readonly List<Border> _surfaces = [], _tiles = [];
    private readonly Dictionary<string, Image> _images = [];
    private readonly List<CheckBox> _checks = [];
    private readonly Dictionary<string, Button> _categories = [];
    private readonly Dictionary<Border, (Canvas Layer, bool Update)> _bands = [];
    private readonly List<ProgramEntry> _visible = [];
    private readonly WrapPanel _wrap = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBox _search = new();
    private readonly TextBlock _title, _subtitle, _hint, _status, _count;
    private readonly Button _back, _install, _cancel, _refresh, _online;
    internal Button UpdateButton { get; private set; } = null!;
    internal Button InstalledButton { get; private set; } = null!;
    private readonly TextBlock _installedCaption;
    internal Button ClearSearchButton { get; }
    private readonly ProgramInstallationMarks _marks;
    private readonly ProgramFileAssociations _fileAssociations;
    private readonly Dictionary<string, ProgramEntry> _sessionEntries = [];
    private IReadOnlyList<ProgramEntry> _results = [];
    private CancellationTokenSource? _searchCancellation;
    private string _resultQuery = "", _searchStatus = "";
    private bool _searching;
    private IEnumerable<ProgramEntry> KnownEntries => ProgramCatalog.Entries.Concat(_sessionEntries.Values).DistinctBy(x => x.PackageId ?? x.Id, StringComparer.OrdinalIgnoreCase);
    internal Button OnlineButton => _online;
    internal void SetQuery(string query) => _search.Text = query;
    internal ProgramProgressPanel? OperationPanel { get; private set; }
    private void ClearFinishedProgress() { if (!IsWorking) { OperationPanel?.Dispose(); OperationPanel = null; } }
    private readonly AnimatedProgressBar _progress = new() { Height = 20, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Grid _root = new();
    private readonly Grid _tabs = new();
    private readonly Border _header;
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private ProgramInfoCard? _info;
    internal ProgramInfoCard? Info => _info;
    private readonly TextBlock _searchHint;
    private bool _english, _compact, _refreshing, _stopping;
    private bool _languageInitialized;
    private string? _renderedKey;
    private ProgramInventory? _renderedInventory;
    private CancellationTokenSource? _prepare;
    private string _category = "all", _phase = "";

    internal ProgramCatalogView(WindowsUpdatesPage page, ProgramAssets assets)
    {
        _page = page; Assets = assets; Service = new ProgramInstallService(portableFile: assets.PortableFile, locationsFile: assets.LocationsFile, cacheFile: assets.InspectionFile); _marks = new(assets.MarksFile); _fileAssociations = new(assets.LocationsFile); UseLayoutRounding = true; SnapsToDevicePixels = true;
        _root.Margin = new Thickness(5, 3, 5, 3);
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _root.RowDefinitions.Add(new() { Height = height });
        _header = new Border { Style = (Style)page.FindResource("UpdatesCard"), Margin = new(0, 0, 0, 7), Padding = new(8, 3, 8, 3), ClipToBounds = true };
        var head = new Grid(); head.ColumnDefinitions.Add(new() { Width = new GridLength(36) }); head.ColumnDefinitions.Add(new()); head.ColumnDefinitions.Add(new() { Width = new GridLength(80) });
        _back = Action("", 30); _back.Width = 30; _back.Padding = new(0);
        _back.HorizontalContentAlignment = HorizontalAlignment.Center; _back.VerticalContentAlignment = VerticalAlignment.Center;
        ProgramButtonIcons.Set(_back, "back", ""); _back.Click += (_, _) => { CloseInfo(); Back?.Invoke(); }; head.Children.Add(_back);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
        _title = Text("", 20); _title.FontWeight = FontWeights.SemiBold; _subtitle = Text("", 10, "SubtleTextBrush"); labels.Children.Add(_title); labels.Children.Add(_subtitle); Grid.SetColumn(labels, 1); head.Children.Add(labels);
        var stripes = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 65,0 L 67,0 L 8,54 L 6,54 Z M 90,0 L 92,0 L 33,54 L 31,54 Z M 94,0 L 110,0 L 51,54 L 35,54 Z"), Width = 74, Height = 40, Stretch = Stretch.Fill, Opacity = .18, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
        stripes.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush"); Grid.SetColumn(stripes, 2); head.Children.Add(stripes);
        _header.Child = head; _root.Children.Add(_header); _surfaces.Add(_header);
        var searchRow = new Grid { Margin = new(0, 0, 0, 6) }; searchRow.ColumnDefinitions.Add(new()); searchRow.ColumnDefinitions.Add(new() { Width = new GridLength(112) }); searchRow.ColumnDefinitions.Add(new() { Width = new GridLength(110) });
        _search.Height = 28; _search.Padding = new(9, 3, 32, 3); _search.FontSize = 12;
        _search.SetResourceReference(TextBox.BackgroundProperty, "InputBrush"); _search.SetResourceReference(TextBox.ForegroundProperty, "TextBrush"); _search.SetResourceReference(TextBox.BorderBrushProperty, "AccentBrush");
        _search.TextChanged += (_, _) => { StopIconWork(); CloseInfo(); CancelSearch(); _results = []; _resultQuery = ""; _searchStatus = ""; RenderTiles(); ResetSearchScroll(); }; var searchHost = new Grid { ClipToBounds = true }; searchHost.Children.Add(_search); searchRow.Children.Add(searchHost);
        _search.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SearchOnlineAsync(); } };
        _searchHint = Text("", 11, "SubtleTextBrush"); _searchHint.Margin = new(10, 0, 0, 0); _searchHint.VerticalAlignment = VerticalAlignment.Center; _searchHint.IsHitTestVisible = false;
        searchHost.Children.Add(_searchHint);
        ClearSearchButton = Action("", 24); ClearSearchButton.Width = 24; ClearSearchButton.MinHeight = 24;
        ClearSearchButton.HorizontalAlignment = HorizontalAlignment.Right; ClearSearchButton.Margin = new(0, 0, 3, 0);
        ClearSearchButton.VerticalAlignment = VerticalAlignment.Center; ClearSearchButton.Visibility = Visibility.Collapsed;
        ClearSearchButton.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 10,10 M 10,0 L 0,10"), Width = 10, Height = 10, Stretch = Stretch.Uniform, StrokeThickness = 1.6 };
        ConfigureSearchClear();
        ClearSearchButton.Click += (_, _) => ClearSearch(); searchHost.Children.Add(ClearSearchButton);
        _search.TextChanged += (_, _) => ClearSearchButton.Visibility = _search.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _search.TextChanged += (_, _) => _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _online = Action("", 28); _online.FontSize = 11; _online.Margin = new(6, 0, 0, 0); _online.Click += async (_, _) => { if (_searching) CancelSearch(); else await SearchOnlineAsync(); }; Grid.SetColumn(_online, 1); searchRow.Children.Add(_online);
        _refresh = Action("", 28); _refresh.Margin = new(6, 0, 0, 0); _refresh.Click += async (_, _) => await RefreshAsync(); Grid.SetColumn(_refresh, 2); searchRow.Children.Add(_refresh); Grid.SetRow(searchRow, 1); _root.Children.Add(searchRow);
        var tabs = _tabs; tabs.Margin = new(0, 0, 0, 7);
        string[] glyphs = ["\uE80A", "\uE774", "\uE90F", "\uE943", "\uE714", "\uE714", "\uE99A", "\uE7FC"];
        var topCategories = ProgramCatalog.Categories.Except(ProgramCatalog.AdditionalCategories).ToArray();
        for (var i = 0; i < topCategories.Length; i++)
        {
            var category = topCategories[i]; tabs.ColumnDefinitions.Add(new());
            var tab = Action("", 27); tab.Margin = new(i == 0 ? 0 : 4, 0, 0, 0); tab.Tag = glyphs[i]; tab.FontSize = 10.5;
            tab.Click += (_, _) => SetCategory(category); _categories[category] = tab; Grid.SetColumn(tab, i); tabs.Children.Add(tab);
        }
        Grid.SetRow(tabs, 2); _root.Children.Add(tabs);
        InitializeAdditionalCategories(tabs);
        _scroll = new ScrollViewer { Content = _wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, VerticalAlignment = VerticalAlignment.Top };
        var viewport = new Grid { ClipToBounds = true }; viewport.Children.Add(_scroll); viewport.SizeChanged += (_, _) => FitTiles(viewport.ActualHeight); Grid.SetRow(viewport, 3); _root.Children.Add(viewport);
        _scroll.SizeChanged += (_, _) => FitTiles(viewport.ActualHeight); _scroll.ScrollChanged += (_, _) => UpdateHint();
        var footer = new Grid { Margin = new(0, 5, 0, 0) }; footer.RowDefinitions.Add(new() { Height = GridLength.Auto }); footer.RowDefinitions.Add(new() { Height = GridLength.Auto }); footer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var actions = new Grid(); actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new() { Width = new GridLength(145) }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _count = Text("", 11, "SubtleTextBrush"); _count.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(_count);
        _install = Action("", 34); _install.Click += async (_, _) => await InstallNewAsync(); Grid.SetColumn(_install, 1); actions.Children.Add(_install);
        _cancel = Action("", 34); _cancel.Width = 136; _cancel.Margin = new(7, 0, 0, 0); _cancel.Click += (_, _) => RequestStop(); Grid.SetColumn(_cancel, 2); actions.Children.Add(_cancel);
        UpdateButton = Action("", 34); UpdateButton.Width = 145; UpdateButton.Margin = new(8, 0, 0, 0);
        UpdateButton.Click += async (_, _) => await ChooseUpdatesAsync(); Grid.SetColumn(UpdateButton, 2); actions.Children.Add(UpdateButton);
        InstalledButton = Action("", 34); InstalledButton.Width = 158; InstalledButton.Margin = new(8, 0, 0, 0);
        InstalledButton.Content = ProgramInstalledVisuals.ButtonContent(out _installedCaption);
        InstalledButton.Click += async (_, _) =>
        {
            if (IsWorking || HostBusy() || SelectionOpen || _refreshing || _searching) return;
            SelectionOpen = true;
            try { await PauseBackgroundInspectionAsync(); }
            finally { SelectionOpen = false; }
            if (HostBusy() || IsWorking) return;
            CloseInfo(); ClearFinishedProgress(); await ProgramRemovalDialog.ShowAsync(this); await RefreshAsync();
        };
        Grid.SetColumn(InstalledButton, 3); actions.Children.Add(InstalledButton);
        footer.Children.Add(actions); Grid.SetRow(_progress, 1); _progress.Margin = new(0, 5, 0, 0); footer.Children.Add(_progress);
        var notes = new Grid { MinHeight = 19 }; notes.ColumnDefinitions.Add(new()); notes.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _status = Text("", 10, "SubtleTextBrush"); _status.Name = "ProgramInspectionStatus"; _status.TextWrapping = TextWrapping.NoWrap; _status.TextTrimming = TextTrimming.CharacterEllipsis; notes.Children.Add(_status);
        _hint = Text("", 10, "AccentBrush"); _hint.Margin = new(6, 0, 0, 0); Grid.SetColumn(_hint, 1); notes.Children.Add(_hint); Grid.SetRow(notes, 2); footer.Children.Add(notes); Grid.SetRow(footer, 4); _root.Children.Add(footer);
        Content = _root; InitializeConnectionOverlay(); InitializeBackgroundInspection();
        SizeChanged += (_, _) =>
        {
            bool compact = ActualWidth < 1000 || ActualHeight < 470; bool changed = compact != _compact; _compact = compact;
            _header.Height = _compact ? 46 : 58;
            _title.FontSize = _compact ? 20 : 24; _subtitle.FontSize = _compact ? 9 : 11;
            if (changed) RenderTiles(); else FitTiles(viewport.ActualHeight);
            FitConnectionCard();
            PositionAdditionalCategories();
        };
        Assets.IconChanged += IconChanged;
        Unloaded += (_, _) => { _hover.Stop(); CloseInfo(); CancelSearch(); _ = LeaveProgramAssetsAsync(); };
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { if (IsLoaded) _ = Assets.WarmupAsync(); });
        SetLanguage(false);
    }
    private TextBlock Text(string value, double size, string resource = "TextBrush")
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, resource); return text;
    }
    internal Button Action(string caption, double height)
    {
        var button = new Button { Content = caption, Height = height, MinHeight = height, Padding = new(5, 0, 5, 0), Style = (Style)_page.FindResource("UpdatesAction"), IsEnabled = true };
        AutomationProperties.SetName(button, caption); return button;
    }
    private string L(string de, string en) => _english ? en : de;
    internal void SetLanguage(bool english)
    {
        if (_languageInitialized && _english == english) return;
        _languageInitialized = true;
        if (_english != english) _searchStatus = "";
        _english = english; CloseInfo();
        DeveloperPane?.RefreshLanguage();
        _title.Text = L("Programme installieren", "Install programs"); _subtitle.Text = L("Auswahl · exakte Paketprüfung vor Installation", "Selection · exact package check before installation");
        _back.ToolTip = L("Zurück zur Übersicht", "Back to overview"); AutomationProperties.SetName(_back, _back.ToolTip.ToString());
        _search.ToolTip = L("Programme suchen", "Search programs"); AutomationProperties.SetName(_search, _search.ToolTip.ToString());
        ClearSearchButton.ToolTip = L("Suche leeren", "Clear search"); AutomationProperties.SetName(ClearSearchButton, ClearSearchButton.ToolTip.ToString());
        _searchHint.Text = L("⌕ Programme im Katalog oder online suchen …", "⌕ Search catalog or online …");
        ProgramButtonIcons.Set(_refresh, "check", L("Prüfen", "Check")); ProgramButtonIcons.Set(_install, "install", L("Installieren", "Install")); ProgramButtonIcons.Set(_cancel, "stop", L("Stoppen", "Stop queue"));
        _cancel.ToolTip = L("Warteschlange stoppen; gestarteten Installer zuerst beenden", "Stop queue; finish the running installer first");
        foreach (var (id, tab) in _categories)
        {
            ProgramButtonIcons.Set(tab, id, ProgramCatalog.Category(id, english), true);
            AutomationProperties.SetName(tab, ProgramCatalog.Category(id, english));
        }
        UpdateAdditionalCategories();
        RenderTiles(); RefreshAvailability();
    }
    internal void SetPalette(ThemePalette palette)
    {
        bool changed = !Palette.Colors.SequenceEqual(palette.Colors) || Palette.FontFamily != palette.FontFamily;
        Palette = palette; FontFamily = new(palette.FontFamily); _progress.ApplyPalette(palette); OperationPanel?.ApplyPalette(); if (changed) CloseInfo();
        DeveloperPane?.Palette();
        if (changed && _connectionLayer.Visibility == Visibility.Visible) ShowConnectionOverlay(_connectionFailure, _retrySearch);
        // Palette application is called by the host's presentation callback. Never trigger that callback recursively.
        foreach (var tile in _tiles) DrawBand(tile);
        UpdateAdditionalCategories();
        ApplyCheckStatusAppearance();
        // The host replaces reference gradients after applying the child palette.
        _ = Dispatcher.BeginInvoke(() => { if (IsLoaded) ApplyCheckStatusAppearance(); }, DispatcherPriority.Loaded);
    }
    internal void SetCategory(string category)
    { if (!ProgramCatalog.Categories.Contains(category)) return; _category = category; CloseAdditionalCategories(); CloseInfo(); RenderTiles(); _scroll.ScrollToTop(); }
    internal void ClearSearch() { CancelSearch(); _search.Clear(); _search.Focus(); }
    internal bool IsInstalled(string id) => Inventory.Installed.ContainsKey(id) || _marks.Contains(id);
    internal bool IsManual(string id) => !Inventory.Installed.ContainsKey(id) && _marks.Contains(id);
    internal bool CanReinstall(ProgramEntry entry) => Service is ProgramInstallService service && !IsManual(entry.Id)
        && service.CanReinstall(entry, Inventory);
    internal DateTimeOffset? InstalledAt(string id) => _marks.InstalledAt(id);
    internal string? MostRecentlyInstalledId => _marks.MostRecentlyInstalledId;
    internal void RecordInstalled(string id)
    { try { _marks.RecordInstalled(id); } catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { WriteLog?.Invoke(L("Installationszeit nicht gespeichert: ", "Installation time could not be saved: ") + e.Message); } }
    internal void ForgetInstallation(string id)
    { try { _marks.ForgetInstallation(id); } catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { WriteLog?.Invoke(L("Installationszeit nicht entfernt: ", "Installation time could not be removed: ") + e.Message); } }
    internal bool CanMark(string id) => ShowManualMark(id) && !IsWorking && !HostBusy() && !_refreshing && !_searching;
    internal string InstallationStatus(string id)
    {
        if ((Inventory.IsStale || Inventory.PendingUpdates?.ContainsKey(id) == true || Inventory.UnverifiedInstalledIds?.Contains(id) == true) && Inventory.Installed.ContainsKey(id)) return Inventory.PendingUpdates?.ContainsKey(id) == true
            ? L("Update zuletzt erkannt · wird geprüft", "Update previously detected · pending verification")
            : L("Installiert · gespeicherter Prüfstand", "Installed · saved state");
        if (ProgramVersions.StoreFamilies.ContainsKey(id) && !Inventory.StoreCheckComplete)
            return Inventory.Evidence?.GetValueOrDefault(id)?.Source == "Windows-App" && Inventory.StoreError is not null
                ? L("Zuletzt bestätigt · Store-Prüfung fehlgeschlagen", "Last confirmed · Store check failed")
                : Inventory.Installed.ContainsKey(id) ? L("Installiert · Store-Prüfung unvollständig", "Installed · Store check incomplete")
                : Inventory.StoreError is null ? L("Store wird geprüft …", "Checking Store …") : L("Store-Prüfung fehlgeschlagen", "Store check failed");
        var proof = Inventory.Evidence?.GetValueOrDefault(id);
        if (proof?.Source == "SelectedFile") return L("Programmdatei vorhanden", "Program executable found")
            + (proof.Version is { } selectedVersion ? " · " + selectedVersion : L(" · Version nicht ermittelbar", " · version unavailable"));
        if (proof?.Source is "Scoop" or "Chocolatey") return proof.ManagedCopies is { Count: > 1 }
            ? L("Mehrere Paketinstallationen\nKeine eindeutige Programmversion", "Multiple package installations\nNo unambiguous program version")
            : proof.LocationEvidence?.Executable is not null
                ? L("Installiert über ", "Installed through ") + proof.Source + (proof.Version is { } managedVersion ? " · " + managedVersion : L("\nProgrammversion nicht ermittelbar", "\nProgram version unavailable"))
                : proof.Source + L("-Paket registriert\nProgrammversion nicht bestätigt", " package registered\nProgram version not confirmed");
        if (proof?.Source == "Portable-Setup") return L("Einrichtungs-EXE vorhanden", "Setup EXE present");
        if (proof?.Source == "Portable") return L("Portable App vorhanden", "Portable app present") + (proof.Version is { } v ? " · " + v : L(" · Version nicht ermittelbar", " · version unavailable"));
        if (Inventory.Installed.TryGetValue(id, out var version)) return string.IsNullOrWhiteSpace(version)
            ? L("Installiert · Version nicht ermittelbar", "Installed · version unavailable") : L("Installiert · ", "Installed · ") + version + (Inventory.InstalledSources?.GetValueOrDefault(id) == "Windows-App" ? L("\nWindows-App · Paketversion", "\nWindows app · package version") : "");
        return IsManual(id) ? L("Installiert · manuell bestätigt\nVersion nicht ermittelt", "Installed · manually confirmed\nVersion not determined")
            : L("Nicht sicher ermittelbar", "Not reliably determined");
    }
    internal void MarkInstalled(ProgramEntry entry, bool installed)
    {
        if (!KnownEntries.Any(x => ReferenceEquals(x, entry)) || !CanMark(entry.Id)) throw new InvalidOperationException(L("Markierung derzeit nicht möglich.", "Cannot change confirmation right now."));
        _marks.Set(entry.Id, installed);
        if (installed && !Inventory.HasUpdate(entry.Id)) Selected.Remove(entry.Id);
        RenderTiles();
    }
    internal void Select(string id, bool selected)
    { if (IsWorking || HostBusy() || SelectionOpen || _refreshing || _searching) return; CancelBackgroundInspection(); if (selected && (!IsInstalled(id) || Inventory.HasUpdate(id))) Selected.Add(id); else Selected.Remove(id); RefreshAvailability(); }
    private void RenderTiles()
    {
        _info?.RefreshInventory();
        if (!Inventory.IsPartial) Selected.RemoveWhere(id => IsInstalled(id) && !Inventory.HasUpdate(id));
        foreach (var (id, tab) in _categories) tab.BorderThickness = new(id == _category ? 2 : 1);
        var query = _search.Text.Trim();
        UpdateCategoryVisibility();
        UpdateAdditionalCategories();
        bool online = query.Length > 0 && _resultQuery == query;
        var entries = ProgramCatalog.Entries.Where(x => (query.Length > 0 || _category == "all" || x.Categories.Contains(_category))
            && (query.Length == 0 || (x.Name + " " + x.Description(_english) + " " + x.Publisher + " " + x.PackageId).Contains(query, StringComparison.OrdinalIgnoreCase)));
        if (online) entries = entries.Concat(_results);
        var ordered = entries.Where(e => IsInstalled(e.Id) || CanOfferNewInstall(e) || e.Id == "teamspeak").DistinctBy(x => x.PackageId ?? x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        string key = _english + ":" + _compact + ":" + _category + ":" + MostRecentlyInstalledId + ":" + string.Join("|", ordered.Select(e => e.Id + ":" + IsInstalled(e.Id) + ":" + IsManual(e.Id) + ":" + Selected.Contains(e.Id) + ":" + Inventory.HasUpdate(e.Id) + ":" + UpdateTarget(e.Id)));
        if (ReferenceEquals(_renderedInventory, Inventory) && _renderedKey == key)
        { FitTiles((_scroll.Parent as FrameworkElement)?.ActualHeight ?? 0); RefreshAvailability(); return; }
        _renderedInventory = Inventory; _renderedKey = key;
        _wrap.Children.Clear(); _images.Clear(); _checks.Clear(); _tiles.Clear(); _visible.Clear(); _bands.Clear();
        foreach (var entry in ordered)
        {
            _visible.Add(entry);
            bool installed = IsInstalled(entry.Id), selectableUpdate = Inventory.HasUpdate(entry.Id), update = selectableUpdate || UpdateTarget(entry.Id) is not null;
            var frame = new Border { CornerRadius = new(8), BorderThickness = new(1), Margin = new(0, 0, 7, 7), ClipToBounds = true, Tag = entry.Id };
            frame.SetResourceReference(Border.BackgroundProperty, "StandardActionButtonBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            var overlay = new Grid { ClipToBounds = true };
            var row = new Grid { Margin = new(7, 6, 7, 6), VerticalAlignment = VerticalAlignment.Center }; row.ColumnDefinitions.Add(new() { Width = new GridLength(29) }); row.ColumnDefinitions.Add(new());
            var open = Action("", 32); open.Height = double.NaN; open.MinHeight = 0; open.Padding = new(0); open.BorderThickness = new(0); open.Background = Brushes.Transparent;
            open.HorizontalContentAlignment = HorizontalAlignment.Stretch; open.VerticalContentAlignment = VerticalAlignment.Stretch; open.Cursor = System.Windows.Input.Cursors.Hand;
            // A transparent full-size surface also receives clicks in the tile's empty space.
            var surface = new FrameworkElementFactory(typeof(Border), "TileSurface");
            surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch);
            surface.AppendChild(presenter);
            var tileTemplate = new ControlTemplate(typeof(Button)) { VisualTree = surface };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("HoverBrush"), "TileSurface")); tileTemplate.Triggers.Add(hover);
            open.Template = tileTemplate;
            var labels = new Grid(); labels.ColumnDefinitions.Add(new() { Width = new GridLength(_compact ? 34 : 40) }); labels.ColumnDefinitions.Add(new());
            var image = new Image { Source = Assets.Icon(entry.Id), Width = _compact ? 24 : 28, Height = _compact ? 24 : 28, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            _images[entry.Id] = image;
            var iconCell = new Grid { Width = _compact ? 30 : 34, Height = _compact ? 30 : 34, HorizontalAlignment = HorizontalAlignment.Left };
            var fallback = ProgramFallbackIcon.Create(_compact ? 22 : 26);
            fallback.Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed; image.Tag = fallback; iconCell.Children.Add(fallback); iconCell.Children.Add(image); labels.Children.Add(iconCell);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 3, 0) };
            var name = Text(entry.Name, _compact ? 11.5 : 12); name.FontWeight = FontWeights.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; stack.Children.Add(name);
            var description = Text(entry.Description(_english), 10); description.LineHeight = 12; description.MaxHeight = 24; description.TextTrimming = TextTrimming.WordEllipsis; description.Margin = new(0, 3, 0, 0); description.Visibility = installed ? Visibility.Collapsed : Visibility.Visible; stack.Children.Add(description);
            if (installed)
            {
                row.VerticalAlignment = VerticalAlignment.Top; row.Margin = new(7, _compact ? 4 : 6, 7, 6);
                labels.Height = _compact ? 24 : 30;
                if (_compact) { iconCell.Height = 24; image.Width = image.Height = 22; }
            }
            Grid.SetColumn(stack, 1); labels.Children.Add(stack); Grid.SetColumn(labels, 1); row.Children.Add(labels); open.Content = row; AutomationProperties.SetName(open, entry.Name + ": " + entry.Description(_english));
            open.Click += (_, _) => ShowInfo(entry, open, true);
            frame.MouseEnter += (_, _) => { _ = Assets.EnsureIconAsync(entry); };
            var check = new CheckBox { Style = (Style)_page.FindResource("UpdateSelection"), IsChecked = Selected.Contains(entry.Id), VerticalAlignment = installed ? VerticalAlignment.Top : VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = installed && !selectableUpdate ? Visibility.Collapsed : Visibility.Visible, Margin = new(7, installed ? (_compact ? 5 : 7) : 0, 0, 0) };
            AutomationProperties.SetName(check, L("Auswählen: ", "Select: ") + entry.Name); check.Checked += (_, _) => Select(entry.Id, true); check.Unchecked += (_, _) => Select(entry.Id, false);
            overlay.Children.Add(open);
            if (installed)
            {
                var layer = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
                overlay.Children.Add(layer); _bands[frame] = (layer, update);
                frame.SizeChanged += (_, _) => DrawBand(frame);
            }
            // Checkbox is above the decorative band and keeps its own click target.
            overlay.Children.Add(check); _checks.Add(check);
            frame.Child = overlay; _wrap.Children.Add(frame); _tiles.Add(frame);
        }
        FitTiles((_scroll.Parent as FrameworkElement)?.ActualHeight ?? 0); RefreshAvailability();
        PresentationChanged?.Invoke();
    }
    private void FitTiles(double availableHeight)
    {
        if (_scroll.ActualWidth <= 0 || availableHeight <= 0) return;
        var width = Math.Floor((_scroll.ActualWidth - 2) / Columns) - 7;
        int rows = _compact ? 3 : 4;
        var height = Math.Max(55, Math.Floor(availableHeight / rows) - 7);
        _scroll.Height = Math.Min(availableHeight, (height + 7) * rows);
        foreach (var tile in _tiles) { tile.Width = Math.Max(120, width); tile.Height = height; DrawBand(tile); }
    }
    private void DrawBand(Border tile)
    {
        if (!_bands.TryGetValue(tile, out var info)) return;
        var layer = info.Layer; layer.Children.Clear();
        double width = Math.Max(1, (double.IsNaN(tile.Width) ? tile.ActualWidth : tile.Width) - 2);
        double height = Math.Max(1, (double.IsNaN(tile.Height) ? tile.ActualHeight : tile.Height) - 2);
        bool compact = _compact; double thickness = compact ? 29 : 25;
        double angle = -Math.Atan2(height * .58, width) * 180 / Math.PI;
        string tileId = tile.Tag?.ToString() ?? "";
        var caption = new TextBlock { Text = info.Update ? "↻  UPDATE" : tileId == MostRecentlyInstalledId ? L("★  NEU INSTALLIERT", "★  JUST INSTALLED") : Inventory.Evidence?.GetValueOrDefault(tileId)?.Source switch
            { "Scoop" or "Chocolatey" => L("✓  PAKET ERKANNT", "✓  PACKAGE FOUND"), "Portable-Setup" => L("✓  EXE VORHANDEN", "✓  EXE PRESENT"), "Portable" => "✓  PORTABLE", _ => L("✓  INSTALLIERT", "✓  INSTALLED") }, FontSize = compact ? 12 : 11,
            FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // Standard matches the approved sample; other themes keep their accent for updates.
        string fill = info.Update ? (Palette.Colors[7] == "#13C9FF" ? "#107EAA" : Palette.Actions[3]) : "#16765C";
        var band = new Border { Width = width + 65, Height = thickness, Background = (Brush)new BrushConverter().ConvertFromString(fill)!,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(info.Update ? Palette.Colors[7] : "#51E2AD")!, BorderThickness = new(0, 1, 0, 1), Child = caption,
            RenderTransformOrigin = new(.5, .5), RenderTransform = new RotateTransform(angle) };
        // The real compact viewport can yield 55 px tiles. Keep the name above the thicker ribbon there too.
        double center = compact ? Math.Max(39.5, height * .57) : height * .65;
        Canvas.SetLeft(band, -32.5); Canvas.SetTop(band, center - thickness / 2); layer.Children.Add(band);
    }
    private void IconChanged(string id) => Dispatcher.BeginInvoke(() =>
    {
        if (_images.TryGetValue(id, out var image)) { image.Source = Assets.Icon(id); if (image.Tag is FrameworkElement fallback) fallback.Visibility = image.Source is null ? Visibility.Visible : Visibility.Collapsed; }
    });
    private void UpdateHint() => _hint.Text = (_scroll.VerticalOffset > 1 ? "↑ " : "") + (_scroll.VerticalOffset < _scroll.ScrollableHeight - 1 ? "↓ " : "")
        + (_scroll.ScrollableHeight > 1 ? L("Scrollen", "Scroll") : "");
    internal void RefreshAvailability()
    {
        if (BackgroundChecking && (IsWorking || HostBusy() || SelectionOpen)) CancelBackgroundInspection();
        var free = !IsWorking && !HostBusy() && !SelectionOpen; _install.IsEnabled = free && Selected.Any(id => !IsInstalled(id)) && !_refreshing && !_searching;
        ProgramButtonIcons.Set(_install, "install", L("Installieren", "Install"));
        var updateCount = UpdateEntries.Count;
        ProgramButtonIcons.Set(UpdateButton, "update", "Updates (" + updateCount + ")");
        UpdateButton.Visibility = updateCount > 0 && !IsWorking ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = free && !_refreshing && !_searching && updateCount > 0;
        _installedCaption.Text = L("Installierte Programme", "Installed programs");
        InstalledButton.IsEnabled = free && !_refreshing && !_searching;
        InstalledButton.Visibility = IsWorking ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(InstalledButton, _installedCaption.Text);
        UpdateCategoryVisibility();
        _refresh.IsEnabled = free && !_refreshing && !_searching; _search.IsEnabled = !IsWorking; ClearSearchButton.IsEnabled = !IsWorking; _back.IsEnabled = true;
        ProgramButtonIcons.Set(_online, _searching ? "stop" : "search", _searching ? L("Suche stoppen", "Stop search") : L("Suche online", "Search online"));
        _online.IsEnabled = free && !_refreshing && (_searching || _search.Text.Trim().Length >= 2);
        foreach (var check in _checks) check.IsEnabled = free && !_searching && !_refreshing;
        _cancel.Visibility = IsWorking ? Visibility.Visible : Visibility.Collapsed; _cancel.IsEnabled = !_stopping;
        _cancel.SetResourceReference(Button.BackgroundProperty, "DangerBrush"); _cancel.SetResourceReference(Button.BorderBrushProperty, "DangerBorderBrush"); _cancel.Foreground = Brushes.White;
        _count.Text = Selected.Count + L(" ausgewählt", " selected") + " · " + _visible.Count + L(" Programme", " programs");
        if (ProgramCatalog.AdditionalCategories.Contains(_category)) _count.Text += " · " + ProgramCatalog.Category(_category, _english);
        _status.Text = IsWorking ? _phase : _searching ? L("WinGet-Suche und Installationsprüfung …", "WinGet search and installation check …")
            : _searchStatus.Length > 0 ? _searchStatus : _refreshing ? L("Installierte Programme werden geprüft …", "Checking installed programs …")
            : Inventory.IsStale ? SavedStatus()
            : !Inventory.UpdateSourcesComplete ? L("Einige Updatequellen nicht bestätigt · später erneut prüfen", "Some update sources unverified · check again later")
            : !Inventory.Complete && Inventory.UpdateCheckComplete ? L("Updates geprüft · Installationserkennung teilweise", "Updates checked · installation detection partial")
            : Inventory.Complete ? Inventory.UpdateCheckComplete ? L("Installierte Programme bleiben sichtbar · Updates geprüft", "Installed programs remain visible · updates checked") : L("Installationen erkannt · Updateprüfung unvollständig", "Installations detected · update check incomplete")
            : L("Erkennung unvollständig · nicht gelistet bedeutet nicht sicher uninstalliert", "Detection incomplete · unlisted does not mean definitely uninstalled");
        if (!IsWorking && !_searching && !_refreshing && Inventory.StoreError is not null)
            _status.Text += L(" · Store-Prüfung fehlgeschlagen", " · Store check failed");
        ApplyCheckStatusAppearance();
        // Stage captions are driven by explicit operation events, not by general refreshes.
        _status.ToolTip = _searchStatus.Length > 0 || _searching ? _status.Text : _marks.Error ?? Inventory.StoreError ?? Inventory.Error ?? _status.Text; UpdateHint();
    }
    internal async Task RefreshAsync()
    {
        if (_refreshing || IsWorking || HostBusy() || _searching) return;
        if (BackgroundChecking && _backgroundOnline) { await _backgroundTask!; return; }
        var previousInventory = Inventory;
        ClearFinishedProgress(); CloseInfo(); _refreshing = true; var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60)); _inspectionCancellation = cancellation;
        BeginCheckStatus();
        ShowConnectionOverlay(); RefreshAvailability();
        try
        {
            await PauseBackgroundInspectionAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var inventory = await InspectInventoryAsync(cancellation.Token, false);
            if (cancellation.IsCancellationRequested || _inspectionCancellation != cancellation) return;
            Inventory = PreserveLastCandidates(previousInventory, inventory); inventory = Inventory; Selected.RemoveWhere(id => IsInstalled(id) && !Inventory.HasUpdate(id));
            FinishCheckStatus(inventory);
            if (inventory.OnlineError is not null) ShowConnectionOverlay(new OnlineConnectionException(!OnlineConnection.Available));
            else if (!inventory.Complete && !inventory.UpdateCheckComplete && inventory.Error is not null) ShowConnectionOverlay(new System.IO.IOException(inventory.Error));
            else HideConnectionOverlay();
        }
        catch (OperationCanceledException)
        {
            if (_inspectionCancellation == cancellation) InterruptCheckStatus(false);
            if (_inspectionCancellation == cancellation && _connectionLayer.Visibility == Visibility.Visible && _connectionFailure is null)
                ShowConnectionOverlay(OnlineConnection.Available ? new TimeoutException(L("Prüfung beendet oder Zeitlimit erreicht.", "Check stopped or timed out.")) : new OnlineConnectionException(true));
        }
        catch (Exception e)
        { if (_inspectionCancellation == cancellation) { InterruptCheckStatus(true); ReportProgramError(e, L("Bestandsprüfung", "Inventory check")); ShowConnectionOverlay(e); } }
        finally
        {
            if (_inspectionCancellation == cancellation) { _inspectionCancellation = null; _refreshing = false; RenderTiles(); }
            cancellation.Dispose();
        }
    }
    internal void CancelSearch()
    {
        if (_searchCancellation is not null) InterruptCheckStatus(false);
        var cancellation = _searchCancellation; _searchCancellation = null; cancellation?.Cancel(); _searching = false;
        if (_connectionLayer.Visibility == Visibility.Visible && _connectionFailure is null && !_refreshing) HideConnectionOverlay();
        RefreshAvailability();
    }
    internal async Task SearchOnlineAsync()
    {
        if (IsWorking || HostBusy() || _refreshing || _searching) return;
        var query = _search.Text.Trim(); if (query.Length < 2 || query.Length > 100) return;
        ClearFinishedProgress();
        BeginCheckStatus();
        var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation; _searching = true; _searchStatus = ""; CloseInfo(); ShowConnectionOverlay(search: true); RefreshAvailability();
        try
        {
            await PauseBackgroundInspectionAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            var results = await Service.SearchAsync(query, cancellation.Token);
            if (cancellation.IsCancellationRequested || _searchCancellation != cancellation || _search.Text.Trim() != query) return;
            foreach (var entry in results.Where(x => !ProgramCatalog.Contains(x))) _sessionEntries[entry.Id] = entry;
            _results = results; _resultQuery = query; RenderTiles();
            if (results.Count > 0) { _checkHasFreshResults = true; _checkStage = CheckStage.Partial; RefreshAvailability(); }
            var inventory = await InspectInventoryAsync(cancellation.Token, true);
            if (cancellation.IsCancellationRequested || _searchCancellation != cancellation || _search.Text.Trim() != query) return;
            Inventory = inventory; Selected.RemoveWhere(id => IsInstalled(id) && !Inventory.HasUpdate(id));
            FinishCheckStatus(inventory);
            if (inventory.OnlineError is not null) ShowConnectionOverlay(new OnlineConnectionException(!OnlineConnection.Available), true); else HideConnectionOverlay();
            _searchStatus = results.Count + L(" WinGet-Treffer · zusätzliche Ergebnisse aus der Onlinequelle", " WinGet results · additional matches from the online source");
            if (inventory.FromCache) _searchStatus += L(" · kürzlich geprüfter Bestand", " · recently checked inventory");
        }
        catch (OperationCanceledException) { if (_searchCancellation == cancellation) { InterruptCheckStatus(false); _searchStatus = L("Suche beendet oder Zeitlimit erreicht.", "Search stopped or timed out."); ShowConnectionOverlay(OnlineConnection.Available ? new TimeoutException(_searchStatus) : new OnlineConnectionException(true), true); } }
        catch (Exception e) { if (_searchCancellation == cancellation) { InterruptCheckStatus(true); _searchStatus = L("Onlinesuche fehlgeschlagen · WinGet und Verbindung prüfen.", "Online search failed · check WinGet and your connection."); ReportProgramError(e, L("Onlinesuche", "Online search")); ShowConnectionOverlay(e, true); } }
        finally
        {
            if (_searchCancellation == cancellation) { _searchCancellation = null; _searching = false; RenderTiles(); ResetSearchScroll(); StartVisibleIconWork(); }
            cancellation.Dispose();
        }
    }
    internal void RequestStop()
    { if (!IsWorking) return; if (DeveloperPane is not null) { DeveloperPane.Stop(); return; } OperationPanel?.StopRequested(); _stopping = true; _prepare?.Cancel(); (Service as IProgramCancelableUpdate)?.StopPortableDownload(); _phase = L("Stop angefordert · laufenden Vorgang zuerst beenden", "Stop requested · finish the current operation first"); RefreshAvailability(); }
    internal Task InstallAsync() => InstallEntriesAsync(KnownEntries.Where(x => Selected.Contains(x.Id) && (!IsInstalled(x.Id) || Inventory.HasUpdate(x.Id))).ToArray());
    internal bool CanOfferNewInstall(ProgramEntry entry) => entry.Id != "teamspeak" && (entry.PackageId is not null
        || entry.Id == "mpv" && Inventory.Managers?.Any(manager => manager.Present && manager.Manager is "Scoop" or "Chocolatey") == true);

    private Task InstallNewAsync() => InstallEntriesAsync(KnownEntries.Where(x => Selected.Contains(x.Id) && !IsInstalled(x.Id)).ToArray(), newOnly: true);
    internal async Task InstallEntriesAsync(ProgramEntry[] entries, bool allowReinstall = false, bool updatesOnly = false, ProgramPackage? expectedUpdate = null, bool newOnly = false)
    {
        if (IsWorking || HostBusy() || _searching || _refreshing || SelectionOpen) return;
        bool singleUpdateRequested = !allowReinstall && entries.Length == 1;
        bool scopedUpdates = !newOnly && !allowReinstall && Service is ProgramInstallService && entries.Length > 0
            && (updatesOnly || entries.All(e => Inventory.Installed.ContainsKey(e.Id)));
        SelectionOpen = true; RefreshAvailability();
        try
        {
            await PauseBackgroundInspectionAsync();
            if (!scopedUpdates && Inventory.IsStale) await RefreshAsync();
            if ((!scopedUpdates && Inventory.IsStale) || _refreshing || _searching || IsWorking || HostBusy()) return;
        }
        finally { SelectionOpen = false; RefreshAvailability(); }
        entries = entries.Where(x => KnownEntries.Any(k => ReferenceEquals(k, x)) && (scopedUpdates ? Inventory.Installed.ContainsKey(x.Id) : allowReinstall
            ? IsInstalled(x.Id) && Service is ProgramInstallService api && api.CanReinstall(x, Inventory)
            : !IsInstalled(x.Id) || !newOnly && Inventory.HasUpdate(x.Id))).DistinctBy(x => x.Id).ToArray();
        if (entries.Length == 0) return;
        IsWorking = true; _stopping = false; CloseInfo(); WorkStateChanged?.Invoke(true); _prepare = new();
        ProgramOperationLog? activeLog = null;
        ProgramInstallElevation? elevation = null;
        bool selectionDismissed = false;
        // A selected snapshot authorizes this operation, not the unrelated saved inventory.
        var workInventory = Inventory;
        _progress.Visibility = Visibility.Collapsed; _progress.IsRunning = true; RefreshAvailability();
        try
        {
            OperationPanel?.Dispose(); OperationPanel = new ProgramProgressPanel(this, entries, reinstall: allowReinstall); NotifyOverlayChanged();
            if (scopedUpdates && Service is ProgramInstallService selectedService)
            {
                _phase = L("Ausgewählte Programm-Updates prüfen …", "Checking selected program updates …");
                OperationPanel.Caption(_phase); RefreshAvailability();
                workInventory = await selectedService.InspectUpdatesAsync(entries, _prepare.Token);
                bool unconfirmed = false;
                foreach (var unavailable in entries.Where(e => !workInventory.HasUpdate(e.Id)))
                {
                    var target = workInventory.Updates?.GetValueOrDefault(unavailable.Id) ?? workInventory.GitHubUpdates?.GetValueOrDefault(unavailable.Id) ?? UpdateTarget(unavailable.Id);
                    bool current = target is not null && ProgramInstallService.CurrentEnough(workInventory.Installed.GetValueOrDefault(unavailable.Id), target);
                    await ApplyEntryCheckAsync(unavailable, workInventory.Evidence?.GetValueOrDefault(unavailable.Id), null, workInventory.CheckedUtc ?? DateTime.UtcNow, _prepare.Token, workInventory);
                    unconfirmed |= !current;
                    OperationPanel.State(unavailable.Id, current ? L("Zielversion bereits vorhanden", "Target version already present")
                        : L("Kein ausführbares Update bestätigt · Herstellerseite prüfen", "No executable update verified · check publisher website"));
                }
                entries = entries.Where(e => workInventory.HasUpdate(e.Id)).ToArray();
                if (entries.Length == 0)
                {
                    if (!unconfirmed) return;
                    throw new System.IO.IOException(workInventory.Error ?? L("Kein passendes Update bestätigt. Die Herstellerseite kann eine neuere Version anbieten; Installationsquelle und Variante müssen übereinstimmen.", "No matching update verified. The publisher may offer a newer version; installation source and variant must match."));
                }
            }
            var requestedUpdates = entries.Where(e => workInventory.HasUpdate(e.Id)).Select(e => e.Id).ToHashSet();
            var packages = new Dictionary<string, ProgramPackage?>();
            var reinstallOptions = new Dictionary<string, IReadOnlyList<ProgramPackage>>();
            var unavailablePackages = new Dictionary<string, string>();
            foreach (var entry in entries)
            {
                string? packageFailure = null;
                _prepare.Token.ThrowIfCancellationRequested(); _phase = L("Paket prüfen: ", "Checking package: ") + entry.Name; OperationPanel?.Caption(L("Auswahl wird geprüft …", "Checking selection …")); RefreshAvailability();
                try
                {
                    if (allowReinstall && Service is ProgramInstallService reinstallService)
                    {
                        var offered = await reinstallService.ResolveReinstallOptionsAsync(entry, Inventory, _prepare.Token);
                        reinstallOptions[entry.Id] = offered;
                        if (offered.Count == 0) throw new System.IO.IOException(L("Keine passende Neuinstallationsversion bestätigt. Bitte den Herstellerweg verwenden.", "No compatible reinstallation version verified. Use the publisher route."));
                        packages[entry.Id] = offered[0];
                    }
                    else packages[entry.Id] = requestedUpdates.Contains(entry.Id) && Service is ProgramInstallService updater
                        ? await updater.ResolveCheckedUpdateAsync(entry, workInventory, _prepare.Token) : await Service.ResolveAsync(entry, _prepare.Token);
                }
                catch (OperationCanceledException) when (!_prepare.IsCancellationRequested) { packages[entry.Id] = null; packageFailure = L("Paketprüfung abgelaufen", "Package check timed out"); }
                catch (Exception e) when (e is System.IO.IOException or System.Net.Http.HttpRequestException or System.ComponentModel.Win32Exception)
                { packages[entry.Id] = null; packageFailure = UiText.Error(e, _english); }
                var prepare = packages[entry.Id]?.ManagedPackage is { NeedsPreparation: true } candidate ? candidate
                    : workInventory.ManagedPackages?.GetValueOrDefault(entry.Id)?.FirstOrDefault(p => p.NeedsPreparation);
                if (prepare is not null && Service is ProgramInstallService managerService)
                {
                    string notice = L("Scoop und seine vorhandenen Paketquellen müssen vor diesem Auftrag aktualisiert werden. Dies ist ein eigener Vorbereitungsschritt. Danach bestätigst du die konkrete Programmauswahl mit den frisch geprüften Versionen. Jetzt vorbereiten?", "Scoop and its existing package sources must be refreshed before this operation. This is a separate preparation step. Afterwards you confirm the exact applications and freshly checked versions. Prepare now?");
                    bool accepted = ConfirmForTests?.Invoke(notice) ?? ThemedMessageBox.Show(Window.GetWindow(this), notice, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                    if (!accepted) { _stopping = true; return; }
                    OperationPanel?.Caption(L("Scoop und Paketquellen vorbereiten …", "Preparing Scoop and package sources …"));
                    activeLog = new("Scoop", L("Vorbereitung", "Preparation"), _english, WriteLog, WriteErrorLog);
                    activeLog.Event(L("Scoop: Vorbereitung gestartet.", "Scoop: Preparation started."));
                    await managerService.PrepareManagedAsync(prepare.Package(), () => { }, message => Dispatcher.Invoke(() => activeLog.Output(message)));
                    activeLog.Event(L("Scoop: Vorbereitung bestätigt.", "Scoop: Preparation verified."));
                    _prepare.Token.ThrowIfCancellationRequested();
                    workInventory = scopedUpdates ? await managerService.InspectUpdatesAsync(entries, _prepare.Token) : await Service.InspectAsync(_prepare.Token);
                    if (!scopedUpdates) Inventory = workInventory;
                    if (allowReinstall)
                    {
                        var offered = await managerService.ResolveReinstallOptionsAsync(entry, Inventory, _prepare.Token); reinstallOptions[entry.Id] = offered;
                        packages[entry.Id] = offered.FirstOrDefault();
                    }
                    else packages[entry.Id] = scopedUpdates
                        ? await managerService.ResolveCheckedUpdateAsync(entry, workInventory, _prepare.Token) : await Service.ResolveAsync(entry, _prepare.Token);
                    if (packages[entry.Id]?.ManagedPackage is { NeedsPreparation: true }) throw new System.IO.IOException(L("Paketquellen sind noch nicht bestätigt. Bitte erneut prüfen.", "Package sources are still unverified. Check again."));
                }
                if (packageFailure is not null) unavailablePackages[entry.Id] = packageFailure;
                OperationPanel?.State(entry.Id, packageFailure ?? L("Bereit", "Ready"), false);
            }
            _prepare.Token.ThrowIfCancellationRequested();
            if (allowReinstall && packages.Values.Any(p => p is null))
                throw new System.IO.IOException(L("Neuinstallation nicht bestätigt. Es wurde nichts gestartet. Bitte Herstellerseite oder Installationsquelle prüfen.", "Reinstallation not verified. Nothing was started. Check the publisher website or installation source."));
            foreach (var key in packages.Keys.ToArray()) if (packages[key]?.CanInstall == false) packages[key] = null;
            if (expectedUpdate is not null && expectedUpdate.ManagedPackage is not { NeedsPreparation: true }
                && (packages.GetValueOrDefault(entries[0].Id) is not { } confirmed || expectedUpdate.Id != confirmed.Id
                    || expectedUpdate.Version != confirmed.Version || expectedUpdate.VersionSource != confirmed.VersionSource
                    || !ProgramInstallService.CheckedUpdateMatches(entries[0], expectedUpdate, workInventory)))
                throw new System.IO.IOException(L("Updateangebot hat sich geändert. Bitte das Programm erneut prüfen.", "The update offer changed. Check the application again."));
            if (scopedUpdates && entries.Length == 1 && packages[entries[0].Id] is null)
                throw new System.IO.IOException(L("Passender Installer nicht bestätigt. Bitte die Herstellerseite prüfen.", "A matching installer could not be verified. Check the publisher website."));
            if (!allowReinstall && Service is ProgramInstallService targetService)
            {
                using var targetGate = new SemaphoreSlim(3);
                var targets = await Task.WhenAll(entries.Where(e => !IsInstalled(e.Id) && packages[e.Id] is not null).Select(async e =>
                {
                    await targetGate.WaitAsync(_prepare.Token);
                    try { return (e.Id, Proof: await targetService.ResolveTargetAsync(packages[e.Id]!, _prepare.Token)); }
                    finally { targetGate.Release(); }
                }));
                foreach (var target in targets) packages[target.Id] = packages[target.Id]! with { TargetProof = target.Proof };
            }
            var question = L("Diese Auswahl ausführen?\n\n", "Continue with this selection?\n\n") + string.Join("\n", entries.Select(x => x.Name + (allowReinstall && IsInstalled(x.Id) ? L(" · Neuinstallation", " · Reinstall") : requestedUpdates.Contains(x.Id) ? L(" · Update", " · Update") : L(" · Installation", " · Install")) + (packages[x.Id] is { } p ? " · " + ProgramInstallService.PackageSource(p, _english) + " " + (p.Version ?? "") : L(" · kein Installer bestätigt", " · no installer verified"))))
                + L("\n\nLizenzen und Administratorabfragen bleiben sichtbar. Bestätigte portable Updates ersetzen nur die Programmdatei und sichern die vorherige Datei unter AppData. Ein gestarteter Installer wird nicht gewaltsam beendet.", "\n\nLicense and administrator prompts remain visible. Verified portable updates replace only the executable and back up the previous file under AppData. A started installer will not be forcibly terminated.");
            IReadOnlyList<ProgramSelection>? choices;
            if (singleUpdateRequested && entries.Length == 1 && requestedUpdates.Contains(entries[0].Id)
                && packages[entries[0].Id] is { CanInstall: true, Version: not null } singlePackage)
                choices = [new(entries[0], singlePackage)];
            else if (ConfirmForTests is not null) choices = ConfirmForTests(question) ? entries.Select(e => new ProgramSelection(e, packages[e.Id])).ToArray() : null;
            else
            {
                var options = new List<ProgramSelection>();
                foreach (var entry in entries)
                {
                    PortablePackage? portable = null;
                    if (!IsInstalled(entry.Id) && entry.Id == "obs-studio")
                    {
                        try { portable = await PortableService.ResolveAsync(entry, _prepare.Token); }
                        catch (Exception e) when (e is System.IO.IOException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
                        { _prepare.Token.ThrowIfCancellationRequested(); WriteLog?.Invoke(entry.Name + L(": Portable-Quelle nicht bestätigt: ", ": portable source not verified: ") + UiText.Error(e, _english)); }
                    }
                    var managerOptions = !allowReinstall && workInventory.ManagedPackages?.GetValueOrDefault(entry.Id) is { } offeredManagers
                        ? offeredManagers.Where(p => p.IsUpdate).Select(p => p.Package()).ToArray() : null;
                    if (packages[entry.Id] is null && portable is null)
                    {
                        ReportMissingInstaller(entry);
                        continue;
                    }
                    options.Add(new(entry, packages[entry.Id], portable, Reinstall: allowReinstall && IsInstalled(entry.Id), ReinstallOptions: reinstallOptions.GetValueOrDefault(entry.Id) ?? managerOptions));
                }
                if (options.Count == 0) return;
                choices = await ProgramSelectionDialog.ShowAsync(this, options, PortableFolderSelector, workInventory);
            }
            if (choices is null) { selectionDismissed = true; _stopping = true; return; }
            if (_stopping) return;
            void ReportMissingInstaller(ProgramEntry entry)
            {
                var reason = unavailablePackages.GetValueOrDefault(entry.Id)
                    ?? L("Kein passender Installer bestätigt. Prüfe die Paketquelle und versuche es erneut.", "No matching installer was verified. Check the package source and try again.");
                OperationPanel?.State(entry.Id, reason);
                WriteLog?.Invoke(entry.Name + ": " + reason);
            }
            HashSet<string> elevatedEntries = choices.Count > 1 && Service is ProgramInstallService { UsesWindowsInstallers: true } && !ElevationService.IsElevated()
                ? choices.Where(c => ProgramInstallElevation.Applies(c, workInventory, allowReinstall)).Select(c => c.Entry.Id).ToHashSet()
                : [];
            if (elevatedEntries.Count != 0 && !choices.Any(c => c.Installer?.ManagedPackage?.Target.Scope == "Machine" && !elevatedEntries.Contains(c.Entry.Id)))
            {
                _phase = L("Windows-Adminbestätigung für die Auswahl …", "Waiting for Windows administrator approval …");
                OperationPanel?.Caption(_phase); RefreshAvailability();
                try { elevation = await ProgramInstallElevation.StartAsync(_prepare.Token); }
                catch (Exception error) when (error is System.IO.IOException or System.ComponentModel.Win32Exception)
                {
                    foreach (var choice in choices) OperationPanel?.State(choice.Entry.Id, L("Adminstart fehlgeschlagen · siehe Protokoll", "Administrator start failed · see log"));
                    throw;
                }
                if (elevation is null)
                {
                    foreach (var choice in choices)
                        OperationPanel?.State(choice.Entry.Id, L("Adminabfrage abgebrochen · nicht gestartet", "Administrator prompt cancelled · not started"));
                    _stopping = true;
                    return;
                }
            }
            if (Service is ProgramInstallService && choices.Any(c => c.Installer?.ManagedPackage?.Target.Scope == "Machine") && !ElevationService.IsElevated())
            {
                string notice = L("Diese Paketauswahl benötigt Administratorrechte. Die App jetzt mit der Windows-Bestätigung neu starten? Anschließend bitte die gewünschte Auswahl erneut prüfen und bestätigen. Es wurde noch keine Programminstallation gestartet.", "This package selection needs administrator privileges. Restart the app with the Windows confirmation now? Afterwards, check and confirm the selection again. No application installation has started yet.");
                bool accepted = ConfirmForTests?.Invoke(notice) ?? ThemedMessageBox.Show(Window.GetWindow(this), notice, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                if (accepted && ElevationService.TryRestartAsAdministrator(false)) _ = Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
                _stopping = true; return;
            }
            foreach (var choice in choices)
            {
                if (_stopping) break;
                var entry = choice.Entry;
                OperationPanel?.Current(entry, L("Prüfen", "Checking"), choice.Installer);
                OperationPanel?.Step(entry.Id, new(ProgramStep.Check, ProgramStepState.Active));
                if (choice.Portable is { } portable && choice.Parent is { } parent)
                {
                    try
                    {
                        _phase = (entry.Id == "vlc" ? L("VLC-ZIP herunterladen: ", "Downloading VLC ZIP: ") : L("Portable herunterladen: ", "Downloading portable: ")) + entry.Name;
                        OperationPanel?.Current(entry, _phase, new(entry.Id, portable.Version, portable.Url, DateTime.UtcNow, VersionSource: "Publisher"),
                            entry.Id == "vlc" ? "VideoLAN · VLC ZIP" : entry.Publisher + " · Portable"); RefreshAvailability();
                        OperationPanel?.Step(entry.Id, new(ProgramStep.Check, ProgramStepState.Done));
                        OperationPanel?.Step(entry.Id, new(ProgramStep.Download, ProgramStepState.Active));
                        void PortableStep(ProgramStepEvent e) => Dispatcher.Invoke(() => OperationPanel?.Step(entry.Id, e));
                        var stageSource = PortableService as IProgramStepSource;
                        if (stageSource is not null) stageSource.StepChanged += PortableStep;
                        var progress = new Progress<double?>(value => { _progress.IsIndeterminate = value is null; if (value is { } percent) _progress.Value = percent; OperationPanel?.Measurement(value); });
                        string executable;
                        try { executable = await PortableService.InstallAsync(entry, portable, parent, progress, _prepare.Token); }
                        finally { if (stageSource is not null) stageSource.StepChanged -= PortableStep; }
                        OperationPanel?.Step(entry.Id, new(ProgramStep.Install, ProgramStepState.Done));
                        OperationPanel?.Step(entry.Id, new(ProgramStep.Finish, ProgramStepState.Active));
                        new ProgramPortableStore(Assets.PortableFile).Remember(entry.Id, executable);
                        RecordInstalled(entry.Id);
                        string configured = entry.Id == "vlc" ? L("ZIP eingerichtet", "ZIP configured") : L("Portable eingerichtet", "Portable configured");
                        WriteLog?.Invoke(entry.Name + ": " + configured + ": " + executable);
                        Selected.Remove(entry.Id); Inventory = await Service.InspectAsync(CancellationToken.None); OperationPanel?.Location(entry.Id, executable); if (choice.DesktopShortcut) OperationPanel?.ShortcutResult(entry.Id, CreateDesktopShortcut(entry, executable)); OperationPanel?.State(entry.Id, configured); RenderTiles();
                    }
                    catch (OperationCanceledException) when (_stopping) { OperationPanel?.State(entry.Id, L("Gestoppt", "Stopped")); break; }
                    catch (Exception e) { OperationPanel?.State(entry.Id, L("Fehler · siehe Protokoll", "Error · see log")); WriteLog?.Invoke(entry.Name + ": " + UiText.Error(e, _english)); }
                    finally { _progress.IsIndeterminate = true; }
                    continue;
                }
                bool update = !allowReinstall && requestedUpdates.Contains(entry.Id);
                if (update)
                {
                    _phase = L("Update erneut bestätigen: ", "Revalidating update: ") + entry.Name; RefreshAvailability();
                    workInventory = Service is ProgramInstallService selected ? await selected.InspectUpdatesAsync([entry], _prepare.Token) : await Service.InspectAsync(_prepare.Token);
                    if (Service is not ProgramInstallService) Inventory = workInventory;
                    if (_stopping) break;
                    if (!workInventory.HasUpdate(entry.Id))
                    {
                        if (Service is ProgramInstallService) await ApplyEntryCheckAsync(entry, workInventory.Evidence?.GetValueOrDefault(entry.Id), null, workInventory.CheckedUtc ?? DateTime.UtcNow, _prepare.Token, workInventory);
                        OperationPanel?.State(entry.Id, L("Update nicht mehr bestätigt · erneut prüfen", "Update no longer verified · check again")); WriteLog?.Invoke(entry.Name + L(": Update nicht mehr bestätigt; bitte erneut prüfen.", ": update no longer confirmed; please check again.")); continue;
                    }
                }
                bool reinstall = allowReinstall && IsInstalled(entry.Id);
                if (reinstall && Service is ProgramInstallService reinstallBackend)
                {
                    _phase = L("Neuinstallation erneut bestätigen: ", "Revalidating reinstallation: ") + entry.Name; RefreshAvailability();
                    Inventory = await Service.InspectAsync(_prepare.Token);
                    if (_stopping) break;
                    if (choice.Installer is not { } selectedPackage || !reinstallBackend.ReinstallStillMatches(entry, selectedPackage, Inventory))
                    { OperationPanel?.State(entry.Id, L("Installationsstand geändert · erneut prüfen", "Installed state changed · check again")); continue; }
                }
                if (IsInstalled(entry.Id) && !update && !reinstall) { OperationPanel?.Step(entry.Id, new(ProgramStep.Check, ProgramStepState.Done)); OperationPanel?.Step(entry.Id, new(ProgramStep.Download, ProgramStepState.Skipped)); OperationPanel?.Step(entry.Id, new(ProgramStep.Install, ProgramStepState.Skipped)); Selected.Remove(entry.Id); OperationPanel?.State(entry.Id, L("Bereits installiert", "Already installed")); continue; }
                bool updateChoiceConfirmed = ProgramInstallService.CheckedUpdateMatches(entry, choice.Installer, workInventory);
                if (update && !updateChoiceConfirmed)
                { OperationPanel?.State(entry.Id, L("Updateversion geändert · erneut prüfen", "Update version changed · check again")); WriteLog?.Invoke(entry.Name + L(": Updateversion nicht mehr bestätigt; bitte erneut prüfen.", ": update version no longer confirmed; please check again.")); continue; }
                if (choice.Installer is not { } package)
                { ReportMissingInstaller(entry); continue; }
                if (update && Service is ProgramInstallService closing)
                {
                    try
                    {
                        _phase = L("Programm wird beendet: ", "Closing application: ") + entry.Name; OperationPanel?.Current(entry, _phase, package); RefreshAvailability();
                        bool closed = await closing.CloseForUpdateAsync(entry, workInventory.Evidence?.GetValueOrDefault(entry.Id), _prepare.Token);
                        _prepare.Token.ThrowIfCancellationRequested();
                        if (closed)
                        {
                            workInventory = await closing.InspectUpdatesAsync([entry], _prepare.Token);
                            bool current = package.ManagedPackage is { } managedAfterClose
                                ? ProgramManagedPackages.ResultVerified(managedAfterClose, workInventory, entry.Id)
                                : workInventory.Installed.TryGetValue(entry.Id, out var now) && ProgramInstallService.CurrentEnough(now, package.Version ?? "");
                            if (!ProgramInstallService.CheckedUpdateMatches(entry, package, workInventory))
                            {
                                await ApplyEntryCheckAsync(entry, workInventory.Evidence?.GetValueOrDefault(entry.Id), null, workInventory.CheckedUtc ?? DateTime.UtcNow, _prepare.Token, workInventory);
                                OperationPanel?.State(entry.Id, current ? L("Zielversion bereits vorhanden", "Target version already present") : L("Prüfstand geändert · erneut prüfen", "Checked state changed · check again")); continue;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_stopping) { OperationPanel?.State(entry.Id, L("Gestoppt", "Stopped")); break; }
                    catch (Exception error)
                    {
                        string message = UiText.Error(error, _english); OperationPanel?.State(entry.Id, L("Schließen fehlgeschlagen · siehe Protokoll", "Could not close · see log"));
                        new ProgramOperationLog(entry.Name, L("Update vorbereiten", "Prepare update"), _english, WriteLog, WriteErrorLog).Error(message, "0x" + error.HResult.ToString("X8")); continue;
                    }
                }
                OperationPanel?.Step(entry.Id, new(ProgramStep.Check, ProgramStepState.Done));
                _phase = ProgramInstallService.PackageSource(package, _english) + L(" wird gestartet: ", " starting: ") + entry.Name; OperationPanel?.Current(entry, _phase, package, installedVersion: workInventory.Installed.GetValueOrDefault(entry.Id)); RefreshAvailability();
                int code;
                var operationLog = activeLog = new(entry.Name, reinstall ? L("Neuinstallation", "Reinstall") : update ? "Update" : L("Installation", "Installation"), _english, WriteLog, WriteErrorLog);
                operationLog.Event(entry.Name + ": " + ProgramInstallService.PackageSource(package, _english) + " · " + (package.Version ?? L("Version unbekannt", "Version unknown")) + L(" · Vorgang gestartet.", " · Operation started."));
                void OnStep(ProgramStepEvent e) => Dispatcher.Invoke(() =>
                {
                    OperationPanel?.Step(entry.Id, e);
                    string label = e.Step switch { ProgramStep.Download => "Download", ProgramStep.Install => L("Installation", "Installation"), ProgramStep.Finish => L("Abschlussprüfung", "Verification"), _ => L("Prüfen", "Check") };
                    if (e.State == ProgramStepState.Active && !_stopping) { _phase = label + ": " + entry.Name; RefreshAvailability(); }
                    if (e.State == ProgramStepState.Done) operationLog.Event(entry.Name + ": " + label + L(" bestätigt.", " verified."));
                });
                var source = Service as IProgramStepSource;
                var downloadSource = Service as IProgramDownloadSource;
                var diagnosticSource = Service as IProgramDiagnosticSource;
                void OnDiagnostic(string message) => Dispatcher.Invoke(() => operationLog.Output(message));
                void OnDownload(ProgramDownload value) => Dispatcher.Invoke(() => OperationPanel?.Download(value));
                if (source is not null) source.StepChanged += OnStep;
                if (downloadSource is not null) downloadSource.DownloadChanged += OnDownload;
                if (diagnosticSource is not null) diagnosticSource.Diagnostic += OnDiagnostic;
                try
                {
                    if (elevation is not null && elevatedEntries.Contains(entry.Id))
                    {
                        var tracker = new ProgramWinGetSteps(OnStep, package.Id, OnDownload);
                        code = await elevation.InstallAsync(choice, line => { tracker.Line(line); operationLog.Output(line); }, _prepare.Token);
                    }
                    else code = update ? await Service.UpgradeAsync(entry, package, () => { })
                        : reinstall && Service is ProgramInstallService direct ? await direct.ReinstallAsync(entry, package, Inventory, () => { }, _prepare.Token)
                        : choice.TargetPath is { } destination && Service is ProgramInstallService installer ? await installer.InstallAtAsync(entry, package, destination, () => { }, _prepare.Token)
                        : await Service.InstallAsync(entry, package, () => { });
                }
                catch (OperationCanceledException) when (_stopping) { OperationPanel?.State(entry.Id, L("Gestoppt", "Stopped")); break; }
                catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)
                { OperationPanel?.State(entry.Id, L("Abgebrochen", "Cancelled")); operationLog.Event(entry.Name + L(": Vom Benutzer abgebrochen.", ": Cancelled by the user.")); continue; }
                catch (Exception e) { OperationPanel?.State(entry.Id, L("Fehler · siehe Protokoll", "Error · see log")); operationLog.Error(UiText.Error(e, _english), "0x" + e.HResult.ToString("X8")); continue; }
                finally { if (source is not null) source.StepChanged -= OnStep; if (downloadSource is not null) downloadSource.DownloadChanged -= OnDownload; if (diagnosticSource is not null) diagnosticSource.Diagnostic -= OnDiagnostic; }
                if (!elevatedEntries.Contains(entry.Id) && Service is ProgramInstallService { LastInstallOutput.Length: > 0 } backend) operationLog.Output(backend.LastInstallOutput);
                operationLog.ProcessEnded(code);
                operationLog.Event(entry.Name + ": " + ProgramInstallService.PackageSource(package, _english) + L(" beendet, Code ", " exited, code ") + "0x" + code.ToString("X8"));
                if (code == 0 && package.ManagedPackage is null) OperationPanel?.Step(entry.Id, new(ProgramStep.Install, ProgramStepState.Done));
                OperationPanel?.Step(entry.Id, new(ProgramStep.Finish, ProgramStepState.Active));
                OperationPanel?.State(entry.Id, L("Abschlussprüfung", "Verifying result"), false);
                // Process exit alone is not installation proof. Keep locks until it has exited, then inspect again.
                workInventory = update && Service is ProgramInstallService checkedService ? await checkedService.InspectUpdatesAsync([entry], CancellationToken.None) : await Service.InspectAsync(CancellationToken.None);
                if (update && Service is ProgramInstallService)
                    await ApplyEntryCheckAsync(entry, workInventory.Evidence?.GetValueOrDefault(entry.Id), package, workInventory.CheckedUtc ?? DateTime.UtcNow, CancellationToken.None, workInventory);
                else Inventory = workInventory;
                bool ResultVerified() => package.ManagedPackage is { } managedResult ? ProgramManagedPackages.ResultVerified(managedResult, workInventory, entry.Id)
                    : package.PortableUpdate is { } portableUpdate
                    ? workInventory.Evidence?.GetValueOrDefault(entry.Id) is { } proof && proof.Path == portableUpdate.Path
                        && ProgramPortableUpdates.SameVersion(ProgramPortableUpdates.FileVersion(entry.Id, portableUpdate.Path) ?? "", portableUpdate.Version)
                        && !workInventory.HasUpdate(entry.Id)
                    : package.VersionSource is "msstore" or "terminal-msix"
                    ? workInventory.StoreCheckComplete && workInventory.Evidence?.GetValueOrDefault(entry.Id) is { Source: "Windows-App", Version: { } storeVersion }
                        && ProgramInstallService.CurrentEnough(storeVersion, package.Version!)
                    : package.ReinstallProof?.Installed.VlcCopy is not null && Service is ProgramInstallService vlcResult
                    ? vlcResult.ReinstallResultVerified(entry, package, workInventory)
                    : workInventory.Installed.TryGetValue(entry.Id, out var detected) && (reinstall
                        ? Service is ProgramInstallService reinstalled && reinstalled.ReinstallResultVerified(entry, package, Inventory)
                        : !update || detected == package.Version && !workInventory.HasUpdate(entry.Id))
                        && (choice.TargetPath is null || ProgramInstallTargets.MatchesLocation(Inventory.Evidence?.GetValueOrDefault(entry.Id)?.Path, choice.TargetPath));
                bool resultVerified = ResultVerified();
                if (package.ReinstallProof?.Installed.VlcCopy is { } selectedCopy && resultVerified)
                    OperationPanel?.Location(entry.Id, selectedCopy.Executable, confirmed: true);
                if (choice.TargetPath is { } requested)
                {
                    string? actual = Inventory.Evidence?.GetValueOrDefault(entry.Id)?.Path;
                    WriteLog?.Invoke(entry.Name + L(": Gewählter Zielordner: ", ": Selected destination: ") + requested
                        + L(" · Erkannter Speicherort: ", " · Detected location: ") + (actual ?? L("nicht ermittelbar", "unavailable")));
                    if (actual is not null) OperationPanel?.Location(entry.Id, actual);
                    if (!ProgramInstallTargets.MatchesLocation(actual, requested)) WriteLog?.Invoke(L("Der gewählte Zielordner wurde nicht bestätigt. Bitte den Installer und Speicherort prüfen.", "The selected destination was not verified. Check the installer and detected location."));
                }
                if (code == 0 && resultVerified)
                {
                    if (package.ManagedPackage is not null) OperationPanel?.Step(entry.Id, new(ProgramStep.Install, ProgramStepState.Done));
                    if (!update) RecordInstalled(entry.Id);
                    if (!update && !reinstall && choice.DesktopShortcut)
                    {
                        var proof = workInventory.Evidence?.GetValueOrDefault(entry.Id);
                        string? executable = await Task.Run(() => DesktopExecutable(entry, proof));
                        OperationPanel?.ShortcutResult(entry.Id, CreateDesktopShortcut(entry, executable));
                    }
                    Selected.Remove(entry.Id); WriteLog?.Invoke(entry.Name + (reinstall ? L(": Neuinstallation bestätigt.", ": reinstallation verified.") : L(": Installation/Update bestätigt.", ": installation/update verified.")));
                }
                else if (code is 3010 or unchecked((int)0x8A150109) or unchecked((int)0x8A15010A) or unchecked((int)0x8A15010B)) WriteLog?.Invoke(entry.Name + L(": Neustart erforderlich; anschließend erneut prüfen.", ": restart required; check again afterwards."));
                else if (code is 1602 or unchecked((int)0x8A15010C)) WriteLog?.Invoke(entry.Name + L(": Installation vom Benutzer abgebrochen.", ": installation cancelled by the user."));
                else WriteLog?.Invoke(entry.Name + ": " + (reinstall && Service is ProgramInstallService finalService
                    ? finalService.ReinstallFailureReason(entry, package, Inventory, _english)
                    : L("Zielversion nicht bestätigt; bitte erneut prüfen. Eintrag bleibt sichtbar.", "Target version not verified; please check again. Entry remains visible.")));
                OperationPanel?.State(entry.Id, code == 0 && resultVerified
                    ? L("Bestätigt", "Verified") : code is 3010 or unchecked((int)0x8A150109) or unchecked((int)0x8A15010A) or unchecked((int)0x8A15010B)
                    ? L("Neustart erforderlich", "Restart required") : (package.VersionSource is "msstore" or "terminal-msix") && code != 0 ? $"WinGet · 0x{code:X8}" : L("Nicht bestätigt · siehe Protokoll", "Not verified · see log"));
                RenderTiles();
            }
        }
        catch (OperationCanceledException) { WriteLog?.Invoke(L("Programmauswahl gestoppt.", "Program queue stopped.")); }
        catch (Exception e)
        {
            activeLog ??= new(L("Programme", "Programs"), L("Vorbereitung", "Preparation"), _english, WriteLog, WriteErrorLog);
            activeLog.Error(UiText.Error(e, _english), "0x" + e.HResult.ToString("X8"));
        }
        finally
        {
            if (entries.Any(e => e.Id == "tukui-client") && Service is ProgramInstallService tukuiService) tukuiService.ClearTukuiPackage();
            if (elevation is not null) await elevation.DisposeAsync();
            _prepare?.Dispose(); _prepare = null; IsWorking = false; _progress.IsRunning = false; _progress.Visibility = Visibility.Collapsed;
            if (selectionDismissed) { OperationPanel?.Dispose(); OperationPanel = null; }
            else OperationPanel?.Finish(_stopping);
            WorkStateChanged?.Invoke(false); RenderTiles();
        }
    }
    internal async void ShowInfo(ProgramEntry entry, FrameworkElement anchor, bool focus)
    {
        bool waitingForReaders = BackgroundChecking;
        var request = ++_infoRequest;
        await PauseBackgroundInspectionAsync();
        if (request != _infoRequest || waitingForReaders && !IsVisible || IsWorking || _refreshing || _searching || SelectionOpen) return;
        ClearFinishedProgress();
        if (_info is { IsOpen: true } && _info.EntryId == entry.Id) { if (focus) _info.Frame.Focus(); _info.ResetIdle(); return; }
        CloseInfo(); _ = Assets.EnsureIconAsync(entry); _info = ProgramInfoCard.Open(this, entry, anchor, focus); NotifyOverlayChanged();
    }
    private long _infoRequest;
    internal void CloseInfo() { _infoRequest++; _hover.Stop(); _info?.Close(); _info = null; }
}
