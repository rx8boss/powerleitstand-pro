using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Reflection;
using System.Windows.Controls;
using PowerControlProWpf;

internal static class StartupTests
{
    internal static int BackgroundQueries(string output)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        start.ArgumentList.Add("--user-query-child-fixture");
        var result = UserQueryProcess.ReadAsync(start, TimeSpan.FromSeconds(8)).GetAwaiter().GetResult();
        if (result.Trim().Length != 80000) throw new Exception("Large stdout/stderr capture failed");
        Console.WriteLine("PASS concurrent stdout/stderr capture, no pipe deadlock");
        var pidFile = Path.Combine(output, "query-child.pid");
        var hanging = new ProcessStartInfo(Environment.ProcessPath!);
        hanging.ArgumentList.Add("--user-query-hang-fixture"); hanging.ArgumentList.Add(pidFile);
        var watch = Stopwatch.StartNew();
        try { UserQueryProcess.ReadAsync(hanging, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); throw new Exception("Hung query was accepted"); }
        catch (TimeoutException) { }
        if (watch.Elapsed > TimeSpan.FromSeconds(7)) throw new Exception("Query deadline was not bounded");
        var pid = int.Parse(File.ReadAllText(pidFile));
        try { using var child = Process.GetProcessById(pid); if (!child.HasExited) throw new Exception("Timed-out query process survived"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS hung query deadline and terminated child process");
        return 0;
    }

    internal static int Run(App app, string output, bool components = false, bool software = false)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var results = new List<object>();
        var rendering = Stopwatch.StartNew();
        if (software) System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Console.WriteLine($"Rendering configuration: {(software ? "SoftwareOnly" : "Default")}: {rendering.Elapsed.TotalMilliseconds:0.0} ms");
        if (components)
        {
            var loadWatch = Stopwatch.StartNew(); var loads = new List<string>();
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => { lock (loads) loads.Add(loadWatch.ElapsedMilliseconds + " " + e.LoadedAssembly.GetName().Name); };
            StartupTimings.Begin();
            _ = new Window(); StartupTimings.Mark("Plain Window");
            _ = new System.Windows.Controls.Button(); StartupTimings.Mark("Plain Button");
            _ = new System.Windows.Controls.TextBlock { Text = "Test" }; StartupTimings.Mark("Plain TextBlock");
            _ = new System.Windows.Controls.CheckBox(); StartupTimings.Mark("Plain CheckBox");
            _ = new System.Windows.Controls.ScrollViewer(); StartupTimings.Mark("Plain ScrollViewer");
            _ = new System.Windows.Controls.ContentControl(); StartupTimings.Mark("Plain ContentControl");
            _ = System.Windows.Markup.XamlReader.Parse("<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Text=\"Test\"/></Grid>"); StartupTimings.Mark("Simple XAML");
            _ = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/PowerLeitstand-PRO;component/Assets/logo.png")); StartupTimings.Mark("Logo image");
            _ = new AnimatedProgressBar(); StartupTimings.Mark("Animated progress");
            _ = new WindowsUpdatesPage(); StartupTimings.Mark("Windows updates page");
            File.WriteAllText(Path.Combine(output, "Components.json"), JsonSerializer.Serialize(StartupTimings.Snapshot(), new JsonSerializerOptions { WriteIndented = true }));
            lock (loads) File.WriteAllLines(Path.Combine(output, "AssemblyLoads.txt"), loads);
            foreach (var stage in StartupTimings.Snapshot()) Console.WriteLine(stage);
        }
        for (int i = 0; i < 2; i++)
        {
            StartupTimings.Begin();
            var settings = AppSettings.Load();
            var main = new MainWindow(Path.Combine(output, "Fixture"), settings) { ShowActivated = false };
            try
            {
                var privateFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                if (!ReferenceEquals(settings, typeof(MainWindow).GetField("_settings", privateFlags)!.GetValue(main)))
                    throw new Exception("Startup settings snapshot was read again");
                var usersField = typeof(MainWindow).GetField("_usersLoadTask", privateFlags)!;
                var usersRead = usersField.GetValue(main) as Task ?? throw new Exception("User background query missing");
                var reload = typeof(MainWindow).GetMethod("LoadUsers", privateFlags)!;
                reload.Invoke(main, new object?[] { null, new RoutedEventArgs() });
                reload.Invoke(main, new object?[] { null, new RoutedEventArgs() });
                if (!ReferenceEquals(usersRead, usersField.GetValue(main))) throw new Exception("Concurrent user queries were started");
                Console.WriteLine("PASS shared startup settings and single in-flight user query");
                if (main.FindName("WindowsUpdatesPage") is not null) throw new Exception("Page four eagerly initialized during startup");
                StartupTimings.Mark("Constructor returned");
                bool rendered = false;
                main.ContentRendered += (_, _) => { rendered = true; StartupTimings.Mark("First content rendered"); };
                main.Show(); StartupTimings.Mark("Show returned");
                var limit = Stopwatch.StartNew();
                while (!rendered && limit.Elapsed.TotalSeconds < 20)
                { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(5); }
                if (!rendered) throw new Exception("Window did not render");
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                StartupTimings.Mark("First idle");
                var stages = StartupTimings.Snapshot(); results.Add(new { Run = i + 1, Stages = stages });
                foreach (var stage in stages) Console.WriteLine($"{i + 1}: {stage.Name}: {stage.DurationMs:0.0} ms / {stage.ElapsedMs:0.0} ms");
                if (main.FindName("WindowsUpdatesPage") is not null) throw new Exception("First frame unexpectedly initialized page four");
                var openWatch = Stopwatch.StartNew();
                main.ShowWindowsUpdatesPage(); app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var page = main.FindName("WindowsUpdatesPage") as WindowsUpdatesPage ?? throw new Exception("Page four not created on navigation");
                if (!page.IsVisible) throw new Exception("Lazy page four not visible");
                Console.WriteLine($"{i + 1}: Page four first open after initial render: {openWatch.Elapsed.TotalMilliseconds:0.0} ms");
                main.ShowUsersPage(); main.ShowWindowsUpdatesPage();
                if (!ReferenceEquals(page, main.FindName("WindowsUpdatesPage"))) throw new Exception("Page four instance not retained");
                Console.WriteLine("PASS deferred page creation, first open and retained page state");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var settingsPanel = (Border)main.FindName("SettingsPanel");
                TextBlock Heading() => UnifiedDialogAppearance.Children<TextBlock>(settingsPanel).First(t => t.Name == "UnifiedDialogHeading");
                var heading = Heading();
                typeof(MainWindow).GetMethod("ApplySettingsDialogAppearance", flags)!.Invoke(main, null);
                if (!ReferenceEquals(heading, Heading())) throw new Exception("Unchanged options header recreated");
                typeof(MainWindow).GetField("_english", flags)!.SetValue(main, true);
                typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null); Pump();
                if (ReferenceEquals(heading, Heading()) || Heading().Text != "Options") throw new Exception("Options cache ignored language change");
                heading = Heading();
                var themes = (ComboBox)main.FindName("ThemeBox");
                themes.SelectedItem = themes.Items.Cast<ComboBoxItem>().First(t => t.Tag?.ToString() == "Classic"); Pump();
                if (ReferenceEquals(heading, Heading())) throw new Exception("Options cache ignored theme change");
                Console.WriteLine("PASS options reuse and language/theme invalidation");
                void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            finally { main.Close(); app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        }
        File.WriteAllText(Path.Combine(output, "Startup.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        var hiddenPage = new WindowsUpdatesPage { Visibility = Visibility.Collapsed };
        var probe = new Probe(); hiddenPage.WinGet = probe;
        var host = new Window { Content = hiddenPage, ShowActivated = false, Width = 800, Height = 500 };
        try
        {
            host.Show(); app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (probe.Calls != 0) throw new Exception("Hidden update page probed WinGet before it was needed");
            hiddenPage.Visibility = Visibility.Visible; app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (probe.Calls != 1 || hiddenPage.WinGetState != WinGetAvailability.Ready) throw new Exception("Opening update page did not probe WinGet");
            hiddenPage.Visibility = Visibility.Collapsed; hiddenPage.Visibility = Visibility.Visible;
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (probe.Calls != 1) throw new Exception("Ready WinGet was probed again on visibility change");
            Console.WriteLine("PASS 3 deferred WinGet startup checks; no package-manager process launched");
        }
        finally { host.Close(); }
        return 0;
    }
    private sealed class Probe : IWinGetService
    {
        internal int Calls;
        public Task<bool> ProbeAsync(CancellationToken token) { Calls++; return Task.FromResult(true); }
        public Task<WinGetPackage?> ResolveAsync(string manufacturer, CancellationToken token) => throw new Exception("Unexpected package resolution");
        public Task<int> InstallAsync(string manufacturer, Action started) => throw new Exception("Unexpected install");
        public void OpenSetup() => throw new Exception("Unexpected setup");
    }
}
