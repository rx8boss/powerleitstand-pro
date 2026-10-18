using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using Image = System.Windows.Controls.Image;
using MessageBox = PowerControlProWpf.ThemedMessageBox;

namespace PowerControlProWpf;

public partial class MainWindow : Window
{
    private sealed record CompactLogEntry(string Time, string Message)
    {
        public string? Detail { get; init; }
        public string? FullTime { get; init; }
        public string Category { get; init; } = "EREIGNIS";
        public string DisplayTime => FullTime ?? Time;
        public string Tooltip => Detail ?? Message;
    }
    private readonly ObservableCollection<CompactLogEntry> _eventLogEntries = new();
    private readonly ObservableCollection<CompactLogEntry> _repairLogEntries = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _noticeAutoCloseTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private int _seconds = 300;
    private DateTimeOffset _timerDeadline;
    private int _nextWarningSecond = 10;
    private bool _finalWarningPlayed;
    private double _warningPlaybackFactor = 1;
    private string? _timerAction;
    private int _pageIndex;
    private bool _english;
    private bool _repairRunning;
    private CancellationTokenSource? _repairCancellation;
    private double _repairPercent;
    private string? _repairCommand;
    private MediaPlayer? _warningPlayer;
    private readonly Dictionary<TextBlock, double> _baseTextSizes = new();
    private readonly Dictionary<Button, double> _baseButtonSizes = new();
    private readonly Dictionary<Image, (double Width, double Height)> _baseImageSizes = new();
    private readonly Dictionary<UIElement, (int Row, int Column, int Span)> _originalHomePositions = new();
    private sealed record ReferenceCardDecoration(FrameworkElement Content, Grid Stripes, FrameworkElement LowerBoundary);
    private readonly Dictionary<Border, ReferenceCardDecoration> _referenceCardDecorations = new();
    private Grid _userReferenceStripes = null!;
    private Grid _repairReferenceStripes = null!;
    private readonly AppSettings _settings = AppSettings.Load();
    private int _effectiveSizeIndex = 3;
    private ScrollViewer? _compactScrollHost;
    private readonly DispatcherTimer _placementSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    internal bool SkipSavedPlacementForTests { get; set; }
    private bool _currentUserIsAdministrator;
    private AvailableUpdate? _availableUpdate;
    private UpdateCheckState _updateCheckState = UpdateCheckState.Checking;
    private bool _updateCheckFailedConnection;
    private int _updateCheckSequence;
    private enum UpdateCheckState { Checking, Current, Available, Failed }
    private readonly string _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "logs");
    private readonly object _logLock = new();
    private string LogFilePath => Path.Combine(_logDirectory, "events.log");
    private string RepairLogFilePath => Path.Combine(_logDirectory, "repair.log");
    internal bool IsTimerActive => _timer.IsEnabled;
    internal bool IsBusy => _timer.IsEnabled || _repairRunning;
    internal bool IsEnglish => _english;
    internal string CurrentThemeName => SelectedTheme();
    internal string CurrentIconSet => ThemePalette.For(SelectedTheme()).IconSet;
    internal string CurrentFontFamily => ThemePalette.For(SelectedTheme()).FontFamily;

    public MainWindow()
    {
        CrossButtonAppearance.Initialize();
        InitializeComponent();
        StandardLogPreview.ItemsSource = _eventLogEntries;
        _eventLogEntries.CollectionChanged += (_, _) => UpdateCompactLogEmptyState();
        _repairLogEntries.CollectionChanged += (_, _) => UpdateCompactLogEmptyState();
        InitializeReferenceCardDecorations();
        InitializeExpandedLog();
        InitializeLiveDiagnostics();
        LayoutUpdated += (_, _) => { UpdateReferenceCardDecorations(); UpdateOptionsViewport(); };
        Closed += (_, _) => { StopRepairProgressAnimation(); StopNeonPresentation(); };
        InstalledVersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.1.0"} · WPF";
        _timer.Tick += TimerTick;
        _noticeAutoCloseTimer.Tick += (_, _) => CloseSettingsNotice(this, new RoutedEventArgs());
        PreviewMouseDown += CloseNoticeOnOutsideClick;
        Deactivated += (_, _) => { if (SettingsNoticePopup.IsOpen) CloseSettingsNotice(this, new RoutedEventArgs()); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SettingsNoticePopup.IsOpen)
            {
                CloseSettingsNotice(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        _placementSaveTimer.Tick += (_, _) => { _placementSaveTimer.Stop(); PersistWindowPosition(); };
        SourceInitialized += (_, _) =>
        {
            if (SkipSavedPlacementForTests) return;
            try
            {
                ApplySize();
                WindowPlacementService.CenterOnCursorScreen(this);
            }
            catch (Exception exception) { Log("Fensterposition konnte nicht gesetzt werden: " + exception.Message); }
        };
        LocationChanged += (_, _) =>
        {
            if (SkipSavedPlacementForTests || _isWindowDragging || !IsLoaded || WindowState != WindowState.Normal) return;
            _placementSaveTimer.Stop();
            _placementSaveTimer.Start();
        };
        Closing += (_, _) => { _placementSaveTimer.Stop(); if (!IsBusy) PersistWindowPosition(); };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                Dispatcher.BeginInvoke(ShowPendingRepairSummary);
        };
        LoadSettings();
        ApplyLanguage();
        Loaded += (_, _) => { ApplyTheme(SelectedTheme()); ApplySize(); };
        LoadPersistedLog();
        LoadPersistedRepairLog();
        LoadAutoStart();
        Log("PowerLeitstand PRO WPF gestartet.");
        LoadUsers(null, null!);
    }

    internal void CenterWindowOnCursorScreen()
    {
        try
        {
            WindowPlacementService.CenterOnCursorScreen(this);
            PersistWindowPosition();
        }
        catch (Exception exception)
        {
            Log((_english ? "Could not center the window: " : "Fenster konnte nicht zentriert werden: ") + exception.Message);
        }
    }

    private void PersistWindowPosition()
    {
        if (SkipSavedPlacementForTests || !WindowPlacementService.TryRemember(this, _settings)) return;
        try { _settings.Save(); }
        catch (Exception exception)
        {
            Log((_english ? "Window position could not be saved: " : "Fensterposition konnte nicht gespeichert werden: ") + exception.Message);
        }
    }

    private static string ThemeId(ComboBoxItem item) => item.Tag?.ToString() ?? item.Content?.ToString() ?? "Standard";
    private string SelectedTheme() => ThemeBox.SelectedItem is ComboBoxItem item ? ThemeId(item) : "Standard";

    private void LoadSettings()
    {
        _english = _settings.Language == "en";
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => ThemeId(item) == _settings.Theme) ?? ThemeBox.Items[0];
        SizeBox.SelectedIndex = Math.Clamp(_settings.SizeIndex, 0, SizeBox.Items.Count - 1);
        WarningSound.IsChecked = _settings.WarningEnabled;
        WarningSoundBox.SelectedItem = WarningSoundBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == _settings.WarningSound) ?? WarningSoundBox.Items[0];
        WarningVolume.Value = Math.Clamp(_settings.WarningVolume, 0, 100);
        RequestAdministratorOnLaunch.IsChecked = _settings.RequestAdministratorOnLaunch;
        ApplySize();
    }

    private void SaveSettings()
    {
        if (!IsLoaded || SkipSavedPlacementForTests) return;
        _settings.Language = _english ? "en" : "de";
        _settings.Theme = SelectedTheme();
        _settings.SizeIndex = SizeBox.SelectedIndex;
        _settings.WarningEnabled = WarningSound.IsChecked == true;
        _settings.WarningSound = (WarningSoundBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system-warning.wav";
        _settings.WarningVolume = (int)Math.Round(WarningVolume.Value);
        _settings.RequestAdministratorOnLaunch = RequestAdministratorOnLaunch.IsChecked == true;
        try { _settings.Save(); }
        catch (Exception exception) { Log((_english ? "Settings could not be saved: " : "Einstellungen konnten nicht gespeichert werden: ") + exception.Message); }
    }

    private void SaveSettingsChanged(object s, RoutedEventArgs e) => SaveSettings();

    internal async Task CheckForUpdatesOnStartupAsync()
    {
        var sequence = ++_updateCheckSequence;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var update = await UpdateService.CheckAsync(cancellation.Token);
            if (sequence != _updateCheckSequence) return;
            _availableUpdate = update;
            SetUpdateCheckState(_availableUpdate is null ? UpdateCheckState.Current : UpdateCheckState.Available);
            if (_availableUpdate is not null)
                (System.Windows.Application.Current as App)?.ShowUpdateNotification(_availableUpdate.Version.ToString());
        }
        catch (Exception exception)
        {
            if (sequence != _updateCheckSequence) return;
            _updateCheckFailedConnection = exception is HttpRequestException or TaskCanceledException;
            SetUpdateCheckState(UpdateCheckState.Failed);
            Log((_english ? "Background update check failed: " : "Update-Prüfung im Hintergrund fehlgeschlagen: ") + exception.Message);
        }
    }

    private void SetUpdateCheckState(UpdateCheckState state)
    {
        _updateCheckState = state;
        var updateColor = state switch
        {
            UpdateCheckState.Current => "ReadyBrush",
            UpdateCheckState.Available => "DangerBorderBrush",
            UpdateCheckState.Failed => "WarningBrush",
            _ => "AccentBrush"
        };
        UpdateNoticeIcon.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, state switch
        {
            UpdateCheckState.Current => "HeaderBellCurrentBrush",
            UpdateCheckState.Available => "HeaderBellAvailableBrush",
            UpdateCheckState.Failed => "HeaderBellWarningBrush",
            _ => "HeaderBellCheckingBrush"
        });
        CheckUpdatesButton.SetResourceReference(Button.BorderBrushProperty, updateColor);
        CheckUpdatesButton.SetResourceReference(Button.BackgroundProperty, state switch
        {
            UpdateCheckState.Current => "UpdateCheckCurrentBrush",
            UpdateCheckState.Available => "UpdateCheckAvailableBrush",
            UpdateCheckState.Failed => "UpdateCheckFailedBrush",
            _ => "ButtonBrush"
        });
        CheckUpdatesButton.ToolTip = state switch
        {
            UpdateCheckState.Current => _english ? "Up to date · check again" : "Aktuell · erneut prüfen",
            UpdateCheckState.Available => _english ? "Update available · check and install" : "Update verfügbar · prüfen und installieren",
            UpdateCheckState.Failed => _english ? "Update check failed · try again" : "Update-Prüfung fehlgeschlagen · erneut versuchen",
            _ => _english ? "Update check in progress" : "Update-Prüfung läuft"
        };
        UpdateNoticeButton.ToolTip = _english ? "Show bell status" : "Glocken-Status anzeigen";
        System.Windows.Automation.AutomationProperties.SetName(UpdateNoticeButton, UpdateNoticeButton.ToolTip.ToString()!);
        UpdateStatus.Visibility = state == UpdateCheckState.Checking || SettingsNoticePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateStatus.Text = state switch
        {
            UpdateCheckState.Current => _english ? "This version is up to date." : "Diese Version ist aktuell.",
            UpdateCheckState.Available => (_english ? "New version available: " : "Neue Version verfügbar: ") + _availableUpdate?.Version,
            UpdateCheckState.Failed => _updateCheckFailedConnection
                ? (_english ? "No connection to GitHub releases." : "Keine Verbindung zu GitHub-Releases.")
                : (_english ? "The update check could not be completed." : "Die Update-Prüfung konnte nicht abgeschlossen werden."),
            _ => _english ? "Checking GitHub releases…" : "GitHub-Releases werden geprüft…"
        };
        NoticeCurrentLabel.Text = _english ? "Up to date" : "Aktuell";
        NoticeConnectionLabel.Text = state == UpdateCheckState.Failed && !_updateCheckFailedConnection
            ? (_english ? "Check failed" : "Prüfung fehlgeschlagen")
            : (_english ? "No connection" : "Keine Verbindung");
        NoticeAvailableLabel.Text = _english ? "Update available" : "Update verfügbar";
    }

    internal void OpenUpdateNotice(object s, RoutedEventArgs e)
    {
        if (SettingsNoticePopup.IsOpen)
        {
            CloseSettingsNotice(this, new RoutedEventArgs());
            return;
        }

        SetSettingsOverlay(showOptions: false, showNotice: false);
        SettingsNoticePopup.IsOpen = true;
        _noticeAutoCloseTimer.Stop();
        _noticeAutoCloseTimer.Start();
    }

    private void CloseNoticeOnOutsideClick(object sender, MouseButtonEventArgs e)
    {
        if (!SettingsNoticePopup.IsOpen || e.OriginalSource is not DependencyObject source || IsWithinVisual(source, UpdateNoticeButton)) return;
        CloseSettingsNotice(sender, new RoutedEventArgs());
    }

    private static bool IsWithinVisual(DependencyObject source, DependencyObject ancestor) => IsInside(source, ancestor);

    private void CloseSettingsNotice(object s, RoutedEventArgs e)
    {
        SettingsNoticePopup.IsOpen = false;
        _noticeAutoCloseTimer.Stop();
    }

    private void BellStatusPopupClosed(object? s, EventArgs e) => _noticeAutoCloseTimer.Stop();

    private void CloseSettingsOverlay(object s, RoutedEventArgs e) =>
        SetSettingsOverlay(showOptions: false, showNotice: false);

    private void OpenProjectOnGitHub(object s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ProjectLinks.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show((_english ? "The GitHub page could not be opened: " : "Die GitHub-Seite konnte nicht geöffnet werden: ") + exception.Message,
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CheckUpdates(object s, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            MessageBox.Show(_english ? "Finish the active timer or repair first." : "Bitte zuerst den aktiven Timer oder die Reparatur beenden.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        CheckUpdatesButton.IsEnabled = false;
        ++_updateCheckSequence;
        _updateCheckFailedConnection = false;
        SetUpdateCheckState(UpdateCheckState.Checking);
        UpdateStatus.Visibility = SettingsNoticePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var update = await UpdateService.CheckAsync();
            _availableUpdate = update;
            if (update is null)
            {
                SetUpdateCheckState(UpdateCheckState.Current);
                return;
            }
            SetUpdateCheckState(UpdateCheckState.Available);
            if (MessageBox.Show(_english ? $"Download and install version {update.Version}? The app will restart." : $"Version {update.Version} herunterladen und installieren? Die App startet danach neu.", "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            UpdateStatus.Text = _english ? "Downloading and verifying SHA-256…" : "Download und SHA-256-Prüfung laufen…";
            await UpdateService.PrepareAndLaunchAsync(update, () => IsBusy);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            _updateCheckFailedConnection = exception is HttpRequestException or TaskCanceledException;
            SetUpdateCheckState(_availableUpdate is null ? UpdateCheckState.Failed : UpdateCheckState.Available);
            Log((_english ? "Update failed: " : "Update fehlgeschlagen: ") + exception.Message);
            MessageBox.Show((_english ? "The update could not be installed. The existing EXE is unchanged: " : "Das Update konnte nicht installiert werden. Die bisherige EXE bleibt erhalten: ") + exception.Message, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
    private void WarningSoundChanged(object s, SelectionChangedEventArgs e) => SaveSettings();
    private void WarningVolumeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WarningVolumeValue is null) return;
        WarningVolumeValue.Text = $"{(int)Math.Round(e.NewValue)}%";
        if (_warningPlayer is not null) _warningPlayer.Volume = e.NewValue / 100 * _warningPlaybackFactor;
        SaveSettings();
    }

    private void ApplySize()
    {
        if (SizeBox.SelectedIndex == 0)
        {
            var available = WindowPlacementService.GetCursorWorkAreaInDips(this);
            var choice = WindowSizeMath.SelectAutomatic(available.Width, available.Height);
            _effectiveSizeIndex = choice.Index;
            MinWidth = Math.Min(860, choice.Width);
            MinHeight = Math.Min(620, choice.Height);
            (Width, Height) = (choice.Width, choice.Height);
            ConfigureCompactScrolling(choice.Width < 860 || choice.Height < 620);
        }
        else
        {
            _effectiveSizeIndex = Math.Clamp(SizeBox.SelectedIndex - 1, 0, 3);
            (Width, Height) = _effectiveSizeIndex switch { 0 => (860, 620), 1 => (900, 650), 2 => (1050, 670), _ => (1200, 760) };
            MinWidth = 860;
            MinHeight = 620;
            ConfigureCompactScrolling(false);
        }
        if (IsLoaded) ApplyLayoutScale();
    }

    private void ConfigureCompactScrolling(bool enabled)
    {
        if (enabled && _compactScrollHost is null)
        {
            Content = null;
            Root.MinWidth = 860;
            Root.MinHeight = 620;
            _compactScrollHost = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = Root
            };
            Content = _compactScrollHost;
        }
        else if (!enabled && _compactScrollHost is not null)
        {
            _compactScrollHost.Content = null;
            Content = Root;
            _compactScrollHost = null;
            Root.MinWidth = 0;
            Root.MinHeight = 0;
        }
    }

    private void ApplyLayoutScale()
    {
        // Re-layout the controls at their final size. A Viewbox/ScaleTransform would raster-scale the glyphs.
        var factor = _effectiveSizeIndex switch { 2 => 1050.0 / 900, 3 => 1200.0 / 900, _ => 1.0 };
        var compact = _effectiveSizeIndex == 0;
        Root.RowDefinitions[0].Height = new GridLength(90 * factor);
        Root.RowDefinitions[2].Height = new GridLength(36);
        Root.RowDefinitions[3].Height = new GridLength(6);
        PageStatusBarRow.Height = new GridLength(105);
        HeaderLayout.ColumnDefinitions[0].Width = new GridLength(245 * factor);
        HeaderLayout.ColumnDefinitions[2].Width = new GridLength(205 * factor);
        FooterLayout.ColumnDefinitions[0].Width = new GridLength(175);
        FooterLayout.ColumnDefinitions[2].Width = new GridLength(175);

        foreach (var textBlock in FindVisualChildren<TextBlock>(Root))
        {
            if (IsInside(textBlock, SettingsPanel) || IsInside(textBlock, SettingsNoticePanel) || IsInside(textBlock, LogBox) || IsInside(textBlock, UserLogBox) || IsInside(textBlock, RepairLogBox) || IsInside(textBlock, UserList)) continue;
            if (!_baseTextSizes.TryGetValue(textBlock, out var baseSize))
                _baseTextSizes[textBlock] = baseSize = textBlock.FontSize;
            textBlock.FontSize = Math.Round(baseSize * factor);
        }
        foreach (var button in FindVisualChildren<Button>(Root))
        {
            if (IsInside(button, SettingsPanel) || IsInside(button, SettingsNoticePanel) || IsInside(button, LogBox) || IsInside(button, UserLogBox) || IsInside(button, RepairLogBox) || IsInside(button, UserList)) continue;
            if (!_baseButtonSizes.TryGetValue(button, out var baseSize))
                _baseButtonSizes[button] = baseSize = button.FontSize;
            button.FontSize = Math.Round(baseSize * factor);
            if (ReferenceEquals(button.Style, Resources["HeaderButton"]) || ReferenceEquals(button.Style, Resources["HeaderNoticeButton"]) || ReferenceEquals(button.Style, Resources["CloseButtonStyle"]) || ReferenceEquals(button.Style, Resources["PageArrowButton"]))
            {
                var isPageArrow = ReferenceEquals(button, PagePrevious) || ReferenceEquals(button, PageNext);
                button.Width = (isPageArrow ? 28 : 42) * factor;
                button.Height = (isPageArrow ? 28 : 36) * factor;
            }
        }
        PageIndicator.Width = 36 * factor;
        AdminRightsBadge.Width = 125 * factor;
        AdminRightsBadge.Height = 31 * factor;
        foreach (var icon in FindVisualChildren<Image>(Root))
        {
            if (IsInside(icon, SettingsPanel) || IsInside(icon, SettingsNoticePanel)) continue;
            if (!_baseImageSizes.TryGetValue(icon, out var baseSize))
                _baseImageSizes[icon] = baseSize = (icon.Width, icon.Height);
            icon.Width = baseSize.Width * factor;
            icon.Height = baseSize.Height * factor;
        }
        BrandLogo.Width = BrandLogo.Height = 34;
        FooterBoltIcon.Width = FooterBoltIcon.Height = 13;
        GitHubProjectButton.FontSize = 9;
        FooterBrandLabel.FontSize = InstalledVersionText.FontSize = 9;

        foreach (var button in new[] { ShutdownButton, RestartButton, LogoutButton, LockButton, TimerShutdown, TimerLock, TimerRestart })
            button.MinHeight = (compact ? 62 : 67) * factor;
        foreach (var label in new[] { TimerShutdownLabel, TimerLockLabel, TimerRestartLabel })
            label.FontSize = Math.Round((compact ? 11 : 12) * factor);
        foreach (var subtitle in new[] { TimerShutdownSub, TimerLockSub, TimerRestartSub })
            subtitle.FontSize = Math.Round((compact ? 9 : 10) * factor);
        AbortTimerButton.MinHeight = 48 * factor;
        foreach (var button in new[] { OpenLogButton, ClearLogButton, RefreshLogButton })
        {
            button.Width = double.NaN;
            button.Height = (compact ? 33 : 35) * factor;
        }
        LogBox.FontSize = 11 * factor;
        UserLogBox.FontSize = Math.Round(13 * factor);
        RepairLogBox.FontSize = Math.Round(14 * factor);
        UserList.FontSize = Math.Round((compact ? 9 : 10) * factor);
        UserList.Resources["UserTableHeaderHeight"] = 25 * factor;
        UserList.Resources["UserTableRowHeight"] = compact ? 24.0 : 25 * factor;
        foreach (var sub in new[] { UsersSub, StatusSub, UserStatusHeaderSub, UserLogHeaderSub,
                     RepairHeaderSub, RepairStatusHeaderSub, RepairLogSub, UserStatusSub, RepairStatusSub })
            sub.FontSize = Math.Round(13 * factor);
        CurrentUserText.FontSize = Math.Round(14 * factor);
        CurrentUserRole.FontSize = Math.Round(12 * factor);
        AdminRightsStatus.FontSize = Math.Round(10 * factor);
        RepairExplanation.FontSize = Math.Round(13 * factor);
        RepairProgressLabel.FontSize = Math.Round(14 * factor);
        RepairTimeText.FontSize = RepairResult.FontSize = Math.Round(13 * factor);
        RepairDismSub.FontSize = RepairSfcSub.FontSize = Math.Round(12 * factor);
        foreach (var button in new[] { RepairDismButton, RepairSfcButton, RepairCancelButton,
                     CreateUserButton, EditUserButton, RefreshUsersButton, ClearRepairLogButton, OpenRepairLogButton })
            button.FontSize = Math.Round(12 * factor);
        foreach (var button in new[] { CreateUserButton, EditUserButton, RefreshUsersButton })
            button.FontSize = Math.Round(13 * factor);
        LogBox.Height = (compact ? 112 : 120) * factor;
        UserLogBox.Height = double.NaN;
        RepairDismButton.Height = RepairSfcButton.Height = RepairCancelButton.Height = (compact ? 40 : 44) * factor;
        RepairActionRow.ColumnDefinitions[4].Width = new GridLength(140 * factor);
        RepairProgress.Height = 11 * factor;
        OpenRepairLogButton.Height = 30 * factor;
        ClearRepairLogButton.Height = 30 * factor;
        ShowRepairProtocolButton.Height = (compact ? 31 : 34) * factor;
        // Let the translated label determine the required width instead of clipping it.
        ShowRepairProtocolButton.Width = double.NaN;
        ShowRepairProtocolButton.MinWidth = (compact ? 174 : 222) * factor;
        ShowRepairProtocolButton.FontSize = Math.Round(12 * factor);
        UpdateRepairProtocolButtonLabel();
        UserList.Height = double.NaN;
        UserList.ItemContainerStyle = (System.Windows.Style)UserList.Resources["UserListRowStyle"];
        CreateUserButton.Width = EditUserButton.Width = 164 * factor;
        CreateUserButton.Height = EditUserButton.Height = RefreshUsersButton.Height = 40 * factor;
        RefreshUsersButton.Width = 132 * factor;
        foreach (var badge in new[] { StatusBadge, UserStatusBadge, RepairStatusBadge })
        {
            badge.Width = badge.Height = 54 * factor;
            badge.CornerRadius = new CornerRadius(27 * factor);
            if (badge.Parent is Grid badgeRow)
            {
                badgeRow.Height = 72 * factor;
                badgeRow.ColumnDefinitions[0].Width = new GridLength(64 * factor);
            }
        }
        SizeUserColumns();
        TimerSub.Width = _effectiveSizeIndex == 0 ? 105 : 124 * factor;
        StandardTimePicker.Height = compact ? 42 : 48;
        StandardTimePicker.Margin = new Thickness(0, compact ? 5 : 7, 0, 0);
        TimerActionRow.Margin = new Thickness(0, compact ? 5 : 7, 0, 0);
        if (TimerValue.Parent is StackPanel timerValueStack && timerValueStack.Parent is Border timerPicker && timerPicker.Parent is Grid timerHeader)
            timerHeader.ColumnDefinitions[1].Width = new GridLength(225 * factor);
        foreach (var card in FindVisualChildren<Border>(Root))
        {
            if (ReferenceEquals(card.Style, Resources["Card"])) SetCardPadding(card, new Thickness((compact ? 12 : 14) * factor));
        }
        ApplyHomeLayout(SelectedTheme());
    }

    // All palettes share the approved composition; colors and icons remain independent.
    // All existing controls are moved, so their event handlers and state remain intact.
    private void ApplyHomeLayout(string theme)
    {
        if (HomePage is null || SizeBox is null || ThemeBox is null) return;
        if (_originalHomePositions.Count == 0)
            foreach (UIElement element in HomePage.Children)
                _originalHomePositions[element] = (Grid.GetRow(element), Grid.GetColumn(element), Grid.GetColumnSpan(element));

        var standard = true;
        if (standard)
        {
            UserList.FontSize = _effectiveSizeIndex switch { 2 => 14, 3 => 15, _ => 13 };
            UserList.Resources["UserTableHeaderHeight"] = _effectiveSizeIndex switch { 2 => 32.0, 3 => 34.0, _ => 30.0 };
            UserList.Resources["UserTableRowHeight"] = _effectiveSizeIndex switch { 0 => 32.0, 1 => 34.0, 2 => 37.0, _ => 40.0 };
        }
        else
        {
            var tableFactor = _effectiveSizeIndex switch { 2 => 1050.0 / 900, 3 => 1200.0 / 900, _ => 1.0 };
            UserList.FontSize = Math.Round((_effectiveSizeIndex == 0 ? 9 : 10) * tableFactor);
            UserList.Resources["UserTableHeaderHeight"] = 25 * tableFactor;
            UserList.Resources["UserTableRowHeight"] = _effectiveSizeIndex == 0 ? 24.0 : 25 * tableFactor;
        }
        Root.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty,
            standard ? "StandardReferenceAppBrush" : "AppBrush");
        foreach (var card in new[] { ImmediateCardSurface, TimerCardSurface, RepairCardSurface })
            card.SetResourceReference(Border.BackgroundProperty,
                standard ? "StandardReferenceCardBrush" : "CardSurfaceBrush");
        ImmediateReferenceStripes.Visibility = TimerReferenceStripes.Visibility =
            standard ? Visibility.Visible : Visibility.Collapsed;
        _userReferenceStripes.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        _repairReferenceStripes.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        UserCardCorner.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        RepairCardCorner.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        StandardStatusLogBar.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        PageStatusBarRow.Height = standard
            ? new GridLength(105)
            : new GridLength(0);
        UserLegacyDetails.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        RepairLegacyDetails.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        RepairPage.RowDefinitions[0].Height = new GridLength(standard ? 1 : 1.04, GridUnitType.Star);
        RepairPage.RowDefinitions[1].Height = standard ? new GridLength(0) : new GridLength(0.96, GridUnitType.Star);
        RepairProgress.Height = standard ? (_effectiveSizeIndex <= 1 ? 13 : 15)
            : 11 * (_effectiveSizeIndex switch { 2 => 1050.0 / 900, 3 => 1200.0 / 900, _ => 1.0 });
        UserPage.RowDefinitions[0].Height = standard ? new GridLength(1, GridUnitType.Star) : new GridLength(1.04, GridUnitType.Star);
        UserPage.RowDefinitions[1].Height = standard ? new GridLength(0) : new GridLength(0.96, GridUnitType.Star);
        UserCardSurface.SetResourceReference(Border.BackgroundProperty,
            standard ? "StandardReferenceCardBrush" : "CardSurfaceBrush");
        StandardStatusColumn.Width = new GridLength(standard && _effectiveSizeIndex >= 2 ? 2.1 : 2.8, GridUnitType.Star);
        StandardLogHeaderColumn.Width = new GridLength(standard && _effectiveSizeIndex >= 2 ? 124 : 110);
        StandardLogPreview.FontSize = standard && _effectiveSizeIndex >= 2 ? 11 : 10;
        StandardLogEmptyState.FontSize = StandardLogPreview.FontSize;
        UpdateStandardMetricLabels();
        foreach (var oldCardPart in new UIElement[] { StatusCardGlow, StatusCardSurface, StatusCardCorner,
                     LogCardGlow, LogCardSurface, LogCardCorner })
            oldCardPart.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        var factor = _effectiveSizeIndex switch { 2 => 1050.0 / 900, 3 => 1200.0 / 900, _ => 1.0 };
        var compact = _effectiveSizeIndex == 0;

        foreach (var (element, position) in _originalHomePositions)
        {
            Grid.SetRow(element, standard ? (position.Row == 0 ? position.Column : 2) : position.Row);
            Grid.SetColumn(element, standard && position.Row == 0 ? 0 : position.Column);
            Grid.SetColumnSpan(element, standard && position.Row == 0 ? 2 : position.Span);
        }

        if (standard)
        {
            HomePage.RowDefinitions[0].Height = new GridLength(0.72, GridUnitType.Star);
            HomePage.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
            HomePage.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            HomePage.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            HomePage.RowDefinitions[0].Height = new GridLength(1.04, GridUnitType.Star);
            HomePage.RowDefinitions[1].Height = new GridLength(0.96, GridUnitType.Star);
            if (HomePage.RowDefinitions.Count == 3) HomePage.RowDefinitions.RemoveAt(2);
            HomePage.ColumnDefinitions[0].Width = new GridLength(0.94, GridUnitType.Star);
            HomePage.ColumnDefinitions[1].Width = new GridLength(1.06, GridUnitType.Star);
        }

        if (ShutdownButton.Parent is System.Windows.Controls.Primitives.UniformGrid immediateActions)
        {
            immediateActions.Columns = standard ? 4 : 2;
            immediateActions.Margin = standard ? new Thickness(0, 3 * factor, 0, 0) : new Thickness(0, 10, 0, 0);
        }
        TimerActionRow.ColumnDefinitions[0].Width = new GridLength(standard ? 1 : 1.2, GridUnitType.Star);
        TimerHeader.Margin = new Thickness(standard ? 9 * factor : 0, 0, 0, 0);
        TimerCardCorner.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        TimerReferenceTopGap.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        TimerReferenceCorner.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        TimerPickerBorder.SetResourceReference(Border.BackgroundProperty,
            standard ? "StandardActionButtonBrush" : "ButtonBrush");
        TimerPickerBorder.SetResourceReference(Border.BorderBrushProperty,
            standard ? "AccentBrush" : "InputBorderBrush");
        TimerPickerBorder.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        StandardTimePicker.Visibility = Visibility.Collapsed;
        CompactStandardTimePicker.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        TimerPickerBorder.MinHeight = 0;
        if (TimerValue.Parent is StackPanel valueStack && valueStack.Parent is Border picker && picker.Parent is Grid timerHeader)
        {
            timerHeader.ColumnDefinitions[0].Width = standard ? new GridLength(250 * factor) : new GridLength(1, GridUnitType.Star);
            timerHeader.ColumnDefinitions[1].Width = standard ? new GridLength(1, GridUnitType.Star) : new GridLength(225 * factor);
            timerHeader.ColumnDefinitions[2].Width = standard ? new GridLength(410) : new GridLength(0);
            timerHeader.ColumnDefinitions[3].Width = standard ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        TimerSub.Width = standard ? 190 * factor : (compact ? 105 : 124 * factor);
        LogBox.Height = standard ? double.NaN : (compact ? 112 : 120) * factor;
        LogBox.Margin = standard ? new Thickness(0, 3 * factor, 0, 0) : new Thickness(0, 5, 0, 0);
        LogSub.Visibility = StatusSub.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        ImmediateSub.FontSize = (standard ? (compact ? 10 : 11) : 12) * factor;
        foreach (var title in new[] { StatusTitle, LogSub.Parent is StackPanel logTitleStack ? logTitleStack.Children.OfType<TextBlock>().First() : StatusTitle })
            title.FontSize = (standard ? 18 : 20) * factor;
        foreach (var header in new[] { (StatusTitle.Parent as FrameworkElement)?.Parent, (LogSub.Parent as FrameworkElement)?.Parent }.OfType<StackPanel>())
            if (header.Children.OfType<Image>().FirstOrDefault() is Image icon)
                icon.Width = icon.Height = (standard ? 26 : 34) * factor;
        foreach (var button in new[] { RefreshLogButton, ClearLogButton, OpenLogButton })
            button.Height = (standard ? (compact ? 24 : 28) : (compact ? 33 : 35)) * factor;
        foreach (var button in new[] { TimerShutdown, TimerLock, TimerRestart })
            if (button.Content is Grid buttonContent)
                buttonContent.ColumnDefinitions[0].Width = new GridLength(standard ? 42 * factor : 34);
        foreach (var step in new[] { TimerMinusMin, TimerPlusMin, TimerMinusHourButton, TimerPlusHourButton })
        {
            step.Style = (Style)FindResource(standard ? "StandardTimerStepButton" : "TimerStepButton");
            step.Padding = standard ? new Thickness(2, 2, 2, 2) : new Thickness(2, 4, 2, 4);
        }
        TimerShutdown.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            standard ? "StandardActionDangerBrush" : "DangerBrush");
        TimerShutdown.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,
            standard ? "StandardActionDangerBorderBrush" : "DangerBorderBrush");
        TimerLock.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            standard ? "StandardActionButtonBrush" : "LockBrush");
        TimerRestart.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            standard ? "StandardActionButtonBrush" : "ButtonBrush");

        foreach (var border in HomePage.Children.OfType<Border>())
            if (ReferenceEquals(border.Style, Resources["Card"]))
                SetCardPadding(border, new Thickness((standard ? (compact ? 7 : 8) : (compact ? 12 : 14)) * factor));

        foreach (var action in new[] { ShutdownButton, RestartButton, LogoutButton, LockButton,
                     TimerShutdown, TimerLock, TimerRestart })
        {
            action.Tag = standard ? "Reference" : null;
            action.MinHeight = (standard ? (compact ? 50 : 54) : (compact ? 62 : 67)) * factor;
            if (action == ShutdownButton || action == RestartButton || action == LogoutButton || action == LockButton)
            {
                action.HorizontalContentAlignment = _effectiveSizeIndex == 0 || standard && _effectiveSizeIndex == 1
                    ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
                action.Padding = standard && compact ? new Thickness(7, 2, 7, 2) : new Thickness(11, 7, 11, 7);
            }
        }
        ImmediateIconImage.Visibility = theme == "Standard" ? Visibility.Collapsed : Visibility.Visible;
        ImmediateIconGlyph.Visibility = theme == "Standard" ? Visibility.Visible : Visibility.Collapsed;
        ImmediateIconGlyph.Width = ImmediateIconGlyph.Height = 34 * factor;
        ImmediateReferenceCorner.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        ImmediateReferenceTopGap.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        ImmediateCardCorner.Visibility = standard ? Visibility.Collapsed : Visibility.Visible;
        ImmediateHeader.Margin = new Thickness(standard ? 9 * factor : 0, 0, 0, 0);
        ImmediateSub.Margin = new Thickness((standard ? 50 : 35) * factor, 2 * factor, 0, 0);
        foreach (var label in new[] { ShutdownLabel, RestartLabel, LogoutLabel, LockLabel })
            label.FontSize = standard && _effectiveSizeIndex <= 1 ? 13 : 15 * factor;
        ShutdownButton.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
            standard ? "StandardActionDangerBrush" : "DangerBrush");
        ShutdownButton.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty,
            standard ? "StandardActionDangerBorderBrush" : "DangerBorderBrush");
        foreach (var action in new[] { RestartButton, LogoutButton, LockButton })
        {
            action.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,
                standard ? "StandardActionButtonBrush" : action == LogoutButton ? "SuccessBrush" : action == LockButton ? "LockBrush" : "ButtonBrush");
            action.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "AccentBrush");
        }
        LogoutSub.Width = standard && _effectiveSizeIndex >= 2 ? double.NaN : 118;
        LogoutSub.TextWrapping = standard && _effectiveSizeIndex >= 2 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        AbortTimerButton.MinHeight = standard && _effectiveSizeIndex == 3
            ? 82
            : (standard ? 56 : 48) * factor;
        AbortTimerButton.Margin = standard ? new Thickness(3, 3, 3, 3) : new Thickness(3, 8, 3, 3);

        if (StatusBadge.Parent is Grid statusRow && statusRow.Parent is Border statusPanel)
        {
            statusRow.Height = (standard ? (compact ? 40 : 44) : 72) * factor;
            statusRow.ColumnDefinitions[0].Width = new GridLength((standard ? 48 : 64) * factor);
            statusPanel.Padding = new Thickness(standard ? 5 * factor : 10);
            statusPanel.Margin = standard ? new Thickness(0, 5 * factor, 0, 0) : new Thickness(0, 12, 0, 0);
            StatusBadge.Width = StatusBadge.Height = (standard ? (compact ? 34 : 38) : 54) * factor;
            StatusBadge.CornerRadius = new CornerRadius(StatusBadge.Width / 2);
            StatusSymbol.FontSize = (standard ? 26 : 36) * factor;
            StatusText.FontSize = (standard ? 16 : 20) * factor;
            StatusDetail.FontSize = (standard ? 10 : 12) * factor;
        }
        Root.RowDefinitions[0].Height = new GridLength(standard && _effectiveSizeIndex >= 2 ? 75 : standard ? 90 : 90 * factor);
        Root.RowDefinitions[2].Height = new GridLength(36);
        HeaderLayout.ColumnDefinitions[2].Width = new GridLength((standard ? 305 : 205) * factor);
        HeaderIcon.Width = HeaderIcon.Height = (standard && _effectiveSizeIndex >= 2 ? 30 : standard ? 36 : 48) * factor;
        TitleText.FontSize = TitleProText.FontSize = (standard && _effectiveSizeIndex >= 2 ? 20 : standard ? 23 : 29) * factor;
        MottoText.FontSize = (standard && _effectiveSizeIndex >= 2 ? 9 : 11) * factor;
        SettingsHeaderButton.Style = (Style)Resources[standard ? "HeaderNoticeButton" : "HeaderButton"];
        SettingsHeaderButton.Width = (standard ? 95 : 42) * factor;
        SettingsHeaderImage.Visibility = theme == "Standard" ? Visibility.Collapsed : Visibility.Visible;
        SettingsHeaderGlyph.Visibility = theme == "Standard" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHeaderLabel.FontSize = (standard ? 9.5 : 11) * factor;
        UpdateNoticeButton.Width = 42 * factor;
        SettingsHeaderLabel.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        HeaderActionsBorder.Background = standard ? Brushes.Transparent : (Brush)Resources["InputBrush"];
        HeaderActionsBorder.BorderThickness = new Thickness(standard ? 0 : 1);
        HeaderActionsBorder.Padding = new Thickness(standard ? 0 : 3);
        HeaderActionsSeparator.Visibility = standard ? Visibility.Visible : Visibility.Collapsed;
        HeaderActionsSeparator.Height = 22 * factor;
        HeaderActionsSeparator.Margin = new Thickness(10 * factor, 0, 10 * factor, 0);
        HeaderMinimizeButton.Style = (Style)Resources[standard ? "HeaderNoticeButton" : "HeaderButton"];
        HeaderCloseButton.Style = (Style)Resources["CloseButtonStyle"];
        HeaderMinimizeButton.Width = HeaderCloseButton.Width = (standard ? 28 : 42) * factor;
        HeaderMinimizeButton.Margin = new Thickness(0, 0, (standard ? 10 : 5) * factor, 0);
        HeaderCloseButton.FontSize = (standard ? 19 : 20) * factor;
        if (SettingsHeaderButton.Parent is StackPanel headerActions)
        {
            headerActions.Children.Remove(SettingsHeaderButton);
            headerActions.Children.Insert(standard ? 0 : 1, SettingsHeaderButton);
        }
        ApplyClassicCancelTheme(theme);
    }

    private void InitializeReferenceCardDecorations()
    {
        // Keep decoration in its own layer, independent of the content padding.
        _userReferenceStripes = CreateReferenceStripes("UserReferenceStripes");
        _repairReferenceStripes = CreateReferenceStripes("RepairReferenceStripes");
        AddReferenceCardLayer(ImmediateCardSurface, ImmediateReferenceStripes, ShutdownButton);
        AddReferenceCardLayer(TimerCardSurface, TimerReferenceStripes, TimerShutdown);
        AddReferenceCardLayer(UserCardSurface, _userReferenceStripes, UserList);
        AddReferenceCardLayer(RepairCardSurface, _repairReferenceStripes, RepairActionRow);
    }

    private Grid CreateReferenceStripes(string name)
    {
        var stripes = new Grid { Name = name };
        foreach (var path in ImmediateReferenceStripes.Children.OfType<System.Windows.Shapes.Path>())
            stripes.Children.Add(new System.Windows.Shapes.Path
            {
                Data = path.Data.Clone(), Fill = path.Fill, Stroke = path.Stroke,
                StrokeThickness = path.StrokeThickness, Opacity = path.Opacity
            });
        RegisterName(name, stripes);
        return stripes;
    }

    private void AddReferenceCardLayer(Border card, Grid stripes, FrameworkElement lowerBoundary)
    {
        var content = (FrameworkElement)card.Child;
        if (stripes.Parent is Grid oldParent) oldParent.Children.Remove(stripes);
        var padding = card.Padding;
        card.Child = null;
        card.Padding = new Thickness(0);
        content.Margin = padding;
        var layer = new Grid { ClipToBounds = true };
        stripes.Width = 120;
        stripes.Height = 75;
        stripes.Margin = new Thickness(0, 1, 1, 0);
        stripes.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        stripes.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        stripes.IsHitTestVisible = false;
        stripes.ClipToBounds = true;
        stripes.Visibility = Visibility.Collapsed;
        layer.Children.Add(stripes);
        layer.Children.Add(content);
        card.Child = layer;
        _referenceCardDecorations.Add(card, new(content, stripes, lowerBoundary));
    }

    private void SetCardPadding(Border card, Thickness padding)
    {
        if (_referenceCardDecorations.TryGetValue(card, out var decoration))
        {
            card.Padding = new Thickness(0);
            decoration.Content.Margin = padding;
        }
        else card.Padding = padding;
    }

    private void UpdateReferenceCardDecorations()
    {
        foreach (var (card, decoration) in _referenceCardDecorations)
        {
            if (decoration.Stripes.Visibility != Visibility.Visible)
            {
                card.ClearValue(UIElement.ClipProperty);
                continue;
            }
            if (!card.IsVisible || card.ActualWidth <= 0 || card.ActualHeight <= 0) continue;
            var rect = new Rect(0, 0, card.ActualWidth, card.ActualHeight);
            var radius = card.CornerRadius.TopLeft;
            if (card.Clip is not RectangleGeometry clip || clip.Rect != rect || clip.RadiusX != radius || clip.RadiusY != radius)
                card.Clip = new RectangleGeometry(rect, radius, radius);
            var limit = decoration.LowerBoundary.TranslatePoint(new System.Windows.Point(0, 0), card).Y;
            var height = Math.Min(75, Math.Max(24, limit - 3));
            if (Math.Abs(decoration.Stripes.Height - height) > 0.1)
                decoration.Stripes.Height = height;
        }
        UpdateRepairProgressAnimation();
    }

    private static bool IsInside(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = InputParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static DependencyObject? InputParent(DependencyObject element) => element switch
    {
        ContentElement content => ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent,
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(element),
        _ => LogicalTreeHelper.GetParent(element)
    };

    private void UpdateRepairProtocolButtonLabel()
    {
        ShowRepairProtocolLabel.Text = _effectiveSizeIndex == 0
            ? (_english ? "View result" : "Protokoll anzeigen")
            : (_english ? "View repair result" : "Reparaturprotokoll anzeigen");
        ShowRepairProtocolButton.ToolTip = _english ? "Show the last repair result" : "Auswertung der letzten Reparatur anzeigen";
    }

    private void Log(string text)
    {
        var entry = $"[{DateTime.Now:dd.MM.yyyy, HH:mm:ss}] {text}";
        AddLogEntry(entry);
        if (StandardLogPreview.Items.Count > 0)
            StandardLogPreview.ScrollIntoView(StandardLogPreview.Items[0]);
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(LogFilePath, entry + Environment.NewLine);
            }
        }
        catch
        {
            // Das Programm bleibt nutzbar, falls Windows den Logordner vorübergehend sperrt.
        }
    }

    private void UpdateStandardMetricLabels()
    {
        UpdateLiveDiagnosticsLabels();
    }

    private void AddLogEntry(string entry)
    {
        LogBox.Items.Insert(0, entry);
        UserLogBox.Items.Insert(0, entry);
        _eventLogEntries.Insert(0, ToCompactLogEntry(entry));
        while (LogBox.Items.Count > 1000) LogBox.Items.RemoveAt(LogBox.Items.Count - 1);
        while (UserLogBox.Items.Count > 1000) UserLogBox.Items.RemoveAt(UserLogBox.Items.Count - 1);
        while (_eventLogEntries.Count > 1000) _eventLogEntries.RemoveAt(_eventLogEntries.Count - 1);
    }

    private static CompactLogEntry ToCompactLogEntry(string entry)
    {
        var end = entry.IndexOf(']');
        if (!entry.StartsWith('[') || end < 0) return new CompactLogEntry("", entry);
        var stamp = entry[1..end];
        var time = stamp[(stamp.LastIndexOf(',') + 1)..].Trim();
        return new CompactLogEntry(time.Length > 5 ? time[..5] : time, entry[(end + 1)..].Trim())
            { FullTime = time, Detail = entry };
    }

    private void UpdateCompactLogContext()
    {
        var repair = _pageIndex == 2;
        StandardLogPreview.ItemsSource = repair ? _repairLogEntries : _eventLogEntries;
        StandardLogPreview.ToolTip = repair ? (_english ? "Repair log" : "Reparaturprotokoll")
            : (_english ? "Event log" : "Ereignisprotokoll");
        var refresh = repair ? (_english ? "Refresh repair log" : "Reparaturprotokoll aktualisieren")
            : (_english ? "Refresh log" : "Protokoll aktualisieren");
        var clear = repair ? (_english ? "Delete repair log" : "Reparaturprotokoll löschen")
            : (_english ? "Clear log" : "Protokoll löschen");
        var open = repair ? (_english ? "Open repair log folder" : "Ordner des Reparaturprotokolls öffnen")
            : (_english ? "Open log folder" : "Protokollordner öffnen");
        foreach (var (button, label) in new[] { (StandardRefreshIconButton, refresh), (StandardClearIconButton, clear), (StandardOpenIconButton, open) })
        {
            button.ToolTip = label;
            System.Windows.Automation.AutomationProperties.SetName(button, label);
        }
        UpdateCompactLogActionState();
        UpdateCompactLogEmptyState();
        UpdateExpandedLogContext();
    }

    private void UpdateCompactLogActionState()
    {
        var canChangeLog = _pageIndex != 2 || !_repairRunning;
        StandardRefreshIconButton.IsEnabled = StandardClearIconButton.IsEnabled = canChangeLog;
        StandardRefreshLogMenuItem.IsEnabled = StandardClearLogMenuItem.IsEnabled = canChangeLog;
    }

    private void UpdateCompactLogEmptyState()
    {
        var empty = (_pageIndex == 2 ? _repairLogEntries : _eventLogEntries).Count == 0;
        StandardLogPreview.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        StandardLogEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        StandardLogEmptyState.Text = _pageIndex == 2
            ? (_english ? "No repair log available yet." : "Noch kein Reparaturprotokoll vorhanden.")
            : (_english ? "No events available yet." : "Noch keine Ereignisse vorhanden.");
    }

    private void RefreshCompactLog(object sender, RoutedEventArgs e)
    {
        if (_pageIndex != 2) { RefreshLog(sender, e); return; }
        if (_repairRunning) return;
        RepairLogBox.Items.Clear();
        _repairLogEntries.Clear();
        LoadPersistedRepairLog();
    }

    private void ClearCompactLog(object sender, RoutedEventArgs e)
    {
        if (_pageIndex == 2) ClearRepairLog(sender, e);
        else ClearLog(sender, e);
    }

    private void OpenCompactLog(object sender, RoutedEventArgs e)
    {
        if (_pageIndex == 2) OpenRepairLog(sender, e);
        else OpenLog(sender, e);
    }

    private void LoadPersistedLog()
    {
        try
        {
            lock (_logLock)
            {
                if (!File.Exists(LogFilePath)) return;
                foreach (var entry in File.ReadLines(LogFilePath).Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(1000).Reverse())
                    AddLogEntry(entry);
                if (StandardLogPreview.Items.Count > 0)
                    StandardLogPreview.ScrollIntoView(StandardLogPreview.Items[0]);
            }
        }
        catch
        {
            // Eine defekte alte Logdatei darf den Start der App nicht verhindern.
        }
    }

    private void ReloadPersistedLog()
    {
        LogBox.Items.Clear();
        UserLogBox.Items.Clear();
        _eventLogEntries.Clear();
        LoadPersistedLog();
    }

    private void ShutdownNow(object s, RoutedEventArgs e) => ConfirmAndExecute(PowerAction.Shutdown);
    private void RestartNow(object s, RoutedEventArgs e) => ConfirmAndExecute(PowerAction.Restart);
    private void LogoutNow(object s, RoutedEventArgs e) => ConfirmAndExecute(PowerAction.SignOut);
    private void LockNow(object s, RoutedEventArgs e) => ConfirmAndExecute(PowerAction.Lock);

    private void ConfirmAndExecute(PowerAction action)
    {
        if (IsBusy)
        {
            MessageBox.Show(_english ? "Finish the active timer or repair first." : "Bitte zuerst den aktiven Timer oder die Reparatur beenden.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var label = ActionLabel(action);
        var question = _english ? $"Run {label} now?" : $"{label} jetzt ausführen?";
        if (MessageBox.Show(question, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ExecuteAction(action);
    }

    private string ActionLabel(PowerAction action) => action switch
    {
        PowerAction.Shutdown => _english ? "shutdown" : "Herunterfahren",
        PowerAction.Restart => _english ? "restart" : "Neustart",
        PowerAction.SignOut => _english ? "sign out" : "Abmelden",
        _ => _english ? "lock Windows" : "Windows sperren"
    };

    private void ExecuteAction(PowerAction action)
    {
        try
        {
            PowerActionDispatcher.Execute(action, (file, arguments) =>
            {
                _ = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })
                    ?? throw new InvalidOperationException("Der Windows-Befehl konnte nicht gestartet werden.");
            });
            Log((_english ? "Action started: " : "Aktion gestartet: ") + ActionLabel(action));
        }
        catch (Exception exception)
        {
            Log((_english ? "Action failed: " : "Aktion fehlgeschlagen: ") + exception.Message);
            MessageBox.Show((_english ? "Windows could not perform this action: " : "Windows konnte die Aktion nicht ausführen: ") + exception.Message, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void StartTimerShutdown(object s, RoutedEventArgs e) => StartTimer("shutdown");
    private void StartTimerLock(object s, RoutedEventArgs e) => StartTimer("lock");
    private void StartTimerRestart(object s, RoutedEventArgs e) => StartTimer("restart");

    private void StartTimer(string action)
    {
        if (IsBusy) return;
        _ = TimerActionDispatcher.Resolve(action);
        _timerAction = action;
        _timerDeadline = DateTimeOffset.UtcNow.AddSeconds(_seconds);
        _nextWarningSecond = 10;
        _finalWarningPlayed = false;
        _timer.Start();
        SetTimerState(true);
        Log(_english ? $"Timer started: {TimerActionDispatcher.Label(action, true)} in {TimerLabel()}." : $"Timer gestartet: {TimerActionDispatcher.Label(action, false)} in {TimerLabel()}.");
    }

    private void AbortTimer(object s, RoutedEventArgs e)
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _warningPlayer?.Stop();
        _timerAction = null;
        SetTimerState(false);
        StatusDetail.Text = _english ? "No action active" : "Keine Aktion aktiv";
        TimerValue.Text = TimerLabel();
        Log(_english ? "Timer cancelled." : "Timer abgebrochen.");
    }

    private void TimerTick(object? s, EventArgs e)
    {
        var remaining = Math.Max(0, (int)Math.Ceiling((_timerDeadline - DateTimeOffset.UtcNow).TotalSeconds));
        if (remaining == 0)
        {
            var action = _timerAction;
            _timer.Stop();
            _warningPlayer?.Stop();
            _timerAction = null;
            SetTimerState(false);
            TimerValue.Text = TimerLabel();
            Log(_english ? "Timer completed." : "Timer abgelaufen.");
            if (action is not null) ExecuteAction(TimerActionDispatcher.Resolve(action));
            return;
        }
        if (WarningSound.IsChecked == true && remaining <= 5 && !_finalWarningPlayed)
        {
            _finalWarningPlayed = true;
            PlayWarningSound("urgent.wav", 1);
        }
        else if (WarningSound.IsChecked == true && remaining > 5 && remaining <= _nextWarningSecond && _nextWarningSecond >= 6)
        {
            _nextWarningSecond -= 2;
            PlayWarningSound(volumeFactor: 0.55);
        }
        var label = TimerLabel(remaining);
        TimerValue.Text = label;
        AbortCountdown.Text = (_english ? "Remaining: " : "Noch: ") + CountdownLabel(remaining);
        StatusDetail.Text = (_english ? "Timer active: " : "Timer aktiv: ") + label;
        UserStatusSub.Text = RepairStatusSub.Text = StatusDetail.Text;
    }

    private void SetTimerState(bool active)
    {
        (System.Windows.Application.Current as App)?.UpdateTrayTimerState(IsBusy);
        RepairDismButton.IsEnabled = RepairSfcButton.IsEnabled = !IsBusy;
        CreateUserButton.IsEnabled = AdminRightsBadge.IsEnabled = !_repairRunning;
        AbortTimerButton.IsEnabled = active;
        ShutdownButton.IsEnabled = RestartButton.IsEnabled = LogoutButton.IsEnabled = LockButton.IsEnabled = !IsBusy;
        TimerShutdown.IsEnabled = TimerLock.IsEnabled = TimerRestart.IsEnabled = !IsBusy;
        TimerMinusMin.IsEnabled = TimerPlusMin.IsEnabled = TimerMinusHourButton.IsEnabled = TimerPlusHourButton.IsEnabled = !IsBusy;
        AbortHint.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        AbortCountdown.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        if (active) AbortCountdown.Text = (_english ? "Remaining: " : "Noch: ") + CountdownLabel(Math.Max(0, (int)Math.Ceiling((_timerDeadline - DateTimeOffset.UtcNow).TotalSeconds)));
        StatusText.Text = active ? (_english ? "Timer active" : "Timer aktiv")
            : _repairRunning ? (_english ? "Repair running" : "Reparatur läuft")
            : (_english ? "Ready" : "Bereit");
        UserStatusText.Text = RepairStatusText.Text = StatusText.Text;
        StatusSymbol.Text = UserStatusSymbol.Text = RepairStatusSymbol.Text = IsBusy ? "◷" : "✓";
        StatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, IsBusy ? "WarningBrush" : "ReadyBrush");
        UserStatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, IsBusy ? "WarningBrush" : "ReadyBrush");
        RepairStatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, IsBusy ? "WarningBrush" : "ReadyBrush");
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, IsBusy ? "WarningBrush" : "ReadyBrush");
        UserStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, IsBusy ? "WarningBrush" : "ReadyBrush");
        RepairStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, IsBusy ? "WarningBrush" : "ReadyBrush");
        if (!active) StatusDetail.Text = UserStatusSub.Text = RepairStatusSub.Text = _repairRunning
            ? (_english ? "Windows repair active" : "Windows-Reparatur aktiv")
            : (_english ? "No action active" : "Keine Aktion aktiv");
        if (!active) UpdateRepairStatus();
    }

    private string TimerLabel() => TimerLabel(_seconds);
    private static string CountdownLabel(int seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
    private string TimerLabel(int seconds)
    {
        var minutes = Math.Max(0, (int)Math.Ceiling(seconds / 60.0));
        if (minutes >= 60)
            return _english ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes / 60} Std. {minutes % 60} Min.";
        return _english ? $"{minutes} minutes" : $"{minutes} Minuten";
    }

    private void UpdateTimer() { _seconds = Math.Clamp(_seconds, 60, 86400); TimerValue.Text = TimerLabel(); }
    private void TimerMinusMinute(object s, RoutedEventArgs e) { _seconds -= 60; UpdateTimer(); }
    private void TimerPlusMinute(object s, RoutedEventArgs e) { _seconds += 60; UpdateTimer(); }
    private void TimerMinusHour(object s, RoutedEventArgs e) { _seconds -= 3600; UpdateTimer(); }
    private void TimerPlusHour(object s, RoutedEventArgs e) { _seconds += 3600; UpdateTimer(); }

    private void SwitchPage(object s, RoutedEventArgs e)
    {
        _pageIndex = Math.Clamp(_pageIndex + (ReferenceEquals(s, PagePrevious) ? -1 : 1), 0, 2);
        HomePage.Visibility = _pageIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        UserPage.Visibility = _pageIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        RepairPage.Visibility = _pageIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePageNavigation();
        // Die Seite wechselt sofort; die Windows-Abfrage erfolgt nur auf Klick auf Aktualisieren.
    }

    private void UpdatePageNavigation()
    {
        PageIndicator.Text = $"{_pageIndex + 1} / 3";
        PagePrevious.IsEnabled = _pageIndex > 0;
        PageNext.IsEnabled = _pageIndex < 2;
        UpdateCompactLogContext();
    }

    internal void ShowRepairPage()
    {
        _pageIndex = 2;
        HomePage.Visibility = UserPage.Visibility = Visibility.Collapsed;
        RepairPage.Visibility = Visibility.Visible;
        UpdatePageNavigation();
    }

    internal void ShowUsersPage()
    {
        _pageIndex = 1;
        HomePage.Visibility = RepairPage.Visibility = Visibility.Collapsed;
        UserPage.Visibility = Visibility.Visible;
        UpdatePageNavigation();
    }

    private void ToggleSettings(object s, RoutedEventArgs e)
    {
        if (_expandedLogOpen) SetExpandedLogOpen(false, false);
        SetSettingsOverlay(showOptions: SettingsPanel.Visibility != Visibility.Visible, showNotice: false);
    }

    private void SetSettingsOverlay(bool showOptions, bool showNotice)
    {
        if (showOptions) LoadAutoStart();
        if (!showNotice && SettingsNoticePopup.IsOpen) CloseSettingsNotice(this, new RoutedEventArgs());
        SettingsShell.Visibility = SettingsPanel.Visibility = showOptions ? Visibility.Visible : Visibility.Collapsed;
        SettingsBackdrop.Visibility = showOptions ? Visibility.Visible : Visibility.Collapsed;
        UpdateNeonOverlayVisibility();
        UpdateStatus.Visibility = showOptions && _updateCheckState != UpdateCheckState.Checking
            ? Visibility.Visible : Visibility.Collapsed;
        var pageInputEnabled = !showOptions;
        HomePage.IsHitTestVisible = UserPage.IsHitTestVisible = RepairPage.IsHitTestVisible = PageNavigation.IsHitTestVisible = pageInputEnabled;
    }
    private void Minimize(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseApp(object s, RoutedEventArgs e) => Close();
    private void ShowStandardLogMenu(object s, RoutedEventArgs e)
    {
        if (StandardLogMenuButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = StandardLogMenuButton;
        menu.IsOpen = true;
    }
    private void DragWindow(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        BeginWindowDragPresentation();
        try { DragMove(); }
        finally { EndWindowDragPresentation(); }
    }
    private void ClearLog(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(_english ? "Do you want to permanently delete the event log?" : "Möchten Sie das Ereignisprotokoll unwiderruflich löschen?", _english ? "Delete event log?" : "Ereignisprotokoll löschen?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { lock (_logLock) { if (File.Exists(LogFilePath)) File.Delete(LogFilePath); } }
        catch (Exception exception)
        {
            MessageBox.Show((_english ? "The log could not be cleared: " : "Das Protokoll konnte nicht gelöscht werden: ") + exception.Message, "Log PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LogBox.Items.Clear();
        UserLogBox.Items.Clear();
        _eventLogEntries.Clear();
        Log(_english ? "Event log cleared." : "Ereignisprotokoll gelöscht.");
    }

    private void RefreshLog(object s, RoutedEventArgs e)
    {
        ReloadPersistedLog();
        Log(_english ? "Event log refreshed." : "Ereignisprotokoll aktualisiert.");
    }

    private void OpenLog(object s, RoutedEventArgs e)
    {
        OpenLogFolder(LogFilePath);
    }

    private void OpenLogFolder(string logFilePath)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var arguments = File.Exists(logFilePath)
                ? $"/select,\"{logFilePath}\""
                : $"\"{_logDirectory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show((_english ? "The log folder could not be opened: " : "Der Protokollordner konnte nicht geöffnet werden: ") + exception.Message, "Log PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void German(object s, RoutedEventArgs e) { _english = false; ApplyLanguage(); LoadUsers(null, null!); SaveSettings(); Log("Sprache: Deutsch"); }
    private void English(object s, RoutedEventArgs e) { _english = true; ApplyLanguage(); LoadUsers(null, null!); SaveSettings(); Log("Language: English"); }

    private void ApplyLanguage()
    {
        Title = "PowerLeitstand PRO";
        TitleText.Text = "PowerLeitstand";
        UpdatePageNavigation();
        MottoText.Text = _english ? "Control Windows · Schedule actions · Repair the system" : "Windows steuern · Aktionen planen · System reparieren";
        SettingsHeaderLabel.Text = _english ? "Settings" : "Einstellungen";
        ImmediateTitle.Text = _english ? "Immediate" : "Sofort"; ImmediateSub.Text = _english ? "Direct system actions" : "Direkte Systemaktionen";
        ShutdownLabel.Text = TimerShutdownLabel.Text = _english ? "Shut down" : "Herunterfahren"; ShutdownSub.Text = _english ? "Turn off PC" : "PC ausschalten";
        RestartLabel.Text = TimerRestartLabel.Text = _english ? "Restart" : "Neustart"; RestartSub.Text = _english ? "Restart PC" : "PC neu starten";
        TimerLockLabel.Text = _english ? "Lock" : "Sperren"; TimerShutdownSub.Text = _english ? "Timer shutdown" : "Timer Herunterfahren"; TimerLockSub.Text = _english ? "Timer lock" : "Timer Sperren"; TimerRestartSub.Text = _english ? "Timer restart" : "Timer Neustart";
        LogoutLabel.Text = _english ? "Sign out" : "Abmelden"; LogoutSub.Text = _english ? "Sign out current user" : "Aktuellen Benutzer abmelden";
        LockLabel.Text = _english ? "Lock" : "Sperren"; LockSub.Text = _english ? "Lock Windows" : "Windows sperren";
        TimerTitle.Text = "Timer"; TimerSub.Text = _english ? "Automatic action after time" : "Automatische Aktion nach Zeit"; AbortLabel.Text = _english ? "Cancel timer" : "Timer abbrechen"; AbortHint.Text = _english ? "Stop the scheduled action" : "Geplante Aktion stoppen";
        TimerMinusMin.Content = _english ? "−1 min" : "−1 Min"; TimerPlusMin.Content = _english ? "+1 min" : "+1 Min"; TimerMinusHourButton.Content = _english ? "−1 hr" : "−1 Std"; TimerPlusHourButton.Content = _english ? "+1 hr" : "+1 Std";
        StatusTitle.Text = UserStatusTitle.Text = RepairStatusTitle.Text = "Status"; StatusSub.Text = UserStatusHeaderSub.Text = RepairStatusHeaderSub.Text = _english ? "System information" : "System-Informationen";
        if (_timer.IsEnabled)
        {
            SetTimerState(true);
            TimerTick(null, EventArgs.Empty);
        }
        else
        {
            SetTimerState(false);
        }
        LogSub.Text = UserLogHeaderSub.Text = _english ? "Event log" : "Ereignisprotokoll"; RefreshLogLabel.Text = _english ? "Refresh" : "Aktualisieren"; ClearLogLabel.Text = _english ? "Clear" : "Löschen"; OpenLogLabel.Text = _english ? "Open folder" : "Ordner öffnen";
        StandardWindowsLabel.Text = _diagnosticsSnapshot.Pc?.Windows ?? "Windows";
        UpdateStandardMetricLabels();
        if (_diagnosticsKind is not null) RefreshDiagnosticsHover();
        StandardLogMenuButton.ToolTip = _english ? "Log actions" : "Log-Aktionen";
        StandardRefreshIconButton.ToolTip = _english ? "Refresh log" : "Protokoll aktualisieren";
        StandardClearIconButton.ToolTip = _english ? "Clear log" : "Protokoll löschen";
        StandardOpenIconButton.ToolTip = _english ? "Open log folder" : "Protokollordner öffnen";
        System.Windows.Automation.AutomationProperties.SetName(StandardRefreshIconButton, StandardRefreshIconButton.ToolTip.ToString()!);
        System.Windows.Automation.AutomationProperties.SetName(StandardClearIconButton, StandardClearIconButton.ToolTip.ToString()!);
        System.Windows.Automation.AutomationProperties.SetName(StandardOpenIconButton, StandardOpenIconButton.ToolTip.ToString()!);
        StandardRefreshLogMenuItem.Header = RefreshLogLabel.Text;
        StandardClearLogMenuItem.Header = ClearLogLabel.Text;
        StandardOpenLogMenuItem.Header = OpenLogLabel.Text;
        UpdateCompactLogContext();
        UsersTitle.Text = _english ? "User overview" : "Benutzerübersicht"; UsersSub.Text = _english ? "Local Windows users" : "Lokale Windows-Benutzer"; RefreshUsersLabel.Text = _english ? "Refresh" : "Aktualisieren"; CreateUserLabel.Text = _english ? "Create user" : "Benutzer erstellen"; EditUserLabel.Text = _english ? "Edit user" : "Benutzer bearbeiten";
        CreateUserButton.ToolTip = _english ? "Create a local user" : "Lokalen Benutzer erstellen";
        EditUserButton.ToolTip = _english ? "Edit selected user" : "Ausgewählten Benutzer bearbeiten";
        RefreshUsersButton.ToolTip = _english ? "Refresh user list" : "Benutzerliste aktualisieren";
        UpdateTitle.Text = _english ? "Updates" : "Updates";
        CheckUpdatesLabel.Text = _english ? "Check for updates" : "Nach Updates suchen";
        RepairTitle.Text = _english ? "Windows repair" : "Windows-Reparatur";
        RepairHeaderSub.Text = _english ? "Check and repair system files" : "Systemdateien prüfen und reparieren";
        RepairExplanation.Text = _english
            ? "Run DISM first, then SFC. Windows will not restart automatically."
            : "Zuerst DISM, danach SFC ausführen. Es wird nichts automatisch neu gestartet.";
        RepairDismSub.Text = _english ? "Repair Windows image" : "Windows-Abbild reparieren";
        RepairSfcSub.Text = _english ? "Check system files" : "Systemdateien prüfen";
        RepairCancelLabel.Text = _english ? "Cancel" : "Abbrechen";
        RepairLogTitle.Text = "Log PRO";
        RepairLogSub.Text = _english ? "Repair log" : "Reparaturprotokoll";
        OpenRepairLogLabel.Text = _english ? "Open folder" : "Ordner öffnen";
        ClearRepairLogLabel.Text = _english ? "Delete" : "Löschen";
        UpdateRepairProtocolButtonLabel();
        UpdateRepairTime();
        if (_repairRunning && _repairCommand is not null)
            RepairProgressLabel.Text = _repairPercent > 0 ? $"{_repairCommand}: {_repairPercent:0.#} %"
                : (_english ? $"{_repairCommand} is running…" : $"{_repairCommand} läuft…");
        else if (_lastRepairCancelled)
            RepairProgressLabel.Text = _english ? "Repair cancelled." : "Reparatur abgebrochen.";
        else if (_lastRepairExitCode is int repairCode)
        {
            _lastRepairSummary = RepairResultSummary.Create(_repairCommand?.StartsWith("SFC", StringComparison.Ordinal) == true ? "sfc" : "dism",
                repairCode, false, _repairOutput.ToString(), GetRepairFailureHint(null), _repairWatch.Elapsed, _english);
            RepairProgressLabel.Text = _lastRepairSummary.Title;
            RepairResult.Text = _lastRepairSummary.Message;
        }
        else RepairProgressLabel.Text = _english ? "Ready. Select a repair." : "Bereit. Reparatur auswählen.";
        UpdateRepairStatus();
        UserNameColumn.Header = _english ? "User" : "Benutzer"; UserRoleColumn.Header = _english ? "Type" : "Typ"; UserDescriptionColumn.Header = _english ? "Description" : "Beschreibung"; UserStateColumn.Header = _english ? "Status" : "Status";
        SettingsTitle.Text = _english ? "Options" : "Optionen"; SettingsSub.Text = _english ? "Application settings" : "Einstellungen der Anwendung"; LanguageTitle.Text = _english ? "Language" : "Sprache"; ThemeTitle.Text = _english ? "Design" : "Design"; SizeTitle.Text = _english ? "App size" : "App-Größe";
        foreach (var (button, selected) in new[] { (GermanButton, !_english), (EnglishButton, _english) })
        {
            button.SetResourceReference(Button.BackgroundProperty, selected ? "ButtonBrush" : "InputBrush");
            button.SetResourceReference(Button.BorderBrushProperty, selected ? "AccentBrush" : "InputBorderBrush");
            button.BorderThickness = selected ? new Thickness(1.5) : new Thickness(1);
        }
        WarningTitle.Text = _english ? "Timer warning" : "Timer-Warnung"; WarningSound.Content = _english ? "Warning enabled" : "Warnung aktiv"; WarningSoundTitle.Text = _english ? "Warning tone" : "Warnton"; WarningVolumeTitle.Text = _english ? "Timer volume" : "Timer-Lautstärke"; TestSoundLabel.Text = "Test";
        AutoStartTitle.Text = _english ? "Start with Windows" : "Mit Windows starten"; AutoStart.Content = _english ? "Autostart enabled" : "Autostart aktiv"; DoneLabel.Text = _english ? "Done" : "Fertig";
        ElevationTitle.Text = _english ? "Admin rights" : "Adminrechte";
        RequestAdministratorOnLaunch.Content = _english ? "Request on launch" : "Beim Start anfordern";
        RequestAdministratorOnLaunch.ToolTip = _english
            ? "Manual launches only; Windows autostart does not request UAC."
            : "Nur bei manuellem Start; Windows-Autostart bleibt ohne UAC-Abfrage.";
        SettingsNoticeHeading.Text = _english ? "Bell status" : "Glocken-Status";
        UpdateNoticeButton.ToolTip = _english ? "Show bell status" : "Glocken-Status anzeigen";
        System.Windows.Automation.AutomationProperties.SetName(UpdateNoticeButton, UpdateNoticeButton.ToolTip.ToString()!);
        GitHubProjectButton.ToolTip = _english ? "Open GitHub project" : "GitHub-Projekt öffnen";
        SetComboContents(SizeBox, _english ? new[] { "Automatic", "Compact · 860 × 620", "Standard · 900 × 650", "Extended · 1050 × 670", "Large · 1200 × 760" } : new[] { "Automatisch", "Kompakt · 860 × 620", "Standard · 900 × 650", "Erweitert · 1050 × 670", "Groß · 1200 × 760" });
        SetComboContents(WarningSoundBox, _english
            ? new[] { "Alarm", "Beep", "Chime", "Siren", "Double tone", "Triple tone", "Gong", "Signal", "Urgent", "Digital", "System alert", "System warning" }
            : new[] { "Alarm", "Piep", "Glockenspiel", "Sirene", "Doppelton", "Dreifachton", "Gong", "Signal", "Dringend", "Digital", "System-Alarm", "System-Warnung" });
        foreach (var item in ThemeBox.Items.OfType<ComboBoxItem>())
        {
            if (ThemeId(item) == "Classic") item.Content = "Windows Classic";
        }
        TimerValue.Text = TimerLabel();
        UpdateCurrentUserLabel();
        SetUpdateCheckState(_updateCheckState);
        if (IsLoaded) (System.Windows.Application.Current as App)?.UpdateTrayPresentation(SelectedTheme(), _english);
    }

    private static void SetComboContents(System.Windows.Controls.ComboBox box, IReadOnlyList<string> values)
    {
        for (var index = 0; index < Math.Min(box.Items.Count, values.Count); index++)
            if (box.Items[index] is ComboBoxItem item) item.Content = values[index];
    }

    private void LoadAutoStart(bool repair = true)
    {
        var status = AutoStartService.Current.Check(AutoStartService.CurrentExecutable, repair);
        AutoStart.IsChecked = status.Enabled == true;
        AutoStart.ToolTip = status.Error is not null
            ? (_english ? "Autostart could not be verified: " : "Autostart konnte nicht bestätigt werden: ") + status.Error
            : status.Enabled == true ? status.Command : _english ? "Autostart disabled" : "Autostart deaktiviert";
        if (status.Updated) Log(_english ? "Autostart updated to the current program file." : "Autostart auf die aktuelle Programmdatei angepasst.");
        if (status.Error is not null) Log((_english ? "Autostart check failed: " : "Autostart-Prüfung fehlgeschlagen: ") + status.Error);
    }

    private void ToggleAutoStart(object s, RoutedEventArgs e)
    {
        var enabled = AutoStart.IsChecked == true;
        var status = AutoStartService.Current.SetEnabled(enabled, AutoStartService.CurrentExecutable);
        LoadAutoStart(repair: false);
        if (status.Error is not null)
        {
            MessageBox.Show((_english ? "Autostart could not be changed: " : "Autostart konnte nicht geändert werden: ") + status.Error,
                "Autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Log(enabled ? _english ? "Windows autostart enabled." : "Windows-Autostart aktiviert."
            : _english ? "Windows autostart disabled." : "Windows-Autostart deaktiviert.");
    }

    private void ThemeChanged(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        var name = ThemeId(item);
        ApplyTheme(name);
        ApplyLayoutScale();
        SaveSettings();
        Log(_english ? $"Design changed: {name}" : $"Design geändert: {name}");
    }

    private void ApplyTheme(string name)
    {
        name = ThemePalette.NormalizeName(name);
        var palette = ThemePalette.For(name);
        var colors = palette.Colors;
        var keys = new[] { "AppBrush", "HeaderBrush", "FooterBrush", "CardBrush", "InputBrush", "TextBrush", "SubtleTextBrush", "AccentBrush", "InputBorderBrush", "ButtonBrush", "HoverBrush" };
        for (var index = 0; index < keys.Length; index++) Resources[keys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[index]));
        // Keep the dimensional card finish tied to the selected palette rather than
        // hard-coding the reference blue into every design.
        var card = (Color)ColorConverter.ConvertFromString(colors[3]);
        var button = (Color)ColorConverter.ConvertFromString(colors[9]);
        var input = (Color)ColorConverter.ConvertFromString(colors[4]);
        Resources["CardSurfaceBrush"] = new LinearGradientBrush(
            BlendColor(card, button, 0.36), BlendColor(card, input, 0.56), 45);
        Resources["HeaderTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.HeaderText));
        Resources["HeaderSubtleBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.HeaderSubtle));
        Resources["FooterTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.FooterText));
        Resources["HeaderAccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            palette.HeaderTextOverride is null ? colors[7] : palette.HeaderText));
        var actionColors = palette.Actions;
        var actionKeys = new[] { "DangerBrush", "DangerBorderBrush", "SuccessBrush", "LockBrush", "CancelBrush", "WarningBrush" };
        for (var index = 0; index < actionKeys.Length; index++) Resources[actionKeys[index]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(actionColors[index]));
        Resources["ReadyBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Ready));
        // Header colours need contrast against Classic's royal blue, independently
        // of the darker status colours used on its light content panels.
        foreach (var (key, source, classic) in new[]
        {
            ("HeaderBellCurrentBrush", "ReadyBrush", "#80F0AE"),
            ("HeaderBellWarningBrush", "WarningBrush", "#FFD56A"),
            ("HeaderBellAvailableBrush", "DangerBorderBrush", "#FF91A3"),
            ("HeaderBellCheckingBrush", "AccentBrush", "#EAF3FF")
        })
            Resources[key] = name == "Classic"
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(classic)) : Resources[source];
        UpdateAdminBadgeBrush(IsAdmin(), name);
        Resources["NoticeCurrentSurfaceBrush"] = new SolidColorBrush(BlendColor(card, (Color)ColorConverter.ConvertFromString(palette.Ready), 0.46));
        Resources["NoticeConnectionSurfaceBrush"] = new SolidColorBrush(BlendColor(card, (Color)ColorConverter.ConvertFromString(actionColors[5]), 0.46));
        Resources["NoticeAvailableSurfaceBrush"] = new SolidColorBrush(BlendColor(card, (Color)ColorConverter.ConvertFromString(actionColors[1]), 0.46));
        Resources["UpdateCheckCurrentBrush"] = new SolidColorBrush(BlendColor(button, (Color)ColorConverter.ConvertFromString(palette.Ready), 0.34));
        Resources["UpdateCheckFailedBrush"] = new SolidColorBrush(BlendColor(button, (Color)ColorConverter.ConvertFromString(actionColors[5]), 0.34));
        Resources["UpdateCheckAvailableBrush"] = new SolidColorBrush(BlendColor(button, (Color)ColorConverter.ConvertFromString(actionColors[1]), 0.34));
        Resources["CardBorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(name == "Standard" || palette.IsNeon ? colors[7] : colors[8]));
        Resources["ActionTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FBFF"));
        Resources["DefaultActionTextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            name is "Classic" or "Linux Mint" ? colors[5] : "#F8FBFF"));
        Resources["SelectionBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[10]));
        var standardUserTable = name == "Standard";
        Resources["UserStatusDisabledBrush"] = standardUserTable
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4D78"))
            : Resources["DangerBorderBrush"];
        Resources["UserStatusActiveBrush"] = standardUserTable
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00CCAE"))
            : Resources["ReadyBrush"];
        Resources["UserTableLineBrush"] = standardUserTable
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#145982"))
            : Resources["InputBorderBrush"];
        Resources["UserTableHeaderBrush"] = standardUserTable
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A3152"))
            : Resources["CardSurfaceBrush"];
        Resources["UserTableSelectedBrush"] = standardUserTable
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#164F7A"))
            : Resources["SelectionBrush"];
        Resources["UserTableSelectedBorderBrush"] = standardUserTable
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2788CC"))
            : Resources["AccentBrush"];
        var glowHex = palette.Glow ?? (name == "Standard" ? colors[7] : null);
        var glowColor = glowHex is null ? Colors.Transparent : (Color)ColorConverter.ConvertFromString(glowHex);
        var glowOpacity = name == "Standard" ? 0.23 : palette.GlowOpacity;
        Resources["NeonGlow"] = new System.Windows.Media.Effects.DropShadowEffect { Color = glowColor, ShadowDepth = 0, BlurRadius = 15, Opacity = glowOpacity };
        Resources["NeonIconGlow"] = new System.Windows.Media.Effects.DropShadowEffect { Color = glowColor, ShadowDepth = 0, BlurRadius = 6, Opacity = glowOpacity * 0.7 };
        System.Windows.Documents.TextElement.SetFontFamily(Root, new System.Windows.Media.FontFamily(palette.FontFamily));
        ClearLogButton.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ActionTextBrush");
        var cardRadius = new CornerRadius(name == "Classic" ? 8 : 13);
        foreach (var border in FindVisualChildren<Border>(Root))
            if (ReferenceEquals(border.Style, Resources["Card"]) || ReferenceEquals(border.Style, Resources["CardGlow"])) border.CornerRadius = cardRadius;
        foreach (var corner in FindVisualChildren<System.Windows.Shapes.Path>(Root))
            if (ReferenceEquals(corner.Style, Resources["CardCorner"]))
                corner.Visibility = Visibility.Visible;
        SettingsPanel.CornerRadius = new CornerRadius(name == "Classic" ? 8 : 12);
        SettingsHalo.CornerRadius = SettingsPanel.CornerRadius;
        SwitchIconSet(palette.IconSet);
        ApplyReferencePalette(name, palette);
        ApplyHomeLayout(name);
        ApplyPageNavigationTheme(name);
        ApplyClassicCancelTheme(name);
        ConfigureNeonPresentation(palette);
        if (_diagnosticsKind is not null) RefreshDiagnosticsHover();
        (System.Windows.Application.Current as App)?.UpdateTrayPresentation(name, _english);
    }

    private static Color BlendColor(Color first, Color second, double amount) => Color.FromRgb(
        (byte)Math.Round(first.R + (second.R - first.R) * amount),
        (byte)Math.Round(first.G + (second.G - first.G) * amount),
        (byte)Math.Round(first.B + (second.B - first.B) * amount));

    private void SwitchIconSet(string set)
    {
        foreach (var image in FindVisualChildren<Image>(Root))
        {
            if (image.Name == nameof(AbortTimerIcon)) continue;
            var source = image.Source?.ToString();
            if (string.IsNullOrWhiteSpace(source) || !source.Contains("/icons/", StringComparison.OrdinalIgnoreCase)) continue;
            var file = Path.GetFileName(source);
            var resource = new Uri($"pack://application:,,,/Assets/icons/{set}/{file}", UriKind.Absolute);
            image.Source = new BitmapImage(resource);
            if (set == "neon" && image.Width >= 25)
                image.SetResourceReference(UIElement.EffectProperty, "NeonIconGlow");
            else image.ClearValue(UIElement.EffectProperty);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void AppSizeChanged(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SizeBox.SelectedIndex < 0) return;
        ApplySize();
        SaveSettings();
    }

    private void TestWarningSound(object s, RoutedEventArgs e)
    {
        if (!PlayWarningSound())
            MessageBox.Show(_english ? "The selected warning sound could not be played." : "Der gewählte Warnton konnte nicht abgespielt werden.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private bool PlayWarningSound(string? overrideFile = null, double volumeFactor = 1)
    {
        var file = overrideFile ?? (WarningSoundBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system-warning.wav";
        if (!WarningSoundBox.Items.OfType<ComboBoxItem>().Any(item => item.Tag?.ToString() == file)) return false;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "sound-cache", file);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var resource = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/sounds/warnings/{file}"))
                    ?? throw new FileNotFoundException(file);
                using (resource.Stream)
                using (var output = File.Create(path)) resource.Stream.CopyTo(output);
            }
            _warningPlayer?.Stop();
            _warningPlayer?.Close();
            _warningPlaybackFactor = volumeFactor;
            _warningPlayer = new MediaPlayer { Volume = WarningVolume.Value / 100 * volumeFactor };
            _warningPlayer.Open(new Uri(path, UriKind.Absolute));
            _warningPlayer.Play();
            return true;
        }
        catch (Exception exception)
        {
            Log((_english ? "Warning sound failed: " : "Warnton fehlgeschlagen: ") + exception.Message);
            return false;
        }
    }

    private async void LoadUsers(object? s, RoutedEventArgs e)
    {
        RefreshUsersButton.IsEnabled = false;
        try
        {
            var users = await Task.Run(() => ReadLocalUsers());
            UserList.Items.Clear();
            foreach (var user in users) UserList.Items.Add(user);
            UpdateCurrentUserLabel(users);
            Log(_english ? "User overview refreshed." : "Benutzerübersicht aktualisiert.");
        }
        catch (Exception exception)
        {
            Log((_english ? "User overview failed: " : "Benutzerübersicht fehlgeschlagen: ") + exception.Message);
            MessageBox.Show((_english ? "Local users could not be loaded: " : "Lokale Benutzer konnten nicht geladen werden: ") + exception.Message, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshUsersButton.IsEnabled = true; }
    }

    private void UpdateCurrentUserLabel(IReadOnlyList<UserEntry>? users = null)
    {
        CurrentUserText.Text = (_english ? "Current user: " : "Aktueller Benutzer: ") + Environment.UserName;
        if (users is not null)
            _currentUserIsAdministrator = users.FirstOrDefault(user =>
                user.Name.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))?.Role == "Administrator";
        var elevated = IsAdmin();
        CurrentUserRole.Text = _currentUserIsAdministrator || elevated ? "Administrator" : (_english ? "Standard user" : "Standardbenutzer");
        CurrentUserRole.SetResourceReference(TextBlock.ForegroundProperty, _currentUserIsAdministrator || elevated ? "ReadyBrush" : "AccentBrush");
        UpdateAdminBadgeAppearance(elevated, SelectedTheme());
    }

    private void UpdateAdminBadgeAppearance(bool elevated, string theme)
    {
        AdminRightsStatus.Text = elevated
            ? _english ? "Admin start" : "Mit Admin-Start"
            : _english ? "No admin start" : "Ohne Admin-Start";
        AdminRightsBadge.ToolTip = elevated
            ? _english ? "The app is running with administrator rights." : "Die App wurde mit Administratorrechten gestartet."
            : _english ? "The app is not running with administrator rights." : "Die App wurde nicht als Administrator gestartet.";
        UpdateAdminBadgeBrush(elevated, theme);
    }

    private void UpdateAdminBadgeBrush(bool elevated, string theme)
    {
        if (theme == "Classic")
        {
            Resources["AdminBadgeBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(elevated ? "#9BF3B5" : "#D5E8FF"));
            AdminRightsBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(elevated ? "#164A67" : "#17498E"));
            return;
        }
        AdminRightsBadge.SetResourceReference(Button.BackgroundProperty, "InputBrush");
        Resources["AdminBadgeBrush"] = theme == "Standard"
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(elevated ? "#36D99B" : "#FFB315"))
            : Resources[elevated ? "ReadyBrush" : "WarningBrush"];
    }

    private void UserSelectionChanged(object s, SelectionChangedEventArgs e) => EditUserButton.IsEnabled = UserList.SelectedItem is UserEntry;

    private void UserListSizeChanged(object s, SizeChangedEventArgs e) => SizeUserColumns();

    private void SizeUserColumns()
    {
        if (UserList.View is not GridView columns || columns.Columns.Count != 4 || UserList.ActualWidth < 400) return;
        var width = UserList.ActualWidth - 20;
        columns.Columns[0].Width = Math.Round(width * 0.16);
        columns.Columns[1].Width = Math.Round(width * 0.215);
        columns.Columns[2].Width = Math.Round(width * 0.465);
        columns.Columns[3].Width = width - columns.Columns[0].Width - columns.Columns[1].Width - columns.Columns[2].Width;
    }

    private async void CreateUser(object s, RoutedEventArgs e)
    {
        if (_repairRunning) return;
        if (!RequireUserAdmin()) return;
        var dialog = new UserEditorDialog(this, null);
        if (dialog.ShowDialog() != true || dialog.Values is not { } values) return;
        var createQuestion = _english ? $"Create local Windows account {values.Name}?" : $"Lokales Windows-Konto {values.Name} erstellen?";
        if (values.WithoutPassword)
            createQuestion += _english ? " This account will have no password. Windows security policy may reject it." : " Dieses Konto hat kein Kennwort. Die Windows-Sicherheitsrichtlinie kann das ablehnen.";
        if (MessageBox.Show(createQuestion, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_repairRunning) return;
        try
        {
            await Task.Run(() => LocalUserManager.Create(values.Name, values.Password, values.FullName, values.Description, values.WithoutPassword, values.Enabled, values.Administrator));
            Log((_english ? "Local account created: " : "Lokales Konto erstellt: ") + values.Name);
            LoadUsers(null, null!);
        }
        catch (Win32Exception exception) when (values.WithoutPassword && exception.NativeErrorCode == 2245)
        {
            var detail = _english
                ? "Windows rejected the empty password under its password policy. Create the account with a password or change the policy separately."
                : "Windows hat das leere Kennwort wegen der Kennwortrichtlinie abgelehnt. Erstelle das Konto mit Kennwort oder ändere die Richtlinie separat.";
            Log((_english ? "Account creation failed for " : "Kontoerstellung fehlgeschlagen für ") + values.Name + ": " + detail);
            MessageBox.Show(detail, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception) { UserEditError(values.Name, exception); }
    }

    private async void EditUser(object s, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is not UserEntry user || !RequireUserAdmin()) return;
        if (IsProtectedUser(user))
        {
            MessageBox.Show(_english ? "This built-in Windows account is protected." : "Dieses integrierte Windows-Konto ist geschützt.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new UserEditorDialog(this, user);
        if (dialog.ShowDialog() != true) return;
        if (dialog.ResetPasswordRequested) { ResetPasswordFor(user); return; }
        if (dialog.DeleteRequested) { await DeleteUser(user); return; }
        if (dialog.Values is not { } values) return;
        if (!user.Name.Equals(values.Name, StringComparison.Ordinal) &&
            user.Sid?.Equals(WindowsIdentity.GetCurrent().User?.Value, StringComparison.OrdinalIgnoreCase) == true)
        {
            MessageBox.Show(_english ? "Sign in with another administrator account to rename the account currently in use."
                                     : "Zum Umbenennen des aktuell verwendeten Kontos bitte mit einem anderen Administratorkonto anmelden.",
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (user.Name.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) && (!values.Enabled || !values.Administrator && user.Role == "Administrator"))
        {
            MessageBox.Show(_english ? "The current account cannot be disabled or demoted here." : "Das aktuelle Konto kann hier nicht deaktiviert oder herabgestuft werden.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (user.Role == "Administrator" && (!values.Enabled || !values.Administrator) && UserList.Items.OfType<UserEntry>().Count(entry => entry.Enabled && entry.Role == "Administrator") <= 1)
        {
            MessageBox.Show(_english ? "The last active administrator account must be retained." : "Das letzte aktive Administratorkonto muss erhalten bleiben.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var editQuestion = _english ? $"Apply changes to {user.Name}?" : $"Änderungen an {user.Name} übernehmen?";
        if (!user.Name.Equals(values.Name, StringComparison.Ordinal))
            editQuestion += _english ? $" Rename to {values.Name}? The profile folder will not be renamed." : $" In {values.Name} umbenennen? Der Profilordner wird nicht umbenannt.";
        if (MessageBox.Show(editQuestion, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => LocalUserManager.Edit(user.Name, values.Name, user.Sid!, values.FullName, values.Description, values.Enabled, values.Administrator, user.Role == "Administrator"));
            Log((_english ? "Local account updated: " : "Lokales Konto bearbeitet: ") + user.Name + (user.Name == values.Name ? string.Empty : " → " + values.Name));
            LoadUsers(null, null!);
        }
        catch (Exception exception) { UserEditError(user.Name, exception); }
    }

    private async Task DeleteUser(UserEntry selected)
    {
        if (!RequireUserAdmin() || IsProtectedUser(selected)) return;
        UserEntry? current;
        try
        {
            var users = await Task.Run(() => ReadLocalUsers(requireAdminMembership: true));
            current = users.FirstOrDefault(user => user.Sid?.Equals(selected.Sid, StringComparison.OrdinalIgnoreCase) == true);
            if (current is null || IsProtectedUser(current))
                throw new InvalidOperationException(_english ? "The selected account no longer exists or is protected." : "Das ausgewählte Konto existiert nicht mehr oder ist geschützt.");
            if (current.Sid?.Equals(WindowsIdentity.GetCurrent().User?.Value, StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException(_english ? "The account currently in use cannot be deleted." : "Das aktuell verwendete Konto kann nicht gelöscht werden.");
            if (current.Enabled && current.Role == "Administrator" && users.Count(user => user.Enabled && user.Role == "Administrator") <= 1)
                throw new InvalidOperationException(_english ? "The last active administrator account must be retained." : "Das letzte aktive Administratorkonto muss erhalten bleiben.");
        }
        catch (Exception exception) { UserEditError(selected.Name, exception); return; }

        var question = _english
            ? $"Permanently delete the local Windows account {current.Name}? The profile folder and user files will not be deleted."
            : $"Lokales Windows-Konto {current.Name} endgültig löschen? Profilordner und Benutzerdateien werden nicht gelöscht.";
        if (MessageBox.Show(question, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => LocalUserManager.Delete(current.Name, current.Sid!));
            Log((_english ? "Local account deleted: " : "Lokales Konto gelöscht: ") + current.Name);
            LoadUsers(null, null!);
        }
        catch (Exception exception) { UserEditError(current.Name, exception); }
    }

    private void OpenAdminStatus(object s, RoutedEventArgs e)
    {
        if (_repairRunning) return;
        if (IsAdmin())
        {
            MessageBox.Show(_english ? "The app is running with administrator rights." : "Die App läuft mit Administratorrechten.",
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        RequestElevation(_pageIndex == 1);
    }

    private bool RequireUserAdmin()
    {
        if (IsAdmin()) return true;
        RequestElevation(openUsersPage: true);
        return false;
    }

    private void RequestElevation(bool openUsersPage)
    {
        if (_repairRunning) return;
        if (!ThemedMessageBox.ShowElevationPrompt(this)) return;
        if (IsBusy)
        {
            MessageBox.Show(_english ? "Finish the active timer or repair before restarting as administrator."
                                     : "Bitte zuerst den aktiven Timer oder die Reparatur beenden, bevor die App als Administrator neu startet.",
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            if (ElevationService.TryRestartAsAdministrator(openUsersPage))
                System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            MessageBox.Show((_english ? "The app could not be restarted as administrator: "
                                      : "Die App konnte nicht als Administrator neu gestartet werden: ") + exception.Message,
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsProtectedUser(UserEntry user) =>
        string.IsNullOrWhiteSpace(user.Sid) || user.Sid.EndsWith("-500", StringComparison.Ordinal) ||
        user.Sid.EndsWith("-501", StringComparison.Ordinal) || user.Sid.EndsWith("-503", StringComparison.Ordinal) ||
        user.Sid.EndsWith("-504", StringComparison.Ordinal);

    private void UserEditError(string name, Exception exception)
    {
        Log((_english ? "Account management failed for " : "Kontoverwaltung fehlgeschlagen für ") + name + ": " + exception.Message);
        MessageBox.Show((_english ? "Windows could not complete the account change. The account may have been partially changed: " : "Windows konnte die Kontoänderung nicht vollständig ausführen. Das Konto kann teilweise geändert worden sein: ") + exception.Message, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Error);
        LoadUsers(null, null!);
    }

    private void ResetPasswordFor(UserEntry user)
    {
        if (!RequireUserAdmin()) return;
        if (IsProtectedUser(user))
        {
            MessageBox.Show(_english ? "This built-in Windows account is protected." : "Dieses integrierte Windows-Konto ist geschützt.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var choice = PasswordResetDialog.Prompt(this, user.Name, _english);
        if (choice is null) return;
        var confirm = choice.RemovePassword
            ? (_english ? $"Remove the password for {user.Name}? This makes local sign-in without a password possible. Any Windows Hello PIN remains separate."
                        : $"Kennwort für {user.Name} entfernen? Damit ist die lokale Anmeldung ohne Kennwort möglich. Eine Windows-Hello-PIN bleibt davon unberührt.")
            : (_english ? $"Reset the Windows password for {user.Name}? This will immediately replace the current password."
                        : $"Windows-Kennwort für {user.Name} zurücksetzen? Das bisherige Kennwort wird sofort ersetzt.");
        if (MessageBox.Show(confirm, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            LocalUserManager.ChangePassword(user.Name, user.Sid!, choice.Password, choice.RemovePassword);
            Log((_english ? (choice.RemovePassword ? "Password removed for " : "Password changed for ")
                          : (choice.RemovePassword ? "Kennwort entfernt für " : "Kennwort geändert für ")) + user.Name + ".");
            MessageBox.Show(_english ? (choice.RemovePassword ? "The password was removed. The Windows Hello PIN was not changed." : "The password was changed.")
                                     : (choice.RemovePassword ? "Das Kennwort wurde entfernt. Die Windows-Hello-PIN wurde nicht geändert." : "Das Kennwort wurde geändert."),
                "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) { UserEditError(user.Name, exception); }
    }

    private IReadOnlyList<UserEntry> ReadLocalUsers(bool requireAdminMembership = false)
    {
        const string usersCommand = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Get-LocalUser | Select-Object Name,FullName,Enabled,Description,@{Name='SidValue';Expression={$_.SID.Value}} | ConvertTo-Json -Compress";
        const string adminsCommand = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Get-LocalGroupMember -SID S-1-5-32-544 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | ConvertTo-Json -Compress";
        using var users = JsonDocument.Parse(RunCaptured("powershell.exe", $"-NoProfile -NonInteractive -Command \"{usersCommand}\""));
        var adminNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var admins = JsonDocument.Parse(RunCaptured("powershell.exe", $"-NoProfile -NonInteractive -Command \"{adminsCommand}\""));
            var names = admins.RootElement.ValueKind == JsonValueKind.Array ? admins.RootElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty) : new[] { admins.RootElement.GetString() ?? string.Empty };
            foreach (var name in names) adminNames.Add(name);
        }
        catch (Exception exception)
        {
            if (requireAdminMembership)
                throw new InvalidOperationException("Die Administratorgruppen-Mitgliedschaft konnte nicht geprüft werden.", exception);
            // Keep the complete user list even if group membership cannot be read.
        }
        var entries = users.RootElement.ValueKind == JsonValueKind.Array ? users.RootElement.EnumerateArray().ToArray() : new[] { users.RootElement };
        return entries.Select(item =>
        {
            var name = item.GetProperty("Name").GetString() ?? "Unbekannt";
            var active = item.GetProperty("Enabled").GetBoolean();
            var description = item.TryGetProperty("Description", out var descriptionValue) && descriptionValue.ValueKind == JsonValueKind.String ? descriptionValue.GetString() ?? string.Empty : string.Empty;
            var fullName = item.TryGetProperty("FullName", out var fullValue) && fullValue.ValueKind == JsonValueKind.String ? fullValue.GetString() ?? string.Empty : string.Empty;
            var sid = item.TryGetProperty("SidValue", out var sidValue) && sidValue.ValueKind == JsonValueKind.String ? sidValue.GetString() : null;
            var admin = adminNames.Any(value => value.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase) || value.Equals(name, StringComparison.OrdinalIgnoreCase));
            return new UserEntry(name, admin ? "Administrator" : (_english ? "Standard user" : "Standardbenutzer"), description, active ? (_english ? "Active" : "Aktiv") : (_english ? "Disabled" : "Deaktiviert"), active, fullName, sid);
        }).OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string RunCaptured(string file, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 }) ?? throw new InvalidOperationException();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000)) { process.Kill(); throw new TimeoutException("Windows-Benutzerabfrage hat zu lange gedauert."); }
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException();
        return output;
    }

    private static bool IsAdmin() => ElevationService.IsElevated();

    public void ExecuteTrayAction(string action)
    {
        Dispatcher.Invoke(() =>
        {
            if (IsBusy)
            {
                MessageBox.Show(_english ? "Finish the active timer or repair first." : "Bitte zuerst den aktiven Timer oder die Reparatur beenden.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            switch (action)
            {
                case "shutdown": ShutdownNow(this, new RoutedEventArgs()); break;
                case "restart": RestartNow(this, new RoutedEventArgs()); break;
                case "lock": LockNow(this, new RoutedEventArgs()); break;
            }
        });
    }
    private void Window_Closing(object? s, CancelEventArgs e) { if (!IsBusy) return; e.Cancel = true; MessageBox.Show(_english ? "Finish the active timer or repair first." : "Bitte zuerst den aktiven Timer oder die Reparatur beenden.", "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning); }

}

public sealed record UserEntry(string Name, string Role, string Description, string Status, bool Enabled, string FullName = "", string? Sid = null);

internal sealed record PasswordResetChoice(string Password, bool RemovePassword);

internal sealed class PasswordResetDialog : Window
{
    private readonly PasswordBox _first = new() { Margin = new Thickness(0, 5, 0, 10) };
    private readonly PasswordBox _second = new() { Margin = new Thickness(0, 5, 0, 12) };
    private readonly CheckBox _removePassword = new();
    private readonly StackPanel _passwordFields = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
    private readonly bool _english;
    private PasswordResetChoice? Result { get; set; }

    private PasswordResetDialog(Window owner, string username, bool english)
    {
        ClassicCancelAppearance.RegisterDialog(this, (owner as MainWindow)?.CurrentThemeName == "Classic");
        _english = english;
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Icon = owner.Icon;
        Title = english ? "Reset password" : "Kennwort zurücksetzen";
        Width = MinWidth = MaxWidth = 390;
        Height = MaxHeight = 370;
        MinHeight = 284;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new System.Windows.Media.FontFamily((owner as MainWindow)?.CurrentFontFamily ?? "Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        var text = owner.TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var subtle = owner.TryFindResource("SubtleTextBrush") as Brush ?? Brushes.LightSteelBlue;
        var accent = owner.TryFindResource("AccentBrush") as Brush ?? Brushes.DeepSkyBlue;
        var input = owner.TryFindResource("InputBrush") as Brush ?? Brushes.Black;
        var inputBorder = owner.TryFindResource("InputBorderBrush") as Brush ?? accent;
        var button = owner.TryFindResource("ButtonBrush") as Brush ?? input;
        var danger = owner.TryFindResource("DangerBrush") as Brush ?? Brushes.DarkRed;
        var dangerBorder = owner.TryFindResource("DangerBorderBrush") as Brush ?? Brushes.IndianRed;
        Background = input;

        var root = new Border
        {
            Background = owner.TryFindResource("CardSurfaceBrush") as Brush ?? input,
            BorderBrush = accent, BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(11), Padding = new Thickness(11, 9, 11, 7)
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(43) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(DrawIcon("M 7,12 A 5,5 0 1 1 12,7 A 5,5 0 0 1 7,12 M 10,10 L 20,20 M 15,15 L 18,12",
            32, accent, new Thickness(8, 0, 12, 0)));
        var resetKey = (System.Windows.Shapes.Path)heading.Children[0];
        resetKey.Data = Geometry.Parse("M 8,2 A 6,6 0 1 1 8,14 A 6,6 0 1 1 8,2 M 12,12 L 22,22 M 17,17 L 20,14 M 19,19 L 22,16");
        resetKey.Stroke = new SolidColorBrush(Color.FromRgb(244, 195, 90));
        resetKey.Width = resetKey.Height = 30;
        resetKey.Margin = new Thickness(7, 0, 14, 0);
        var headingText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(new TextBlock { Text = Title, Foreground = text, FontSize = 19, FontWeight = FontWeights.Bold });
        headingText.Children.Add(new TextBlock
        {
            Text = english ? "Set a new local Windows password" : "Lokales Windows-Kennwort neu setzen",
            Foreground = subtle, FontSize = 11
        });
        heading.Children.Add(headingText);
        header.Children.Add(heading);
        var close = new Button { Content = "×", Style = owner.FindResource("OptionsCloseButton") as System.Windows.Style, Width = 37, Height = 35, FontSize = 21 };
        close.Click += (_, _) => DialogResult = false;
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        layout.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(1, 5, 1, 0) };
        body.Children.Add(new TextBlock
        {
            Text = (english ? "New password for: " : "Neues Kennwort für: ") + username,
            Foreground = text, FontSize = 13, FontWeight = FontWeights.Bold
        });
        body.Children.Add(new Border { Height = 8, Background = Brushes.Transparent });
        _passwordFields.Children.Add(PasswordRow(english ? "New password" : "Neues Kennwort", english ? "Enter password" : "Kennwort eingeben",
            _first, owner, text, subtle, accent, input, inputBorder, button));
        _passwordFields.Children.Add(PasswordRow(english ? "Repeat password" : "Kennwort wiederholen",
            english ? "Repeat password" : "Kennwort erneut eingeben",
            _second, owner, text, subtle, accent, input, inputBorder, button));
        body.Children.Add(new Border
        {
            Background = input, BorderBrush = inputBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = _passwordFields
        });
        var removeRow = new Grid { Height = 39, Margin = new Thickness(0, 7, 0, 0) };
        removeRow.ColumnDefinitions.Add(new ColumnDefinition());
        removeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        removeRow.Children.Add(new TextBlock
        {
            Text = english ? "Remove password (less secure)" : "Kennwort entfernen (unsicher)",
            Foreground = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0)
        });
        _removePassword.Style = owner.FindResource("SettingsSwitch") as System.Windows.Style;
        _removePassword.Width = 57;
        _removePassword.Height = 27;
        _removePassword.VerticalAlignment = VerticalAlignment.Center;
        _removePassword.Margin = new Thickness(0, 0, 8, 0);
        System.Windows.Automation.AutomationProperties.SetName(_removePassword,
            english ? "Remove password" : "Kennwort entfernen");
        Grid.SetColumn(_removePassword, 1);
        removeRow.Children.Add(_removePassword);
        body.Children.Add(new Border
        {
            Background = input, BorderBrush = inputBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Child = removeRow
        });
        _removePassword.Checked += (_, _) =>
        {
            _first.Clear();
            _second.Clear();
            _passwordFields.Visibility = Visibility.Collapsed;
            _error.Visibility = Visibility.Collapsed;
            Top += (Height - 284) / 2;
            Height = 284;
        };
        _removePassword.Unchecked += (_, _) =>
        {
            _passwordFields.Visibility = Visibility.Visible;
            Top -= (370 - Height) / 2;
            Height = 370;
            _first.Focus();
        };
        var pinPanel = new StackPanel { Margin = new Thickness(8, 6, 8, 0) };
        pinPanel.Children.Add(new TextBlock
        {
            Text = english ? "A Windows Hello PIN stays separate. Only the signed-in user can manage it in Windows."
                           : "Die Windows-Hello-PIN bleibt getrennt. Nur der angemeldete Benutzer verwaltet sie in Windows.",
            Foreground = subtle, FontSize = 11, TextWrapping = TextWrapping.Wrap
        });
        var pinButton = new Button
        {
            Content = english ? "Open Windows sign-in options" : "Windows-Anmeldeoptionen öffnen",
            Style = owner.FindResource("UserToolbarButton") as System.Windows.Style,
            Background = button, BorderBrush = accent, Foreground = text,
            Height = 27, FontSize = 11, Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        pinButton.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:signinoptions") { UseShellExecute = true }); }
            catch (Exception exception)
            {
                _error.Text = (english ? "Could not open Windows settings: " : "Windows-Einstellungen konnten nicht geöffnet werden: ") + exception.Message;
                _error.Visibility = Visibility.Visible;
            }
        };
        pinPanel.Children.Add(pinButton);
        body.Children.Add(pinPanel);
        _error.Foreground = dangerBorder;
        _error.FontSize = 11;
        _error.Margin = new Thickness(3, 5, 0, 0);
        _error.Visibility = Visibility.Collapsed;
        body.Children.Add(_error);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var actions = new Grid { Margin = new Thickness(13, 0, 13, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = ActionButton(owner, "M 2,2 L 18,18 M 18,2 L 2,18", english ? "Cancel" : "Abbrechen",
            danger, dangerBorder, new SolidColorBrush(Color.FromRgb(255, 75, 75)), Brushes.White);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => DialogResult = false;
        actions.Children.Add(cancel);
        var confirm = ActionButton(owner, "M 3,10 L 8,15 L 18,4", english ? "Change password" : "Kennwort ändern",
            button, accent, accent, text);
        var confirmContent = (StackPanel)confirm.Content;
        var confirmIcon = (System.Windows.Shapes.Path)confirmContent.Children[0];
        var confirmLabel = (TextBlock)confirmContent.Children[1];
        _removePassword.Checked += (_, _) =>
        {
            confirmLabel.Text = english ? "Remove password" : "Kennwort entfernen";
            confirm.Background = danger;
            confirm.BorderBrush = dangerBorder;
            confirmIcon.Stroke = Brushes.White;
            confirmLabel.Foreground = Brushes.White;
        };
        _removePassword.Unchecked += (_, _) =>
        {
            confirmLabel.Text = english ? "Change password" : "Kennwort ändern";
            confirm.Background = button;
            confirm.BorderBrush = accent;
            confirmIcon.Stroke = accent;
            confirmLabel.Foreground = text;
        };
        confirm.IsDefault = true;
        confirm.Click += Confirm;
        Grid.SetColumn(confirm, 2);
        actions.Children.Add(confirm);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        root.Child = layout;
        Content = root;
        Loaded += (_, _) => _first.Focus();
    }

    private Border PasswordRow(string label, string hint, PasswordBox hidden, Window owner,
        Brush text, Brush subtle, Brush accent, Brush input, Brush inputBorder, Brush button)
    {
        var row = new Grid { Height = 42 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        caption.Children.Add(DrawIcon("M 4,9 L 17,9 L 17,20 L 4,20 Z M 7,9 L 7,6 A 3.5,3.5 0 0 1 14,6 L 14,9", 19, accent, new Thickness(0, 0, 8, 0)));
        caption.Children.Add(new TextBlock { Text = label, Foreground = text, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(caption);
        var shell = new Border
        {
            Background = input, BorderBrush = accent, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Margin = new Thickness(3, 5, 7, 5)
        };
        var fieldGrid = new Grid();
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition());
        fieldGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(33) });
        hidden.Margin = new Thickness(0);
        hidden.Background = Brushes.Transparent;
        hidden.Foreground = text;
        hidden.BorderThickness = new Thickness(0);
        hidden.FontSize = 12;
        hidden.Padding = new Thickness(8, 3, 4, 3);
        var revealed = new System.Windows.Controls.TextBox
        {
            Visibility = Visibility.Collapsed, Background = Brushes.Transparent, Foreground = text,
            BorderThickness = new Thickness(0), FontSize = 12, Padding = new Thickness(8, 3, 4, 3)
        };
        fieldGrid.Children.Add(hidden);
        fieldGrid.Children.Add(revealed);
        var placeholder = new TextBlock
        {
            Text = hint, Foreground = subtle, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            IsHitTestVisible = false
        };
        fieldGrid.Children.Add(placeholder);
        bool visible = false;
        bool clearingRevealed = false;
        void SyncHint() => placeholder.Visibility = (visible ? revealed.Text.Length : hidden.Password.Length) == 0 ? Visibility.Visible : Visibility.Collapsed;
        hidden.PasswordChanged += (_, _) =>
        {
            if (hidden.Password.Length == 0 && revealed.Text.Length > 0)
            {
                clearingRevealed = true;
                revealed.Clear();
                clearingRevealed = false;
            }
            SyncHint();
        };
        revealed.TextChanged += (_, _) => { if (!clearingRevealed) hidden.Password = revealed.Text; SyncHint(); };
        var eye = new Button
        {
            Style = owner.FindResource("SmallButton") as System.Windows.Style,
            Background = button, BorderBrush = accent, BorderThickness = new Thickness(1),
            Padding = new Thickness(3), ToolTip = _english ? "Show password" : "Kennwort anzeigen",
            Content = DrawIcon("M 1,10 C 5,3 15,3 19,10 C 15,17 5,17 1,10 Z M 10,7 A 3,3 0 1 0 10,13 A 3,3 0 1 0 10,7",
                18, text, new Thickness(0))
        };
        eye.Click += (_, _) =>
        {
            visible = !visible;
            if (visible)
            {
                revealed.Text = hidden.Password;
                revealed.Visibility = Visibility.Visible;
                hidden.Visibility = Visibility.Collapsed;
                revealed.Focus();
                revealed.CaretIndex = revealed.Text.Length;
            }
            else
            {
                hidden.Password = revealed.Text;
                clearingRevealed = true;
                revealed.Clear();
                clearingRevealed = false;
                hidden.Visibility = Visibility.Visible;
                revealed.Visibility = Visibility.Collapsed;
                hidden.Focus();
            }
            eye.ToolTip = visible ? (_english ? "Hide password" : "Kennwort verbergen")
                                  : (_english ? "Show password" : "Kennwort anzeigen");
            SyncHint();
        };
        Grid.SetColumn(eye, 1);
        fieldGrid.Children.Add(eye);
        shell.Child = fieldGrid;
        Grid.SetColumn(shell, 1);
        row.Children.Add(shell);
        return new Border { BorderBrush = inputBorder, BorderThickness = new Thickness(0, 0, 0, 0.7), Child = row };
    }

    private static System.Windows.Shapes.Path DrawIcon(string geometry, double size, Brush stroke, Thickness margin) =>
        new()
        {
            Data = Geometry.Parse(geometry), Width = size, Height = size, Stretch = Stretch.Uniform,
            Stroke = stroke, StrokeThickness = 1.9, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, VerticalAlignment = VerticalAlignment.Center, Margin = margin
        };

    private static Button ActionButton(Window owner, string icon, string label,
        Brush background, Brush outline, Brush iconColor, Brush textColor)
    {
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        content.Children.Add(DrawIcon(icon, 19, iconColor, new Thickness(0, 0, 7, 0)));
        content.Children.Add(new TextBlock { Text = label, Foreground = textColor, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return new Button
        {
            Style = owner.FindResource("UserToolbarButton") as System.Windows.Style,
            Background = background, BorderBrush = outline, BorderThickness = new Thickness(1.8),
            Height = 42, Padding = new Thickness(5, 3, 5, 3), Content = content
        };
    }

    private void Confirm(object? sender, RoutedEventArgs e)
    {
        if (_removePassword.IsChecked == true)
        {
            Result = new PasswordResetChoice(string.Empty, true);
            DialogResult = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(_first.Password)) { _error.Text = _english ? "Enter a password." : "Bitte ein Kennwort eingeben."; _error.Visibility = Visibility.Visible; return; }
        if (_first.Password != _second.Password) { _error.Text = _english ? "The passwords do not match." : "Die Kennwörter stimmen nicht überein."; _error.Visibility = Visibility.Visible; return; }
        Result = new PasswordResetChoice(_first.Password, false); DialogResult = true;
    }

    public static PasswordResetChoice? Prompt(Window owner, string username, bool english) { var dialog = new PasswordResetDialog(owner, username, english); return dialog.ShowDialog() == true ? dialog.Result : null; }
}
