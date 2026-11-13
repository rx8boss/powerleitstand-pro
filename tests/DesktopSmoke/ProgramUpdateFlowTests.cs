using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramUpdateFlowTests
{
    internal static int Edges(App app, string output)
    {
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks = 0;
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            var fake = new ProgramCatalogTests.Fake(); view.Service = fake; view.ConfirmForTests = _ => true; var logs = new List<string>(); view.WriteLog = logs.Add;
            Assert(UiText.Translate("Die portable Paketprüfsumme stimmt nicht überein.", true) == "Portable package checksum mismatch.", "portable error translated to English");
            Assert(UiText.Translate("Die portable Paketprüfsumme stimmt nicht überein.", false) == "Die portable Paketprüfsumme stimmt nicht überein.", "portable error remains German");
            foreach (bool english in new[] { false, true })
            {
                view.SetLanguage(english); var entry = ProgramCatalog.Entries.Single(x => x.Id == "tukui-client");
                fake.ResolveHandler = (_, _) => Task.FromResult<ProgramPackage?>(new(entry.Id, null, entry.Website, DateTime.UtcNow, VersionSource: "Publisher", CanInstall: false));
                view.ShowInfo(entry, view, false); Wait(view.Info!.Loading);
                var text = string.Join("\n", UnifiedDialogAppearance.Children<TextBlock>(view.Info.Frame).Select(x => x.Text));
                Assert(text.Contains(english ? "The checked publisher source does not provide" : "Die geprüfte Herstellerquelle liefert")
                    && !text.Contains(english ? "Die geprüfte Herstellerquelle liefert" : "The checked publisher source does not provide"), "publisher note uses only selected UI language");
                view.CloseInfo();
            }
            fake.ResolveHandler = null; view.SetLanguage(false);
            foreach (int code in new[] { 1603, 3010, unchecked((int)0x8A150109) })
            {
                fake.Installed["firefox"] = "1.0"; fake.Updates["firefox"] = "2.0"; fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Wait(view.RefreshAsync()); view.Select("firefox", true); var work = view.InstallAsync(); Pump(); Assert(view.IsWorking, "lock remains until installer returns"); fake.Pending.SetResult(code); Wait(work);
                Assert(view.Inventory.HasUpdate("firefox") && view.Selected.Contains("firefox"), "failed/reboot installer does not fabricate updated version");
                Assert(!logs.Any(x => x.Contains("Installation/Update bestätigt")), "no false success after exit " + code);
                Assert(!view.IsWorking && !main.IsBusy, "installer exit releases locks " + code);
                if (code == 3010 || code == unchecked((int)0x8A150109)) Assert(logs.Any(x => x.Contains("Neustart erforderlich")), "restart-required result explained");
                logs.Clear(); fake.Pending = null; view.Selected.Clear();
            }
            fake.Installed["firefox"] = "1.0"; fake.Updates["firefox"] = "2.0"; Wait(view.RefreshAsync()); view.Select("firefox", true);
            fake.Started.Clear(); fake.InspectHandler = _ => Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?> { ["firefox"] = "1.0" }, Updates: new Dictionary<string, string> { ["firefox"] = "3.0" }));
            Wait(view.InstallAsync()); Assert(fake.Started.Count == 0 && logs.Any(x => x.Contains("Updateversion nicht mehr bestätigt")), "changed target version cannot silently replace approved update");
            fake.InspectHandler = null; view.Selected.Clear(); fake.Installed.Clear(); fake.Updates.Clear(); Wait(view.RefreshAsync());
            var first = ProgramCatalog.Entries.Single(x => x.Id == "firefox"); var second = ProgramCatalog.Entries.Single(x => x.Id == "7-zip"); view.Select(first.Id, true); view.Select(second.Id, true);
            fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); var queue = view.InstallAsync(); Pump(); view.RequestStop(); Pump(); Assert(!queue.IsCompleted && main.IsBusy, "cancel does not kill active installer or prematurely unlock");
            fake.Pending.SetResult(0); Wait(queue); Assert(fake.Started.Count == 1 && !main.IsBusy && !view.IsWorking, "cancel prevents second installer and releases locks after process exit");
            Console.WriteLine("PASS " + checks + " targeted failure, restart, changed-version and cancellation checks. Fixture installers only."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { main.Close(); Pump(); }
    }
    internal static int Probe() => Task.Run(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var service = new ProgramInstallService(); var inventory = await service.InspectAsync(limit.Token);
        if (!inventory.HasUpdate("google-chrome")) throw new Exception("Actual Chrome update not actionable: " + inventory.Error);
        var entry = ProgramCatalog.Entries.Single(x => x.Id == "google-chrome");
        var package = await service.ResolveAsync(entry, limit.Token);
        if (package?.Id != "Google.Chrome.EXE" || package.Version != inventory.Updates![entry.Id]) throw new Exception("Chrome source/target mismatch");
        var command = service.UpgradeCommand(entry, package);
        Console.WriteLine("PASS real read-only Chrome inspection: " + inventory.Installed[entry.Id] + " -> " + package.Version + " / " + package.Id);
        Console.WriteLine("PASS exact upgrade command prepared but NOT executed: " + string.Join(' ', command.ArgumentList));
        var obs = await new ProgramPortable().ResolveAsync(ProgramCatalog.Entries.Single(x => x.Id == "obs-studio"), limit.Token);
        Console.WriteLine(obs is null ? "OBS portable not offered: no verified asset/digest" : "PASS official OBS portable metadata/digest: " + obs.Version);
        Console.WriteLine("No installer, portable download, agreement acceptance or real update performed."); return 0;
    }).GetAwaiter().GetResult();
    internal static int Run(App app, string output)
    {
        int checks = 0; var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        OnlineConnection.NetworkForTests = () => true;
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 40) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException("Fixture timeout"); task.GetAwaiter().GetResult(); Pump(); }
        void Save(Window window, string name) { window.UpdateLayout(); var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file); }
        string Caption(Button b) => b.Content is TextBlock t ? t.Text : b.Content?.ToString() ?? "";
        try
        {
            Assert(ProgramVersions.IsNewer("130.0.9", "130.0.10"), "numeric version ordering");
            foreach (var pair in new[] { ("2.0", "1.0"), ("1.0", "1.0.0"), ("Unknown", "2.0"), ("1.2-beta", "2.0"), ("1", "999999999999999") })
                Assert(!ProgramVersions.IsNewer(pair.Item1, pair.Item2), "no downgrade/equal/unknown/opaque/overflow: " + pair);
            var installed = new Dictionary<string, string?>(); var ids = new Dictionary<string, string>();
            var updates = ProgramInstallService.ParseUpdates("Name ID Version Verfügbar\nGoogle Chrome  Google.Chrome.EXE  155.0.8059.85  156.0.8078.48\n", installed, identities: ids);
            Assert(updates["google-chrome"] == "156.0.8078.48" && ids["google-chrome"] == "Google.Chrome.EXE", "real German source-filtered Chrome EXE row");
            foreach (var row in new[] { "Chrome Google.Chrome.EXE 1.0 2.0 evil", "Chrome NotGoogle.Chrome.EXE 1.0 2.0", "Chrome Google.Chrome.EXE Unknown 2.0", "Chrome Google.Chrome.EXE 2.0 1.0", "Chrome Google.Chrome.EXE 1.0 2.0 pinned" })
                Assert(ProgramInstallService.ParseUpdates(row, new Dictionary<string, string?>()).Count == 0, "unconfirmed update rejected: " + row);
            var calls = new List<ProcessStartInfo>();
            var backend = new ProgramInstallService((start, _) =>
            {
                calls.Add(start);
                if (start.ArgumentList[0] == "export") { File.WriteAllText(start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1], "{\"Sources\":[{\"SourceDetails\":{\"Name\":\"winget\"},\"Packages\":[{\"PackageIdentifier\":\"Google.Chrome.EXE\",\"Version\":\"155.0.8059.85\"}]}]}"); return Task.FromResult(new WinGetResult(0, "")); }
                return Task.FromResult(new WinGetResult(0, start.ArgumentList[0] == "show" ? "Google Chrome [Google.Chrome.EXE]\nVersion: 156.0.8078.48\n" : "Chrome Google.Chrome.EXE 155.0.8059.85 156.0.8078.48"));
            }, (_, _) => throw new Exception("Actual installer forbidden"));
            var task = backend.InspectAsync(CancellationToken.None); Wait(task); Assert(task.Result.HasUpdate("google-chrome"), "EXE variant actionable");
            var chrome = ProgramCatalog.Entries.Single(x => x.Id == "google-chrome"); var resolved = backend.ResolveAsync(chrome, CancellationToken.None); Wait(resolved);
            Assert(resolved.Result?.Id == "Google.Chrome.EXE", "resolution preserves installed package identity");
            var command = backend.UpgradeCommand(chrome, resolved.Result!);
            Assert(command.ArgumentList.Contains("Google.Chrome.EXE") && command.ArgumentList.Contains("156.0.8078.48") && !command.ArgumentList.Contains("--all"), "update command exact source/variant/version");
            var partialBackend = new ProgramInstallService((start, _) => start.ArgumentList[0] == "export"
                ? Task.FromException<WinGetResult>(new OperationCanceledException("Fixture export timeout"))
                : Task.FromResult(new WinGetResult(0, "Chrome Google.Chrome.EXE 155.0.8059.85 156.0.8078.48")));
            var partial = partialBackend.InspectAsync(CancellationToken.None); Wait(partial);
            Assert(partial.Result.HasUpdate("google-chrome") && !partial.Result.Complete && partial.Result.UpdateCheckComplete, "confirmed Chrome upgrade survives unrelated export timeout");
            Assert(ProgramPublisherVersions.BroadcastVersion("Version 2.2.0 https://international.download.nvidia.com/Windows/broadcast/2.3.0/NVIDIA_Broadcast_v2.3.0.71988672.exe") == "2.3.0", "official Windows binary link disambiguates stale page banner");
            var tukui = ProgramCatalog.Entries.Single(x => x.Id == "tukui-client");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert(ProgramPublisherVersions.ReadAsync(tukui, cancelled.Token).GetAwaiter().GetResult() is null,
                    "Tukui publisher page without version metadata is not fetched during checks");
            }
            Assert(!ProgramIconSources.IsPublic(IPAddress.Loopback) && !ProgramIconSources.IsPublic(IPAddress.Parse("192.168.0.1")) && ProgramIconSources.IsPublic(IPAddress.Parse("8.8.8.8")), "publisher icons cannot connect to local/private IPv4");
            Assert(!ProgramIconSources.IsPublic(IPAddress.Parse("::1")) && !ProgramIconSources.IsPublic(IPAddress.Parse("fe80::1")), "no local IPv6 icon endpoint");
            var release = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "32.2.2", assets = new[] { new { name = "OBS-Studio-32.2.2-Windows-x64.zip", digest = "sha256:" + new string('a', 64), size = 12345, browser_download_url = "https://github.com/obsproject/obs-studio/releases/download/32.2.2/OBS-Studio-32.2.2-Windows-x64.zip" } } });
            var portable = ProgramPortable.ParseRelease(release)!; Assert(portable.Version == "32.2.2", "official portable identity plus digest");
            Assert(ProgramPortable.ParseRelease(release.Replace("sha256:", "missing:")) is null, "portable without confirmed checksum not offered");
            Assert(ProgramPortable.ParseRelease(release.Replace("\"prerelease\":false", "\"prerelease\":true")) is null, "prerelease not silently selected");
            foreach (string name in new[] { "../escape.exe", "/escape.exe", "CON.txt", "bin/../escape.exe", "bin/64bit/obs64.exe" })
            {
                string archive = Path.Combine(output, Guid.NewGuid() + ".zip"), target = Path.Combine(output, Guid.NewGuid().ToString("N"));
                using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("fixture, not executable"); }
                try { Wait(ProgramPortable.ExtractAsync(archive, target, CancellationToken.None)); Assert(name == "bin/64bit/obs64.exe" && File.Exists(Path.Combine(target, name)), "safe portable archive extracts"); }
                catch (IOException) { Assert(name != "bin/64bit/obs64.exe", "unsafe portable path rejected"); }
            }
            var cache = Path.Combine(output, "CacheFixture"); Directory.CreateDirectory(cache);
            var icon = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(icon)); using var bytes = new MemoryStream(); png.Save(bytes);
            int requests = 0; var assets = new ProgramAssets(cache, (_, _) => { requests++; return Task.FromResult(bytes.ToArray()); }, readLocalIcons: false); var firefox = ProgramCatalog.Entries.First(x => x.Id == "firefox");
            Wait(assets.EnsureIconAsync(firefox)); Wait(assets.WarmupAsync()); int after = requests; Wait(assets.WarmupAsync()); Assert(requests == after, "repeat warmup respects freshness/dedup");
            var current = Path.Combine(cache, "firefox-" + firefox.AssetVersion + ".png"); File.WriteAllText(Path.Combine(cache, "firefox-2020-01-01.png"), "obsolete"); File.WriteAllText(Path.Combine(cache, "personal.png"), "keep");
            Wait(assets.CleanupAsync()); Assert(File.Exists(current) && !File.Exists(Path.Combine(cache, "firefox-2020-01-01.png")) && File.Exists(Path.Combine(cache, "personal.png")), "cleanup keeps current and foreign files");
            string localFolder = Path.Combine(output, "Location with spaces"); Directory.CreateDirectory(localFolder); string localFile = Path.Combine(localFolder, "Program.exe"); File.WriteAllText(localFile, "fixture");
            Assert(ProgramLocation.DirectoryOf(localFile) == localFolder && ProgramLocation.DirectoryOf(localFolder) == localFolder && ProgramLocation.DirectoryOf("relative.exe") is null, "folder button never executes program or uses relative location");
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            {
                var main = new MainWindow(Path.Combine(output, "Fixtures", theme)) { ShowActivated = false };
                try
                {
                    main.Show(); ((ComboBox)main.FindName("ThemeBox")).SelectedItem = ((ComboBox)main.FindName("ThemeBox")).Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                    main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
                    var iconDir = Path.Combine(output, "Icons", theme); Directory.CreateDirectory(iconDir);
                    var existingIcons = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "App Icons");
                    if (Directory.Exists(existingIcons)) foreach (var file in Directory.EnumerateFiles(existingIcons, "*.png", SearchOption.TopDirectoryOnly)) File.Copy(file, Path.Combine(iconDir, Path.GetFileName(file)), true);
                    var isolatedAssets = new ProgramAssets(iconDir) { AllowOnlineIcons = false }; Wait(isolatedAssets.WarmupAsync());
                    typeof(WindowsUpdatesPage).GetField("_programAssets", flags)!.SetValue(page, isolatedAssets); var view = page.ShowPrograms(false); var fake = new ProgramCatalogTests.Fake(); view.Service = fake;
                    foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 2, 4 })
                    {
                        typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english); typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null); ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump(); view.SetQuery("");
                        fake.Installed[chrome.Id] = "155.0.8059.85"; fake.Updates[chrome.Id] = "156.0.8078.48"; Wait(view.RefreshAsync());
                        Assert(view.Inventory.HasUpdate(chrome.Id), "Chrome update state reaches tile"); view.Select(chrome.Id, true); Assert(view.UpdateButton.IsEnabled && !view.InstallButton.IsEnabled, "installed update selectable through dedicated update action"); view.Selected.Clear();
                        var pending = new TaskCompletionSource<ProgramInventory>(TaskCreationOptions.RunContinuationsAsynchronously); fake.InspectHandler = token => pending.Task.WaitAsync(token); var refresh = view.RefreshAsync(); Pump();
                        var tabs = (Grid)typeof(ProgramCatalogView).GetField("_tabs", flags)!.GetValue(view)!;
                        Assert(tabs.Visibility == Visibility.Collapsed, "categories hidden during loading");
                        var layer = (Grid)typeof(ProgramCatalogView).GetField("_connectionLayer", flags)!.GetValue(view)!; var card = (Border)layer.Children[0];
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(card).Any(x => x.Text == (english ? "Checking programs …" : "Programme prüfen …")), "initial loader caption");
                        Assert(UnifiedDialogAppearance.Children<ScrollViewer>(card).Single().Content is Border { BorderThickness.Left: 1 }, "framed loading notice");
                        UnifiedDialogAppearance.Children<Button>(card).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(refresh); Assert(tabs.Visibility == Visibility.Visible && !view.HasOverlay, "stop restores categories and controls"); pending.TrySetResult(new(false, new Dictionary<string, string?>())); fake.InspectHandler = null;
                        view.SetQuery("Browser"); Pump(); Assert(view.ClearSearchButton.ActualHeight == 20 && view.ClearSearchButton.ActualWidth == 20 && view.ClearSearchButton.Effect is null, "search clear hit area stays small and has no overflow glow");
                        var searchPending = new TaskCompletionSource<IReadOnlyList<ProgramEntry>>(TaskCreationOptions.RunContinuationsAsynchronously); fake.SearchHandler = (_, token) => searchPending.Task.WaitAsync(token); var searching = view.SearchOnlineAsync(); Pump();
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(layer).Any(x => x.Text == (english ? "Searching programs online …" : "Programme online suchen …")), "online loader heading");
                        if (!english && theme == "Standard" && size is 1 or 4) Save(main, "Suche-" + size);
                        searchPending.SetResult(ProgramCatalog.Entries.Where(x => x.Categories.Contains("browser")).ToArray()); Wait(searching);
                        var scroll = (ScrollViewer)typeof(ProgramCatalogView).GetField("_scroll", flags)!.GetValue(view)!; Assert(scroll.VerticalOffset == 0 && tabs.Visibility == Visibility.Collapsed, "results reset once to top and categories remain hidden until clear");
                        view.SetQuery(""); view.SetCategory("all"); Pump(); scroll.ScrollToBottom(); Pump(); double before = scroll.VerticalOffset;
                        typeof(ProgramCatalogView).GetMethod("IconChanged", flags)!.Invoke(view, new object[] { firefox.Id }); Pump(); Assert(scroll.VerticalOffset == before, "icon event preserves user scrolling");
                        fake.InspectHandler = _ => Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?> { [chrome.Id] = "155.0.8059.85" }, Updates: new Dictionary<string, string> { [chrome.Id] = "156.0.8078.48" }, Evidence: new Dictionary<string, ProgramInstallation> { [chrome.Id] = new("155.0.8059.85", "Registry", localFile) })); Wait(view.RefreshAsync());
                        view.ShowInfo(chrome, view, false); Wait(view.Info!.Loading); var folder = UnifiedDialogAppearance.Children<Button>(view.Info.Frame).Single(x => x.ToolTip?.ToString() == (english ? "Open program folder" : "Programmordner öffnen")); Assert(folder.IsEnabled && folder.Width == 24 && folder.Height == 22, "small framed folder action");
                        if (!english && theme == "Standard" && size is 1 or 4) Save(main, "Programminfo-" + size); view.CloseInfo(); fake.InspectHandler = null;
                        var options = new[] { new ProgramSelection(ProgramCatalog.Entries.Single(x => x.Id == "obs-studio"), new("OBSProject.OBSStudio", "32.2.2", "Fixture", DateTime.UtcNow), portable), new ProgramSelection(firefox, new("Mozilla.Firefox", "150.0", "Fixture", DateTime.UtcNow)) };
                        var dialog = ProgramSelectionDialog.ShowAsync(view, options); Pump(); Assert(view.SelectionOpen && view.HasOverlay, "summary suspends background overlay effects");
                        var root = (Grid)main.FindName("Root"); var selectionLayer = root.Children.OfType<Grid>().Single(x => Panel.GetZIndex(x) == 6000);
                        var buttons = UnifiedDialogAppearance.Children<Button>(selectionLayer).ToArray(); var start = buttons.Single(x => Caption(x) == (english ? "↓ Start selected" : "↓ Auswahl starten"));
                        Assert(start.IsEnabled, "installer defaults can start after review");
                        buttons.Single(x => Caption(x) == "Portable").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(!start.IsEnabled, "portable target required");
                        var frame = (Border)selectionLayer.Children[0]; var bounds = frame.TransformToAncestor(main).TransformBounds(new Rect(frame.RenderSize)); Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= main.ActualWidth && bounds.Bottom <= main.ActualHeight, "summary fits main window");
                        if (!english && size is 1 or 4 && theme is "Standard" or "Neon Pink") Save(main, "Installationsauswahl-" + theme.Replace(' ', '-') + "-" + size);
                        buttons.Single(x => x.Tag?.ToString() == "installer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(start.IsEnabled, "switch back to installer clears target requirement");
                        buttons.Single(x => Caption(x) == (english ? "Cancel" : "Abbrechen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(dialog); Assert(dialog.Result is null && !view.SelectionOpen, "cancel closes without installer");
                        fake.Installed[chrome.Id] = "155.0.8059.85"; fake.Updates[chrome.Id] = "156.0.8078.48"; Wait(view.RefreshAsync()); view.Select(chrome.Id, true); view.ConfirmForTests = _ => true; Wait(view.InstallAsync()); Assert(!view.Inventory.HasUpdate(chrome.Id) && fake.Installed[chrome.Id] == "156.0.8078.48", "queued upgrade rechecked after exit"); view.ConfirmForTests = null;
                    }
                    Console.WriteLine("PASS update, search, folder and summary: " + theme + " / DE+EN / compact+standard+large");
                }
                finally { main.Close(); Pump(); }
            }
            Console.WriteLine("PASS " + checks + " overall program-flow checks. Fixture installers only; no real installation or cache deletion."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { OnlineConnection.NetworkForTests = null; }
    }
}

