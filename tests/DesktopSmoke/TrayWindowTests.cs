using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class TrayWindowTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0;
        var errors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Save(Window window, string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        MainWindow? main = null;
        try
        {
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            foreach (var visible in new[] { false, true })
            {
                var calls = new List<string>();
                var state = new TraySnapshot(true, true, false, "05:00", "Example", visible);
                var popup = new TrayQuickWindow(theme, english, () => state, calls.Add) { ShowActivated = false };
                popup.Show(); Pump();
                var caption = ((TextBlock)popup.FindName("OpenCaption")).Text;
                Assert(caption == (visible ? english ? "Minimize window" : "Fenster minimieren" : english ? "Open window" : "Fenster öffnen"), "localized window action");
                Assert(((Button)popup.FindName("OpenButton")).IsEnabled, "window action remains usable during timer");
                Assert(!((Button)popup.FindName("ShutdownButton")).IsEnabled, "conflicting action stays blocked");
                if (theme == "Standard" && !english) Save(popup, visible ? "Taskleiste-Minimieren" : "Taskleiste-Oeffnen");
                ((Button)popup.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Assert(calls.SequenceEqual(new[] { visible ? "minimize" : "open" }) && !popup.IsVisible, "exactly one state-dependent action and safe dismissal");
            }
            main = new MainWindow(Path.Combine(output, "Fixture")) { SkipSavedPlacementForTests = true, ShowActivated = false };
            typeof(App).GetField("_mainWindow", flags)!.SetValue(app, main); app.MainWindow = main;
            main.Show(); Pump();
            var timer = (DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(main)!;
            typeof(MainWindow).GetField("_timerDeadline", flags)!.SetValue(main, DateTimeOffset.UtcNow.AddMinutes(5));
            typeof(MainWindow).GetField("_timerAction", flags)!.SetValue(main, "shutdown"); timer.Start();
            typeof(App).GetMethod("DispatchTrayAction", flags)!.Invoke(app, new object[] { "minimize" }); Pump();
            Assert(!main.IsVisible && main.WindowState == WindowState.Minimized && timer.IsEnabled, "minimize keeps timer alive");
            var click = typeof(App).GetMethod("HandleTrayClick", flags)!;
            click.Invoke(app, new[] { Enum.Parse(click.GetParameters()[0].ParameterType, "Left") });
            var clickTimer = (DispatcherTimer)typeof(App).GetField("_trayClickTimer", flags)!.GetValue(app)!;
            Assert(clickTimer.IsEnabled && clickTimer.Interval.TotalMilliseconds > 0, "single click waits for double click");
            app.HandleTrayDoubleClick(); Pump();
            Assert(!clickTimer.IsEnabled && main.IsVisible && main.WindowState == WindowState.Normal && timer.IsEnabled, "double click restores same window without stopping timer");
            click.Invoke(app, new[] { Enum.Parse(click.GetParameters()[0].ParameterType, "Left") });
            Assert(!clickTimer.IsEnabled, "trailing click does not reopen popup");
            Assert(typeof(App).GetField("_trayWindow", flags)!.GetValue(app) is null, "double click leaves no popup");
            Assert(app.Windows.OfType<MainWindow>().Count() == 1, "no duplicate main window");
            timer.Stop(); typeof(MainWindow).GetMethod("SetTimerState", flags)!.Invoke(main, new object[] { false });
            Assert(errors.Count == 0, "no unhandled dispatcher errors");
            Console.WriteLine($"PASS TRAY WINDOW: {checks} assertions, seven themes, DE/EN, minimize/open, timer continuity, double click and trailing click suppression. No power actions executed.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            if (main is not null)
            {
                ((DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(main)!).Stop();
                main.Close();
            }
        }
    }
}
