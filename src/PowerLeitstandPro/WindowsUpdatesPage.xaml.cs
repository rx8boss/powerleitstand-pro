using System.Windows;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Binding = System.Windows.Data.Binding;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace PowerControlProWpf;

public partial class WindowsUpdatesPage : UserControl
{
    internal static readonly Uri NvidiaForumUrl = new("https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/");
    internal Action<Uri> OpenExternalLink { get; set; } = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    private bool _english;
    private ManufacturerSoftwareView? _manufacturerView;
    private ProgramCatalogView? _programView;
    private ProgramAssets? _programAssets;
    internal ProgramCatalogView? ProgramView => _programView;
    internal Action? ProgramOverlayChanged { get; set; }
    internal bool PrivacyWorking { get; set; }
    internal string? ExternalOperationLabel(bool english) => _programView?.IsWorking == true
        ? english ? "Program installation active" : "Programminstallation aktiv"
        : PrivacyWorking ? english ? "O&O download / application active" : "O&O-Download / Anwendung aktiv" : null;
    internal void ConfigureProgramAssets(string directory) => _programAssets ??= new ProgramAssets(directory);
    private ProgramAssets Assets => _programAssets ??= new ProgramAssets(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "App Icons"));
    internal Task LoadProgramIconsAsync() => Assets.WarmupAsync();
    private ThemePalette _manufacturerPalette = ThemePalette.For("Standard");
    private readonly DispatcherTimer _esuClock = new();
    internal WindowsEsuInfo EsuInfo { get; set; } = WindowsEsuInfo.Read();
    internal Func<DateTime> EsuNow { get; set; } = () => DateTime.Now;
    private bool _esuRegistrationConfirmed;
    internal Action? ConfirmEsuRegistration { get; set; }
    private bool _firstOpenCheckPending, _firstOpenCheckQueued;
    private Func<bool>? _firstOpenCheckEnabled;
    internal Action? ManufacturerPresentationChanged { get; set; }
    internal ManufacturerSoftwareView? ManufacturerView => _manufacturerView;
    internal bool HasInstallOverlay => OperationSurface.IsVisible;
    internal IEnumerable<Border> CardSurfaces => new[] { HeaderSurface, SearchSurface, UpdatesSurface, EsuSurface, DriversSurface, StartupSurface }.Concat(_manufacturerView?.CardSurfaces ?? []).Concat(_programView?.CardSurfaces ?? []);
    internal bool IsCompactLayout { get; private set; }

    public WindowsUpdatesPage()
    {
        StartupTimings.Mark("Updates page fields initialized");
        InitializeComponent();
        StartupTimings.Mark("Updates page XAML initialized");
        SetLanguage(false);
        InitializeOperations();
        CancelSearchButton.Click += (_, _) => CancelOperation();
        OverviewRoot.IsVisibleChanged += (_, _) => QueueFirstOpenCheck();
        SearchRing.IsVisibleChanged += (_, _) => UpdateSearchRing();
        InitializeWinGet();
        ManufacturersButton.Click += (_, _) => ShowManufacturerSoftware();
        ProgramsButton.Click += (_, _) => { ShowPrograms(inspect: !HostBusy() && !_working); };
        OpenStartupButton.Click += async (_, _) => await OpenStartupAsync();
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseStartup(); };
        PrivacyButton.Click += (_, _) =>
        {
            if (HostBusy() || _working) return;
            try { new PrivacyDownloadWindow(Window.GetWindow(this)!, this, _manufacturerPalette, _english).ShowDialog(); }
            catch (Exception e) { WriteLog?.Invoke(UiText.Error(e, _english)); }
        };
        EsuDetailsButton.Click += (_, _) =>
        {
            if (Window.GetWindow(this) is { } owner && EsuSurface.IsVisible)
                new WindowsEsuWindow(owner, this, _manufacturerPalette, _english).ShowDialog();
        };
        _esuClock.Tick += (_, _) => { _esuClock.Stop(); RefreshEsuVisibility(); ScheduleEsuClock(); };
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        Loaded += (_, _) => { UpdateResponsiveLayout(); RefreshEsuVisibility(); ScheduleEsuClock(); };
        StartupTimings.Mark("Updates page constructed");
    }

    internal void SetLanguage(bool english)
    {
        _english = english;
        string Label(string de, string en) => english ? en : de;
        PageTitle.Text = "System & Software";
        PageSubtitle.Text = Label("Updates, Programme und Autostart", "Updates, apps and startup");
        UpdatesTitle.Text = Label("Windows-Updates", "Windows updates");
        UpdatesSubtitle.Text = Label("Wichtige Updates, Sicherheitsupdates und optionale Updates", "Important, security and optional updates");
        DriversTitle.Text = Label("Treiber-Updates", "Driver updates");
        StartupTitle.Text = Label("Autostart", "Startup apps");
        StartupSubtitle.Text = Label("Programme beim Windows-Start verwalten", "Manage apps that start with Windows");
        DriversSubtitle.Inlines.Clear();
        DriversSubtitle.Inlines.Add(new Run(Label("Treiber · ", "Drivers · ")));
        var forumTip = AppToolTips.Create(DriversSubtitle, Label("NVIDIA-Hotfix-Ankündigungen; Kompatibilität im Beitrag prüfen.", "NVIDIA hotfix announcements; check compatibility in each post."));
        forumTip.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        var forum = new Hyperlink(new Run("Forum & Hotfix")) { NavigateUri = NvidiaForumUrl, ToolTip = forumTip };
        forum.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetName(forum, "Forum & Hotfix");
        forum.RequestNavigate += (_, e) => { try { OpenExternalLink(e.Uri); } catch (Exception ex) { WriteLog?.Invoke(UiText.Error(ex, _english)); } e.Handled = true; };
        DriversSubtitle.Inlines.Add(forum);
        DriversSubtitle.Inlines.Add(new Run(Label(" · Kompatibilität prüfen", " · check compatibility")));
        DriversSubtitle.ToolTip = Label("Windows Update für Treiber; NVIDIA-Hotfix-Ankündigungen im Forum. Kompatibilität im Beitrag prüfen.", "Windows Update drivers; NVIDIA hotfix announcements in the forum. Check compatibility in each post.");
        AutomationProperties.SetName(DriversSubtitle, Label("Treiber · Forum & Hotfix · Kompatibilität prüfen", "Drivers · Forum & Hotfix · check compatibility"));
        const string chip = "M 5,5 H 19 V 19 H 5 Z M 8,1 V 5 M 16,1 V 5 M 8,19 V 23 M 16,19 V 23 M 1,8 H 5 M 1,16 H 5 M 19,8 H 23 M 19,16 H 23";
        const string search = "M 17,10 A 7,7 0 1 1 3,10 A 7,7 0 1 1 17,10 M 15,15 L 23,23";
        const string shield = "M 12,1 L 21,5 V 12 C 21,18 16,22 12,24 C 8,22 3,18 3,12 V 5 Z";
        SetAction(ManufacturersButton, Label("Hersteller-Software", "Manufacturer software"), chip);
        SetAction(ProgramsButton, Label("Programme", "Programs"), "M 2,2 H 10 V 10 H 2 Z M 15,2 H 23 V 10 H 15 Z M 2,15 H 10 V 23 H 2 Z M 15,15 H 23 V 23 H 15 Z");
        SetAction(PrivacyButton, "O&O ShutUp10", shield + " M 8,12 L 11,15 L 17,8");
        SetAction(SearchUpdatesButton, Label("Updates suchen", "Check for updates"), search);
        SetAction(OpenStartupButton, Label("Autostart öffnen", "Open startup apps"), "M 3,4 H 21 V 20 H 3 Z M 7,8 H 17 M 7,12 H 17 M 7,16 H 13");
        SetAction(CheckDriversButton, Label("Treiber prüfen", "Check drivers"), chip);
        SetAction(EsuDetailsButton, Label("Details öffnen", "Open details"), shield + " M 8,12 L 11,15 L 17,8");
        SetAction(InstallUpdatesButton, Label("Ausgewählte Updates installieren", "Install selected updates"), "M 12,1 V 15 M 7,10 L 12,15 L 17,10 M 2,16 V 22 H 22 V 16");
        ManufacturersButton.Width = english ? 184 : 166;
        EsuEndDate.Text = Label("Sicherheitsupdates bis 12.10.2027", "Security updates through 10/12/2027");
        EsuStatus.Text = Label("Registrierung in Windows prüfen", "Check enrollment in Windows");
        EsuDescription.Text = Label("Voraussetzungen in der App prüfen", "Check requirements in the app");
        RefreshEsuVisibility();
        RefreshOperationLanguage();
        UpdateResponsiveLayout();
        _manufacturerView?.SetLanguage(english);
        _programView?.SetLanguage(english);
        UpdateScrollHint();
        UpdateWinGetCaption();
    }

    private void SetAction(Button button, string caption, string path)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(path), StrokeThickness = 1.7, Width = 17, Height = 17,
            Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0),
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        var label = new TextBlock { Text = caption, FontSize = IsCompactLayout ? 11 : 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Control.Foreground)) { Source = button });
        content.Children.Add(icon); content.Children.Add(label); button.Content = content;
        button.ToolTip = caption;
        AutomationProperties.SetName(button, caption);
    }

    internal void SetEsuRegistrationConfirmed(bool confirmed)
    {
        _esuRegistrationConfirmed = confirmed;
        RefreshEsuVisibility();
        if (IsLoaded) ScheduleEsuClock();
    }

    internal void RefreshEsuVisibility()
    {
        EsuSurface.Visibility = !_esuRegistrationConfirmed && EsuInfo.ShowOn(DateOnly.FromDateTime(EsuNow()))
            ? Visibility.Visible : Visibility.Collapsed;
        if (EsuSurface.Visibility == Visibility.Collapsed) _esuClock.Stop();
        UpdateScrollHint();
    }

    private void ScheduleEsuClock()
    {
        _esuClock.Stop();
        var now = EsuNow();
        if (_esuRegistrationConfirmed || !EsuInfo.ShowOn(DateOnly.FromDateTime(now))) return;
        _esuClock.Interval = now.Date.AddDays(1) - now + TimeSpan.FromMilliseconds(100);
        _esuClock.Start();
    }

    internal void ConfigureFirstOpenCheck(Func<bool> enabled, bool runOnOpen)
    {
        _firstOpenCheckEnabled = enabled;
        _firstOpenCheckPending = runOnOpen;
        QueueFirstOpenCheck();
    }

    private void QueueFirstOpenCheck()
    {
        if (!_firstOpenCheckPending || _firstOpenCheckQueued || !OverviewRoot.IsVisible) return;
        if (_firstOpenCheckEnabled?.Invoke() != true) { _firstOpenCheckPending = false; return; }
        if (_working || HostBusy()) return;
        _firstOpenCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _firstOpenCheckQueued = false;
            if (!_firstOpenCheckPending || !OverviewRoot.IsVisible) return;
            if (_firstOpenCheckEnabled?.Invoke() != true) { _firstOpenCheckPending = false; return; }
            if (_working || HostBusy()) return;
            _firstOpenCheckPending = false;
            _ = SearchAsync(null);
        }));
    }

    private void RefreshSearchPresentation()
    {
        if (!_searchOperation) return;
        SearchTitle.Text = _phase switch
        {
            "search" => L("Windows-Updates werden gesucht …", "Checking Windows updates …"),
            "drivers" => L("Angebotene Treiber werden gesucht …", "Checking offered drivers …"),
            "search-all" => L("Windows- und Treiberupdates werden gesucht …", "Checking Windows and driver updates …"),
            "cancelled" => L("Suche beendet", "Search stopped"),
            "error" => L("Updatesuche fehlgeschlagen", "Update search failed"),
            "searched" => L("Prüfung unvollständig", "Check incomplete"),
            _ => ""
        };
        SearchSubtitle.Text = _stopping ? L("Windows beendet die Suche …", "Windows is stopping the search …") : _phase switch
        {
            "search-all" => L("Automatische Prüfung · beim ersten Öffnen dieser Seite", "Automatic check · when this page first opens"),
            "cancelled" => L("Bisherige Ergebnisse bleiben erhalten.", "Existing results are retained."),
            "error" => _failure,
            "searched" => L("Vor der Installation erneut prüfen.", "Check again before installing."),
            _ => L("Die Ergebnisse erscheinen in der Liste.", "Results will appear in the list.")
        };
        if (_stopping) SearchTitle.Text = L("Suche wird beendet …", "Stopping search …");
        SoftwareSearchLabel.Text = L("Windows-Updates", "Windows updates");
        DriverSearchLabel.Text = L("Treiber-Updates", "Driver updates");
        SoftwareSearchState.Text = DriverSearchState.Text = _stopping ? L("Wird beendet", "Stopping") : L("Wird gesucht", "Searching");
        AutomationProperties.SetName(SearchProgress, L("Fortschritt der Updatesuche", "Update search progress"));
        UpdateSearchRing();
    }

    private void UpdateSearchRing()
    {
        if (SearchRing.IsVisible && _working && SystemParameters.ClientAreaAnimation)
        {
            if (!SearchRingRotation.HasAnimatedProperties)
                SearchRingRotation.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else SearchRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void UpdateResponsiveLayout()
    {
        if (HeaderGrid is null || ActualWidth <= 0) return;
        var wasCompact = IsCompactLayout;
        IsCompactLayout = ActualWidth < 1000 || ActualHeight < 480;
        HeaderGrid.RowDefinitions[0].Height = new GridLength(IsCompactLayout ? 42 : 54);
        HeaderGrid.ColumnDefinitions[0].Width = new GridLength(IsCompactLayout ? 36 : 44);
        StripeColumn.Width = new GridLength(IsCompactLayout ? 72 : 120);
        HeaderStripes.Width = IsCompactLayout ? 64 : 100;
        HeaderStripes.Height = IsCompactLayout ? 42 : 54;
        PageTitle.FontSize = IsCompactLayout ? 20 : 24;
        PageSubtitle.FontSize = IsCompactLayout ? 9.5 : 11;
        PageSubtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        PageSubtitle.TextWrapping = TextWrapping.NoWrap;
        PageSubtitle.ToolTip = PageSubtitle.Text;
        Grid.SetRow(Toolbar, 0); Grid.SetColumn(Toolbar, 2); Grid.SetColumnSpan(Toolbar, 1);
        Toolbar.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        ManufacturersButton.Width = IsCompactLayout ? 155 : _english ? 184 : 166;
        ProgramsButton.Width = IsCompactLayout ? 105 : 128;
        PrivacyButton.Width = IsCompactLayout ? 140 : 158;
        ProgramsButton.Margin = PrivacyButton.Margin = new Thickness(IsCompactLayout ? 6 : 8, 0, 0, 0);
        foreach (var button in new[] { ManufacturersButton, ProgramsButton, PrivacyButton })
        {
            button.Height = IsCompactLayout ? 32 : 36;
            button.Padding = new Thickness(IsCompactLayout ? 7 : 10, 3, IsCompactLayout ? 7 : 10, 3);
            foreach (var text in UnifiedDialogAppearance.Children<TextBlock>(button)) text.FontSize = IsCompactLayout ? 11 : 12;
            foreach (var icon in UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(button))
            {
                icon.Width = icon.Height = IsCompactLayout ? 15 : 17;
                icon.Margin = new Thickness(0, 0, IsCompactLayout ? 6 : 8, 0);
            }
        }
        foreach (var card in new[] { HeaderSurface, SearchSurface, UpdatesSurface, EsuSurface, DriversSurface, StartupSurface })
        {
            card.Margin = new Thickness(0, 0, 0, IsCompactLayout ? 5 : 8);
            card.Padding = card == HeaderSurface ? new Thickness(IsCompactLayout ? 10 : 13, IsCompactLayout ? 6 : 8, IsCompactLayout ? 10 : 13, IsCompactLayout ? 6 : 8)
                : new Thickness(IsCompactLayout ? 10 : 12, IsCompactLayout ? 6 : 10, IsCompactLayout ? 10 : 12, IsCompactLayout ? 6 : 10);
        }
        foreach (var card in new[] { UpdatesSurface, DriversSurface })
        {
            var grid = (Grid)card.Child;
            if (IsCompactLayout) card.Padding = new Thickness(10, 3, 10, 3);
            grid.RowDefinitions[0].Height = card == UpdatesSurface ? GridLength.Auto : new GridLength(IsCompactLayout ? 34 : 44);
            grid.RowDefinitions[0].MinHeight = IsCompactLayout ? 34 : 44;
            foreach (var border in grid.Children.OfType<Border>()) border.Margin = new Thickness(0, IsCompactLayout ? 3 : 8, 0, 0);
        }
        UpdatesTitle.FontSize = DriversTitle.FontSize = StartupTitle.FontSize = IsCompactLayout ? 18 : 20;
        EsuTitle.FontSize = IsCompactLayout ? 16 : 18;
        UpdatesSubtitle.FontSize = DriversSubtitle.FontSize = StartupSubtitle.FontSize = IsCompactLayout ? 10 : 11;
        EsuEndDate.FontSize = EsuDescription.FontSize = IsCompactLayout ? 10 : 11;
        SearchUpdatesButton.Height = CheckDriversButton.Height = EsuDetailsButton.Height = OpenStartupButton.Height = IsCompactLayout ? 32 : 36;
        InstallUpdatesButton.Width = IsCompactLayout ? 276 : 288;
        InstallUpdatesButton.Height = IsCompactLayout ? 34 : 36;
        UpdateWinGetCaption();
        if (wasCompact != IsCompactLayout) RefreshOperationLanguage();
        UpdateScrollHint();
    }

    private void PageScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateScrollHint();
    internal ManufacturerSoftwareView ShowManufacturerSoftware(bool inspect = true)
    {
        if (_manufacturerView is null)
        {
            _manufacturerView = new ManufacturerSoftwareView(this)
            {
                HostBusy = () => HostBusy(), WorkStateChanged = active => WorkStateChanged?.Invoke(active),
                Back = ShowUpdateOverview, WriteLog = line => WriteLog?.Invoke(line),
                WriteErrorLog = error => { if (WriteErrorLog is not null) WriteErrorLog(error); else WriteLog?.Invoke(error.Source + ": ERROR · " + error.Message); },
                PresentationChanged = () => ManufacturerPresentationChanged?.Invoke()
            };
        }
        _programView?.CloseInfo(); ManufacturerHost.Content = _manufacturerView;
        _manufacturerView.SetLanguage(_english); _manufacturerView.SetPalette(_manufacturerPalette);
        OverviewRoot.Visibility = Visibility.Collapsed; ManufacturerHost.Visibility = Visibility.Visible;
        ManufacturerPresentationChanged?.Invoke();
        if (inspect) _ = _manufacturerView.RefreshAsync();
        return _manufacturerView;
    }
    internal void ShowUpdateOverview()
    {
        _programView?.CloseInfo();
        ManufacturerHost.Visibility = Visibility.Collapsed; OverviewRoot.Visibility = Visibility.Visible;
        RefreshAvailability(); ManufacturerPresentationChanged?.Invoke();
    }
    internal ProgramCatalogView ShowPrograms(bool inspect = true)
    {
        if (_programView is null)
        {
            _programView = new ProgramCatalogView(this, Assets)
            {
                HostBusy = () => HostBusy(), Back = ShowUpdateOverview,
                WorkStateChanged = active => WorkStateChanged?.Invoke(active), WriteLog = line => WriteLog?.Invoke(line),
                WriteErrorLog = error => { if (WriteErrorLog is not null) WriteErrorLog(error); else WriteLog?.Invoke(error.Source + ": ERROR · " + error.Message); },
                PresentationChanged = () => ManufacturerPresentationChanged?.Invoke()
            };
        }
        _programView.SetPalette(_manufacturerPalette); _programView.SetLanguage(_english);
        ManufacturerHost.Content = _programView; OverviewRoot.Visibility = Visibility.Collapsed; ManufacturerHost.Visibility = Visibility.Visible;
        ManufacturerPresentationChanged?.Invoke(); if (inspect) _ = _programView.OpenAsync(); return _programView;
    }
    private void UpdateScrollHint()
    {
        if (PageScroll is null || ScrollHint is null) return;
        var top = PageScroll.VerticalOffset > 1;
        var bottom = PageScroll.VerticalOffset < PageScroll.ScrollableHeight - 1;
        var directions = (top ? "↑ " : "") + (bottom ? "↓ " : "");
        ScrollHint.Text = top || bottom
            ? directions + (_english ? "Scroll for more" : "Weitere Inhalte durch Scrollen")
            : (_english ? "Restart only after confirmation" : "Neustart nur nach Bestätigung");
    }
}
