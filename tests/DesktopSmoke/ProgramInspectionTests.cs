using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramInspectionTests
{
    private sealed class ProgressFake : IProgramInstallService, IProgramInspectionProgress
    {
        public event Action<ProgramInventory>? InventoryChanged;
        internal readonly TaskCompletionSource<ProgramInventory> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Emit(ProgramInventory inventory) => InventoryChanged?.Invoke(inventory);
        public Task<ProgramInventory> InspectAsync(CancellationToken token) => Pending.Task.WaitAsync(token);
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry e, CancellationToken t) => Task.FromResult<ProgramPackage?>(null);
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string q, CancellationToken t) => Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public Task<int> InstallAsync(ProgramEntry e, ProgramPackage p, Action s) => throw new Exception("No installer");
        public Task<int> UpgradeAsync(ProgramEntry e, ProgramPackage p, Action s) => throw new Exception("No installer");
        public void OpenWebsite(ProgramEntry e) { }
    }
    internal static int Run(App app, string output, bool captureImages = true)
    {
        int count = 0; void Assert(bool value, string text) { if (!value) throw new Exception(text); count++; }
        Task.Run(async () =>
        {
            var first = new TaskCompletionSource<ProgramInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exportGate = new TaskCompletionSource<WinGetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            int scans = 0;
            var service = new ProgramInstallService(async (start, token) =>
            {
                if (start.ArgumentList[0] == "search") return new(0, start.ArgumentList.Contains("second")
                    ? "Second  PerfVendor.Second  2.0" : "First  PerfVendor.First  2.0");
                if (start.ArgumentList[0] == "list") { Interlocked.Increment(ref scans); return new(0, "First  PerfVendor.First  1.0  2.0  winget\nSecond  PerfVendor.Second  1.0  2.0  winget"); }
                if (start.ArgumentList[0] == "export")
                {
                    Interlocked.Increment(ref scans); string file = start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1];
                    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new { Sources = new[] { new { SourceDetails = new { Name = "winget" }, Packages = new[] {
                        new { PackageIdentifier = "PerfVendor.First", Version = "1.0" }, new { PackageIdentifier = "PerfVendor.Second", Version = "1.0" } } } } }), token);
                    return await exportGate.Task.WaitAsync(token);
                }
                throw new Exception("Unexpected query");
            }, (_, _) => throw new Exception("No installer in read-only test"),
                localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation>()));
            var discovered = (await service.SearchAsync("first", CancellationToken.None)).Single();
            service.InventoryChanged += snapshot => first.TrySetResult(snapshot);
            var scan = service.InspectAsync(CancellationToken.None);
            var partial = await first.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Assert(partial.IsPartial && !scan.IsCompleted, "First local snapshot arrives before blocked WinGet export");
            exportGate.SetResult(new(0, "")); var complete = await scan;
            Assert(complete.Complete && complete.UpdateCheckComplete && !complete.IsPartial && complete.HasUpdate(discovered.Id), "Final snapshot confirms matching update");
            Assert(!partial.Installed.ContainsKey(discovered.Id), "Previously emitted snapshot is immutable");
            int prior = scans; var cached = await service.InspectForSearchAsync(CancellationToken.None);
            Assert(cached.FromCache && scans == prior && cached.CheckedUtc == complete.CheckedUtc, "Recent search reuses completed scan without new WinGet process");
            var second = (await service.SearchAsync("second", CancellationToken.None)).Single();
            var enriched = await service.InspectForSearchAsync(CancellationToken.None);
            Assert(enriched.Installed.ContainsKey(second.Id) && enriched.HasUpdate(second.Id) && scans == prior, "New search result mapped using cached export and upgrade identities");
            await service.InspectAsync(CancellationToken.None);
            Assert(scans == prior + 2, "Explicit check always performs a fresh inventory");
            var recentField = typeof(ProgramInstallService).GetField("_recentInventory", BindingFlags.NonPublic | BindingFlags.Instance)!;
            recentField.SetValue(service, complete with { CheckedUtc = DateTime.UtcNow.AddMinutes(-1) });
            int beforeExpiry = scans; var renewed = await service.InspectForSearchAsync(CancellationToken.None);
            Assert(!renewed.FromCache && scans == beforeExpiry + 2, "Expired session inventory is checked again");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool stopped = false;
            try { await service.InspectForSearchAsync(canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "Canceled search cannot return a stale cache");
        }).GetAwaiter().GetResult();
        Task.Run(async () =>
        {
            int githubCalls = 0;
            var githubService = new ProgramInstallService((_, _) => Task.FromResult(new WinGetResult(0, "")),
                (_, _) => throw new Exception("No installer in GitHub notice test"), github: (entry, _) =>
                {
                    Interlocked.Increment(ref githubCalls);
                    return Task.FromResult<ProgramPackage?>(new(entry.Id, null, "Official GitHub release", DateTime.UtcNow,
                        OnlineVersion: "99.0.0", VersionSource: "GitHub", CanInstall: false));
                });
            var powerToys = ProgramCatalog.Entries.Single(x => x.Id == "powertoys");
            var installed = new Dictionary<string, string?> { [powerToys.Id] = "1.0.0" };
            var notices = await githubService.ReadGithubNoticesAsync([powerToys], installed, CancellationToken.None);
            Assert(notices.GetValueOrDefault(powerToys.Id) == "99.0.0" && githubCalls == 1, "Newer stable GitHub release becomes an informational notice");
            notices = await githubService.ReadGithubNoticesAsync([powerToys], new Dictionary<string, string?> { [powerToys.Id] = null }, CancellationToken.None);
            Assert(notices.Count == 0, "Unknown local version cannot create a GitHub update notice");
        }).GetAwaiter().GetResult();
        Assert(ProgramCatalog.Entries.Length == 148 && ProgramCatalog.Entries.Count(e => e.InstalledNames is not null) == 44, "148 catalog entries including Filee and AdwCleaner; 44 explicit installation-name mappings");
        Assert(ProgramCatalog.Entries.All(e => e.Categories.All(ProgramCatalog.Categories.Contains)), "All category mappings are selectable");
        Assert(ProgramCatalog.Entries.Where(e => e.InstalledNames is not null).All(e => e.PreviewUrl is null && e.IconUrl == "" && e.Description(false).Length > 15 && e.Description(true).Length > 15), "Descriptions bilingual, no screenshot or fixed-icon downloads");
        var libre = ProgramCatalog.Entries.Single(e => e.PackageId == "TheDocumentFoundation.LibreOffice");
        Assert(ProgramLocalInventory.Matches(libre, "LibreOffice 24.2.1", "The Document Foundation")
            && !ProgramLocalInventory.Matches(libre, "LibreOffice Helper 24.2.1", "The Document Foundation")
            && !ProgramLocalInventory.Matches(libre, "LibreOffice 24.2.1", "Other Publisher"), "New aliases accept a version suffix, reject helpers and wrong publisher");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(3); } task.GetAwaiter().GetResult(); Pump(); }
        var progressMain = new MainWindow(Path.Combine(output, "ProgressSettings")) { ShowActivated = false };
        try
        {
            progressMain.Show(); progressMain.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)progressMain.FindName("WindowsUpdatesPage");
            var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; var fake = new ProgressFake(); view.Service = fake; Pump();
            var scan = view.RefreshAsync(); Pump(); Assert(view.HasLoadingOverlay, "Initial scan shows loading overlay");
            fake.Emit(new(false, new Dictionary<string, string?> { ["firefox"] = "1.0" }, IsPartial: true)); Pump();
            Assert(view.HasLoadingOverlay && !scan.IsCompleted && view.Inventory.Installed.ContainsKey("firefox"), "Partial inventory is tracked while the progress dialog and selection lock remain until completion");
            view.Select("google-chrome", true); Assert(view.Selected.Count == 0, "Selection remains locked while verification continues");
            fake.Pending.SetResult(new(true, new Dictionary<string, string?> { ["firefox"] = "1.0" }, UpdateCheckComplete: true)); Wait(scan);
            Assert(!view.HasLoadingOverlay, "Successful final result closes the initial loading overlay");
            fake.Emit(new(false, new Dictionary<string, string?> { ["stale"] = "1.0" }, IsPartial: true)); Pump();
            Assert(view.Inventory.Complete && !view.Inventory.Installed.ContainsKey("stale"), "Late partial callback cannot overwrite final result");
        }
        finally { progressMain.Close(); Pump(); }
        var githubMain = new MainWindow(Path.Combine(output, "GitHubNoticeSettings")) { ShowActivated = false };
        try
        {
            githubMain.Show(); githubMain.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)githubMain.FindName("WindowsUpdatesPage");
            var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            var powerToys = ProgramCatalog.Entries.Single(x => x.Id == "powertoys");
            var fake = new ProgramCatalogTests.Fake
            {
                InspectHandler = _ => Task.FromResult(new ProgramInventory(true,
                    new Dictionary<string, string?> { [powerToys.Id] = "1.0.0" },
                    Updates: new Dictionary<string, string>(), UpdateCheckComplete: true,
                    Evidence: new Dictionary<string, ProgramInstallation> { [powerToys.Id] = new("1.0.0", "Registry") },
                    GitHubUpdates: new Dictionary<string, string> { [powerToys.Id] = "99.0.0" })),
                ResolveHandler = (_, _) => Task.FromResult<ProgramPackage?>(new(powerToys.Id, null, "Official GitHub release", DateTime.UtcNow,
                    OnlineVersion: "99.0.0", VersionSource: "GitHub", CanInstall: false))
            };
            view.Service = fake; Wait(view.RefreshAsync()); view.SetCategory("all"); Pump();
            var tile = view.Tiles.Single(x => (string?)x.Tag == powerToys.Id);
            Assert(view.Inventory.HasUpdateNotice(powerToys.Id) && !view.Inventory.HasUpdate(powerToys.Id) && view.UpdateEntries.Any(e => e.Id == powerToys.Id),
                "GitHub notice is a candidate for selected verification, never an already authorized executable update");
            Assert(UnifiedDialogAppearance.Children<CheckBox>(tile).Single().Visibility == Visibility.Collapsed,
                "Informational GitHub notice cannot be selected for automatic installation");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(tile).Any(x => x.Text.Contains("UPDATE")),
                "GitHub notice is visible on the installed program tile");
            view.ShowInfo(powerToys, tile, false); Wait(view.Info!.Loading);
            var infoText = string.Join("\n", UnifiedDialogAppearance.Children<TextBlock>(view.Info.Frame).Select(x => x.Text));
            Assert(infoText.Contains("GitHub-Version erkannt") && infoText.Contains("Herstellerseite öffnen") && !infoText.Contains("Update auswählbar"),
                "GitHub information explains the safe publisher-page route");
        }
        finally { githubMain.Close(); Pump(); }
        foreach (string theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
        {
            var main = new MainWindow(Path.Combine(output, "Settings", theme)) { ShowActivated = false };
            try
            {
                main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
                var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; view.Service = new ProgramCatalogTests.Fake();
                var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(e => e.Tag?.ToString() == theme);
                Wait(view.RefreshAsync());
                foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump(); view.SetCategory("all");
                    var tile = view.Tiles[0]; page.SetLanguage(english); view.SetCategory("all"); Pump();
                    Assert(ReferenceEquals(tile, view.Tiles[0]), "Unchanged presentation retains tile objects " + theme);
                    view.MoreCategoriesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    var menu = view.AdditionalCategoriesFrame; var root = (FrameworkElement)view.Content;
                    var bounds = menu.TransformToAncestor(root).TransformBounds(new Rect(menu.RenderSize));
                    Assert(menu.Visibility == Visibility.Visible && bounds.Left >= 0 && bounds.Right <= root.ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= root.ActualHeight + 1, "Additional category menu contained " + theme);
                    Assert(menu.Background is SolidColorBrush brush && brush.Color.A == 255 && brush.Opacity == 1, "Category menu opaque, including Neon " + theme);
                    foreach (var category in ProgramCatalog.AdditionalCategories)
                    { view.SetCategory(category); Pump(); Assert(view.VisibleEntries.Count > 0 && view.VisibleEntries.All(e => e.Categories.Contains(category)), "Additional category filters correctly " + category); }
                    view.SetCategory("office"); view.MoreCategoriesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    if (captureImages && theme == "Standard" && !english)
                    {
                        var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(main);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(Path.Combine(output, "Kategorien-Umgesetzt-" + size + ".png")); png.Save(file);
                    }
                    view.SetQuery("LibreOffice"); Pump();
                    var tabs = (Grid)typeof(ProgramCatalogView).GetField("_tabs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
                    Assert(menu.Visibility == Visibility.Collapsed && tabs.Visibility == Visibility.Collapsed, "Search hides all categories and closes menu"); view.ClearSearch();
                }
            }
            finally { main.Close(); Pump(); }
        }
        Console.WriteLine("PASS " + count + " inspection/cache/catalog/category checks; no real installer executed."); return 0;
    }
}
