using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class Page4NavigationTests
{
    internal static int Failure(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var main = new MainWindow(Path.Combine(output, "FailureFixture")) { ShowActivated = false };
        try
        {
            main.Show(); Pump(); main.ShowRepairPage(); Pump();
            var attempts = 0;
            main.UpdatesPageFactory = () => { attempts++; throw new IOException("Simulated resource load failure"); };
            main.ShowWindowsUpdatesPage(); Pump();
            var indicator = (System.Windows.Controls.TextBlock)main.FindName("PageIndicator");
            if (indicator.Text != "3 / 4" || !((FrameworkElement)main.FindName("RepairPage")).IsVisible)
                throw new Exception("Failed navigation hid the current page or changed the page index");
            if (main.FindName("WindowsUpdatesPage") is not null || ((System.Windows.Controls.ContentControl)main.FindName("WindowsUpdatesHost")).Content is not null)
                throw new Exception("Failed page was retained");
            var evidence = Path.Combine(output, "FailureFixture", "Logs", "windows-updates-error.txt");
            if (!File.ReadAllText(evidence).Contains("Simulated resource load failure")) throw new Exception("Detailed local failure evidence missing");
            main.UpdatesPageFactory = () => { attempts++; return new WindowsUpdatesPage(); };
            main.ShowWindowsUpdatesPage(); Pump();
            if (attempts != 2 || indicator.Text != "4 / 4" || main.FindName("WindowsUpdatesPage") is not WindowsUpdatesPage { IsVisible: true })
                throw new Exception("Retry did not create a fresh visible page");
            main.ShowRepairPage(); main.ShowWindowsUpdatesPage(); Pump();
            if (attempts != 2) throw new Exception("Successful page was unnecessarily recreated");
            if (File.Exists(Path.Combine(output, "FailureFixture", "Logs", "page4-load.txt")))
                throw new Exception("Opening Windows Updates created a routine navigation log");
            Console.WriteLine("PASS recoverable resource failure, retained repair page/index, local diagnostics, successful retry, single successful creation");
            return 0;
        }
        finally { main.Close(); Pump(); }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    // Real read-only system sampling and the compiled page resources; fixture logs only.
    internal static int Run(App app, string output, bool delayed = false)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        StartupTimings.Begin(Path.Combine(output, "Navigation-Timings.json"));
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
        var failures = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => failures.Add(e.Exception);
        var sample = typeof(MainWindow).GetMethod("ReadLiveDiagnosticsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            main.Show(); Pump();
            if (main.FindName("WindowsUpdatesPage") is not null) throw new Exception("Page four was eagerly created");
            if (delayed)
            {
                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(270))
                {
                    Sample(); Pump(); Thread.Sleep(200);
                }
                Console.WriteLine("PASS delayed first navigation after 270 seconds of live sampling");
                Console.Out.Flush();
            }
            for (var i = 0; i < 15; i++)
            {
                // Navigate while the sampler is still running, as in a live application.
                var reading = (Task)sample.Invoke(main, null)!;
                main.ShowRepairPage(); Pump();
                main.ShowWindowsUpdatesPage(); Pump();
                while (!reading.IsCompleted) { Pump(); Thread.Sleep(10); }
                reading.GetAwaiter().GetResult();
                var page = main.FindName("WindowsUpdatesPage") as WindowsUpdatesPage ?? throw new Exception("Page four missing");
                if (!page.IsVisible) throw new Exception("Page four not visible");
                main.ShowUsersPage(); Pump(); main.ShowWindowsUpdatesPage(); Pump();
                if (!ReferenceEquals(page, main.FindName("WindowsUpdatesPage"))) throw new Exception("Page recreated on revisit");
                Console.WriteLine($"PASS {i + 1}: real metrics, first/repeated page navigation, retained page");
                Console.Out.Flush();
                Thread.Sleep(100);
            }
            if (failures.Count > 0) throw new AggregateException(failures);
            StartupTimings.Save();
            Console.WriteLine("PASS read-only navigation regression; no update search, installation, repair or autostart mutation");
            return 0;
        }
        finally { main.Close(); Pump(); }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Sample()
        {
            var read = (Task)sample.Invoke(main, null)!;
            while (!read.IsCompleted) { Pump(); Thread.Sleep(10); }
            read.GetAwaiter().GetResult();
        }
    }
}
