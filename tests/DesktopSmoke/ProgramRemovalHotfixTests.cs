using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramRemovalHotfixTests
{
    internal static int Run(App app, string output)
    {
        int checks = Task.Run(Backend).GetAwaiter().GetResult();
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var oldReader = ProgramRemovalDialog.Reader;
        InstalledProgram[] sample = [new("registry-sample", "3D Printer Manager", "Vendor", "1.0", @"C:\Apps\Printer", "Registry"),
            new("store|Vendor.Wintoys_abc123", "Wintoys", "Vendor", "3.0.22.0", @"C:\Apps\Wintoys", "Windows-App", PackageFamily: "Vendor.Wintoys_abc123"),
            new("registry-blocked", "Gesicherter Eintrag", "Vendor", "1", null, "Registry", Protected: true, BlockReason: "file"),
            new("weather", "Wetter", "Microsoft", "1.0.0.0", null, "Windows-App", PackageFamily: "Microsoft.BingWeather_8wekyb3d8bbwe", Inbox: true)];
        ProgramRemovalDialog.Reader = _ => sample;
        var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Until(Func<bool> test)
        {
            var clock = Stopwatch.StartNew();
            while (!test() && clock.Elapsed.TotalSeconds < 10) { Pump(); Thread.Sleep(5); }
            Assert(test(), "Expected hotfix UI state not reached");
        }
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage();
            var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            var view = page.ShowPrograms(false); view.Service = new ProgramCatalogTests.Fake(); view.Assets.AllowOnlineIcons = false;
            var host = (Grid)main.FindName("Root");
            foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                var task = ProgramRemovalDialog.ShowAsync(view);
                Grid Overlay() => host.Children.OfType<Grid>().Single(g => g.Name == "InstalledProgramsOverlay");
                bool Shows(string text) => UnifiedDialogAppearance.Children<TextBlock>(Overlay()).Any(t => t.IsVisible && t.Text.Contains(text));
                void ClickType(string type) => UnifiedDialogAppearance.Children<Button>(Overlay()).Single(b => b.Name == "InstalledType_" + type).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => Shows("Wintoys"));
                Assert(Shows("3D Printer Manager") && !Shows("Gesicherter Eintrag"), "All excludes blocked entries");
                Assert(Shows(english ? "1 programs" : "1 Programme"), "Counter excludes blocked programs");
                var search = UnifiedDialogAppearance.Children<TextBox>(Overlay()).Single();
                search.Text = "Gesicherter Eintrag"; Pump();
                Assert(Shows(english ? "No matching entries" : "Keine passenden Einträge") && !Shows("Gesicherter Eintrag"), "Search excludes blocked entries");
                search.Clear();
                ClickType("apps"); Pump();
                Assert(Shows("Wintoys") && !Shows("3D Printer Manager"), "App filter excludes desktop programs");
                Assert(!Shows("Wetter"), "Inbox entry excluded from Store filter");
                var removeButtons = UnifiedDialogAppearance.Children<Button>(Overlay()).Where(b => b.IsVisible && (b.Content as TextBlock)?.Text == (english ? "Uninstall" : "Deinstallieren")).ToArray();
                Assert(removeButtons.Length == 1 && removeButtons[0].IsEnabled, "Unmapped app removable, protected app blocked");
                foreach (var button in UnifiedDialogAppearance.Children<Button>(Overlay()).Where(b => b.IsVisible))
                {
                    var bounds = button.TransformToAncestor(Overlay()).TransformBounds(new Rect(button.RenderSize));
                    Assert(bounds.Right <= Overlay().ActualWidth + 1 && bounds.Bottom <= Overlay().ActualHeight + 1, "Actions fit compact dialog");
                }
                if (!english && size == 1)
                {
                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(main);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(Path.Combine(output, "Installierte-Programme-und-Apps.png")); png.Save(file);
                }
                ClickType("defaults"); Pump(); Assert(Shows("Wetter") && !Shows("Wintoys"), "Dedicated view contains optional inbox app only");
                ClickType("programs"); Pump(); Assert(!Shows("Gesicherter Eintrag"), "Programs exclude blocked entries"); Assert(Shows("3D Printer Manager") && !Shows("Wintoys"), "Program filter excludes apps");
                UnifiedDialogAppearance.Children<Button>(Overlay()).Single(b => (b.Content as TextBlock)?.Text == (english ? "Close" : "Schließen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => task.IsCompleted);
            }
        }
        finally { ProgramRemovalDialog.Reader = oldReader; main.Close(); Pump(); }
        File.WriteAllText(Path.Combine(output, "Hotfix-Checks.json"), JsonSerializer.Serialize(new { Checks = checks, Passed = true, RealPackagesRemoved = false }));
        Console.WriteLine($"PASS {checks} hotfix identity, guarded PowerShell fixture and DE/EN filter/layout checks. No installed app removed.");
        return 0;
    }

    private static async Task<int> Backend()
    {
        int checks = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        Assert(ProgramStoreApplications.Eligible(3, false, true, "Vendor.App_abc123")
            && !ProgramStoreApplications.Eligible(0, false, true, "Vendor.App_abc123")
            && !ProgramStoreApplications.Eligible(3, true, true, "Vendor.App_abc123")
            && !ProgramStoreApplications.Eligible(3, false, false, "Vendor.App_abc123")
            && !ProgramStoreApplications.Eligible(2, false, true, "Microsoft.Windows.FilePicker_cw5n1h2txyewy")
            && ProgramStoreApplications.Eligible(2, false, true, "Microsoft.BingWeather_8wekyb3d8bbwe"), "Origin, user-facing main app and exact inbox-family filter");
        var row = ProgramStoreApplications.Rows([new("Vendor.Unknown_abc123", "Example", "Vendor", "1.2.0.0", null, false)]).Single();
        Assert(!row.Protected && row.CatalogId is null && ProgramRemovalService.StoreFamily(row) == "Vendor.Unknown_abc123", "Unknown app uses its own family");
        Assert(ProgramRemovalDialog.MatchesType(row, "apps") && !ProgramRemovalDialog.MatchesType(row, "programs"), "Source filter uses package type");
        Assert(ProgramRemovalService.ScanRemainders(row).Count == 0, "Display name never authorizes deleting another app's data");
        Assert(ProgramStoreApplications.FriendlyName("Package.Id", "App name") == "App name" && ProgramStoreApplications.FriendlyName("Package.Id", "ms-resource:Missing") == "Package.Id", "Friendly name has safe fallback");
        foreach (var invalid in new[] { row with { Protected = true }, row with { PackageFamily = "Bad'; exit 0;#" }, row with { Version = "1.0';exit 0;#" }, row with { Source = "Registry" } })
        {
            bool blocked = false; try { ProgramRemovalService.StoreUninstallCommand(invalid); } catch (IOException) { blocked = true; }
            Assert(blocked, "Invalid or protected package cannot build a command");
        }
        var command = ProgramRemovalService.StoreUninstallCommand(row);
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(command.ArgumentList[^1]));
        Assert(!script.Contains("-AllUsers") && !script.Contains("Provisioned") && script.Contains("PackageFullName") && !command.UseShellExecute && command.CreateNoWindow, "Removal uses exact current-user registration without an external window");
        foreach (var sample in new (int Count, string Version, bool Protected, bool Framework, bool Allowed)[] { (1, "1.2.0.0", false, false, true),
            (0,"1.2.0.0",false,false,false), (2,"1.2.0.0",false,false,false), (1,"2.0.0.0",false,false,false), (1,"1.2.0.0",true,false,false), (1,"1.2.0.0",false,true,false) })
        {
            // These functions replace both Appx cmdlets inside the child process; no installed package is touched.
            string fixture = "function Get-AppxPackage { [CmdletBinding()] param($PackageTypeFilter) for($i=0;$i -lt " + sample.Count + ";$i++){[pscustomobject]@{PackageFamilyName='Vendor.Unknown_abc123';Version=[version]'" + sample.Version
                + "';PackageFullName='Fixture_1.2.0.0_x64__abc123';NonRemovable=$" + sample.Protected.ToString().ToLowerInvariant() + ";IsFramework=$" + sample.Framework.ToString().ToLowerInvariant()
                + ";IsResourcePackage=$false}}}; function Remove-AppxPackage { [CmdletBinding()] param($Package) if($Package -ne 'Fixture_1.2.0.0_x64__abc123'){throw 'Wrong identity'}; 'FIXTURE_REMOVED' }; ";
            var start = new ProcessStartInfo(command.FileName) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(fixture + script)) }) start.ArgumentList.Add(arg);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await WinGetService.QueryAsync(start, timeout.Token);
            Assert((result.ExitCode == 0 && result.Output.Contains("FIXTURE_REMOVED")) == sample.Allowed, "Runtime fixture verifies version/count/protection guard");
        }
        return checks;
    }

    internal static int Live(string output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var clock = Stopwatch.StartNew(); double? first = null; int partials = 0, resolvedFiles = 0;
        var fast = ProgramRemovalService.ReadWithLocations(timeout.Token, new ProgramPathResolver((_, _) => resolvedFiles++), fast: true,
            partial: rows => { first ??= clock.Elapsed.TotalMilliseconds; partials++; });
        double fastMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart(); var full = ProgramRemovalService.ReadWithLocations(timeout.Token, new ProgramPathResolver()); double fullMs = clock.Elapsed.TotalMilliseconds;
        if (partials < 1 || first is null || first > fastMs || resolvedFiles != 0) throw new Exception("Initial list still waits for complete executable resolution");
        File.WriteAllText(Path.Combine(output, "Ladezeit-Vergleich.json"), JsonSerializer.Serialize(new { FirstRowsMs = first, FastCompleteMs = fastMs, PreviousCompleteMs = fullMs, FastRows = fast.Count, PreviousRows = full.Count, FileResolutionsBeforeSelection = resolvedFiles, RealPackagesRemoved = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"LIVE READ-ONLY: first list {first:F0}ms; complete {fastMs:F0}ms; previous full scan {fullMs:F0}ms; {fast.Count}/{full.Count} rows. No installed app removed.");
        return 0;
    }

    internal static int LiveUi(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var oldReader = ProgramRemovalDialog.Reader; ProgramRemovalDialog.Reader = null;
        var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage();
            var view = ((WindowsUpdatesPage)main.FindName("WindowsUpdatesPage")).ShowPrograms(false);
            view.Service = new ProgramCatalogTests.Fake(); view.Assets.AllowOnlineIcons = false;
            var host = (Grid)main.FindName("Root"); var clock = Stopwatch.StartNew();
            var task = ProgramRemovalDialog.ShowAsync(view);
            var overlay = host.Children.OfType<Grid>().Single(g => g.Name == "InstalledProgramsOverlay");
            bool Ready() => UnifiedDialogAppearance.Children<Button>(overlay).Any(b => b.Name == "InstalledType_programs" && b.IsEnabled);
            while (!Ready() && clock.Elapsed.TotalSeconds < 30) { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); Thread.Sleep(5); }
            if (!Ready()) throw new Exception("Live dialog did not become usable");
            double usableMs = clock.Elapsed.TotalMilliseconds;
            UnifiedDialogAppearance.Children<Button>(overlay).Single(b => b.Name == "InstalledType_programs").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UnifiedDialogAppearance.Children<Button>(overlay).Single(b => (b.Content as TextBlock)?.Text == "Schließen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!task.IsCompleted || host.Children.Contains(overlay)) throw new Exception("Live dialog cannot close while additional sources load");
            File.WriteAllText(Path.Combine(output, "Echtes-Fenster-Ladezeit.json"), JsonSerializer.Serialize(new { UsableAfterMs = usableMs, CanFilterWhileLoading = true, CanCloseWhileLoading = true, RealPackagesRemoved = false }));
            Console.WriteLine($"LIVE UI: installed-program dialog usable after {usableMs:F0}ms; filtering and closing work during remaining checks. No installed app removed.");
            return 0;
        }
        finally { ProgramRemovalDialog.Reader = oldReader; main.Close(); }
    }
}
