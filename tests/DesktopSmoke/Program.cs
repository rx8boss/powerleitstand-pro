using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static void FocusMessage(Window window, bool active) => SendMessage(new System.Windows.Interop.WindowInteropHelper(window).Handle,
        0x0006, active ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--portable-update-failed-start")) return 7;
        if (args.Contains("--images-only")) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (Array.IndexOf(args,"--application-close-child") is var closeChild && closeChild>=0)
            return ProgramApplicationCloseTests.Child(args[closeChild+1],args[closeChild+2]=="refuse",args[closeChild+2]=="hidden");
        if (args.Contains("--user-query-child-fixture")) { Console.WriteLine(new string('A', 80000)); Console.Error.WriteLine(new string('E', 80000)); return 0; }
        if (Array.IndexOf(args, "--user-query-hang-fixture") is var hold && hold >= 0) { File.WriteAllText(args[hold + 1], Environment.ProcessId.ToString()); Thread.Sleep(30000); return 0; }
        if (args.Contains("--hidden-child-fixture")) { Console.WriteLine(new string('A', 80000)); Console.Error.WriteLine("fixture stderr"); Thread.Sleep(300); return 7; }
        if (args.Contains("--step-child-fixture")) { Console.WriteLine("Downloading https://example.invalid/fixture.exe"); Console.WriteLine("Successfully verified installer hash"); Console.WriteLine("Starting package install..."); Console.Out.Flush(); Thread.Sleep(700); Console.WriteLine("Successfully installed"); return 0; }
        if (args.Contains("--download-child-fixture"))
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            using var stderr = new StreamWriter(Console.OpenStandardError(), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            stderr.WriteLine("\uFEFFDownload läuft https://example.invalid/fixture.exe"); Thread.Sleep(150);
            Console.Write("  25 MB / 100 MB\r"); Console.Out.Flush(); Thread.Sleep(150);
            stderr.WriteLine("Der Installer-Hash wurde erfolgreich überprüft"); Thread.Sleep(150);
            Console.WriteLine("Paketinstallation wird gestartet..."); Console.Out.Flush(); Thread.Sleep(500);
            Console.WriteLine("Erfolgreich installiert"); return 0;
        }
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "PowerLeitstand-Tray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        // A compiled test runner has its own entry assembly; use the app's pack resources.
        typeof(System.Windows.Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(MainWindow).Assembly);
        var app = new IsolatedTestApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int cases = 0;
        var dispatcherErrors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); }
        void Pump(Window window) { window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        void Save(TrayQuickWindow window, string name)
        {
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        }
        try
        {
            CrossButtonAppearance.Initialize();
            if (args.Contains("--portable-update-window")) return PortableAppUpdateTests.RunWindow(app, output, args[2], args[3], args[4]);
            if (args.Contains("--portable-app-update")) return Task.Run(() => PortableAppUpdateTests.RunAsync(output, args[2], args[3])).GetAwaiter().GetResult();
            if (args.Contains("--neon-presentation")) return NeonPresentationTests.Run(app, output);
            if (args.Contains("--developer-packages")) return DeveloperPackageTests.Run(app, output);
            if (args.Contains("--program-rows")) return ProgramRowsTests.Run(app, output);
            if (args.Contains("--program-presentation")) return ProgramPresentationTests.Run(app, output);
            if (args.Contains("--vlc-reinstall")) return Task.Run(() => ProgramVlcReinstallTests.Run(output)).GetAwaiter().GetResult();
            if (args.Contains("--vlc-reinstall-local")) return Task.Run(() => ProgramVlcReinstallTests.Local(output)).GetAwaiter().GetResult();
            if (args.Contains("--vlc-normal-local-ui")) return ProgramVlcNormalTests.Run(app, output);
            if (args.Contains("--vlc-source-live")) return Task.Run(() => ProgramVlcNormalTests.Live(output)).GetAwaiter().GetResult();
            if (args.Contains("--catalog-additions")) return Task.Run(() => ProgramCatalogAdditionsTests.Run(output)).GetAwaiter().GetResult();
            if (args.Contains("--tooltips")) return AppToolTipTests.Run(app, output);
            if (args.Contains("--descriptions")) return ProgramDescriptionTests.Run(app, output, args.Contains("--descriptions-live"), args.Contains("--logic-only"));
            if (args.Contains("--app-logs")) return AppLogTests.Run(app, output, false);
            if (args.Contains("--app-logs-ui")) return AppLogTests.Run(app, output, true);
            if (args.Contains("--scoop-git")) return Task.Run(() => ProgramScoopGitTests.Run(output, false)).GetAwaiter().GetResult();
            if (args.Contains("--scoop-git-live")) return Task.Run(() => ProgramScoopGitTests.Run(output, true)).GetAwaiter().GetResult();
            if (args.Contains("--check-status")) return ProgramCheckStatusTests.Run(app, output, !args.Contains("--skip-images"));
            if (args.Contains("--managed-packages")) return Task.Run(() => ProgramManagedPackageTests.Run(output, false)).GetAwaiter().GetResult();
            if (args.Contains("--managed-ui")) return ProgramManagedUiTests.Run(app, output);
            if (args.Contains("--managed-packages-live")) return Task.Run(() => ProgramManagedPackageTests.Run(output, true)).GetAwaiter().GetResult();
            if (args.Contains("--detection-completion")) return Task.Run(() => ProgramDetectionCompletionTests.Run(output, false)).GetAwaiter().GetResult();
            if (args.Contains("--detection-completion-live")) return Task.Run(() => ProgramDetectionCompletionTests.Run(output, true)).GetAwaiter().GetResult();
            if (args.Contains("--installed-loading")) return ProgramInstalledLoadingTests.Run(app, output, !args.Contains("--skip-images"));
            if (args.Contains("--reinstall")) return Task.Run(() => ProgramReinstallTests.Run(output)).GetAwaiter().GetResult();
            if (args.Contains("--reinstall-ui")) return ProgramReinstallUiTests.Run(app, output);
            if (args.Contains("--reinstall-large-image")) return ProgramReinstallUiTests.Run(app, output, largeImageOnly: true);
            if (args.Contains("--reinstall-flow")) return ProgramReinstallFlowTests.Run(app, output);
            if (args.Contains("--dialog-inventory")) return ProgramDialogInventoryTests.Run(app, output, args.Contains("--observe-before"), args.Contains("--images-only"));
            if (args.Contains("--store-inventory")) return Task.Run(() => ProgramStoreInventoryTests.Run(output)).GetAwaiter().GetResult();
            if (args.Contains("--store-inventory-live")) return Task.Run(() => ProgramStoreInventoryTests.Live(output)).GetAwaiter().GetResult();
            if (args.Contains("--program-paths")) return ProgramPathTests.Run(output);
            if (args.Contains("--program-paths-live")) return Task.Run(() => ProgramPathTests.Live(output)).GetAwaiter().GetResult();
            if (args.Contains("--program-paths-msi")) return Task.Run(() => ProgramPathTests.Msi(output)).GetAwaiter().GetResult();
            if (args.Contains("--teamspeak-info")) return TeamSpeakInfoTests.Run(app, output, args.Contains("--logic-only"));
            if (args.Contains("--teamspeak-actions")) return Task.Run(() => TeamSpeakActionTests.Run(output)).GetAwaiter().GetResult();
            if (args.Contains("--teamspeak-artifact")) return TeamSpeakActionTests.Artifact(output, args.Last());
            if (args.Contains("--teamspeak-prepare-live")) return Task.Run(() => TeamSpeakActionTests.PrepareLive(output)).GetAwaiter().GetResult();
            if (args.Contains("--repair-tools")) return RepairToolsTests.Run(output);
            if (args.Contains("--download-repair")) return ProgressRepairTests.Run(app, output);
            if (args.Contains("--teamspeak-confirm-existing")) return Task.Run(() => ProgressRepairTests.ConfirmExisting(output, args[^2], args[^1])).GetAwaiter().GetResult();
            if (args.Contains("--teamspeak-live")) return TeamSpeakInfoTests.Live(output);
            if (args.Contains("--program-info-update")) return ProgramInfoUpdateTests.Run(app, output, args.Contains("--logic-only"));
            if (args.Contains("--scoped-updates")) return ProgramScopedUpdateTests.Logic(output);
            if (args.Contains("--scoped-updates-ui")) return ProgramScopedUpdateTests.Ui(app, output);
            if (args.Contains("--wowup-update-live")) return ProgramScopedUpdateTests.WowUpLive(app, output);
            if (args.Contains("--publisher-portable")) return Task.Run(()=>ProgramPublisherPortableTests.Logic(output)).GetAwaiter().GetResult();
            if (args.Contains("--application-close")) return Task.Run(()=>ProgramApplicationCloseTests.Run(output)).GetAwaiter().GetResult();
            if (args.Contains("--single-check-close-ui")) return ProgramSingleCheckCloseTests.Ui(app,output);
            if (args.Contains("--publisher-portable-ui")) return ProgramPublisherPortableTests.Ui(app,output);
            if (Array.IndexOf(args,"--tukui-standalone") is var tukuiFiles && tukuiFiles>=0)
                return Task.Run(()=>TukuiStandaloneTests.Files(output,args[tukuiFiles+1],args[tukuiFiles+2])).GetAwaiter().GetResult();
            if (Array.IndexOf(args,"--tukui-standalone-ui") is var tukuiUi && tukuiUi>=0)
                return TukuiStandaloneTests.Ui(app,output,args[tukuiUi+1],args[tukuiUi+2]);
            if (Array.IndexOf(args,"--publisher-portable-files") is var publisherFiles && publisherFiles>=0)
                return Task.Run(()=>ProgramPublisherPortableTests.Files(output,args[publisherFiles+1],args[publisherFiles+2])).GetAwaiter().GetResult();
            if (args.Contains("--startup-background")) return StartupTests.BackgroundQueries(output);
            if (args.Contains("--page4-failure")) return Page4NavigationTests.Failure(app, output);
            if (args.Contains("--page4-live")) return Page4NavigationTests.Run(app, output, args.Contains("--delayed"));
            if (args.Contains("--startup-timing")) return StartupTests.Run(app, output, args.Contains("--components"), args.Contains("--software-rendering"));
            if (args.Contains("--program-info-layout")) return ProgramInfoLayoutTests.Run(app, output);
            if (args.Contains("--program-targets")) return ProgramInstallTargetTests.Run(app, output, !args.Contains("--logic-only"));
            if (args.Contains("--program-targets-live")) return ProgramInstallTargetTests.Live(output);
            if (args.Contains("--github-plan-probe")) return ProgramGitHubRemovalTests.Probe(output);
            if (args.Contains("--github-removal")) return ProgramGitHubRemovalTests.Run(app, output, args.Contains("--logic-only"));
            if (args.Contains("--inspection-cache")) return ProgramInspectionCacheTests.Run(app, output, false);
            if (args.Contains("--inspection-cache-ui")) return ProgramInspectionCacheTests.Run(app, output, true);
            if (args.Contains("--inspection-live")) return ProgramInspectionCacheTests.Live(output, null);
            if (args.Contains("--inspection-baseline")) return ProgramInspectionCacheTests.Live(output, args.Last());
            if (args.Contains("--inspection-performance")) return ProgramInspectionTests.Run(app, output, !args.Contains("--skip-images"));
            if (args.Contains("--icon-fallback")) return ProgramIconFallbackTests.Run(output, args.Contains("--live-icons"));
            if (args.Contains("--google-icons")) return ProgramGoogleIconTests.Run(output, args.Contains("--live-google"));
            if (args.Contains("--program-expansion")) return ProgramExpansionTests.Run(output);
            if (args.Contains("--publisher-live")) return ProgramExpansionTests.LiveGpu(output);
            if (args.Contains("--lightshot-ui")) return ProgramLightshotTests.Run(app, output);
            if (args.Contains("--winget-api-probe")) { try { Console.WriteLine(Task.Run(ProgramWinGetApi.Probe).GetAwaiter().GetResult()); return 0; } catch (Exception e) { Console.WriteLine("WinGet API live probe unavailable: " + e.GetType().Name + " / " + e.Message); return 2; } }
            if (args.Contains("--portable-updates")) return ProgramPortableUpdateTests.Run(app, output);
            if (args.Contains("--obs-portable")) return Task.Run(() => ObsPortableTests.RunAsync(output)).GetAwaiter().GetResult();
            if (args.Contains("--vlc-zip")) return Task.Run(() => VlcZipTests.RunAsync(output)).GetAwaiter().GetResult();
            if (args.Contains("--vlc-official")) return Task.Run(() => VlcZipTests.OfficialAsync(output)).GetAwaiter().GetResult();
            if (args.Contains("--destinations")) return Task.Run(() => ProgramDestinationTests.Logic(output)).GetAwaiter().GetResult();
            if (args.Contains("--selection-cancel")) return ProgramSelectionCancelTests.Run(app, output);
            if (args.Contains("--destination-flow")) return ProgramDestinationTests.Flow(app, output);
            if (args.Contains("--destinations-ui")) return ProgramDestinationTests.Ui(app, output, args.Contains("--theme") ? args[Array.IndexOf(args, "--theme") + 1] : "Standard");
            if (args.Contains("--obs-package")) return Task.Run(() => ObsPortableTests.VerifyOfficialAsync(output, args[Array.IndexOf(args, "--package-path") + 1])).GetAwaiter().GetResult();
            if (args.Contains("--portable-ui")) return ProgramPortableUpdateTests.Ui(app, output);
            if (args.Contains("--portable-preview")) return ProgramPortableUpdateTests.Ui(app, output, previewOnly: true);
            if (args.Contains("--program-managers")) return ProgramManagerTests.Run(app, output);
            if (args.Contains("--program-stages")) return ProgramStageTests.Run(app, output);
            if (args.Contains("--program-progress")) return ProgramProgressTests.Run(app, output);
            if (args.Contains("--program-click")) return ProgramClickTests.Run(app, output);
            if (args.Contains("--program-edges")) return ProgramUpdateFlowTests.Edges(app, output);
            if (args.Contains("--program-flow")) return ProgramUpdateFlowTests.Run(app, output);
            if (args.Contains("--program-update-probe")) return ProgramUpdateFlowTests.Probe();
            if (args.Contains("--program-detection")) return ProgramDetectionTests.Run(app, output);
            if (args.Contains("--program-sources")) return ProgramCatalogTests.ProbeSources(output);
            if (args.Contains("--program-live-search")) return ProgramCatalogTests.LiveSearch();
            if (args.Contains("--program-version-probe")) return ProgramOnlineTests.Probe();
            if (args.Contains("--program-online")) return ProgramOnlineTests.Run(app, output, args.Contains("--online-retest"));
            if (args.Contains("--program-catalog")) return ProgramCatalogTests.Run(app, output, args.Contains("--live-assets"), args.Contains("--program-screenshots"));
            if (args.Contains("--unified-dialogs")) return DialogConsistencyTests.Run(app, output);
            if (args.Contains("--dialog-routes")) return DialogConsistencyTests.Run(app, output, routesOnly: true);
            if (args.Contains("--tray-window")) return TrayWindowTests.Run(app, output);
            if (args.Contains("--overlay-dialogs")) return OverlayDialogTests.Run(app, output);
            if (args.Contains("--activity-status")) return ActivityStatusTests.Run(app, output);
            if (args.Contains("--windows-updates-shell")) return WindowsUpdatesShellTests.Run(app, output);
            if (args.Contains("--windows-update-progress")) return WindowsUpdateProgressTests.Run(app, output);
            if (args.Contains("--window-corners")) return WindowCornerTests.Run(app, output);
            if (args.Contains("--window-outline")) return WindowOutlineTests.Run(app, output);
            foreach (var (flag, theme) in new[] { ("standard", "Standard"), ("classic", "Classic"), ("blue", "Neon Blau"), ("pink", "Neon Pink"), ("green", "Neon Grün"), ("mint", "Linux Mint"), ("dark", "Dark") })
                if (args.Contains("--window-corners-" + flag)) return WindowCornerTests.Run(app, output, onlyTheme: theme);
            if (args.Contains("--startup-registry")) return Page4OverviewTests.Run(app, output, registryOnly: true);
            if (args.Contains("--page4-overview")) return Page4OverviewTests.Run(app, output);
            if (args.Contains("--startup-packages")) return Task.Run(() => StartupPackageTests.RunAsync(output)).GetAwaiter().GetResult();
            if (args.Contains("--startup-packages-ui")) return StartupPackageTests.RunUi(app, output);
            if (args.Contains("--esu")) return WindowsEsuTests.Run(app, output);
            if (args.Contains("--esu-account-live")) return WindowsEsuTests.AccountLive();
            if (args.Contains("--animated-progress")) return AnimatedProgressTests.Run(app, output);
            if (args.Contains("--windows-updates")) return WindowsUpdateTests.Run(app, output, args.Contains("--cached-wua-probe"));
            if (args.Contains("--manufacturers")) return ManufacturerTests.Run(app, output);
            if (args.Contains("--winget")) return WinGetTests.Run(app, output);
            if (args.Contains("--winget-probe")) return WinGetTests.ProbeOnly();
            if (args.Contains("--install-dialog")) return InstallDialogTests.Run(app, output);
            if (args.Contains("--languages")) return LanguageTests.Run(app, output);
            if (args.Contains("--update-bell")) return UpdateManagerTests.Bell(app, output);
            if (args.Contains("--updates")) return UpdateManagerTests.Run(app, output);
            if (args.Contains("--manager-exe")) return ManagerExeIntegration.Run(output, args.Last());
            // Regression: previous checks used non-active windows and missed Close -> Deactivated -> Close.
            if (args.Contains("--active-only"))
            {
                foreach (var (name, expected) in new[] { ("OpenButton", "open"), ("CenterButton", "center"), ("CancelTimerButton", "cancel"),
                    ("ShutdownButton", "shutdown"), ("RestartButton", "restart"), ("LogoutButton", "logout"), ("LockButton", "lock"), ("ExitButton", "exit") })
                {
                    var state = new TraySnapshot(name == "CancelTimerButton", name == "CancelTimerButton", false, "05:00", "Herunterfahren");
                    var calls = new List<string>();
                    var popup = new TrayQuickWindow("Standard", false, () => state, action => calls.Add(action));
                    // Reproduce the native focus-loss notification observed in the crash log while Close is in progress.
                    popup.Closing += (_, _) => typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(popup, new object[] { EventArgs.Empty });
                    popup.ShowActivated = false; popup.Show(); Pump(popup);
                    Assert(popup.IsLoaded && popup.IsVisible, "Regression requires a loaded native window");
                    ((Button)popup.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(dispatcherErrors.Count == 0, "Active menu click raised: " + string.Join("; ", dispatcherErrors.Select(e => e.Message)));
                    Assert(calls.SequenceEqual(new[] { expected }), "action not dispatched exactly once: " + name);
                    Assert(!popup.IsVisible, "menu not dismissed after action");
                    Console.WriteLine("PASS popup click with focus loss during close " + name);
                }
                UiSoundService.MuteForTests = true;
                var activeFlags = BindingFlags.NonPublic | BindingFlags.Instance;
                var activeMain = new MainWindow { SkipSavedPlacementForTests = true };
                typeof(MainWindow).GetField("_logDirectory", activeFlags)!.SetValue(activeMain, Path.Combine(output, "ClickFixtureLogs"));
                typeof(App).GetField("_mainWindow", activeFlags)!.SetValue(app, activeMain);
                app.MainWindow = activeMain;
                activeMain.Show(); Pump(activeMain); activeMain.Hide();
                var activeTimer = (DispatcherTimer)typeof(MainWindow).GetField("_timer", activeFlags)!.GetValue(activeMain)!;
                typeof(MainWindow).GetField("_timerAction", activeFlags)!.SetValue(activeMain, "shutdown");
                typeof(MainWindow).GetField("_timerDeadline", activeFlags)!.SetValue(activeMain, DateTimeOffset.UtcNow.AddMinutes(5));
                activeTimer.Start();
                void ClickThroughApp(string name)
                {
                    var popup = new TrayQuickWindow("Standard", false, activeMain.GetTraySnapshot,
                        action => typeof(App).GetMethod("DispatchTrayAction", activeFlags)!.Invoke(app, new object[] { action }));
                    typeof(App).GetField("_trayWindow", activeFlags)!.SetValue(app, popup);
                    popup.Closed += (_, _) => typeof(App).GetField("_trayWindow", activeFlags)!.SetValue(app, null);
                    popup.ShowActivated = false; popup.Show(); Pump(popup);
                    Assert(popup.IsLoaded && popup.IsVisible, "App integration requires a loaded popup");
                    ((Button)popup.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    Assert(dispatcherErrors.Count == 0 && !popup.IsVisible, "App dispatch/close error for " + name);
                }
                ClickThroughApp("CancelTimerButton");
                Assert(!activeTimer.IsEnabled && !activeMain.IsBusy && !activeMain.GetTraySnapshot().TimerActive, "App menu did not cancel actual activeTimer");
                Console.WriteLine("PASS active popup -> production App dispatch -> actual activeTimer stopped; fixture logs only");
                ClickThroughApp("CenterButton"); Pump(activeMain);
                var area = WindowPlacementService.GetCursorWorkAreaInDips(activeMain);
                Assert(activeMain.IsVisible && Math.Abs(activeMain.Left + activeMain.ActualWidth / 2 - (area.Left + area.Width / 2)) <= 2 &&
                    Math.Abs(activeMain.Top + activeMain.ActualHeight / 2 - (area.Top + area.Height / 2)) <= 2, "Main window not restored and centered");
                Console.WriteLine("PASS active popup -> production App dispatch -> actual activeMain window restored and centered");
                var outside = new TrayQuickWindow("Standard", false, activeMain.GetTraySnapshot, _ => throw new Exception("Outside click dispatched an action"));
                outside.ShowActivated = false; outside.Show(); Pump(outside); typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(outside, new object[] { EventArgs.Empty }); Pump(activeMain);
                Assert(!outside.IsVisible && dispatcherErrors.Count == 0, "Focus loss did not safely dismiss popup");
                outside.Dismiss(); outside.Dismiss();
                Console.WriteLine("PASS focus loss and repeated dismissal; no action dispatched");
                activeMain.Close();
                Console.WriteLine("PASS active menu lifecycle regression and real App activeTimer/centering integration; no power actions.");
                return 0;
            }
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            foreach (var busy in new[] { false, true })
            {
                var state = new TraySnapshot(busy, busy, false, "05:00", english ? "Shut down" : "Herunterfahren");
                var dispatched = new List<string>();
                var window = new TrayQuickWindow(theme, english, () => state, action => dispatched.Add(action))
                { Left = 25, Top = 25, ShowActivated = false };
                try
                {
                    window.Show(); Pump(window);
                    var exit = (Button)window.FindName("ExitButton");
                    exit.ApplyTemplate();
                    var exitFrame = exit.Template.FindName("ExitFrame", exit) as Border;
                    Assert(exitFrame is not null && exitFrame.BorderThickness.Left >= 2 && exitFrame.Opacity == 1,
                        "Exit outline was removed or faded by global X styling: " + theme + " / " + busy);
                    foreach (var name in new[] { "OpenButton", "ShutdownButton", "RestartButton", "LogoutButton", "LockButton", "ExitButton", "CenterButton" })
                    {
                        var element = (FrameworkElement)window.FindName(name);
                        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                        Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, "clipped " + theme + " " + name);
                        Assert(name is "OpenButton" or "CenterButton" ? element.IsEnabled : element.IsEnabled == !busy, "incorrect enabled state " + name);
                    }
                    Assert(((Button)window.FindName("CancelTimerButton")).IsEnabled == busy, "cancel state");
                    if (!english && theme is "Standard" or "Classic" or "Neon Blau") Save(window, theme.Replace(" ", "-") + (busy ? "-Timer" : "-Bereit"));
                    // A repair/update also blocks actions without displaying a fictional timer.
                    state = new(true, false, true, "", ""); window.RefreshState(); Pump(window);
                    Assert(((FrameworkElement)window.FindName("TimerCard")).Visibility == Visibility.Collapsed && !((Button)window.FindName("ExitButton")).IsEnabled, "repair guard");
                    // Simulate a stale enabled presentation: the dispatch guard must still reject all actions.
                    var invoke = typeof(TrayQuickWindow).GetMethod("Invoke", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    foreach (var action in new[] { "shutdown", "restart", "logout", "lock", "exit" }) invoke.Invoke(window, new object[] { action, true });
                    Assert(dispatched.Count == 0, "busy action dispatched");
                    state = new(false, false, false, "", ""); window.RefreshState(); Pump(window);
                    Assert(((Button)window.FindName("ShutdownButton")).IsEnabled && ((Button)window.FindName("ExitButton")).IsEnabled, "not reenabled after completion");
                    // A small available work area scrolls internally without visible scrollbars.
                    var scroll = (ScrollViewer)window.FindName("MenuScroll"); scroll.MaxHeight = 210; Pump(window);
                    scroll.ScrollToBottom(); Pump(window);
                    Assert(scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Collapsed && scroll.VerticalOffset > 0, "small work area not scrollable");
                    cases++;
                }
                finally { window.Close(); }
            }
            // Verify the timer-to-popup data and real cancellation using an isolated log directory.
            UiSoundService.MuteForTests = true;
            var main = new MainWindow { SkipSavedPlacementForTests = true };
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(MainWindow).GetField("_logDirectory", flags)!.SetValue(main, Path.Combine(output, "FixtureLogs"));
            typeof(MainWindow).GetField("_timerAction", flags)!.SetValue(main, "shutdown");
            typeof(MainWindow).GetField("_timerDeadline", flags)!.SetValue(main, DateTimeOffset.UtcNow.AddMinutes(5));
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(main)!;
            timer.Start();
            Assert(main.GetTraySnapshot() is { Busy: true, TimerActive: true }, "main timer not visible to tray");
            main.AbortTrayTimer();
            Assert(!main.IsBusy && !timer.IsEnabled && !main.GetTraySnapshot().TimerActive, "timer cancellation did not stop/release");
            main.Close();
            Console.WriteLine($"PASS {cases} tray design/language/timer cases; bounds, state refresh, stale dispatch guards and hidden scrolling checked. Real main timer cancellation checked with fixture logs; no power actions performed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { app.Shutdown(); }
    }
}
