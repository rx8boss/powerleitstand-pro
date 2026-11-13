using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

public sealed class ManufacturerSoftwareView : UserControl
{
    private readonly WindowsUpdatesPage _page;
    private readonly List<Border> _cards = [];
    private readonly Dictionary<string, Button> _primary = [], _websites = [];
    private IReadOnlyDictionary<string, ManufacturerState> _states = new Dictionary<string, ManufacturerState>();
    private bool _english, _compact, _refreshing, _working;
    private ScrollViewer? _scroll;
    private TextBlock? _hint;
    private ThemePalette _palette = ThemePalette.For("Standard");
    internal IManufacturerService Service { get; set; } = new ManufacturerService();
    internal Func<bool> HostBusy { get; set; } = () => false;
    internal Action<bool>? WorkStateChanged { get; set; }
    internal Action? Back { get; set; }
    internal Action? PresentationChanged { get; set; }
    internal Action<string>? WriteLog { get; set; }
    internal Action<AppLogError>? WriteErrorLog { get; set; }
    private void ReportError(Exception error, string operation)
    {
        if (error is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }) return;
        if (WriteErrorLog is not null) WriteErrorLog(new(L("Herstellersoftware", "Manufacturer software"), operation, UiText.Error(error, _english), "0x" + error.HResult.ToString("X8")));
        else WriteLog?.Invoke(UiText.Error(error, _english));
    }
    internal IReadOnlyList<Border> CardSurfaces => _cards;
    internal IReadOnlyDictionary<string, Button> PrimaryButtons => _primary;
    internal IReadOnlyDictionary<string, ManufacturerState> States => _states;
    internal bool IsWorking => _working;
    private string L(string de, string en) => _english ? en : de;

    internal ManufacturerSoftwareView(WindowsUpdatesPage page)
    {
        _page = page; UseLayoutRounding = true; SnapsToDevicePixels = true;
        Build();
        SizeChanged += (_, _) =>
        {
            var compact = ActualWidth < 1000 || ActualHeight < 470;
            if (_compact != compact) { _compact = compact; Build(); PresentationChanged?.Invoke(); }
        };
    }
    internal void SetLanguage(bool english) { if (_english == english) return; _english = english; Build(); PresentationChanged?.Invoke(); }
    internal void SetPalette(ThemePalette palette) { _palette = palette; FontFamily = new FontFamily(palette.FontFamily); }
    internal async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true; RefreshAvailability();
        try { _states = await Service.InspectAsync(); }
        catch (Exception exception) { ReportError(exception, L("Prüfung", "Check")); _states = new Dictionary<string, ManufacturerState>(); }
        finally { _refreshing = false; UpdateCaptions(); }
    }
    private TextBlock Text(string value, double size, string brush = "TextBrush")
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush); return text;
    }
    private static System.Windows.Shapes.Path Icon(string data, double size)
    {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.7, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush"); return icon;
    }
    private Button Action(string label, string data)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Icon(data, _compact ? 13 : 16); icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextBrush"); icon.Margin = new Thickness(0,0,7,0);
        panel.Children.Add(icon); panel.Children.Add(Text(label, _compact ? 11 : 12));
        var button = new Button { Content = panel, Width = _english ? (_compact ? 158 : 178) : (_compact ? 138 : 150), Height = _compact ? 23 : 31, Padding = new Thickness(5,0,5,0), Style = (Style)_page.FindResource("UpdatesAction"), IsEnabled = true };
        AutomationProperties.SetName(button, label); return button;
    }
    private void Build()
    {
        var oldOffset = _scroll?.VerticalOffset ?? 0;
        _cards.Clear(); _primary.Clear(); _websites.Clear();
        var root = new Grid { Margin = new Thickness(5,3,5,3) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(_compact ? 18 : 22) });
        root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = new GridLength(27) });
        var header = new Border { Style = (Style)_page.FindResource("UpdatesCard"), Margin = new Thickness(0), Padding = new Thickness(12,6,12,6), Height = _compact ? 56 : 72, ClipToBounds = true };
        var hg = new Grid(); hg.ColumnDefinitions.Add(new() { Width = new GridLength(44) }); hg.ColumnDefinitions.Add(new()); hg.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); hg.ColumnDefinitions.Add(new() { Width = new GridLength(120) });
        hg.Children.Add(Icon("M 2,5 H 24 V 23 H 2 Z M 2,10 H 24 M 7,2 V 7 M 19,2 V 7 M 7,15 L 11,19 L 19,12", _compact ? 26 : 32));
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; heading.Children.Add(Text(L("Hersteller-Software", "Manufacturer software"), _compact ? 20 : 24));
        heading.Children.Add(Text(L("Aktuelle passende Version · Modellprüfung vor Installation", "Current matching version · model check before installation"), _compact ? 9 : 11, "SubtleTextBrush")); Grid.SetColumn(heading,1); hg.Children.Add(heading);
        var back = Action(L("Zurück zur Übersicht", "Back to overview"), "M 15,2 L 5,12 L 15,22"); back.Name = "ManufacturersBackButton";
        back.Width = 172; back.Height = _compact ? 32 : 36; back.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(back,2); hg.Children.Add(back); back.Click += (_, _) => Back?.Invoke();
        var stripes = new System.Windows.Shapes.Path { Name = "ManufacturersStripes", Data = Geometry.Parse("M 65,0 L 67,0 L 8,54 L 6,54 Z M 90,0 L 92,0 L 33,54 L 31,54 Z M 94,0 L 110,0 L 51,54 L 35,54 Z"), Width = _compact ? 78 : 100, Height = _compact ? 44 : 54, Stretch = Stretch.Fill, Opacity = .18, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
        stripes.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush"); Grid.SetColumn(stripes,3); hg.Children.Add(stripes);
        header.Child = hg; root.Children.Add(header); _cards.Add(header);
        var list = new StackPanel();
        var scroll = _scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        Grid.SetRow(scroll,2); root.Children.Add(scroll);
        const string chip = "M 4,4 H 20 V 20 H 4 Z M 8,0 V 4 M 16,0 V 4 M 8,20 V 24 M 16,20 V 24 M 0,8 H 4 M 0,16 H 4 M 20,8 H 24 M 20,16 H 24";
        for (var i = 0; i < ManufacturerCatalog.Apps.Length; i++)
        {
            var app = ManufacturerCatalog.Apps[i];
            var card = new Border { Style = (Style)_page.FindResource("UpdatesCard"), Margin = new Thickness(0,0,0,6), Padding = new Thickness(12,3,12,3) };
            var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(44) }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            grid.Children.Add(Icon(i < 2 ? "M 2,5 H 23 V 20 H 2 Z M 6,9 H 18 V 16 H 6 Z M 23,9 H 28 V 16 H 23 M 7,20 V 24 M 13,20 V 24" : i is >= 4 and <= 6 ? "M 6,2 H 21 L 25,21 H 2 Z M 3,15 H 24 M 17,18 H 20" : chip, _compact ? 25 : 31));
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
            var name = Text(app.Title(_english), _compact ? 13 : 18); name.FontWeight = FontWeights.SemiBold; labels.Children.Add(name);
            labels.Children.Add(Text(app.Summary(_english), _compact ? 9 : 11, "SubtleTextBrush"));
            labels.Children.Add(Text(new Uri(app.ProductPage).Host + " ↗", _compact ? 9 : 10, "AccentBrush")); Grid.SetColumn(labels,1); grid.Children.Add(labels);
            var buttons = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var primary = Action(L("Installieren", "Install"), "M 12,1 V 16 M 7,11 L 12,16 L 17,11 M 2,17 V 23 H 22 V 17");
            primary.Tag = app.Id; primary.Margin = new Thickness(0,0,0,_compact ? 3 : 5); buttons.Children.Add(primary); _primary[app.Id] = primary;
            primary.Click += async (_, _) => await ActivateAsync(app);
            var website = Action(L("Herstellerseite", "Manufacturer website"), "M 12,2 H 22 V 12 M 22,2 L 10,14 M 9,5 H 3 V 22 H 20 V 16");
            website.Tag = app.Id; buttons.Children.Add(website); _websites[app.Id] = website;
            website.Click += (_, _) => OpenWebsite(app);
            Grid.SetColumn(buttons,2); grid.Children.Add(buttons); card.Child = grid; _cards.Add(card); list.Children.Add(card);
        }
        void Fit()
        {
            if (scroll.ActualHeight <= 0) return;
            // Four rows fit at normal text size. With enlarged text keep controls readable and scroll.
            foreach (var card in _cards.Skip(1)) card.MinHeight = _compact ? 58 : 78;
            foreach (var card in _cards.Skip(1)) card.Height = Math.Max(scroll.ActualHeight / 4 - 6, _compact ? 58 : 78);
        }
        scroll.SizeChanged += (_, _) => Fit();
        _hint = Text("", 10, "SubtleTextBrush"); _hint.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(_hint,3); root.Children.Add(_hint);
        scroll.ScrollChanged += (_, _) => UpdateHint();
        Content = root;
        root.Loaded += (_, _) => { Fit(); scroll.ScrollToVerticalOffset(oldOffset); UpdateHint(); };
        UpdateCaptions();
    }

    private void UpdateHint()
    {
        if (_scroll is null || _hint is null) return;
        _hint.Text = (_scroll.VerticalOffset > 1 ? "↑ " : "") + (_scroll.VerticalOffset < _scroll.ScrollableHeight - 1 ? "↓ " : "") + L("Weitere Hersteller durch Scrollen", "Scroll for more manufacturers");
    }
    private void UpdateCaptions()
    {
        foreach (var app in ManufacturerCatalog.Apps)
        {
            var state = _states.GetValueOrDefault(app.Id) ?? new(ManufacturerInstallState.Unknown);
            var label = _refreshing ? L("Prüfung…", "Checking…") : state.Installation switch
            {
                ManufacturerInstallState.Installed => L("Öffnen", "Open"),
                ManufacturerInstallState.NoLaunchTarget => L("Startziel prüfen", "Check launch target"),
                ManufacturerInstallState.Unknown => L("Status unklar", "Status unknown"),
                _ => WinGetService.PackageId(app.Id) is null ? L("Downloadseite", "Download page") : L("Mit WinGet", "Via WinGet")
            };
            var content = (StackPanel)_primary[app.Id].Content;
            ((TextBlock)content.Children[1]).Text = label;
            var icon = (System.Windows.Shapes.Path)content.Children[0];
            icon.Data = Geometry.Parse(state.Installation == ManufacturerInstallState.Installed ? "M 3,3 H 21 V 21 H 3 Z M 8,12 H 17 M 13,8 L 17,12 L 13,16" : WinGetService.PackageId(app.Id) is null ? "M 12,2 H 22 V 12 M 22,2 L 10,14 M 9,5 H 3 V 22 H 20 V 16" : "M 12,1 V 16 M 7,11 L 12,16 L 17,11 M 2,17 V 23 H 22 V 17");
            AutomationProperties.SetName(_primary[app.Id], label + " · " + app.Title(_english));
            AutomationProperties.SetName(_websites[app.Id], L("Herstellerseite", "Manufacturer website") + " · " + app.Title(_english));
        }
        RefreshAvailability();
    }
    internal void UpdateInstallRoutes() => UpdateCaptions();
    internal void RefreshAvailability()
    {
        foreach (var app in ManufacturerCatalog.Apps)
        {
            if (!_primary.TryGetValue(app.Id, out var button)) continue;
            var state = _states.GetValueOrDefault(app.Id) ?? new(ManufacturerInstallState.Unknown);
            button.IsEnabled = !_refreshing && !_working && !HostBusy() && (state.Installation == ManufacturerInstallState.Installed || state.Installation == ManufacturerInstallState.Missing && (WinGetService.PackageId(app.Id) is null || _page.WinGetState != WinGetAvailability.Missing));
            button.ToolTip = state.Installation == ManufacturerInstallState.Installed ? L("Geprüftes Herstellerprogramm öffnen", "Open verified manufacturer application")
                : state.Installation is ManufacturerInstallState.Unknown or ManufacturerInstallState.NoLaunchTarget
                    ? L("Installationsstatus oder Startziel nicht eindeutig bestätigt. Bitte Herstellerseite verwenden.", "Installation state or launch target could not be confirmed. Please use the manufacturer website.")
                    : WinGetService.PackageId(app.Id) is null ? L("Offizielle Produkt- oder Downloadseite öffnen; keine automatische Installation.", "Open the official product or download page; no automatic installation.")
                    : _page.WinGetState == WinGetAvailability.Missing ? L("WinGet zuerst in der Übersicht einrichten. Die Herstellerseite bleibt verfügbar.", "Set up WinGet in the overview first. The manufacturer website remains available.")
                    : L("Mit WinGet installieren: Gerät und exaktes Paket werden vor der Bestätigung geprüft.", "Install via WinGet: device and exact package are checked before confirmation.");
        }
    }
    private void OpenWebsite(ManufacturerApp app)
    {
        try { Service.OpenWebsite(app); }
        catch (Exception e) { ReportError(e, L("Webseite öffnen", "Open website")); ThemedMessageBox.Show(Window.GetWindow(this), L("Seite konnte nicht geöffnet werden: ", "Could not open website: ") + UiText.Error(e, _english)); }
    }
    internal async Task ActivateAsync(ManufacturerApp app)
    {
        if (_working || _refreshing || HostBusy()) return;
        var state = _states.GetValueOrDefault(app.Id) ?? new(ManufacturerInstallState.Unknown);
        if (state.Installation is not (ManufacturerInstallState.Installed or ManufacturerInstallState.Missing)) return;
        if (state.Installation == ManufacturerInstallState.Missing && WinGetService.PackageId(app.Id) is null) { OpenWebsite(app); return; }
        if (state.Installation == ManufacturerInstallState.Missing && _page.WinGetState == WinGetAvailability.Missing) return;
        _working = true; WorkStateChanged?.Invoke(true); RefreshAvailability();
        try
        {
            if (state.Installation == ManufacturerInstallState.Installed && state.Executable is { } file)
            {
                await Service.LaunchAsync(app, file, false); WriteLog?.Invoke(L("Hersteller-App geöffnet: ", "Manufacturer application opened: ") + app.Title(_english));
            }
            else
            {
                var dialog = new ManufacturerDownloadWindow(app, Service, _palette, _english, _page.WinGet) { Owner = Window.GetWindow(this) };
                dialog.ShowDialog();
                WriteLog?.Invoke(dialog.StartedInstaller ? L("Herstellerinstaller übergeben: ", "Manufacturer installer handed over: ") + app.Title(_english)
                    : L("Herstellerdownload beendet: ", "Manufacturer download ended: ") + app.Title(_english));
            }
        }
        catch (Exception exception) { ReportError(exception, L("Öffnen/Installieren", "Open/install")); ThemedMessageBox.Show(Window.GetWindow(this), UiText.Error(exception, _english), "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _working = false; WorkStateChanged?.Invoke(false); await RefreshAsync(); }
    }
}
