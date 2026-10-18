using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;

namespace PowerControlProWpf;

public partial class UpdateManagerWindow : Window
{
    internal sealed record Choice(string Title, string Subtitle, string Badge, object Payload)
    {
        public string Summary => Payload is AppBackup || string.IsNullOrWhiteSpace(Badge) ? Subtitle : Badge;
    }
    private readonly AppBackupStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<UpdateRelease>>> _load;
    private readonly Func<bool> _busy;
    private readonly bool _english;
    private readonly CancellationTokenSource _cancel = new();
    private readonly CancellationToken _token;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Version _installed = AppVersion.Installed;
    private IReadOnlyList<UpdateRelease> _releases = [];
    private bool _recovery, _working, _closed, _verified, _notes;
    private int _selectionSequence;
    internal ThemePalette CurrentPalette { get; }
    internal bool IsEnglish => _english;
    internal AvailableUpdate? SelectedUpdate { get; private set; }
    internal bool IsRecoveryView => _recovery;
    internal bool IsNotesView => _notes;
    internal Task PendingSelection { get; private set; } = Task.CompletedTask;
    internal bool ReleaseLoadSucceeded { get; private set; }
    internal AvailableUpdate? LatestRelease => _releases.FirstOrDefault()?.Update;

    internal UpdateManagerWindow(ThemePalette palette, bool english, Func<bool> isBusy, AppBackupStore? store = null,
        Func<CancellationToken, Task<IReadOnlyList<UpdateRelease>>>? load = null, bool autoLoad = true)
    {
        InitializeComponent();
        CurrentPalette = palette;
        _token = _cancel.Token; _store = store ?? new(); _load = load ?? UpdateReleaseCatalog.LoadAsync;
        _busy = isBusy; _english = english;


        Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        foreach (var (key, i) in new[] { ("DialogBackground", 0), ("DialogInput", 4), ("DialogText", 5), ("DialogSubtleText", 6), ("DialogAccent", 7), ("DialogLine", 8) }) Resources[key] = Solid(palette.Colors[i]);
        DialogAppearance.ApplySurface(this, DialogFrame, palette);
        FontFamily = new FontFamily(palette.FontFamily);
        CrossButtonAppearance.PreserveCustomSurface(HeaderCloseButton);
        CrossButtonAppearance.PreserveCustomSurface(CloseButton);
        CrossButtonAppearance.PreserveCustomSurface(DeleteBackupButton);
        InstalledText.Text = (english ? "Installed: " : "Installiert: ") + _installed;
        BackupLocation.Text = "AppData · Power Control PRO · Backups · " + (english ? "up to four backups" : "maximal vier Sicherungen");
        BackupLocation.ToolTip = _store.Root;
        if (english)
        {
            BackButton.ToolTip = "Back"; HeaderCloseButton.ToolTip = "Close";
            System.Windows.Automation.AutomationProperties.SetName(BackButton, "Back");
            System.Windows.Automation.AutomationProperties.SetName(HeaderCloseButton, "Close");
            RecoveryHeading.Text = "Restore a previous version"; RecoveryButton.Content = "Restore …";
            KeepHint.Text = "Settings and logs are preserved. Finish active timers or repairs first.";
            InstallHint.Text = "Before installation"; NotesScroll.ToolTip = "Scroll with mouse wheel or arrow keys";
            NotesHint.Text = "Scroll for more changes"; DeleteBackupButton.Content = "Delete backup …";
            ListScrollHint.Text = "⌄ Scroll for more versions"; ChangesButton.Content = "Show changes …  ›"; RecoveryButton.Content = "Restore points …  ›";
        }
        _refresh.Tick += (_, _) => RefreshActions();
        Loaded += async (_, _) => { FitWorkArea(); _refresh.Start(); if (autoLoad) await ReloadAsync(); };
        SizeChanged += (_, _) => AdaptLayout();
        Closing += (_, e) => { if (_working) e.Cancel = true; };
        Closed += (_, _) => { _closed = true; _refresh.Stop(); _cancel.Cancel(); _cancel.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseWindow(this, new()); e.Handled = true; } };
        ApplyView();
    }

    internal async Task ReloadAsync()
    {
        StatusText.Text = _english ? "Loading GitHub releases …" : "GitHub-Versionen werden geladen …";
        try
        {
            _releases = await _load(_token);
            ReleaseLoadSucceeded = true;
            if (_closed) return;
            StatusText.Text = _english ? "Download and file verification are shown before restart." : "Download und Dateiprüfung werden vor dem Neustart angezeigt.";
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { return; }
        catch (Exception e)
        {
            if (_closed) return;
            StatusText.Text = (_english ? "GitHub unavailable; local backups remain usable: " : "GitHub nicht erreichbar; lokale Sicherungen bleiben verfügbar: ") + UiText.Error(e, _english);
        }
        if (!_recovery) ApplyView();
    }

    private void ApplyView()
    {
        _selectionSequence++;
        _verified = false;
        _notes = false; ApplyNavigation();
        Bolt.Visibility = Visibility.Visible;
        HeaderText.Text = _recovery ? (_english ? "Select a backup" : "Sicherung auswählen")
            : (_english ? "Updates and recovery" : "Updates und Wiederherstellung");
        ListHeading.Text = _recovery ? (_english ? "Select a backup" : "Sicherung auswählen") : (_english ? "Select a version" : "Version auswählen");
        ListHint.Text = _recovery ? (_english ? "The previous EXE is saved before each update." : "Vor jedem Update wird die bisherige EXE gesichert.")
            : (_english ? "Compatible published versions with an update function." : "Kompatible veröffentlichte Versionen mit Updatefunktion.");
        RecoveryPanel.Visibility = _recovery ? Visibility.Collapsed : Visibility.Visible;
        DeleteBackupButton.Visibility = _recovery ? Visibility.Visible : Visibility.Collapsed;
        InstallCaption.Text = _recovery ? (_english ? "Restore" : "Wiederherstellen")
            : (_english ? "Install version" : "Version installieren");
        CloseButton.Content = _recovery ? (_english ? "Back to updates" : "Zurück zu Updates") : (_english ? "Close" : "Schließen");
        var choices = new List<Choice>();
        if (_recovery)
        {
            try
            {
                var backups = _store.List();
                LatestText.Text = $"{backups.Count} / {AppBackupStore.Limit} " + (_english ? "backups" : "Sicherungen");
                foreach (var b in backups) choices.Add(new("Version " + b.Version, b.Created.ToLocalTime().ToString("dd.MM.yyyy · HH:mm"), choices.Count == 0 ? (_english ? "Most recent backup" : "Zuletzt gesichert") : "", b));
            }
            catch (Exception e) { StatusText.Text = UiText.Error(e, _english); }
        }
        else
        {
            var newest = _releases.FirstOrDefault();
            LatestText.Text = newest is null ? (_english ? "No release data loaded" : "Keine Versionsdaten geladen")
                : (newest.Update.Version > _installed ? (_english ? "Available: " : "Verfügbar: ") : (_english ? "Latest on GitHub: " : "Neueste auf GitHub: ")) + newest.Update.Version;
            foreach (var r in _releases) choices.Add(new("Version " + r.Update.Version,
                r.Published == DateTimeOffset.MinValue ? "GitHub" : r.Published.ToLocalTime().ToString("dd.MM.yyyy · HH:mm"),
                r.Update.Version == _installed ? (_english ? "Installed" : "Installiert") : r == newest ? (_english ? "Latest release" : "Neueste Veröffentlichung") : (_english ? "Earlier version" : "Frühere Version"), r));
        }
        VersionList.ItemsSource = choices;
        if (choices.Count > 0)
        {
            VersionList.SelectedIndex = 0;
            // A value-equal selection may survive ItemsSource replacement without SelectionChanged.
            PendingSelection = UpdateSelectionAsync();
        }
        else
        {
            DetailHeading.Text = _english ? "No selection" : "Keine Auswahl";
            DetailSubheading.Text = _recovery ? (_english ? "No local backups yet." : "Noch keine lokalen Sicherungen vorhanden.") : (_english ? "No compatible published releases loaded." : "Keine kompatiblen Veröffentlichungen geladen.");
            NotesText.Text = ""; VerificationText.Visibility = Visibility.Collapsed;
        }
        ListScrollHint.Visibility = choices.Count > 3 ? Visibility.Visible : Visibility.Collapsed;
        RefreshActions();
    }

    private void VersionSelected(object sender, SelectionChangedEventArgs e) => PendingSelection = UpdateSelectionAsync();

    private async Task UpdateSelectionAsync()
    {
        var sequence = ++_selectionSequence;
        _verified = false;
        RefreshActions();
        NotesScroll.ScrollToTop();
        if (VersionList.SelectedItem is not Choice choice) return;
        if (choice.Payload is UpdateRelease release)
        {
            DetailHeading.Text = (_english ? "Changes in version " : "Änderungen in Version ") + release.Update.Version;
            DetailSubheading.Text = _english ? "Release notes from GitHub" : "Versionshinweise von GitHub";
            NotesText.Text = PlainNotes(AppReleaseNotes.Select(release.Notes, _english));
            VerificationText.Visibility = Visibility.Collapsed;
            _verified = true;
        }
        else if (choice.Payload is AppBackup backup)
        {
            DetailHeading.Text = (_english ? "Selected backup: " : "Ausgewählte Sicherung: ") + backup.Version;
            DetailSubheading.Text = (_english ? "Saved: " : "Gesichert: ") + backup.Created.ToLocalTime().ToString("dd.MM.yyyy · HH:mm");
            NotesText.Text = PlainNotes(AppReleaseNotes.Select(backup.Notes, _english));
            VerificationText.Visibility = Visibility.Visible;
            VerificationText.Text = _english ? "Verifying backup …" : "Sicherung wird geprüft …";
            try
            {
                var valid = await Task.Run(() => _store.VerifyAsync(backup, _token), _token);
                if (_closed || sequence != _selectionSequence) return;
                _verified = valid;
                VerificationText.Text = valid ? (_english ? "✓ SHA-256 matches" : "✓ SHA-256-Prüfsumme stimmt überein") : (_english ? "Backup damaged; cannot restore." : "Sicherung beschädigt; Wiederherstellung gesperrt.");
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { return; }
            catch (Exception ex) { if (_closed || sequence != _selectionSequence) return; VerificationText.Text = UiText.Error(ex, _english); }
        }
        if (_closed) return;
        RefreshActions();
        _ = Dispatcher.BeginInvoke((Action)(() => { FitListHeight(); UpdateScrollHints(); }), DispatcherPriority.Loaded);
    }

    private string PlainNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return _english ? "No release notes stored for this version." : "Für diese Version sind keine Versionshinweise hinterlegt.";
        // GitHub content is displayed as text, never executed as HTML or a command.
        return Regex.Replace(Regex.Replace(notes.Replace("\r", ""), @"(?m)^\s*#{1,6}\s*", ""), @"\n{3,}", "\n\n").Trim();
    }

    private void RefreshActions()
    {
        if (_closed) return;
        var choice = VersionList.SelectedItem as Choice;
        InstallButton.IsEnabled = !_working && !_busy() && _verified && choice is not null &&
            (choice.Payload is AppBackup || choice.Payload is UpdateRelease r && r.Update.Version != _installed);
        DeleteBackupButton.IsEnabled = !_working && choice?.Payload is AppBackup;
        RecoveryButton.IsEnabled = !_working;
        ChangesButton.IsEnabled = !_working && choice is not null;
        VersionList.IsEnabled = !_working;
    }

    private void InstallSelected(object s, RoutedEventArgs e)
    {
        RefreshActions();
        if (!InstallButton.IsEnabled || VersionList.SelectedItem is not Choice choice) return;
        var update = choice.Payload switch
        {
            UpdateRelease r => r.Update,
            AppBackup b => new AvailableUpdate(Version.Parse(b.Version), "", b.Sha256) { SourceBackup = b, Notes = b.Notes },
            _ => null
        };
        if (update is null) return;
        if (new UpdateInstallWindow(CurrentPalette, _english, _installed, update) { Owner = this }.ShowDialog() != true) return;
        if (_busy()) { RefreshActions(); return; }
        SelectedUpdate = update;
        DialogResult = true;
    }

    private async void DeleteBackup(object s, RoutedEventArgs e)
    {
        if (_working || VersionList.SelectedItem is not Choice { Payload: AppBackup backup }) return;
        var question = (_english ? "Permanently delete this application backup?\n" : "Diese Programmsicherung endgültig löschen?\n") + backup.Label;
        if (ThemedMessageBox.Show(this, question, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _working = true; RefreshActions();
        try { await Task.Run(() => _store.Delete(backup)); StatusText.Text = _english ? "Backup deleted." : "Sicherung gelöscht."; }
        catch (Exception ex) { ThemedMessageBox.Show(this, UiText.Error(ex, _english), "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _working = false; ApplyView(); }
    }
    internal void ShowRecoveryView() { if (_working) return; _recovery = true; ApplyView(); }
    private void OpenRecovery(object s, RoutedEventArgs e) => ShowRecoveryView();
    internal void ShowNotesView()
    {
        if (_working || VersionList.SelectedItem is not Choice) return;
        _notes = true;
        ApplyNavigation();
        NotesScroll.ScrollToTop();
        _ = Dispatcher.BeginInvoke((Action)(() => { NotesScroll.Focus(); UpdateScrollHints(); }), DispatcherPriority.Loaded);
    }
    private void OpenNotes(object s, RoutedEventArgs e) => ShowNotesView();
    private void ApplyNavigation()
    {
        if (DetailPanel is null) return;
        OverviewActions.Visibility = _notes ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel.Visibility = _notes ? Visibility.Visible : Visibility.Collapsed;
        BodyScroll.Visibility = _notes ? Visibility.Collapsed : Visibility.Visible;
        VersionSummary.Visibility = _notes || _recovery ? Visibility.Collapsed : Visibility.Visible;
        RecoveryPanel.Visibility = !_notes && !_recovery ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _notes || _recovery ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Visibility = _notes ? Visibility.Collapsed : Visibility.Visible;
        FooterButtons.Columns = _notes ? 1 : ActualWidth < 380 ? 1 : 2;
        DetailActions.Columns = _recovery && ActualWidth >= 420 ? 2 : 1;
        CloseButton.Style = (Style)FindResource(_notes || _recovery ? "ActionButton" : "DangerButton");
        HeaderText.Text = _notes ? (_english ? "Version changes" : "Änderungen der Version")
            : _recovery ? (_english ? "Select a backup" : "Sicherung auswählen")
            : (_english ? "Updates and recovery" : "Updates und Wiederherstellung");
        CloseButton.Content = _notes ? (_english ? "← Back to overview" : "← Zurück zur Übersicht")
            : _recovery ? (_english ? "Back to updates" : "Zurück zu Updates") : (_english ? "Close" : "Schließen");
        BodyScroll.ScrollToTop();
    }
    private void BackToUpdates(object s, RoutedEventArgs e)
    {
        if (_working) return;
        if (_notes) { _notes = false; ApplyNavigation(); ChangesButton.Focus(); return; }
        _recovery = false; ApplyView();
    }
    private void CloseWindow(object s, RoutedEventArgs e)
    {
        if (_working) return;
        if ((_notes || _recovery) && s != HeaderCloseButton) BackToUpdates(s, e); else Close();
    }
    private void DragHeader(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Clicking an icon inside a button must not initiate a window drag.
        for (var origin = e.OriginalSource as DependencyObject; origin is not null && origin != s;)
        {
            if (origin is System.Windows.Controls.Primitives.ButtonBase) return;
            origin = origin is Visual ? VisualTreeHelper.GetParent(origin) : LogicalTreeHelper.GetParent(origin);
        }
        try { DragMove(); } catch (InvalidOperationException) { }
    }
    private void NotesScrolled(object s, ScrollChangedEventArgs e) => UpdateScrollHints();
    private void UpdateScrollHints()
    {
        if (NotesUp is null || NotesDown is null || NotesScroll is null || NotesHint is null) return;
        NotesUp.Visibility = NotesScroll.VerticalOffset > .5 ? Visibility.Visible : Visibility.Collapsed;
        NotesDown.Visibility = NotesScroll.ScrollableHeight - NotesScroll.VerticalOffset > .5 ? Visibility.Visible : Visibility.Collapsed;
        NotesHint.Visibility = NotesScroll.ScrollableHeight > .5 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ScrollNotesUp(object s, RoutedEventArgs e) => NotesScroll.PageUp();
    private void ScrollNotesDown(object s, RoutedEventArgs e) => NotesScroll.PageDown();
    private void FitWorkArea()
    {
        var area = WindowPlacementService.GetCursorWorkAreaInDips(this);
        var ownerWidth = Owner is { ActualWidth: > 0 } ? Owner.ActualWidth - 32 : area.Width - 24;
        var ownerHeight = Owner is { ActualHeight: > 0 } ? Owner.ActualHeight - 32 : area.Height - 24;
        var availableWidth = Math.Max(240, Math.Min(area.Width - 24, ownerWidth));
        var availableHeight = Math.Max(240, Math.Min(area.Height - 24, ownerHeight));
        MinWidth = Math.Min(420, availableWidth); MinHeight = Math.Min(400, availableHeight);
        Width = Math.Max(MinWidth, Math.Min(620, availableWidth));
        Height = Math.Max(MinHeight, Math.Min(480, availableHeight));
        var centerX = Owner is { ActualWidth: > 0 } ? Owner.Left + Owner.ActualWidth / 2 : area.Left + area.Width / 2;
        var centerY = Owner is { ActualHeight: > 0 } ? Owner.Top + Owner.ActualHeight / 2 : area.Top + area.Height / 2;
        Left = Math.Clamp(centerX - Width / 2, area.Left, area.Right - Width);
        Top = Math.Clamp(centerY - Height / 2, area.Top, area.Bottom - Height);
        AdaptLayout();
    }
    private void BodySizeChanged(object s, SizeChangedEventArgs e) => FitListHeight();
    private void FitListHeight()
    {
        if (VersionList is null || BodyScroll.ActualHeight <= 0) return;
        var hint = ListScrollHint.Visibility == Visibility.Visible ? ListScrollHint.ActualHeight + 4 : 0;
        var verification = VerificationText.Visibility == Visibility.Visible ? VerificationText.ActualHeight + 8 : 0;
        VersionList.MaxHeight = Math.Max(35, BodyScroll.ActualHeight - ListHeading.ActualHeight - hint - verification - 10);
    }
    private void AdaptLayout()
    {
        if (BodyGrid is null) return;
        FitListHeight();
        HeaderText.FontSize = ActualWidth < 480 ? 17 : 21;
        BodyScroll.ToolTip = _english ? "Scroll for more entries." : "Für weitere Einträge scrollen.";
        ApplyNavigation();
    }
}
