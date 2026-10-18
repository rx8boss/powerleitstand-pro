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
            if (args.Contains("--install-dialog")) return InstallDialogTests.Run(app, output);
            if (args.Contains("--languages")) return LanguageTests.Run(app, output);
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
