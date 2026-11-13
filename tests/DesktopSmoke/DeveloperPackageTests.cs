using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class DeveloperPackageTests
{
    internal static int Run(App app, string output)
    {
        int checks = 0;
        void Assert(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task)
        {
            var time = Stopwatch.StartNew(); while (!task.IsCompleted && time.Elapsed.TotalSeconds < 30) { Pump(); Thread.Sleep(3); }
            Assert(task.IsCompleted, "Fixture completes"); task.GetAwaiter().GetResult(); Pump();
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        OnlineConnection.NetworkForTests = () => true; UiSoundService.MuteForTests = true;
        int installs = 0; bool changed = false, installed = false, failVerify = false, holdRead = false, missingModule = false;
        TaskCompletionSource? gate = null;
        var v1 = new DeveloperPackage("Fixture.Module", "1.0.0", "Controlled test module", "Fixture", "MIT", "Dependency >= 1.0", Path.Combine(output, "Fixture.Module"));
        var v2 = v1 with { Version = "2.0.0" };
        async Task<WinGetResult> Query(ProcessStartInfo start, CancellationToken token)
        {
            Assert(!start.UseShellExecute && start.CreateNoWindow, "Commands stay in app; no console");
            string script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList.Last()));
            Assert(script.Contains("-Scope CurrentUser") && script.Contains("-NoClobber") && !script.Contains("Set-ExecutionPolicy"), "User scope and conflict guard; no policy mutation");
            string action = start.Environment["PL_PACKAGE_ACTION"]!;
            if (missingModule) return new(42, "");
            if (action == "search" && holdRead) await Task.Delay(Timeout.Infinite, token);
            if (action == "install") { installs++; Assert(!token.CanBeCanceled, "Running package is awaited instead of killed on queue stop"); if (gate is not null) await gate.Task; installed = true; }
            var result = action is "search" or "resolve" ? changed ? v2 with { Version = "3.0.0" } : v2 : installed && !failVerify ? v2 : v1;
            return new(0, "PL_PACKAGES_JSON:" + JsonSerializer.Serialize(new[] { result }));
        }
        var service = new DeveloperPackages(run: Query);
        try
        {
            byte[] ModuleArchive(string? extra)
            {
                using var bytes = new MemoryStream();
                using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                {
                    using (var writer = new StreamWriter(archive.CreateEntry("Microsoft.PowerShell.PSResourceGet.psd1").Open()))
                        writer.Write("@{ ModuleVersion = '1.1.1' }");
                    if (extra is not null) archive.CreateEntry(extra);
                }
                return bytes.ToArray();
            }
            string moduleTarget = Path.Combine(output, "ModuleSetup", Guid.NewGuid().ToString("N"));
            DeveloperPackages.InstallModuleArchive(ModuleArchive(null), moduleTarget);
            Assert(File.Exists(Path.Combine(moduleTarget, "Microsoft.PowerShell.PSResourceGet.psd1")), "Module is placed at its final path only after extraction");
            bool escaped = false;
            try { DeveloperPackages.InstallModuleArchive(ModuleArchive("../outside.ps1"), moduleTarget + "-unsafe"); }
            catch (IOException) { escaped = true; }
            Assert(escaped && !Directory.Exists(moduleTarget + "-unsafe") && !File.Exists(Path.Combine(output, "ModuleSetup", "outside.ps1")), "Archive traversal cannot publish a partial module or write outside staging");
            var archiveSource = Path.Combine(output, "ChocolateyFixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(archiveSource);
            File.WriteAllText(Path.Combine(archiveSource, "fixture.txt"), "retained");
            var originalPath = Environment.GetEnvironmentVariable("Path");
            try
            {
                Environment.SetEnvironmentVariable("Path", originalPath + ";" + Path.Combine(archiveSource, "bin"));
                var archived = DeveloperPackages.ArchiveChocolatey(archiveSource, archiveSource + "-backup");
                Assert(!Directory.Exists(archiveSource) && File.Exists(Path.Combine(archived, "fixture.txt"))
                    && Environment.GetEnvironmentVariable("Path") == originalPath,
                    "Chocolatey removal preserves files and removes only the matching PATH entry");
            }
            finally { Environment.SetEnvironmentVariable("Path", originalPath); }
            var labelled = new Button(); ProgramButtonIcons.Set(labelled, "check", ""); ProgramButtonIcons.Set(labelled, "check", "Prüfen");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(labelled).Single().Text == "Prüfen", "Caption appears when an icon-only button gains a label");
            ProgramButtonIcons.Set(labelled, "check", "Check"); Assert(UnifiedDialogAppearance.Children<TextBlock>(labelled).Single().Text == "Check", "Caption switches language without duplicating controls");
            foreach (string bad in new[] { "bad;exit", "../x", "*", "a'b", "a\nb" })
            {
                bool rejected = false; try { DeveloperPackages.Validate(bad, "1.0.0"); } catch (IOException) { rejected = true; }
                Assert(rejected, "Untrusted package ID rejected");
            }
            foreach (string bad in new[] { "1.0;exit", "latest", "../../" })
            { bool rejected = false; try { DeveloperPackages.Validate("Valid.Package", bad); } catch (IOException) { rejected = true; } Assert(rejected, "Untrusted version rejected"); }
            var command = service.PowerShellCommand("install", "Fixture.Module", "2.0.0");
            File.WriteAllText(Path.Combine(output, "PowerShell-Skript.ps1"), Encoding.Unicode.GetString(Convert.FromBase64String(command.ArgumentList.Last())), new UTF8Encoding(true));
            Assert(command.ArgumentList.Contains("-NonInteractive") && command.ArgumentList.Contains("-NoProfile") && !command.ArgumentList.Contains("Fixture.Module"), "Constant encoded command with values passed as data");
            var winget = new WinGetService();
            var chocolateyProduct = Guid.Parse("EB32B51C-3AD8-4007-AA53-74EC1A1B84D5");
            Assert(DeveloperPackages.IsMatchingChocolateyMsi(chocolateyProduct, "MsiExec.exe /X" + chocolateyProduct.ToString("B")),
                "Registered Chocolatey MSI command matches the product identity");
            Assert(!DeveloperPackages.IsMatchingChocolateyMsi(chocolateyProduct, "msiexec /X" + Guid.NewGuid().ToString("B"))
                && !DeveloperPackages.IsMatchingChocolateyMsi(chocolateyProduct, "msiexec /X" + chocolateyProduct.ToString("B") + " /extra"),
                "Different MSI products and additional arguments are rejected");
            var silent = winget.Command("chocolatey", true, unattended: true);
            Assert(silent.ArgumentList.Contains("Chocolatey.Chocolatey") && silent.ArgumentList.Contains("--exact")
                && silent.ArgumentList.Contains("--silent") && silent.ArgumentList.Contains("--disable-interactivity")
                && !silent.ArgumentList.Contains("--interactive") && !silent.UseShellExecute,
                "Chocolatey uses the exact silent package route after app confirmation");
            Assert(winget.Command("kingston", true).ArgumentList.Contains("--interactive"),
                "Existing manufacturer installer remains interactive");
            var phases = new List<string>(); Wait(service.InstallAsync(false, v2, CancellationToken.None, phases.Add));
            Assert(installs == 1 && phases.SequenceEqual(new[] { "check", "install", "verify", "done" }), "Selected package completes ordered stages with independent verification");
            Wait(service.InstallAsync(false, v2, CancellationToken.None, _ => { })); Assert(installs == 1, "An externally completed target is not installed again");
            changed = true; bool blocked = false;
            try { Wait(service.InstallAsync(false, v2, CancellationToken.None, _ => { })); } catch (IOException) { blocked = true; }
            Assert(blocked && installs == 1, "Changed target version blocks execution"); changed = false;
            using (var cancel = new CancellationTokenSource())
            { cancel.Cancel(); blocked = false; try { Wait(service.InstallAsync(false, v2, cancel.Token, _ => { })); } catch (OperationCanceledException) { blocked = true; } Assert(blocked && installs == 1, "Cancellation before start does not install"); }
            failVerify = true; blocked = false;
            try { Wait(service.InstallAsync(false, v2, CancellationToken.None, _ => { })); } catch (IOException) { blocked = true; }
            Assert(blocked && installs == 2, "Exit success without installed target is not success"); failVerify = false;
            string root = Path.Combine(output, "Packages"); Directory.CreateDirectory(root); var pkg = Path.Combine(root, "Fixture.Module.1.0.0"); Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "Fixture.Module.nuspec"), "<package><metadata><id>Fixture.Module</id><version>1.0.0</version><description>Fixture</description><authors>Fixture</authors><license>MIT</license></metadata></package>");
            Assert(DeveloperPackages.ReadNuGet(root).Single().Location == pkg, "NuGet inventory reads actual nuspec identity and location");
            var badDir = Path.Combine(root, "Bad"); Directory.CreateDirectory(badDir); File.WriteAllText(Path.Combine(badDir, "Bad.nuspec"), "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///not-read'>]><package>&x;</package>");
            Assert(DeveloperPackages.ReadNuGet(root).Count == 1, "Malformed and external-entity package metadata excluded");
            var ng = new DeveloperPackages(read: (uri, _) => Task.FromResult(Encoding.UTF8.GetBytes(uri.AbsolutePath.EndsWith("index.json") ? "{\"resources\":[{\"@type\":\"SearchQueryService\",\"@id\":\"https://evil.invalid/query\"}]}" : "{}")));
            blocked = false; try { Wait(ng.SearchAsync(true, "test", CancellationToken.None)); } catch (IOException) { blocked = true; }
            Assert(blocked, "NuGet service-index redirect to unapproved source blocked");
            var metadata = new DeveloperPackages(read: (uri, _) => Task.FromResult(Encoding.UTF8.GetBytes(uri.AbsolutePath.EndsWith("index.json")
                ? "{\"resources\":[{\"@type\":\"SearchQueryService/3.5.0\",\"@id\":\"https://azuresearch-usnc.nuget.org/query\"}]}"
                : "{\"data\":[{\"id\":\"Fixture.Module\",\"version\":\"2.0.0\",\"description\":\"Fixture\",\"authors\":\"Fixture\",\"licenseUrl\":\"https://example.invalid/license\"}]}")));
            var searchResult = metadata.SearchAsync(true, "Fixture", CancellationToken.None); Wait(searchResult);
            Assert(searchResult.Result.Single().Version == "2.0.0" && searchResult.Result.Single().Dependencies == "", "NuGet search returns exact identity/version without inventing dependencies");
            service.NuGetExecutable = Path.Combine(output, "nuget.exe"); service.NuGetDirectory = root;
            var nc = service.NuGetCommand(v2, Path.Combine(output, "fixed.config"));
            Assert(nc.ArgumentList.Contains("-NonInteractive") && nc.ArgumentList.Contains(DeveloperPackages.NuGetSource) && nc.ArgumentList.Contains("-ConfigFile") && !nc.UseShellExecute, "NuGet arguments bind exact version and official source");

            installed = false;
            var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
            try
            {
                main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
                var trayActions = new List<PowerAction>();
                foreach (var tag in new[] { "shutdown", "restart", "logout", "lock", "unknown" }) main.ExecuteTrayAction(tag, trayActions.Add);
                Assert(trayActions.SequenceEqual(new[] { PowerAction.Shutdown, PowerAction.Restart, PowerAction.SignOut, PowerAction.Lock }), "Tray sends exactly the four direct system actions to the controlled dispatcher");
                Pump(); typeof(ProgramCatalogView).GetField("_developerPackages", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, service);
                Wait(view.OpenDeveloperPackagesAsync()); var pane = view.DeveloperPane!;
                Assert(pane is not null && view.SelectionOpen && !view.IsWorking, "Nested page opens without startup work or global install lock");
                missingModule = true; Wait(pane!.LoadAsync());
                var setupButton = UnifiedDialogAppearance.Children<Button>(pane).Single(b => b.Name == "DeveloperManagerSetup");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(pane).Any(t => t.Text.Contains("PSResourceGet fehlt")), "Missing PSResourceGet has a localized setup hint");
                setupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                var setupLayer = UnifiedDialogAppearance.Children<Grid>(main).Single(g => g.Name == "ManagerSetupLayer");
                Assert(UnifiedDialogAppearance.Children<AnimatedProgressBar>(setupLayer).Single().Height == 29,
                    "Setup dialog contains the standard installation progress bar");
                var cancelButton = UnifiedDialogAppearance.Children<Button>(setupLayer).Single(b => b.Name == "ManagerSetupCancel");
                Assert(cancelButton.Content is string text && text == "Abbrechen"
                    && ReferenceEquals(cancelButton.Style, main.FindResource("ManagerDialogTextButton")), "Cancel is a plain text action");
                var setupImage = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                setupImage.Render(main);
                var setupPng = new PngBitmapEncoder();
                setupPng.Frames.Add(BitmapFrame.Create(setupImage));
                using (var setupFile = File.Create(Path.Combine(output, "Einrichtung-DE-Kompakt.png"))) setupPng.Save(setupFile);
                UnifiedDialogAppearance.Children<Button>(setupLayer).Single(b => b.Name == "ManagerSetupCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Assert(!view.IsWorking && installs == 2 && !UnifiedDialogAppearance.Children<Grid>(main).Any(g => g.Name == "ManagerSetupLayer"),
                    "Declining the embedded setup dialog leaves the installation idle");
                missingModule = false; Wait(pane.LoadAsync());
                foreach (var theme in new[] { "Standard", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Classic", "Dark" })
                foreach (bool en in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (string)i.Tag == theme);
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(en); Pump();
                    Assert(pane!.IsVisible && pane.ActualWidth > 400 && pane.ActualHeight > 150, "Package pane fills actual available app content area");
                    foreach (var button in UnifiedDialogAppearance.Children<Button>(pane).Where(b => b.IsVisible))
                    {
                        bool scrolling = false; for (DependencyObject? p = VisualTreeHelper.GetParent(button); p is not null && !ReferenceEquals(p, pane); p = VisualTreeHelper.GetParent(p)) scrolling |= p is ScrollViewer;
                        var at = button.TranslatePoint(new Point(0, 0), pane!);
                        Assert(at.X >= -1 && at.X + button.ActualWidth <= pane!.ActualWidth + 1 && (scrolling || at.Y >= -1 && at.Y + button.ActualHeight <= pane.ActualHeight + 1), "Fixed buttons stay within pane; scrollable actions stay within columns");
                    }
                    foreach (var action in new[] { "DeveloperSearch", "DeveloperCheck", "DeveloperInstall", "DeveloperUpdate" })
                        Assert(UnifiedDialogAppearance.Children<Button>(pane).Single(b => b.Name == action).Content is StackPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text.Length > 0), "All main action captions are visible");
                    Assert(UnifiedDialogAppearance.Children<ScrollViewer>(pane).All(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden), "Lists and details scroll with hidden scrollbars");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(pane).Any(t => t.Text == (en ? "Package managers" : "Paketmanager")), "DE/EN heading follows app language");
                    if (theme == "Standard")
                    {
                        UnifiedDialogAppearance.Children<Button>(pane).Single(b => b.Content is StackPanel p && p.Children.OfType<TextBlock>().Any(t => t.Text == "Fixture.Module")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(pane).Any(t => t.Text.Contains(en ? "Not checked online yet" : "Noch nicht online geprüft")), "Local installed version is never presented as an online version");
                        var bmp = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32); bmp.Render(main);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var f = File.Create(Path.Combine(output, $"Paketmanager-{(en ? "EN" : "DE")}-{size}.png")); png.Save(f);
                    }
                }
                var target = new ProgramManagedTarget("Scoop", output, "fixture-engine", "7zip", "User", "main", "64bit", "1.0.0", output, "stamp", null);
                var offer = new ProgramManagedPackage(target, "2.0.0", "https://example.invalid/fixture", "hash", null, false, "MIT", "");
                typeof(ProgramCatalogView).GetProperty("Inventory", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view,
                    new ProgramInventory(true, new Dictionary<string,string?> { ["7-zip"] = "1.0.0" },
                        Updates: new Dictionary<string,string> { ["7-zip"] = "2.0.0" },
                        ManagedPackages: new Dictionary<string,IReadOnlyList<ProgramManagedPackage>> { ["7-zip"] = new[] { offer } }));
                page.SetLanguage(false); UnifiedDialogAppearance.Children<Button>(pane!).Single(b => b.Name == "DeveloperManagerScoop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var deadline = Stopwatch.StartNew();
                while (pane!.Packages.Count == 0 && deadline.Elapsed.TotalSeconds < 5) { Pump(); Thread.Sleep(3); }
                Pump();
                Assert(pane!.Packages.Single().Version == "2.0.0" && UnifiedDialogAppearance.Children<TextBlock>(pane).Any(t => t.Text == "Update verfügbar"), "Existing managers keep installed and offered versions separate");
                Assert(!UnifiedDialogAppearance.Children<CheckBox>(pane).Any(), "Scoop rows have status text without disabled checkboxes");
                var updateLink = UnifiedDialogAppearance.Children<Button>(pane).Single(b => b.Name == "DeveloperUpdate");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(updateLink).Any(t => t.Text == "Updates auswählen (1)"), "Scoop opens the shared picker when an update can be selected");
                var picker = pane.ExecuteAsync(true); Pump();
                var pickerLayer = UnifiedDialogAppearance.Children<Grid>(main).Single(g => g.Name == "ProgramUpdatesOverlay");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(pickerLayer).Any(t => t.Text == "7-Zip"), "Shared picker contains the Scoop update");
                var pickerImage = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                pickerImage.Render(main);
                var pickerPng = new PngBitmapEncoder();
                pickerPng.Frames.Add(BitmapFrame.Create(pickerImage));
                using (var pickerFile = File.Create(Path.Combine(output, "Scoop-Updateauswahl.png"))) pickerPng.Save(pickerFile);
                UnifiedDialogAppearance.Children<Button>(pickerLayer).Single(b => UnifiedDialogAppearance.Children<TextBlock>(b).Any(t => t.Text == "Abbrechen"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wait(picker);
                Wait(view.OpenDeveloperPackagesAsync());
                pane = view.DeveloperPane!;
                var query = UnifiedDialogAppearance.Children<TextBox>(pane).Single(); query.Text = "Fixture";
                holdRead = true; var pendingRead = pane.SearchAsync(); Pump(); pane.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); Wait(pendingRead); holdRead = false;
                Assert(query.IsEnabled && !view.IsWorking, "Leaving the page cancels its own read and restores controls without running an installer");
                Wait(pane.SearchAsync());
                var select = UnifiedDialogAppearance.Children<CheckBox>(pane).Single(); select.IsChecked = true; view.ConfirmForTests = _ => false;
                Wait(pane.ExecuteAsync(true)); Assert(installs == 2 && !view.IsWorking, "Declined selection does not run package manager");
                view.ConfirmForTests = _ => true; gate = new(TaskCreationOptions.RunContinuationsAsynchronously); var operation = pane.ExecuteAsync(true); Pump();
                Assert(!operation.IsCompleted && view.IsWorking && !query.IsEnabled && UnifiedDialogAppearance.Children<CheckBox>(pane).All(c => !c.IsEnabled), "Active package locks source, selection and other mutations");
                view.RequestStop(); Assert(view.IsWorking && !operation.IsCompleted, "Stop keeps running package and lock alive until verified exit"); gate.TrySetResult(); Wait(operation);
                Assert(installs == 3 && !view.IsWorking, "Controlled install finishes and releases work lock");
                UnifiedDialogAppearance.Children<Button>(pane).Single(b => b.Name == "DeveloperPackagesBack").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(view.DeveloperPane is null && !view.SelectionOpen, "Back restores normal automatic program routing");
            }
            finally { main.Close(); Pump(); }
            Console.WriteLine($"PASS {checks} developer-package checks; fake package managers only; no personal packages installed."); return 0;
        }
        finally { OnlineConnection.NetworkForTests = null; }
    }
}
