using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PowerControlProWpf;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _ownedIcon;
    private TrayQuickWindow? _trayWindow;
    private SingleInstanceService? _instance;
    private MainWindow? _mainWindow;
    private string _theme = "Standard";
    private bool _english;
    private bool _updateBalloonActive;
    private bool _activationPending;
    private readonly DispatcherTimer _trayClickTimer = new();
    private long _ignoreTrayClickUntil;
    private bool _trayClickTimerConfigured;

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, failure) => LogUnhandledError(failure.Exception, "UI");
        AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
        { if (failure.ExceptionObject is Exception error) LogUnhandledError(error, "Runtime"); };
        if (Environment.GetEnvironmentVariable("POWERLEITSTAND_STARTUP_TRACE") is { Length: > 0 } timingPath)
            StartupTimings.Begin(timingPath);
        base.OnStartup(e);
        StartupTimings.Mark("Application startup");
        if (e.Args.Length > 0 && e.Args[0] == "--windows-update-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await WindowsUpdateService.RunHelperAsync(e.Args); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
            finally { Shutdown(); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--program-install-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await ProgramInstallElevation.RunHelperAsync(e.Args); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
            finally { Shutdown(); }
            return;
        }
        var startupSettings = AppSettings.Load();
        _english = startupSettings.Language == "en";
        if (e.Args.Length > 0 && e.Args[0] == "--repair-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await RepairEngine.RunHelperAsync(e.Args); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError(exception.ToString()); }
            finally { Shutdown(); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--update-helper")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await UpdateService.RunHelperAsync(e.Args); }
            finally { Shutdown(); }
            return;
        }
        try
        {
            await SingleInstanceService.WaitForHandoffAsync(e.Args);
            _instance = new SingleInstanceService();
            if (!_instance.TryAcquire())
            {
                if (!await _instance.ActivateExistingAsync())
                    System.Windows.MessageBox.Show(_english ? "PowerLeitstand PRO is already running but is not responding right now. Please wait a moment and try again." : "PowerLeitstand PRO läuft bereits, reagiert aber gerade nicht. Bitte kurz warten und erneut versuchen.", "PowerLeitstand PRO");
                Shutdown();
                return;
            }
            _instance.Listen(() => Dispatcher.BeginInvoke(() =>
            {
                if (_mainWindow is null) _activationPending = true;
                else RestoreWindow();
            }));
        }
        catch (Exception exception)
        {
            LogUnhandledError(exception, "Startup");
            System.Windows.MessageBox.Show((_english ? "Application startup could not be coordinated:\n" : "Programmstart konnte nicht koordiniert werden:\n") + UiText.Error(exception, _english), "PowerLeitstand PRO");
            Shutdown(); return;
        }
        var trayStart = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        StartupTimings.Mark("Single instance acquired");
        string? elevationError = null;
        if (!trayStart && !e.Args.Contains("--elevation-attempted", StringComparer.OrdinalIgnoreCase) &&
            startupSettings.RequestAdministratorOnLaunch && !ElevationService.IsElevated())
        {
            try
            {
                if (ElevationService.TryRestartAsAdministrator(e.Args.Contains("--page-users", StringComparer.OrdinalIgnoreCase)))
                { Shutdown(); return; }
            }
            catch (Exception exception) { elevationError = exception.Message; }
        }
        SetCurrentProcessExplicitAppUserModelID("com.powerleitstandpro.desktop.wpf");
        _mainWindow = new MainWindow(null, startupSettings);
        _mainWindow.ContentRendered += (_, _) =>
        {
            StartupTimings.Mark("First content rendered"); StartupTimings.Save();
            Dispatcher.BeginInvoke(() => { StartupTimings.Mark("Dispatcher idle"); StartupTimings.Save(); }, DispatcherPriority.ApplicationIdle);
        };
        MainWindow = _mainWindow;
        _mainWindow.StateChanged += (_, _) =>
        {
            if (_mainWindow?.WindowState == WindowState.Minimized)
                Dispatcher.BeginInvoke(() =>
                {
                    if (_mainWindow?.WindowState != WindowState.Minimized) return;
                    _mainWindow.ShowInTaskbar = false;
                    _mainWindow.Hide();
                });
        };
        CreateTrayIcon();
        StartupTimings.Mark("Tray icon created");
        bool updateCheckStarted = false;
        _mainWindow.ContentRendered += async (_, _) =>
        {
            if (updateCheckStarted) return;
            updateCheckStarted = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_mainWindow.IsLoaded) await _mainWindow.CheckForUpdatesOnStartupAsync();
        };
        bool pageFourPreparationStarted = false;
        _mainWindow.ContentRendered += async (_, _) =>
        {
            if (pageFourPreparationStarted) return;
            pageFourPreparationStarted = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(1500);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (_mainWindow.IsLoaded && _mainWindow.IsVisible)
                await _mainWindow.PrepareUpdatesPageInBackgroundAsync();
        };
        if (e.Args.Contains("--page-users", StringComparer.OrdinalIgnoreCase)) _mainWindow.ShowUsersPage();
        if (e.Args.Contains("--update-ready", StringComparer.Ordinal))
        {
            _mainWindow.ContentRendered += async (_, _) =>
            {
                // Require a rendered window and another live dispatcher turn after a short settling interval.
                await Task.Delay(2000);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!_mainWindow.IsLoaded || !_mainWindow.IsVisible) return;
                try { await UpdateStartupConfirmation.SendAsync(e.Args); }
                catch (Exception exception) { System.Diagnostics.Trace.TraceWarning("Update-Startbestätigung: " + exception.Message); }
            };
        }
        if (trayStart && !_activationPending) _mainWindow.ShowInTaskbar = false;
        else _mainWindow.Show();
        if (elevationError is not null)
            ThemedMessageBox.Show(_mainWindow, elevationError, "PowerLeitstand PRO", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CreateTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/app-icon.ico"));
        if (resource is not null)
        {
            using (resource.Stream)
            using (var icon = new System.Drawing.Icon(resource.Stream)) _ownedIcon = (System.Drawing.Icon)icon.Clone();
        }
        _trayIcon = new Forms.NotifyIcon { Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application, Text = "PowerLeitstand PRO", Visible = true };
        ConfigureTrayClickTimer();
        _trayIcon.MouseClick += (_, e) => HandleTrayClick(e.Button);
        _trayIcon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) HandleTrayDoubleClick(); };
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            RestoreWindow();
            if (_updateBalloonActive) _mainWindow?.OpenUpdateNotice(this, new RoutedEventArgs());
            else _mainWindow?.ShowRepairPage();
            _updateBalloonActive = false;
        };
    }

    private void ShowTrayMenu()
    {
        if (_mainWindow is null) return;
        if (_trayWindow is not null) { _trayWindow.Dismiss(); return; }
        var popup = new TrayQuickWindow(_theme, _english, () => _mainWindow.GetTraySnapshot(), DispatchTrayAction);
        _trayWindow = popup;
        popup.Closed += (_, _) => { if (_trayWindow == popup) _trayWindow = null; };
        popup.ShowAtCursor();
    }

    internal void HandleTrayClick(Forms.MouseButtons button)
    {
        if (button == Forms.MouseButtons.Right) { _trayClickTimer.Stop(); ShowTrayMenu(); return; }
        if (button != Forms.MouseButtons.Left || Environment.TickCount64 < _ignoreTrayClickUntil) return;
        ConfigureTrayClickTimer();
        _trayClickTimer.Stop(); _trayClickTimer.Start();
    }

    private void ConfigureTrayClickTimer()
    {
        if (_trayClickTimerConfigured) return;
        _trayClickTimerConfigured = true;
        _trayClickTimer.Interval = TimeSpan.FromMilliseconds(Forms.SystemInformation.DoubleClickTime + 30);
        _trayClickTimer.Tick += (_, _) => { _trayClickTimer.Stop(); ShowTrayMenu(); };
    }

    internal void HandleTrayDoubleClick()
    {
        _trayClickTimer.Stop();
        _ignoreTrayClickUntil = Environment.TickCount64 + Forms.SystemInformation.DoubleClickTime;
        _trayWindow?.Dismiss(); RestoreWindow();
    }

    private void DispatchTrayAction(string action)
    {
        if (_mainWindow is null) return;
        if (action == "open") { RestoreWindow(); return; }
        if (action == "minimize")
        {
            _mainWindow.WindowState = WindowState.Minimized;
            _mainWindow.ShowInTaskbar = false; _mainWindow.Hide();
            return;
        }
        if (action == "center") { RestoreWindow(); _mainWindow.CenterWindowOnCursorScreen(); return; }
        if (action == "cancel") { _mainWindow.AbortTrayTimer(); return; }
        if (_mainWindow.IsBusy) return;
        if (action == "exit") { _mainWindow.Close(); return; }
        _mainWindow.ExecuteTrayAction(action);
    }

    internal void UpdateTrayTimerState(bool active) => _trayWindow?.RefreshState();
    internal void UpdateTrayPresentation(string theme, bool english)
    {
        _theme = theme; _english = english;
        _trayWindow?.Dismiss();
    }

    internal void ShowRepairNotification(bool success, string summary)
    {
        _updateBalloonActive = false;
        _trayIcon?.ShowBalloonTip(8000, "PowerLeitstand PRO", summary, success ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Error);
    }
    internal void ShowUpdateNotification(string version)
    {
        _updateBalloonActive = true;
        _trayIcon?.ShowBalloonTip(8000, "PowerLeitstand PRO", _english ? $"Version {version} is available. Click to view the update." : $"Version {version} ist verfügbar. Zum Update hier klicken.", Forms.ToolTipIcon.Info);
    }

    private void RestoreWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.ShowInTaskbar = true;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayClickTimer.Stop();
        _trayWindow?.Dismiss();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _ownedIcon?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
}
