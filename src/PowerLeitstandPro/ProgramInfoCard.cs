using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Image = System.Windows.Controls.Image;
using Brush = System.Windows.Media.Brush;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;

namespace PowerControlProWpf;

internal sealed partial class ProgramInfoCard
{
    private readonly ProgramCatalogView _view;
    private readonly ProgramEntry _entry;
    private readonly FrameworkElement _anchor;
    private readonly Grid _host;
    private readonly Window _owner;
    private readonly Border _frame;
    private readonly Border _shade;
    private readonly CancellationTokenSource _cancel = new();
    private readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly TextBlock _available, _availabilityNote, _details, _what, _error;
    private readonly TextBlock _sourceSummary;
    private readonly TextBlock _installedVersion, _installedText;
    private readonly Grid _installedVersionField, _availableVersionField;
    private readonly TextBlock? _publisherVersionNote;
    private readonly Button _versionAction;
    private readonly Button _updateAction, _uninstallAction;
    private ProgramPackage? _resolvedPackage;
    private string? _confirmedOnline;
    private bool _choosingFile;
    private readonly TeamSpeakInfoView? _teamSpeak;
    private readonly List<Grid> _fields = [];
    private readonly Image _logo;
    private readonly FrameworkElement _fallback;
    private bool _closed;
    private readonly Grid? _manualRow;
    private readonly System.Windows.Controls.CheckBox? _manualSwitch;
    internal Border Frame => _frame;
    internal Task Loading { get; }
    internal bool IsOpen => !_closed;
    internal string EntryId => _entry.Id;
    internal TeamSpeakInfoView? TeamSpeak => _teamSpeak;
    private string L(string de, string en) => _view.English ? en : de;
    internal static ProgramInfoCard? Open(ProgramCatalogView view, ProgramEntry entry, FrameworkElement anchor, bool focus)
    {
        var owner = Window.GetWindow(view);
        var root = owner?.FindName("Root") as Grid ?? owner?.Content as Grid;
        return owner is null || root is null ? null : new(view, entry, anchor, owner, root, focus);
    }
    private ProgramInfoCard(ProgramCatalogView view, ProgramEntry entry, FrameworkElement anchor, Window owner, Grid host, bool focus)
    {
        _view = view; _entry = entry; _anchor = anchor; _owner = owner; _host = host;
        var palette = view.Palette; UnifiedDialogAppearance.Resources(owner, palette);
        _frame = new Border { Width = Math.Min(view.Compact ? 420 : 580, host.ActualWidth - 28), Height = Math.Min(540, Math.Max(220, host.ActualHeight - 28)), MaxHeight = Math.Max(220, host.ActualHeight - 28), Margin = new(14),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new(16), BorderThickness = new(1.4), Padding = new(view.Compact ? 20 : 26), Focusable = true };
        DialogAppearance.ApplySurface(owner, _frame, palette);
        _frame.SetResourceReference(Border.BackgroundProperty, "DialogSurface"); _frame.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new Grid { MinHeight = 54 }; heading.ColumnDefinitions.Add(new() { Width = new GridLength(70) }); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        _logo = new Image { Source = view.Assets.Icon(entry.Id), Width = 54, Height = 54, Stretch = Stretch.Uniform };
        _fallback = ProgramFallbackIcon.Create(46, "UnifiedAccent");
        _fallback.Visibility = _logo.Source is null ? Visibility.Visible : Visibility.Collapsed;
        var iconCell = new Grid(); iconCell.Children.Add(_fallback); iconCell.Children.Add(_logo);
        heading.Children.Add(iconCell); var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = Text(entry.Name, view.Compact ? 25 : 29); title.FontWeight = FontWeights.SemiBold; titles.Children.Add(title);
        titles.Children.Add(Text(entry.Publisher, 13, true)); Grid.SetColumn(titles, 1); heading.Children.Add(titles);
        var close = new Button { Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,0 L 12,12 M 12,0 L 0,12"), Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.6 } };
        UnifiedDialogAppearance.Button(owner, close, DialogButtonKind.Close); close.Click += (_, _) => Close(); close.ToolTip = L("Schließen", "Close"); AutomationProperties.SetName(close, close.ToolTip.ToString()); Grid.SetColumn(close, 2); heading.Children.Add(close);
        var headingSurface = new StackPanel { Margin = new(0, 0, 0, 16) }; headingSurface.Children.Add(heading);
        var headingRule = Divider(); headingRule.Margin = new(70, 14, 0, 0); headingSurface.Children.Add(headingRule); root.Children.Add(headingSurface);
        var stripe = new System.Windows.Shapes.Path { Data = Geometry.Parse("M30,0 L32,0 L2,30 L0,30 Z M40,0 L42,0 L12,30 L10,30 Z M46,0 L54,0 L24,30 L16,30 Z"),
            Width = 54, Height = 36, Stretch = Stretch.Fill, Opacity = .22, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
        stripe.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UnifiedAccent"); Grid.SetColumn(stripe, 1); heading.Children.Insert(0, stripe); titles.Margin = new(0, 0, 52, 0);
        var body = new StackPanel();
        _what = Text(entry.Description(view.English), view.Compact ? 14 : 16); _what.Name = "ProgramInfoDescription";
        _what.LineHeight = view.Compact ? 21 : 24; _what.MinHeight = view.Compact ? 21 : 24; _what.Margin = new(0, 0, 0, 2); body.Children.Add(_what);
        _descriptionNote = Text("", 11, true); _descriptionNote.Name = "ProgramInfoDescriptionNote";
        _descriptionNote.Visibility = Visibility.Collapsed; body.Children.Add(_descriptionNote);
        AddVlcCopyPicker(body);
        body.Children.Add(Divider());
        var installedVersion = _installedVersion = Text(ProgramVersions.ConfirmedVersion(view.Inventory.Installed.GetValueOrDefault(entry.Id)) ?? L("Konnte nicht ermittelt werden", "Could not be determined"), view.Compact ? 14 : 15);
        installedVersion.Name = "ProgramInfoInstalledVersion";
        var versionCell = new Grid(); versionCell.ColumnDefinitions.Add(new()); versionCell.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        installedVersion.VerticalAlignment = VerticalAlignment.Center; versionCell.Children.Add(installedVersion);
        _updateAction = Action(L("Update", "Update")); _updateAction.Name = "ProgramInfoUpdateAction";
        _updateAction.Width = 142; _updateAction.Height = _updateAction.MinHeight = 34;
        _updateAction.Margin = new(8, 0, 0, 0); _updateAction.Visibility = Visibility.Collapsed;
        _updateAction.Background = new SolidColorBrush(Color.FromRgb(7, 85, 69));
        _updateAction.BorderBrush = new SolidColorBrush(Color.FromRgb(25, 216, 174)); _updateAction.Foreground = System.Windows.Media.Brushes.White;
        _updateAction.Resources["UnifiedHover"] = new SolidColorBrush(Color.FromRgb(10, 118, 88));
        _updateAction.Click += async (_, _) =>
        {
            if (_closed || !view.CanChooseProgramFile) return;
            if (EaOpenAvailable) { await OpenEaAsync(); return; }
            if (!CanUpdate() && NeedsEntryCheck)
            {
                if (view.Service is ProgramInstallService inspection) { await RecheckSavedEntryAsync(inspection); return; }
                Close(); await view.RefreshAsync(); view.ReopenInfo(entry, view); return;
            }
            if (!CanUpdate()) { RefreshVersionPresentation(); return; }
            try { var offer = _resolvedPackage; Close(); await view.InstallEntriesAsync([entry], updatesOnly: true, expectedUpdate: offer); }
            catch (Exception error) { view.WriteLog?.Invoke(UiText.Error(error, view.English)); }
        };
        Grid.SetColumn(_updateAction, 1); versionCell.Children.Add(_updateAction);
        _installedVersionField = Field(L("Installierte Version", "Installed version"), versionCell);
        body.Children.Add(_installedVersionField);
        _available = Text(L("Wird geprüft …", "Checking …"), view.Compact ? 14 : 15);
        _available.Name = "ProgramInfoAvailableVersion";
        var availability = new StackPanel(); availability.Children.Add(_available);
        _availabilityNote = Text("", 11, true); _availabilityNote.Visibility = Visibility.Collapsed; availability.Children.Add(_availabilityNote);
        _availableVersionField = Field(L("Verfügbare Version", "Available version"), availability);
        body.Children.Add(_availableVersionField);
        var installedText = _installedText = Text(view.InstallationStatus(entry.Id), view.Compact ? 14 : 16); installedText.FontWeight = FontWeights.SemiBold;
        installedText.Name = "ProgramInfoInstalledStatus";
        var installedRow = new Grid(); installedRow.ColumnDefinitions.Add(new()); installedRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        installedText.VerticalAlignment = VerticalAlignment.Center; installedRow.Children.Add(installedText);
        var statusActions = new StackPanel { Margin = new(7, 0, 0, 0) }; Grid.SetColumn(statusActions, 1); installedRow.Children.Add(statusActions);
        _versionAction = Action(""); _versionAction.Width = 142; _versionAction.Height = _versionAction.MinHeight = 36; _versionAction.FontSize = 13;
        statusActions.Children.Add(_versionAction);
        _versionAction.Click += async (_, _) =>
        {
            if (_closed || !view.CanChooseProgramFile) return;
            await ReinstallAsync();
        };
        _uninstallAction = Action(L("Deinstallieren …", "Uninstall …"), true); _uninstallAction.Name = "ProgramInfoUninstallAction";
        _uninstallAction.Width = 142; _uninstallAction.Height = _uninstallAction.MinHeight = 36; _uninstallAction.FontSize = 13;
        _uninstallAction.Margin = new(0, 6, 0, 0); statusActions.Children.Add(_uninstallAction);
        _uninstallAction.Click += async (_, _) =>
        {
            if (_closed || !view.CanChooseProgramFile || !view.Inventory.Installed.ContainsKey(entry.Id)) return;
            try { Close(); await ProgramRemovalDialog.ShowAsync(view, entry); await view.RefreshAsync(); }
            catch (Exception error) { view.WriteLog?.Invoke(UiText.Error(error, view.English)); }
        };
        RefreshVersionPresentation();
        if (!view.Inventory.Installed.ContainsKey(entry.Id))
        {
            var manualRow = _manualRow = new Grid { Margin = new(0, 0, 0, 8), Visibility = Visibility.Collapsed };
            manualRow.ColumnDefinitions.Add(new()); manualRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); manualRow.ColumnDefinitions.Add(new() { Width = new GridLength(35) });
            var label = Text(L("Als installiert markieren", "Mark as installed"), 11);
            label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new(0, 0, 8, 0); manualRow.Children.Add(label);
            var mark = _manualSwitch = new System.Windows.Controls.CheckBox { Style = (Style)owner.FindResource("DialogStatusSwitch"), Width = 42, Height = 23,
                IsChecked = view.IsManual(entry.Id), IsEnabled = view.CanMark(entry.Id) };
            mark.ToolTip = L("Manuelle Bestätigung, kein automatischer Nachweis. Ermittelt keine Version und prüft keine Updates.", "Manual confirmation, not automatic detection. Does not determine a version or check updates.");
            AutomationProperties.SetName(mark, label.Text); Grid.SetColumn(mark, 1); manualRow.Children.Add(mark);
            var state = Text(mark.IsChecked == true ? L("An", "On") : L("Aus", "Off"), 11); state.Margin = new(7, 0, 0, 0); state.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(state, 2); manualRow.Children.Add(state); body.Children.Add(manualRow);
            var markError = Text("", 10, true); markError.Visibility = Visibility.Collapsed; body.Children.Add(markError);
            bool resetting = false;
            void Changed(object sender, RoutedEventArgs args)
            {
                if (resetting) return;
                try
                {
                    view.MarkInstalled(entry, mark.IsChecked == true);
                    RefreshVersionPresentation(); state.Text = mark.IsChecked == true ? L("An", "On") : L("Aus", "Off");
                    markError.Visibility = Visibility.Collapsed;
                }
                catch (Exception e)
                {
                    resetting = true; mark.IsChecked = view.IsManual(entry.Id); resetting = false;
                    markError.Text = L("Markierung nicht gespeichert: ", "Confirmation not saved: ") + UiText.Error(e, view.English); markError.Visibility = Visibility.Visible;
                }
            }
            mark.Checked += Changed; mark.Unchecked += Changed;
        }
        _locationField = Field(L("Programmdatei", "Program executable"), new Grid());
        body.Children.Add(_locationField); RefreshLocation();
        if (entry.Id == "turtle-beach-swarm-ii")
        {
            _publisherVersionNote = Text("", 11.5, true);
            _publisherVersionNote.Name = "ProgramInfoPublisherVersionNote";
            _publisherVersionNote.Margin = new(0, 0, 0, 7);
            _publisherVersionNote.Visibility = Visibility.Collapsed;
            body.Children.Add(_publisherVersionNote);
        }
        var status = new StackPanel(); status.Children.Add(Text(L("Installationsstatus", "Installation status"), 13, true));
        installedRow.Margin = new(0, 5, 0, 0); status.Children.Add(installedRow);
        _details = Text(Details(), 11.5, true); _details.Margin = new(0, 8, 0, 0); _details.Visibility = Visibility.Collapsed;
        _sourceSummary = Text(SourceSummary(), 11, true);
        var detailsToggle = new Button { Content = _sourceSummary, Name = "ProgramInfoDetailsToggle", Margin = new(0, 12, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent, Padding = new(2, 4, 2, 4), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Left };
        detailsToggle.SetResourceReference(Control.ForegroundProperty, "UnifiedMuted");
        detailsToggle.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
              <Border x:Name="DetailFrame" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" CornerRadius="4">
                <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="DetailFrame" Property="Background" Value="{DynamicResource UnifiedHover}"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="DetailFrame" Property="BorderBrush" Value="{DynamicResource UnifiedAccent}"/><Setter TargetName="DetailFrame" Property="BorderThickness" Value="1"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        detailsToggle.ToolTip = L("Paket- und Erkennungsdetails anzeigen", "Show package and detection details");
        AutomationProperties.SetName(detailsToggle, detailsToggle.ToolTip.ToString());
        detailsToggle.Click += (_, _) => { _details.Visibility = _details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; _sourceSummary.Text = SourceSummary(); };
        body.Children.Add(detailsToggle); body.Children.Add(_details);
        _error = Text("", 11, true); _error.Visibility = Visibility.Collapsed; body.Children.Add(_error);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false };
        if (entry.Id == "teamspeak") scroll.Content = _teamSpeak = new TeamSpeakInfoView(view, owner, Close);
        var bodyArea = new Grid(); bodyArea.RowDefinitions.Add(new()); bodyArea.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var scrollHost = new Border { Child = scroll }; bodyArea.Children.Add(scrollHost);
        if (entry.Id != "teamspeak")
        {
            // Keep both status actions above the fixed footer even when descriptions or
            // version strings need scrolling. The original dialog dimensions stay intact.
            var fixedStatus = new StackPanel(); fixedStatus.Children.Add(Divider()); fixedStatus.Children.Add(status);
            Grid.SetRow(fixedStatus, 1); bodyArea.Children.Add(fixedStatus);
        }
        Grid.SetRow(bodyArea, 1); root.Children.Add(bodyArea); DialogLayout.ScrollHint(scroll, view.English);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = new GridLength(12) }); footer.ColumnDefinitions.Add(new());
        var website = Action(L("↗ Webseite", "↗ Website")); website.Name = "ProgramInfoWebsite";
        website.ToolTip = L("Herstellerwebseite öffnen", "Open publisher website"); website.Click += (_, _) =>
        {
            try { view.Service.OpenWebsite(entry); }
            catch (Exception e) { if (_teamSpeak is not null) _teamSpeak.ReportError(e); else if (_error is not null) { _error.Text = UiText.Error(e, view.English); _error.Visibility = Visibility.Visible; } }
        };
        var dismiss = Action(L("Schließen", "Close")); dismiss.Click += (_, _) => Close();
        footer.Children.Add(website);
        Grid.SetColumn(dismiss, 2);
        footer.Children.Add(dismiss);
        var footerSurface = new Border { Child = footer, BorderThickness = new(0, 1, 0, 0), Padding = new(0, 16, 0, 0), Margin = new(0, 16, 0, 0) };
        footerSurface.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent"); Grid.SetRow(footerSurface, 2); root.Children.Add(footerSurface);
        _shade = new Border { Background = System.Windows.Media.Brushes.Black, Opacity = .42, IsHitTestVisible = false };
        Grid.SetRowSpan(_shade, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(_shade, Math.Max(1, host.ColumnDefinitions.Count)); Panel.SetZIndex(_shade, 4999); host.Children.Add(_shade);
        _frame.Child = root; Grid.SetRowSpan(_frame, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(_frame, Math.Max(1, host.ColumnDefinitions.Count)); Panel.SetZIndex(_frame, 5000); host.Children.Add(_frame);
        _frame.PreviewMouseDown += Interacted; _frame.PreviewKeyDown += Interacted; _frame.PreviewMouseWheel += Interacted;
        _view.IsVisibleChanged += ViewVisibilityChanged;
        _leave.Tick += (_, _) => Close();
        _owner.PreviewKeyDown += Escape; _owner.SizeChanged += Resized;
        _view.SizeChanged += Resized;
        _leave.Start();
        _view.Assets.IconChanged += IconChanged;
        if (focus) _frame.Dispatcher.BeginInvoke(() => website.Focus());
        Loading = _teamSpeak is not null ? _teamSpeak.InitializeAsync() : LoadAsync();
    }
    private TextBlock Text(string text, double size, bool muted = false)
    { var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText"); return t; }
    private Grid Field(string label, FrameworkElement value)
    {
        var row = new Grid { Margin = new(0, 7, 0, 7) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(_view.Compact ? 150 : 230) }); row.ColumnDefinitions.Add(new()); _fields.Add(row);
        var title = Text(label + ":", _view.Compact ? 13 : 15); title.Margin = new(0, 0, 12, 0); title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title); Grid.SetColumn(value, 1); row.Children.Add(value); return row;
    }
    private Border Divider()
    {
        var line = new Border { Height = 1, Opacity = .55, Margin = new(0, 9, 0, 9) };
        line.SetResourceReference(Border.BackgroundProperty, "UnifiedAccent"); return line;
    }
    private Button Action(string caption, bool danger = false)
    {
        var button = new Button { Content = new TextBlock { Text = caption }, Height = 40, MinHeight = 40, Padding = new(8, 0, 8, 0), FontSize = 14 };
        UnifiedDialogAppearance.Button(_owner, button, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary); AutomationProperties.SetName(button, caption); return button;
    }
    private bool TukuiStandalone => _entry.Id == "tukui-client" && (_singleEntryEvidence ?? _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id)) is { } proof && ProgramTukuiUpdates.Eligible(proof);
    private bool HasVersionCheck => _entry.PackageId is not null || StoreKnown || ProgramVersions.StoreFamilies.ContainsKey(_entry.Id)
        || _confirmedOnline is not null || _resolvedPackage is not null || _view.Inventory.HasUpdate(_entry.Id) || _view.Inventory.HasUpdateNotice(_entry.Id)
        || _view.Inventory.PendingUpdates?.ContainsKey(_entry.Id) == true || _view.Inventory.UnverifiedUpdateIds?.Contains(_entry.Id) == true
        || ProgramVersions.GithubRepository(_entry) is not null || ProgramPublisherVersions.SupportsOnlineVersion(_entry.Id)
        || _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id) is { } proof
            && (proof.ManagedCopies is { Count: > 0 } || ProgramPortableUpdates.Eligible(_entry.Id, proof))
        || _view.Service is ProgramInstallService service && service.ManagedProblem(_entry.Id) is not null;
    private bool NeedsEntryCheck => HasVersionCheck && (_view.Inventory.IsStale || _view.Inventory.PendingUpdates?.ContainsKey(_entry.Id) == true
        || (TukuiStandalone || _view.Service is ProgramInstallService) && !CanUpdate()
        || _view.Inventory.UnverifiedInstalledIds?.Contains(_entry.Id) == true
        || !CanUpdate() && (_view.Inventory.HasUpdateNotice(_entry.Id) || _resolvedPackage?.ManagedPackage?.IsUpdate == true
            || ProgramVersions.IsNewer(_view.Inventory.Installed.GetValueOrDefault(_entry.Id), _confirmedOnline)));
    private bool SingleUpdateAvailable => _singleEntryCheckedUtc is not null && _singleEntryUpdates is { } check
        && ProgramInstallService.CheckedUpdateMatches(_entry, _resolvedPackage, check);
    private bool CanUpdate() => _singleEntryCheckedUtc is not null ? SingleUpdateAvailable
        : ProgramInstallService.CheckedUpdateMatches(_entry, _resolvedPackage, _view.Inventory);
    private void RefreshVersionPresentation()
    {
        string? installedVersion = _singleEntryCheckedUtc is null ? _view.Inventory.Installed.GetValueOrDefault(_entry.Id) : _singleEntryEvidence?.Version;
        bool newer = _resolvedPackage?.ManagedPackage is { } managed
            ? managed.IsUpdate : ProgramVersions.IsNewer(installedVersion, _confirmedOnline);
        bool installed = _view.Inventory.Installed.ContainsKey(_entry.Id);
        _installedVersion.Text = ProgramVersions.ConfirmedVersion(installedVersion)
            ?? (StorePending ? _view.Inventory.StoreError is null ? L("Wird geprüft …", "Checking …") : L("Prüfung fehlgeschlagen", "Check failed")
                : L("Konnte nicht ermittelt werden", "Could not be determined"));
        _installedVersion.SetResourceReference(TextBlock.ForegroundProperty, newer ? "UnifiedDangerBorder" : "UnifiedText");
        if (newer) _available.SetResourceReference(TextBlock.ForegroundProperty, "ReadyBrush");
        else _available.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedText");
        _installedText.Text = _singleEntryCheckedUtc is not null
            ? _singleEntryEvidence is null ? L("Einzelprüfung: nicht bestätigt · Gesamtbestand bleibt gespeichert", "Single check: not verified · saved inventory retained")
                : newer ? L("Installiert · Update verfügbar · einzeln geprüft", "Installed · update available · checked individually")
                : ProgramInstallService.CurrentEnough(_singleEntryEvidence.Version, _confirmedOnline ?? "") ? L("Installiert · aktuell · einzeln geprüft", "Installed · up to date · checked individually")
                : L("Installiert · einzeln geprüft", "Installed · checked individually")
            : newer && !_view.Inventory.IsStale ? L("Installiert · Update verfügbar", "Installed · update available") : _view.InstallationStatus(_entry.Id);
        if (_singleEntryCheckedUtc is not null && _singleEntryEvidence is null || newer || StorePending && _view.Inventory.StoreError is not null) _installedText.Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 67));
        else _installedText.SetResourceReference(TextBlock.ForegroundProperty, installed ? "ReadyBrush" : "UnifiedMuted");
        bool update = CanUpdate(), publisher = EaOpenAvailable;
        if (publisher) _installedText.Text = L("Installiert · Update über EA App", "Installed · update through EA app");
        bool pending = NeedsEntryCheck && !update && !publisher;
        _updateAction.Visibility = update || publisher || pending ? Visibility.Visible : Visibility.Collapsed;
        bool showAvailableVersion = HasVersionCheck || update || publisher;
        _installedVersionField.Visibility = ProgramVersions.ConfirmedVersion(installedVersion) is not null || showAvailableVersion
            ? Visibility.Visible : Visibility.Collapsed;
        _availableVersionField.Visibility = showAvailableVersion ? Visibility.Visible : Visibility.Collapsed;
        _updateAction.Name = publisher ? "ProgramInfoOpenPublisherApp" : "ProgramInfoUpdateAction";
        _updateAction.Content = new TextBlock { Text = pending ? L("Erneut prüfen", "Check again") : publisher ? L("EA App öffnen", "Open EA app") : L("Update installieren", "Install update") };
        _updateAction.ToolTip = pending ? L("Installation und verfügbare Version dieses Programms erneut prüfen", "Check this application's installation and available version again")
            : publisher ? L("EA App zum Aktualisieren öffnen. Automatische Updates müssen in EA aktiviert sein.", "Open EA app to update. Automatic updates must be enabled in EA.")
            : L("Update installieren · bestätigte aktuelle Version", "Install update · verified current version");
        AutomationProperties.SetName(_updateAction, pending ? L("Programmstand erneut prüfen", "Check application state again") : publisher ? L("EA App zum Aktualisieren öffnen", "Open EA app to update") : L("Update installieren", "Install update"));
        _uninstallAction.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        _versionAction.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        _versionAction.Name = "ProgramInfoStatusReinstall";
        var caption = TukuiStandalone && _resolvedPackage?.PortableUpdate is null ? L("Herstellerseite", "Publisher site") : L("Neu installieren", "Reinstall");
        _versionAction.Content = new TextBlock { Text = caption }; AutomationProperties.SetName(_versionAction, caption);
        _versionAction.ToolTip = _view.Service is ProgramInstallService service
            ? ProgramInstallService.ReinstallExplanation(service.ReinstallCapability(_entry, _view.Inventory), _view.English)
            : L("Vorhandene Installationsquelle prüfen", "Check the existing installation source");
        _uninstallAction.ToolTip = L("Zugeordnete Installation anzeigen · Deinstallation separat bestätigen", "Show the matching installation · confirm uninstall separately");
        _versionAction.IsEnabled = _uninstallAction.IsEnabled = _view.CanChooseProgramFile && !_openingPublisher && !_singleEntryChecking;
        _updateAction.IsEnabled = _versionAction.IsEnabled && _metadataLoaded;
        if (_view.Inventory.IsStale || _view.Inventory.UnverifiedInstalledIds?.Contains(_entry.Id) == true) _versionAction.IsEnabled = false;
    }
    private async Task ChooseProgramFileAsync(Button button)
    {
        if (_closed || _choosingFile || !_view.CanChooseProgramFile) return;
        _choosingFile = true; _leave.Stop(); button.IsEnabled = false;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = L("Programmdatei auswählen", "Select program executable"), Filter = L("Windows-Programme (*.exe)|*.exe", "Windows programs (*.exe)|*.exe"), CheckFileExists = true, Multiselect = false, DereferenceLinks = false };
            if (dialog.ShowDialog(_owner) != true || _closed) return;
            _error.Text = L("Programmdatei wird geprüft …", "Verifying program executable …"); _error.Visibility = Visibility.Visible;
            await _view.RememberProgramFileAsync(_entry, dialog.FileName);
            if (!_closed) _view.ReopenInfo(_entry, _anchor);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!_closed) { _error.Text = UiText.Error(error, _view.English); _error.Visibility = Visibility.Visible; } }
        finally { _choosingFile = false; if (!_closed) { button.IsEnabled = _view.CanChooseProgramFile; ResetIdle(); } }
    }
    private string Details()
    {
        var details = (TukuiStandalone ? L("Offizielles Standalone-Paket · gezielte Prüfung.", "Official standalone package · explicit verification.")
            : _entry.PackageId is null ? L("Offizielle Produktseite · Installation beim Anbieter.", "Official product page · install through the provider.") : "WinGet · " + _entry.PackageId)
            + L("\nLizenz und Systemvoraussetzungen beim Anbieter prüfen.", "\nCheck license and system requirements with the provider.");
        if (_view.Inventory.Evidence?.TryGetValue(_entry.Id, out var proof) == true)
        {
            if (proof.ManagedCopies is { Count: > 0 }) details = (_resolvedPackage?.ManagedPackage?.Description(_view.English)
                ?? L("Paketmanager-Quelle wird geprüft.", "Checking package-manager source."))
                + L("\nPaketversion und Programmversion bleiben getrennt.", "\nPackage and program versions stay separate.");
            var source = proof.Source switch { "Registry" => L("Windows-Programmliste", "Windows installed programs"), "Portable" => L("Portable Programmdatei", "Portable executable"), "Portable-Setup" => L("Portable Einrichtungs-EXE", "Portable setup EXE"), "Windows-App" => L("Windows-App-Paket", "Windows app package"), "SelectedFile" => L("Manuell zugeordnete Programmdatei", "Manually associated executable"), _ => proof.Source };
            details += L("\n\nErkannt über: ", "\n\nDetected through: ") + source;
            if (proof.Source == "SelectedFile" && _resolvedPackage?.PortableUpdate is null) details += L("\nDatei und Versionsdaten geprüft; Installationsart und Updateweg nicht bestätigt.", "\nFile and version metadata checked; installation channel and update route are not verified.");
            if (proof.Source == "Portable-Setup") details += L("\nNur das Einrichtungsprogramm ist vorhanden. Die Einrichtung für ein Spiel ist damit nicht bestätigt. Die Installer-Version ist keine MelonLoader-Version.", "\nOnly the setup program is present. Setup for a game is not confirmed. The installer version is not the MelonLoader version.");
            if (proof.Variant is not null) details += L("\nVariante: ", "\nVariant: ") + proof.Variant;
            details += proof.Path is null ? L("\nProgrammpfad nicht bestätigt.", "\nProgram location not confirmed.") : "";
            if (proof.ManagedCopies is { Count: > 0 } copies)
            {
                details += L("\n\nPaketmanager-Abgleich (lokal, nur gelesen):", "\n\nPackage-manager matches (local, read-only):");
                foreach (var copy in copies)
                {
                    details += "\n" + copy.Manager + " · " + copy.PackageId + " · " + L("Paketversion ", "package version ") + copy.PackageVersion
                        + "\n" + (copy.Scope == "User" ? L("Benutzerebene", "Current user") : copy.Scope == "Machine" ? L("Alle Benutzer", "All users") : L("Konfigurierte Ablage", "Configured location"))
                        + " · " + ProgramLocation.Shorten(copy.PackageDirectory);
                    details += "\n" + L("Programmversion: ", "Program version: ") + (copy.Location?.FileVersion ?? L("Nicht bestätigt", "Not confirmed"));
                    if (copy.Location?.Executable is { } executable) details += " · " + ProgramLocation.Shorten(executable);
                }
                details += L("\nPaketversion und Programmversion können abweichen. Kein automatischer Wechsel zu WinGet.", "\nPackage and program versions may differ. No automatic switch to WinGet.");
                if (copies.Count > 1 || proof.Source is not ("Scoop" or "Chocolatey")) details += L("\nMehrere Bezugswege erkannt; Update-Zuordnung bleibt zur Klärung gesperrt.", "\nMultiple sources detected; update routing remains blocked pending clarification.");
            }
            if (!proof.WinGetCompatible) details += L("\nDie erkannte Variante wird nicht über dieses WinGet-Paket aktualisiert.", "\nThis detected variant is not updated through this WinGet package.");
        }
        return _entry.Purpose(_view.English) + "\n\n" + details;
    }
    private string SourceSummary()
    {
        var source = _entry.PackageId is null ? L("Hersteller", "Publisher") : "WinGet";
        var proof = _view.Inventory.Evidence?.GetValueOrDefault(_entry.Id);
        if (_resolvedPackage?.ManagedPackage is { } managed) source = managed.Manager;
        else if (TukuiStandalone) source = L("Hersteller · Standalone", "Publisher · Standalone");
        else if (_entry.Id == "vlc" && _resolvedPackage?.VersionSource == "Publisher") source = "VideoLAN";
        else if (proof?.Source is "Scoop" or "Chocolatey") source = proof.Source;
        if (proof?.Source == "Windows-App" || proof is null && StorePending) source = "Microsoft Store";
        var detected = proof?.Source switch
        {
            "Registry" => L("Windows-Programmliste", "Windows installed programs"),
            "Portable" => L("Portable Programmdatei", "Portable executable"),
            "Portable-Setup" => L("Portable Einrichtungs-EXE", "Portable setup EXE"),
            "Windows-App" => L("Windows-App-Paket", "Windows app package"),
            "SelectedFile" => L("Zugeordnete Programmdatei", "Associated executable"),
            null => null,
            _ => proof.Source
        };
        return L("Quelle: ", "Source: ") + source + (detected is null ? "" : L(" · erkannt über ", " · detected through ") + detected)
            + (_details.Visibility == Visibility.Visible ? "  ▾" : "  ▸");
    }
    private async Task LoadAsync()
    {
        _view.VersionChecking(_entry.Id);
        string? confirmed = null;
        ProgramPackage? descriptionPackage = null;
        try
        {
            await LoadVlcCopiesAsync();
            if (_publisherVersionNote is { } note)
            {
                var version = await Task.Run(ProgramPublisherVersions.ReadSwarmReleaseNoteVersion, _cancel.Token);
                if (_closed) return;
                if (version is not null)
                {
                    note.Text = L("Letzte Versionsinfo von Turtle Beach · ", "Latest version noted by Turtle Beach · ") + version;
                    note.Visibility = Visibility.Visible;
                }
            }
            var package = ForDisplayedVariant(await _view.Service.ResolveAsync(_entry, _cancel.Token));
            if (_closed) return;
            if (package is null && _view.Service is ProgramInstallService manager && manager.ManagedProblem(_entry.Id) is { } problem)
            { _error.Text = UiText.Translate(problem, _view.English); _error.Visibility = Visibility.Visible; }
            _resolvedPackage = package;
            descriptionPackage = package;
            var githubNotice = _view.Inventory.GitHubUpdates?.GetValueOrDefault(_entry.Id);
            confirmed = ProgramVersions.ConfirmedVersion(githubNotice ?? package?.OnlineVersion ?? package?.Version);
            _confirmedOnline = confirmed;
            _view.Assets.RegisterIcon(_entry, package?.IconUrl, package?.IconWebsite); _ = _view.Assets.EnsureIconAsync(_entry);
            _available.Text = confirmed ?? L("Nicht ermittelbar", "Unavailable");
            if (_entry.Id.StartsWith("winget:", StringComparison.Ordinal))
                _what.Text = package?.Description is { Length: > 0 } description
                    ? L("Herstellertext (ggf. Englisch):\n", "Publisher description:\n") + description
                    : L("Keine bestätigte Herstellerbeschreibung verfügbar. Details über die Paketquelle öffnen.", "No confirmed publisher description available. Open the package source for details.");
            _details.Text = Details() + (package?.CanInstall == true ? L("\nBestätigtes Paket: ", "\nConfirmed package: ") + package.Id : "") + (package?.CanInstall == false ? L("\nOnlinequelle: Versionsinformation, kein Installationsnachweis.\n", "\nOnline source: version information, not installation evidence.\n") + package.Details + "\n" + package.CheckedUtc.ToLocalTime().ToString("g") : "") + (package is null ? L("\nPaketprüfung ohne bestätigtes Ergebnis.", "\nPackage check did not return a confirmed result.") : "");
            if (package is not null) _details.Text += L("\nGeprüft: ", "\nChecked: ") + package.CheckedUtc.ToLocalTime().ToString("g");
            if (githubNotice is not null || package?.VersionSource is { Length: > 0 })
                _details.Text += L("\nVersionsquelle: ", "\nVersion source: ") + (githubNotice is not null ? "GitHub" : package!.VersionSource);
            if (package?.PortableUpdate is { } portable)
                _details.Text += PortableSourceDetails(portable);
            if (package?.GitHubUpdate is { } github)
                _details.Text += github.Kind == "VentoyArchive"
                    ? L("\nOffizielles Windows-Paket mit Prüfsumme. Programmdateien werden gesichert; Einstellungen und USB-Laufwerke bleiben unberührt.", "\nOfficial Windows package with checksum. Program files are backed up; settings and USB drives are preserved.")
                    : L("\nOffizieller GitHub-Installer mit Prüfsumme. Aktualisiert die vorhandene Installation.", "\nOfficial GitHub installer with checksum. Updates the existing installation.");
            else if (package?.VersionSource == "Publisher" && package.PortableUpdate is null) _details.Text += confirmed is null
                ? L("\nDie geprüfte Herstellerquelle liefert keine eindeutige Windows-Clientversion.", "\nThe checked publisher source does not provide an unambiguous Windows client version.")
                : L("\nOffizielle Windows-Versionsangabe; Installation über die Herstellerseite.", "\nOfficial Windows version information; install through the publisher website.");
            _packageDetails = _details.Text[Details().Length..];
        }
        catch (Exception e)
        {
            if (!_closed) { _available.Text = L("Nicht ermittelbar", "Unavailable"); _error.Text = UiText.Error(e, _view.English); _error.Visibility = Visibility.Visible; }
        }
        finally
        {
            if (!_closed)
            {
                _availabilityNote.Text = _availabilityNote.Text.TrimStart(' ', '·', '\r', '\n');
                _availabilityNote.Visibility = _availabilityNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                _view.VersionChecked(_entry.Id, confirmed);
                _metadataLoaded = true; RefreshInventory();
                if (_manualRow is not null) _manualRow.Visibility = _view.ShowManualMark(_entry.Id) ? Visibility.Visible : Visibility.Collapsed;
                if (_manualSwitch is not null) _manualSwitch.IsEnabled = _view.CanMark(_entry.Id);
            }
        }
        if (!_closed) DescriptionLoading = LoadDescriptionAsync(descriptionPackage);
    }
    internal void ResetIdle() { if (!_closed) { _leave.Stop(); _leave.Start(); } }
    private void Interacted(object sender, RoutedEventArgs e) => ResetIdle();
    private void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!_view.IsVisible) Close(); }
    private void IconChanged(string id)
    {
        if (id != _entry.Id) return;
        _frame.Dispatcher.BeginInvoke(() => { if (_closed) return; _logo.Source = _view.Assets.Icon(id); _fallback.Visibility = _logo.Source is null ? Visibility.Visible : Visibility.Collapsed; });
    }
    private void Escape(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void Resized(object sender, SizeChangedEventArgs e)
    {
        _frame.MaxHeight = Math.Max(220, _host.ActualHeight - 28);
        _frame.Height = Math.Min(540, _frame.MaxHeight);
        _frame.Width = Math.Max(240, Math.Min(_view.Compact ? 420 : 580, _host.ActualWidth - 28));
        _frame.Padding = new(_view.Compact ? 20 : 26);
        _updateAction.Width = 142;
        foreach (var field in _fields) field.ColumnDefinitions[0].Width = new GridLength(_view.Compact ? 150 : 230);
        _teamSpeak?.RefreshLayout();
    }
    internal void Close()
    {
        _publisherCancellation?.Cancel();
        if (_closed) return; _closed = true; _cancel.Cancel(); _descriptionCancel.Cancel(); _leave.Stop();
        _teamSpeak?.Dispose();
        _view.Assets.IconChanged -= IconChanged;
        _frame.PreviewMouseDown -= Interacted; _frame.PreviewKeyDown -= Interacted; _frame.PreviewMouseWheel -= Interacted;
        _view.IsVisibleChanged -= ViewVisibilityChanged;
        _owner.PreviewKeyDown -= Escape; _owner.SizeChanged -= Resized; _host.Children.Remove(_frame); _host.Children.Remove(_shade); _view.NotifyOverlayChanged();
        _view.SizeChanged -= Resized;
    }
}
