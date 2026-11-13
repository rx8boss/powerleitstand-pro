using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramCatalogTests
{
    internal static int LiveSearch() => Task.Run(async () =>
    {
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var backend = new ProgramInstallService(); var results = await backend.SearchAsync("WinRAR", token.Token);
        var item = results.Single(x => x.PackageId == "RARLab.WinRAR");
        var details = await backend.ResolveAsync(item, token.Token);
        if (details?.Version is null) throw new Exception("Exact live package check did not return a version");
        Console.WriteLine("PASS real read-only WinGet search and exact metadata: " + item.PackageId + " / " + details.Version);
        Console.WriteLine("No installer started, no agreement accepted."); return 0;
    }).GetAwaiter().GetResult();
    internal static int ProbeSources(string output) => Task.Run(async () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var uri = await PrivacyDownloadService.ResolveAsync(timeout.Token);
        Console.WriteLine("Official current x64 O&O source: " + uri);
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await CatalogWeb.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        Console.WriteLine("HTTP " + (int)response.StatusCode + "; length = " + response.Content.Headers.ContentLength);
        Console.WriteLine("Read-only source check. No executable downloaded or opened; no license accepted."); return 0;
    }).GetAwaiter().GetResult();
    internal sealed class Fake : IProgramInstallService
    {
        internal Dictionary<string, string?> Installed = [];
        internal Dictionary<string, string> Updates = [];
        internal TaskCompletionSource<int>? Pending;
        internal List<string> Started = [];
        internal int Websites;
        internal IReadOnlyList<ProgramEntry> SearchResults = [];
        internal Func<string, CancellationToken, Task<IReadOnlyList<ProgramEntry>>>? SearchHandler;
        internal Func<CancellationToken, Task<ProgramInventory>>? InspectHandler;
        internal Func<ProgramEntry, CancellationToken, Task<ProgramPackage?>>? ResolveHandler;
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token) => SearchHandler?.Invoke(query, token) ?? Task.FromResult(SearchResults);
        public Task<ProgramInventory> InspectAsync(CancellationToken token) => InspectHandler?.Invoke(token) ?? Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?>(Installed), Updates: new Dictionary<string, string>(Updates), UpdateCheckComplete: true));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token) => ResolveHandler?.Invoke(entry, token) ?? Task.FromResult(entry.PackageId is null ? null : new ProgramPackage(entry.PackageId, Updates.GetValueOrDefault(entry.Id), "Fixture", DateTime.UtcNow));
        public async Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started)
        { var code = await InstallAsync(entry, package, started); if (code == 0) { Installed[entry.Id] = package.Version; Updates.Remove(entry.Id); } return code; }
        public async Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started)
        {
            Started.Add(entry.Id); started(); var code = Pending is null ? 0 : await Pending.Task;
            if (code == 0) Installed[entry.Id] = "1.2.3"; return code;
        }
        public void OpenWebsite(ProgramEntry entry) => Websites++;
    }
    internal sealed class PrivacyFake : IPrivacyDownloadService
    {
        public string DownloadsDirectory => "Fixture Downloads";
        internal int Downloads, Opens;
        internal TaskCompletionSource<string>? Pending;
        public async Task<string> DownloadAsync(bool consent, IProgress<PrivacyProgress> progress, CancellationToken token)
        {
            if (!consent) throw new Exception("License bypass"); Downloads++;
            progress.Report(new(30, 100));
            return Pending is null ? "Fixture.exe" : await Pending.Task.WaitAsync(token);
        }
        public Task OpenAsync(string path) { Opens++; return Task.CompletedTask; }
    }
    internal static int Run(App app, string output, bool liveAssets = false, bool screenshotsOnly = false)
    {
        int checks = 0; var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task, int seconds = 20)
        {
            var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < seconds) { Pump(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new TimeoutException("Fixture task timed out"); task.GetAwaiter().GetResult(); Pump();
        }
        Rect Bounds(FrameworkElement child, Visual parent) => child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
        void Save(Window window, string name)
        {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        try
        {
            Assert(ProgramCatalog.Entries.Length == 148 && ProgramCatalog.Entries.Select(x => x.Id).Distinct().Count() == 148, "148 unique installation identities");
            Assert(ProgramCatalog.Entries.Single(e => e.Id == "filee").PackageId == "KnifeLemon.Filee" && ProgramCatalog.Entries.Single(e => e.Id == "adwcleaner").PackageId == "Malwarebytes.AdwCleaner", "Filee and AdwCleaner use verified WinGet identities");
            Assert(!ProgramCatalog.Entries.Any(x => x.Id == "vlc-zip"), "No additional VLC ZIP entry");
            Assert(ProgramCatalog.Entries.All(x => x.PreviewUrl is null), "No catalog screenshot URLs remain");
            foreach (var name in new[] { "WinRAR", "Vortex", "WowUp-CF", "Tukui Client", "Steam", "Epic Games Launcher", "Ubisoft Connect", "Satisfactory Mod Manager", "CurseForge", "Battle.net", "Roblox Client", "MelonLoader", "UltraPlus Manager", "Geek Uninstaller", "BCUninstaller", "MSI Afterburner", "ArtCraft Launcher", "DLSS Swapper", "TeamSpeak", "CompactGUI", "Autoruns" })
                Assert(ProgramCatalog.Entries.Any(x => x.Name == name), "Requested software included: " + name);
            Assert(ManufacturerCatalog.Apps.Any(x => x.Id == "asus-displaywidget") && ManufacturerCatalog.Apps.Any(x => x.Id == "turtle-beach"), "Requested manufacturer apps included");
            Assert(ProgramCatalog.Entries.Count(x => x.Categories.Contains("browser")) == 17, "17 browsers");
            Assert(ProgramCatalog.Entries.Count(x => x.Categories.Contains("ai")) == 18, "18 AI entries");
            Assert(ProgramCatalog.Entries.All(x => new Uri(x.Website).Scheme == "https" && (x.IconUrl.Length == 0 || new Uri(x.IconUrl).Scheme == "https")), "Fixed HTTPS metadata or explicit neutral icon");
            Assert(!CatalogWeb.IsAllowed(new("https://evil.example/test.png"), new HashSet<string> { "www.firefox.com" }), "Unlisted host rejected");
            Assert(!CatalogWeb.IsAllowed(new("http://www.firefox.com/test.png"), new HashSet<string> { "www.firefox.com" }), "HTTP rejected");
            var liveFormat = "Name   ID            Version\n----------------------------\nWinRAR RARLab.WinRAR 7.23.0\n";
            Assert(ProgramInstallService.ParseSearch(liveFormat).Single().Id == "RARLab.WinRAR", "Real WinGet table with single separator spaces parsed");
            Assert(ProgramInstallService.ParseSearch("Wrong  Example.…  1.0\nWrong  ../evil  1.0").Count == 0, "Truncated/path identities rejected");
            var searchCalls = new List<ProcessStartInfo>();
            var searchBackend = new ProgramInstallService((start, _) =>
            {
                searchCalls.Add(start);
                return Task.FromResult(start.ArgumentList[0] == "search" ? new WinGetResult(0,"Example Paint  Example.Paint  4.0\nExample Paint  Example.Paint  4.0")
                    : new WinGetResult(0,"Found [Example.Paint]\nVersion: 4.0\nDescription: Draw and edit images.\nHomepage: https://example.com/"));
            }, (_, _) => throw new Exception("Search/metadata must not start installers"));
            var searchTask = searchBackend.SearchAsync("paint", CancellationToken.None); Wait(searchTask); var discovered = searchTask.Result.Single();
            Assert(discovered.PackageId == "Example.Paint", "Online result has exact identity and duplicates removed");
            var metadataTask = searchBackend.ResolveAsync(discovered, CancellationToken.None); Wait(metadataTask);
            Assert(metadataTask.Result?.Description == "Draw and edit images.", "Verified publisher description available without screenshot");
            Assert(searchCalls.All(x => x.ArgumentList[0] is "search" or "show"), "Discovery and info are read-only");
            try { searchBackend.Command(discovered with { Website = "https://evil.example/" }, true); throw new Exception("Modified discovered entry allowed"); } catch (InvalidOperationException) { checks++; }
            var validatedCommand = searchBackend.Command(discovered, true, "4.0");
            Assert(validatedCommand.ArgumentList.Contains("Example.Paint") && validatedCommand.ArgumentList.Contains("--exact"), "Only original discovered instance enters exact install route");
            var entry = ProgramCatalog.Entries.First(x => x.PackageId is not null);
            var calls = new List<ProcessStartInfo>(); var backend = new ProgramInstallService((start, token) => { calls.Add(start); return Task.FromResult(new WinGetResult(0, "Found [" + entry.PackageId + "]\nVersion: 2.3.4")); }, (start, started) => { calls.Add(start); started(); return Task.FromResult(0); });
            var resolve = backend.ResolveAsync(entry, CancellationToken.None); Wait(resolve); Assert(resolve.Result?.Version == "2.3.4", "Available version parsed from exact response");
            Wait(backend.InstallAsync(entry, resolve.Result!, () => { }));
            Assert(calls.All(x => x.ArgumentList.Contains("--exact") && x.ArgumentList.Contains("winget") && !x.FileName.Equals("winget", StringComparison.OrdinalIgnoreCase)), "Exact fixed-source fixed-executable route");
            Assert(calls.Last().ArgumentList.Contains("--silent") && !calls.Last().ArgumentList.Any(x => x.StartsWith("--accept")), "Automatic installation without implicit agreement acceptance");
            var upgrade = backend.UpgradeCommand(entry, resolve.Result!);
            Assert(upgrade.ArgumentList[0] == "upgrade" && upgrade.ArgumentList.Contains("--exact") && upgrade.ArgumentList.Contains("--version")
                && !upgrade.ArgumentList.Contains("--no-upgrade") && !upgrade.ArgumentList.Contains("--all") && !upgrade.ArgumentList.Contains("--include-unknown"), "Upgrade is a separate exact-version user action, never bulk/unknown");
            var known = new Dictionary<string, string?>();
            var parsedUpdates = ProgramInstallService.ParseUpdates("Name Id Version Available Source\nFirefox Mozilla.Firefox 1.0 2.0 winget\n", known);
            Assert(parsedUpdates.GetValueOrDefault("firefox") == "2.0" && known.GetValueOrDefault("firefox") == "1.0", "Exact update row yields installed and available versions");
            foreach (var row in new[] { "Firefox NotMozilla.Firefox 1.0 2.0 winget", "Firefox Mozilla.Firefox 1.0 2.0 evil", "Firefox Mozilla.Firefox Unknown 2.0 winget", "Firefox Mozilla.Firefox 1.0 Unknown winget", "Firefox Mozilla.Firefox 1.0 1.0 winget", "Firefox Mozilla.Firefo… 1.0 2.0 winget" })
                Assert(ProgramInstallService.ParseUpdates(row, new Dictionary<string, string?>()).Count == 0, "Unconfirmed/mismatched update row rejected: " + row);
            var inspectCalls = new List<ProcessStartInfo>();
            var inspector = new ProgramInstallService((start, _) =>
            {
                inspectCalls.Add(start);
                if (start.ArgumentList[0] == "export")
                {
                    var exportPath = start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1];
                    File.WriteAllText(exportPath, "{\"Sources\":[{\"SourceDetails\":{\"Name\":\"winget\"},\"Packages\":[{\"PackageIdentifier\":\"Mozilla.Firefox\",\"Version\":\"1.0\"}]}]}");
                    return Task.FromResult(new WinGetResult(0, ""));
                }
                return Task.FromResult(new WinGetResult(0, "Firefox Mozilla.Firefox 1.0 2.0 winget"));
            }, (_, _) => throw new Exception("Inspection must not start installers"));
            var inventoryTask = inspector.InspectAsync(CancellationToken.None); Wait(inventoryTask);
            Assert(inventoryTask.Result.HasUpdate("firefox"), "Read-only inventory discovers upgrade");
            Assert(inspectCalls.Select(x => x.ArgumentList[0]).SequenceEqual(new[] { "list", "export" }) && inspectCalls.First().ArgumentList.Contains("--upgrade-available"), "Inspection uses only export and list, never upgrade/install");
            try { backend.Command(entry with { PackageId = "Other.Package" }, true); throw new Exception("Altered package allowed"); } catch (InvalidOperationException) { checks++; }
            var export = ProgramInstallService.ParseExport("{\"Sources\":[{\"SourceDetails\":{\"Name\":\"winget\"},\"Packages\":[{\"PackageIdentifier\":\"Mozilla.Firefox\",\"Version\":\"1.0\"}]},{\"SourceDetails\":{\"Name\":\"evil\"},\"Packages\":[{\"PackageIdentifier\":\"Google.Chrome\"}]}]}");
            Assert(export.Count == 1 && export["Mozilla.Firefox"] == "1.0", "Inventory ignores mismatched source");
            Assert(PrivacyDownloadService.ResolvePage("<a href='https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe'>Download 64-bit</a>").Host == "dl5.oo-software.com", "Current official x64 page parser");
            foreach (var html in new[] { "<a href='https://evil.example/OOSU10.exe'>64-bit</a>", "<a href='https://dl5.oo-software.com/files/ooshutup10/OOSU10ARM64.exe'>ARM64</a>" })
                try { PrivacyDownloadService.ResolvePage(html); throw new Exception("Wrong architecture/source accepted"); } catch (InvalidDataException) { checks++; }
            var directory = Path.Combine(output, "DownloadFixture"); Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "Keep.txt"), "keep");
            int resolved = 0, verified = 0; var bytes = new byte[4096]; bytes[0] = 77; bytes[1] = 90;
            var privacyService = new PrivacyDownloadService(directory, (path, publisher) => { verified++; return publisher == PrivacyDownloadService.ExpectedPublisher; }, token => { resolved++; return Task.FromResult(new Uri("https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe")); }, (uri, token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            try { Wait(privacyService.DownloadAsync(false, new Progress<PrivacyProgress>(), CancellationToken.None)); throw new Exception("Consent bypass"); } catch (InvalidOperationException) { checks++; }
            Assert(resolved == 0 && verified == 0, "No network/file before license consent");
            var got = privacyService.DownloadAsync(true, new Progress<PrivacyProgress>(), CancellationToken.None); Wait(got);
            Assert(File.Exists(got.Result) && verified == 1 && !Directory.EnumerateFiles(directory, "*.partial").Any(), "Verified file promoted after stream closed");
            var badSignature = new PrivacyDownloadService(directory, (_, _) => false, token => Task.FromResult(new Uri("https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe")), (uri, token) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
            try { Wait(badSignature.DownloadAsync(true, new Progress<PrivacyProgress>(), CancellationToken.None)); throw new Exception("Bad signature accepted"); } catch (InvalidDataException) { checks++; }
            Assert(File.ReadAllText(Path.Combine(directory, "Keep.txt")) == "keep" && !Directory.EnumerateFiles(directory, "*.partial").Any(), "Only own partial removed on failed signature");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { Wait(privacyService.DownloadAsync(true, new Progress<PrivacyProgress>(), cancellation.Token)); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
                Assert(!Directory.EnumerateFiles(directory, "*.partial").Any(), "Cancelled download leaves no partial file");
            }
            var marksFile = Path.Combine(output, "ManualFixture", "marks.json");
            var marks = new ProgramInstallationMarks(marksFile); marks.Set("tukui-client", true);
            Assert(new ProgramInstallationMarks(marksFile).Contains("tukui-client"), "Manual confirmation survives reload independently of automatic inventory");
            marks.Set("tukui-client", false); Assert(!new ProgramInstallationMarks(marksFile).Contains("tukui-client"), "Manual confirmation can be removed persistently");
            File.WriteAllText(marksFile, "broken json"); var corruptMarks = new ProgramInstallationMarks(marksFile);
            Assert(corruptMarks.Error is not null && !corruptMarks.Contains("tukui-client"), "Corrupt manual state is not installation evidence");
            var tukui = ProgramCatalog.Entries.Single(x => x.Id == "tukui-client");
            Assert(ProgramInstallService.MatchesRegistry(tukui, "TukuiClient", "Tukui.org"), "Normalized registered client name recognized");
            Assert(!ProgramInstallService.MatchesRegistry(tukui, "TukuiClient Helper", "Tukui") && !ProgramInstallService.MatchesRegistry(tukui, "Tukui Client", "Other publisher"), "No fuzzy or wrong-publisher installation evidence");
            byte[] iconBytes;
            using (var memory = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
                encoder.Save(memory); iconBytes = memory.ToArray();
            }
            int iconRequests = 0; var iconCompletion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var iconEntry = ProgramCatalog.Entries.First();
            var requestedIcons = new ProgramAssets(Path.Combine(output, "AssetRetry", "icons"), (_, _) => { Interlocked.Increment(ref iconRequests); return iconCompletion.Task; }, readLocalIcons: false);
            var requestOne = requestedIcons.EnsureIconAsync(iconEntry); var requestTwo = requestedIcons.EnsureIconAsync(iconEntry);
            Assert(ReferenceEquals(requestOne, requestTwo), "Concurrent hover and startup share one icon request");
            iconCompletion.SetResult(iconBytes); Wait(requestOne); Wait(requestedIcons.EnsureIconAsync(iconEntry));
            Assert(iconRequests == 1 && requestedIcons.Icon(iconEntry.Id) is not null, "Loaded hover icon replaces fallback and is reused");
            int failedRequests = 0;
            var failedIcons = new ProgramAssets(Path.Combine(output, "FailedAsset", "icons"), (_, _) => { Interlocked.Increment(ref failedRequests); throw new HttpRequestException("Fixture offline"); }, readLocalIcons: false);
            Wait(failedIcons.EnsureIconAsync(iconEntry)); Wait(failedIcons.EnsureIconAsync(iconEntry));
            Assert(failedRequests == 2 && failedIcons.Icon(iconEntry.Id) is null, "Failed hover requests use retry cooldown and keep fallback");
            Wait(failedIcons.EnsureIconAsync(iconEntry with { IconUrl = "https://example.com/unapproved.png" }));
            Assert(failedRequests == 2, "Modified unapproved icon entry cannot be fetched");
            var ownCache = Path.Combine(Path.GetTempPath(), "PowerLeitstand-IconFixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(ownCache);
            foreach (var item in ProgramCatalog.Entries)
            {
                var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(ownCache, item.Id + "-" + item.AssetVersion + ".png")); encoder.Save(file);
            }
            var offlineCache = new ProgramAssets(ownCache) { AllowOnlineIcons = false, Diagnostic = text => Console.WriteLine("OFFLINE CACHE " + text) }; Wait(offlineCache.WarmupAsync());
            Assert(ProgramCatalog.Entries.All(x => offlineCache.Icon(x.Id) is null) && !Directory.Exists(ownCache), "Legacy cache removed; offline uses lightning without HTTP");
            if (Directory.Exists(ownCache)) throw new Exception("Legacy cache remains");
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" }.Where(x => !screenshotsOnly || x == "Standard"))
            {
                var main = new MainWindow(Path.Combine(output, "Fixtures", theme)) { ShowActivated = false };
                try
                {
                    main.Show(); var themeBox = (ComboBox)main.FindName("ThemeBox"); themeBox.SelectedItem = themeBox.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                    main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); var fake = new Fake(); view.Service = fake; view.ConfirmForTests = _ => true; view.Assets.AllowOnlineIcons = liveAssets;
                    // Optional real HTTP asset check, isolated cache only. No installers are launched.
                    if (liveAssets && theme == "Standard")
                    {
                        view.Assets.Diagnostic = text => Console.WriteLine("ASSET " + text);
                        Wait(view.Assets.WarmupAsync(), 220);
                        Console.WriteLine("Loaded icons: " + ProgramCatalog.Entries.Count(x => view.Assets.Icon(x.Id) is not null));
                        Assert(ProgramCatalog.Entries.Count(x => view.Assets.Icon(x.Id) is not null) >= 45, "Live official icon sources decoded");
                    }
                    foreach (var english in new[] { false, true })
                    foreach (int size in screenshotsOnly ? new[] { 1, 4 } : new[] { 1, 2, 4 })
                    {
                        typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english); typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump(); view.SetCategory("ai"); Pump();
                        Assert(view.Columns == (view.ActualWidth < 1000 || view.ActualHeight < 470 ? 3 : 6), "Responsive column count");
                        if (size == 4) Assert(view.Columns == 6, "1200x760 has six columns");
                        var rows = view.Tiles.Take(view.Columns * 3).ToArray(); Assert(rows.Length == view.Columns * 3, "Three complete AI rows");
                        var scroll = UnifiedDialogAppearance.Children<ScrollViewer>(view).Single(); Assert(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "Hidden scrollbar");
                        foreach (var tile in rows)
                        {
                            var rectangle = Bounds(tile, scroll); Assert(rectangle.Left >= -1 && rectangle.Right <= scroll.ActualWidth + 1 && rectangle.Bottom <= scroll.ActualHeight + 1, $"Visible tile contained {theme}/{english}/{size}: {rectangle} in {scroll.ActualWidth}x{scroll.ActualHeight}");
                            foreach (var image in UnifiedDialogAppearance.Children<Image>(tile))
                            { var bounds = Bounds(image, tile); Assert(bounds.Left >= 2 && bounds.Top >= 2 && bounds.Right <= tile.ActualWidth - 2 && bounds.Bottom <= tile.ActualHeight - 2, "Icon padded inside tile"); }
                        }
                        view.SetCategory("all"); view.SetQuery("ArtCraft"); Pump();
                        Assert(view.VisibleEntries.Single().Id == "artcraft-launcher", "ArtCraft searchable in shipped catalog");
                        Assert(view.ClearSearchButton.Visibility == Visibility.Visible, "Clear X visible for nonempty query");
                        if (theme == "Standard" && !english) Save(main, "ArtCraft-Suche-" + size);
                        var clearBounds = Bounds(view.ClearSearchButton, view); Assert(clearBounds.Left >= 0 && clearBounds.Right <= view.ActualWidth && clearBounds.Bottom <= view.ActualHeight, "Search clear control contained in all sizes/themes/languages");
                        view.ClearSearch(); Assert(view.VisibleEntries.Count == ProgramCatalog.Entries.Length && view.ClearSearchButton.Visibility == Visibility.Collapsed, "Clear X restores catalog");
                        var artcraft = ProgramCatalog.Entries.Single(x => x.Id == "artcraft-launcher");
                        view.ShowInfo(artcraft, view, false); Pump(); var unknownCard = view.Info!.Frame;
                        var markSwitch = UnifiedDialogAppearance.Children<CheckBox>(unknownCard).Single();
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(unknownCard).Any(x => x.Text == (english ? "Not reliably determined" : "Nicht sicher ermittelbar")), "Unknown installation is not reported as absent");
                        markSwitch.IsChecked = true; Pump();
                        Assert(view.Info?.IsOpen == true, "Manual switch keeps the info card open");
                        Assert(view.IsManual(artcraft.Id) && !view.Inventory.Installed.ContainsKey(artcraft.Id) && !view.Inventory.HasUpdate(artcraft.Id), "Manual confirmation never fabricates version or update evidence");
                        Assert(new ProgramInstallationMarks(view.Assets.MarksFile).Contains(artcraft.Id), "UI switch persists confirmation");
                        Assert(UnifiedDialogAppearance.Children<CheckBox>(view.Tiles.Single(x => (string?)x.Tag == artcraft.Id)).Single().Visibility == Visibility.Collapsed, "Manually installed tile hides install selection");
                        view.Select(artcraft.Id, true); Assert(!view.Selected.Contains(artcraft.Id), "Manually confirmed software cannot queue a fresh installation");
                        view.CloseInfo(); view.ShowInfo(artcraft, view, false); Pump();
                        Assert(UnifiedDialogAppearance.Children<CheckBox>(view.Info!.Frame).Single().IsChecked == true, "Manual switch restored on reopening");
                        if (theme == "Standard" && !english) Save(main, "Manuell-Markiert-" + size);
                        view.MarkInstalled(artcraft, false); view.CloseInfo();
                        fake.Installed[tukui.Id] = null; Wait(view.RefreshAsync());
                        Assert(view.IsInstalled(tukui.Id) && !view.CanMark(tukui.Id) && view.InstallationStatus(tukui.Id).Contains(english ? "version unavailable" : "Version nicht ermittelbar"), "Detected installation without version remains installed");
                        fake.Installed.Remove(tukui.Id); Wait(view.RefreshAsync()); view.SetCategory("ai");
                        var buttonBounds = Bounds(view.InstallButton, view); Assert(buttonBounds.Bottom <= view.ActualHeight + 1, "Install footer contained");
                        if (theme == "Standard" && !english && size is 1 or 2) Save(main, size == 1 ? "Programme-Kompakt" : "Programme-Standard");
                        if (theme == "Standard" && !english && size == 4) Save(main, "Programme-Gross");
                        view.SetCategory("browser"); fake.Installed["firefox"] = "1.0"; fake.Installed["google-chrome"] = "1.0"; fake.Updates["google-chrome"] = "2.0";
                        Wait(view.RefreshAsync()); Pump();
                        var installedTile = view.Tiles.Single(x => (string?)x.Tag == "firefox");
                        var updateTile = view.Tiles.Single(x => (string?)x.Tag == "google-chrome");
                        Assert(UnifiedDialogAppearance.Children<CheckBox>(installedTile).Single().Visibility == Visibility.Collapsed, "Installed tile hides selection in every size/theme/language");
                        var updateBox = UnifiedDialogAppearance.Children<CheckBox>(updateTile).Single();
                        Assert(updateBox.Visibility == Visibility.Visible, "Update selection remains visible");
                        var checkBounds = Bounds(updateBox, updateTile); Assert(checkBounds.Left >= 1 && checkBounds.Top >= 1 && checkBounds.Right <= updateTile.ActualWidth && checkBounds.Bottom <= updateTile.ActualHeight, "Left update checkbox is fully inside tile");
                        Assert(UnifiedDialogAppearance.Children<Button>(updateTile).Count() == 1, "No separate update button in tile, only info action");
                        Assert(UnifiedDialogAppearance.Children<Canvas>(updateTile).All(x => !x.IsHitTestVisible), "Decorative band cannot intercept checkbox/info clicks");
                        foreach (var tile in new[] { installedTile, updateTile })
                        {
                            var statusText = UnifiedDialogAppearance.Children<TextBlock>(tile).Single(x => x.Text.Contains("UPDATE") || x.Text.Contains("INSTALLIERT") || x.Text.Contains("INSTALLED"));
                            var textBounds = Bounds(statusText, tile);
                            Assert(textBounds.Left >= 0 && textBounds.Top >= 0 && textBounds.Right <= tile.ActualWidth + 1 && textBounds.Bottom <= tile.ActualHeight + 1, "Entire band caption contained at actual size");
                        }
                        if (theme == "Standard" && !english && size is 1 or 4) Save(main, size == 1 ? "Statusbaender-Kompakt" : "Statusbaender-Gross");
                        fake.Installed.Clear(); fake.Updates.Clear(); Wait(view.RefreshAsync()); view.SetCategory("ai"); Pump();
                        var firefox = ProgramCatalog.Entries.First(x => x.Id == "firefox"); view.ShowInfo(firefox, view, false); Pump();
                        Wait(view.Info!.Loading, 35);
                        var root = (Grid)main.FindName("Root"); var card = root.Children.OfType<Border>().First(x => Panel.GetZIndex(x) == 5000);
                        var cardBounds = Bounds(card, root); Assert(cardBounds.Left >= 0 && cardBounds.Top >= 0 && cardBounds.Right <= root.ActualWidth + 1 && cardBounds.Bottom <= root.ActualHeight + 1, "Info card inside main window");
                        Assert(UnifiedDialogAppearance.Children<Button>(card).Count() == 3, "Only website and close actions; no preview tabs");
                        Assert(UnifiedDialogAppearance.Children<Image>(card).Count() == 1, "Info contains only the small app icon");
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(card).Any(x => x.Text == (english ? "What does the program do?" : "Was macht das Programm?")), "Localized text explanation replaces screenshot");
                        Assert(!Directory.Exists(Path.Combine(output, "Fixtures", theme, "App Previews")), "No persistent screenshot cache");
                        if (theme == "Standard" && !english && size == 1) Save(main, "Programminfo-Kompakt");
                        view.CloseInfo(); Assert(!root.Children.OfType<Border>().Any(x => Panel.GetZIndex(x) == 5000), "Info closes without leftover overlay");
                        var privacy = new PrivacyDownloadWindow(main, page, ThemePalette.For(theme), english, new PrivacyFake()) { ShowActivated = false }; privacy.Show(); Pump();
                        Assert(!privacy.StartButton.IsEnabled && !privacy.Working, "License gate initially closed"); privacy.Consent.IsChecked = true; Assert(privacy.StartButton.IsEnabled, "Explicit consent enables download");
                        privacy.Consent.ApplyTemplate(); var consentBorder = (Border)privacy.Consent.Template.FindName("Box", privacy.Consent);
                        Assert(consentBorder.ActualWidth == 20 && consentBorder.BorderBrush is SolidColorBrush, "O&O license checkbox has a visible themed border");
                        foreach (var button in UnifiedDialogAppearance.Children<Button>(privacy)) { var bounds = Bounds(button, privacy); Assert(bounds.Left >= -1 && bounds.Right <= privacy.ActualWidth + 1 && bounds.Bottom <= privacy.ActualHeight + 1, "O&O controls contained"); }
                        if (theme == "Standard" && !english && size == 1) Save(privacy, "OO-Lizenz-Kompakt"); privacy.Close();
                    }
                    view.SetCategory("all"); Wait(view.RefreshAsync()); fake.Installed["firefox"] = "1.0"; Wait(view.RefreshAsync()); Assert(view.VisibleEntries.Any(x => x.Id == "firefox"), "Confirmed installed app remains visible");
                    Border FirefoxTile() => view.Tiles.Single(x => (string?)x.Tag == "firefox");
                    Assert(UnifiedDialogAppearance.Children<CheckBox>(FirefoxTile()).Single().Visibility == Visibility.Collapsed, "Installed checkbox hidden");
                    view.Select("firefox", true); Assert(!view.Selected.Contains("firefox"), "Installed/current app cannot be selected programmatically");
                    fake.Updates["firefox"] = "2.0"; Wait(view.RefreshAsync());
                    Assert(UnifiedDialogAppearance.Children<CheckBox>(FirefoxTile()).Single().Visibility == Visibility.Visible, "Confirmed update reveals checkbox");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(FirefoxTile()).Any(x => x.Text.Contains("UPDATE")), "Confirmed update band");
                    view.Select("firefox", true); Wait(view.InstallAsync());
                    Assert(fake.Installed["firefox"] == "2.0" && !fake.Updates.ContainsKey("firefox"), "Shared button upgrades confirmed version");
                    Assert(UnifiedDialogAppearance.Children<CheckBox>(FirefoxTile()).Single().Visibility == Visibility.Collapsed, "Completed update hides checkbox again");
                    fake.Started.Clear();
                    var onlineEntry = new ProgramEntry("winget:fixture.paint", "Fixture Paint", ["online"], "Fixture.Paint", "Fixture", "Malt Bilder", "Paints images", "https://example.com/", "", null, null, "fixture");
                    fake.SearchResults = [onlineEntry, ProgramCatalog.Entries.First(x => x.Id == "firefox")];
                    view.SetQuery("paint"); Wait(view.SearchOnlineAsync());
                    Assert(view.VisibleEntries.Any(x => x.Id == onlineEntry.Id), "External software appears in same tile list");
                    view.Select(onlineEntry.Id, true); view.SetQuery(""); Assert(view.Selected.Contains(onlineEntry.Id), "External selection survives clearing search");
                    Wait(view.InstallAsync()); Assert(fake.Started.SequenceEqual(new[] { onlineEntry.Id }), "Shared bottom action includes discovered software");
                    fake.Started.Clear(); fake.Installed.Clear(); fake.Updates.Clear();
                    var oldSearch = new TaskCompletionSource<IReadOnlyList<ProgramEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    fake.SearchHandler = (q, _) => q == "old" ? oldSearch.Task : Task.FromResult<IReadOnlyList<ProgramEntry>>([onlineEntry]);
                    view.SetQuery("old"); var pendingSearch = view.SearchOnlineAsync(); view.SetQuery("paint"); Wait(view.SearchOnlineAsync());
                    oldSearch.SetResult([ProgramCatalog.Entries.First(x => x.Id == "firefox")]); Wait(pendingSearch);
                    Assert(view.VisibleEntries.Any(x => x.Id == onlineEntry.Id) && !view.VisibleEntries.Any(x => x.Id == "firefox"), "Stale search cannot replace newer results");
                    var clearedSearch = new TaskCompletionSource<IReadOnlyList<ProgramEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
                    fake.SearchHandler = (_, _) => clearedSearch.Task; view.SetQuery("clear pending"); var clearPending = view.SearchOnlineAsync();
                    view.ClearSearch(); clearedSearch.SetResult([onlineEntry]); Wait(clearPending);
                    Assert(view.VisibleEntries.Count == ProgramCatalog.Entries.Length && !view.VisibleEntries.Any(x => x.Id == onlineEntry.Id), "Cleared search rejects late online response");
                    fake.SearchHandler = null; view.SetQuery(""); Wait(view.RefreshAsync());
                    var first = ProgramCatalog.Entries.First(x => x.PackageId is not null && x.Id != "firefox"); var second = ProgramCatalog.Entries.First(x => x.PackageId is not null && x.Id != first.Id && x.Id != "firefox");
                    fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); view.Select(first.Id, true); view.Select(second.Id, true); var batch = view.InstallAsync(); Pump();
                    Assert(view.IsWorking && main.IsBusy && fake.Started.Count == 1, "Global busy lock during active installer"); view.RequestStop(); Pump(); Assert(!batch.IsCompleted && main.IsBusy, "Stop cannot unlock or kill a running installer");
                    fake.Pending.SetResult(0); Wait(batch); Assert(!main.IsBusy && !view.IsWorking && fake.Started.Count == 1, "Stop prevents next package; lock released after exit");
                    Assert(view.VisibleEntries.Any(x => x.Id == first.Id) && view.VisibleEntries.Any(x => x.Id == second.Id), "Installed and uninstalled entries both remain visible");
                    page.ShowManufacturerSoftware(false); Pump(); Assert(page.ManufacturerView?.IsVisible == true && !view.IsVisible, "Shared host returns to manufacturer view"); page.ShowPrograms(false); Pump(); Assert(view.IsVisible, "Catalog navigation restored");
                    var privacyFake = new PrivacyFake { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var downloadWindow = new PrivacyDownloadWindow(main, page, ThemePalette.For(theme), false, privacyFake); downloadWindow.Show(); downloadWindow.Consent.IsChecked = true;
                    var downloadTask = downloadWindow.StartAsync(); Pump(); Assert(main.IsBusy && privacyFake.Downloads == 1, "O&O holds global busy lock"); privacyFake.Pending.SetResult("Fixture.exe"); Wait(downloadTask); Assert(privacyFake.Opens == 1 && !main.IsBusy && !page.PrivacyWorking, "Verified download handoff completes and unlocks"); downloadWindow.Close();
                    Console.WriteLine("PASS programs / info / O&O: " + theme + " DE/EN, compact/standard/large, queue and process locks");
                }
                finally { main.Close(); Pump(); }
            }
            Console.WriteLine("PASS " + checks + " checks. Fixture installers only; no system changes."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}




