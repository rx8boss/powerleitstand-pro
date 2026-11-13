using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramOnlineTests
{
    internal static int Probe() => Task.Run(async () =>
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var local = await ProgramVersions.ReadStorePackagesAsync(limit.Token);
        Console.WriteLine("PASS local current-user Windows package query; mapped installed entries: " + local.Count);
        var entry = ProgramCatalog.Entries.Single(x => x.Id == "dlss-swapper");
        var release = await ProgramVersions.GithubAsync(entry, limit.Token);
        if (release?.OnlineVersion is null || release.CanInstall) throw new Exception("GitHub metadata was not confirmed as non-installable online information");
        Console.WriteLine("PASS official GitHub release metadata: " + release.OnlineVersion + "; no installation evidence or installer execution");
        return 0;
    }).GetAwaiter().GetResult();
    internal static int Run(App app, string output, bool retest = false)
    {
        int checks = 0; var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var stop = Stopwatch.StartNew(); while (!task.IsCompleted && stop.Elapsed.TotalSeconds < 40) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException("Fixture operation timed out"); task.GetAwaiter().GetResult(); Pump(); }
        void Save(Window window, string name) { window.UpdateLayout(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream); }
        try
        {
            var store = ProgramVersions.ParseStorePackages("[{\"PackageFamilyName\":\"OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0\",\"Version\":\"1.2.3.4\"},{\"PackageFamilyName\":\"OpenAI.ChatGPT-Desktop_wrongpublisher\",\"Version\":\"9.9.9.9\"}]");
            Assert(store.Count == 1 && store["chatgpt"] == "1.2.3.4", "Store identity includes publisher hash; lookalike cannot replace installed version");
            Assert(ProgramVersions.ParseReleaseVersion("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v1.2.3\"}") == "1.2.3", "Stable GitHub tag becomes online version");
            Assert(ProgramVersions.ParseReleaseVersion("{\"draft\":false,\"prerelease\":true,\"tag_name\":\"v9.0.0\"}") is null, "Prerelease excluded");
            Assert(ProgramVersions.ParseReleaseVersion("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"nightly\"}") is null, "Unconfirmed release label excluded");
            Assert(ProgramVersions.ConfirmedVersion("Unknown") is null && ProgramVersions.ConfirmedVersion("1.2.3") is not null, "Unknown is not a confirmed version");
            Assert(ProgramVersions.GithubRepository(ProgramCatalog.Entries.First(x => x.Id == "chatgpt")) is null, "Non-GitHub product not mapped to a guessed repository");
            OnlineConnection.NetworkForTests = () => false;
            try { OnlineConnection.RequireNetwork(); throw new Exception("Offline network action allowed"); } catch (OnlineConnectionException e) { Assert(e.Offline && UiText.Error(e, true).StartsWith("No internet"), "Offline message translated"); }
            try { Wait(CatalogWeb.ReadAsync(new Uri("https://example.com/"), new HashSet<string> { "example.com" }, 1024, CancellationToken.None)); throw new Exception("Offline HTTP allowed"); } catch (OnlineConnectionException) { checks++; }
            void Offline(Task action, string name) { try { Wait(action); throw new Exception("Offline action started: " + name); } catch (OnlineConnectionException) { checks++; } }
            Offline(new WindowsUpdateService().SearchAsync(false, CancellationToken.None), "Windows update");
            Offline(UpdateService.CheckAsync(), "GitHub app update");
            Offline(new PrivacyDownloadService(Path.Combine(output, "OfflineDownloads")).DownloadAsync(true, new Progress<PrivacyProgress>(), CancellationToken.None), "O&O download");
            Offline(WinGetService.InstallProcessAsync(new ProcessStartInfo("DO_NOT_START.exe"), () => throw new Exception("Installer started offline")), "installer process");
            try { new ManufacturerService().OpenWebsite(ManufacturerCatalog.Apps[0]); throw new Exception("Browser started offline"); } catch (OnlineConnectionException) { checks++; }
            Assert(!Directory.Exists(Path.Combine(output, "OfflineDownloads")), "Offline download creates no download file");
            Assert(!UiText.Error(new HttpRequestException("Denied", null, HttpStatusCode.Forbidden), false).Contains("Keine Internetverbindung"), "HTTP 403 is provider error, not internet loss");
            OnlineConnection.NetworkForTests = () => true;
            foreach (var theme in (retest ? new[] { "Standard", "Neon Blau", "Neon Pink", "Neon Grün" } : new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" }))
            {
                var main = new MainWindow(Path.Combine(output, "Fixtures", theme)) { ShowActivated = false };
                try
                {
                    main.Show(); var themeBox = (ComboBox)main.FindName("ThemeBox"); themeBox.SelectedItem = themeBox.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                    main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); var fake = new ProgramCatalogTests.Fake(); view.Service = fake; view.Assets.AllowOnlineIcons = false;
                    var entry = ProgramCatalog.Entries.Single(x => x.Id == "artcraft-launcher");
                    foreach (bool english in new[] { false, true }) foreach (int size in (retest ? new[] { 1, 4 } : new[] { 1, 2, 4 }))
                    {
                        typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english); typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump();
                        var pending = new TaskCompletionSource<ProgramPackage?>(TaskCreationOptions.RunContinuationsAsynchronously);
                        fake.ResolveHandler = (_, token) => pending.Task.WaitAsync(token); view.ShowInfo(entry, view, false); Pump();
                        var card = view.Info!;
                        Assert(UnifiedDialogAppearance.Children<CheckBox>(card.Frame).Single().Parent is Grid row && row.Visibility == Visibility.Collapsed, "Manual switch hidden until version check completes");
                        pending.SetResult(new ProgramPackage(entry.Id, null, "Fixture GitHub release", DateTime.UtcNow, OnlineVersion: "2.3.4", VersionSource: "GitHub", CanInstall: false)); Wait(card.Loading);
                        Assert(!view.CanMark(entry.Id) && !view.ShowManualMark(entry.Id), "Confirmed available version hides manual switch despite unknown installation");
                        Assert(!view.Inventory.Installed.ContainsKey(entry.Id), "Online release does not fabricate local installation"); view.CloseInfo();
                        fake.ResolveHandler = (_, _) => Task.FromResult<ProgramPackage?>(null); view.ShowInfo(entry, view, false); Wait(view.Info!.Loading);
                        Assert(view.ShowManualMark(entry.Id) && view.CanMark(entry.Id), "Unknown installation and unknown version enable manual confirmation");
                        if (theme.StartsWith("Neon"))
                        {
                            var frames = (List<(AdornerLayer, NeonFrameAdorner)>)typeof(MainWindow).GetField("_neonFrames", flags)!.GetValue(main)!;
                            Assert(frames.Count > 0 && frames.All(x => x.Item2.Opacity == 0), "Neon adorners cannot paint over an open hover card");
                            foreach (var item in frames) item.Item2.Strength = 1;
                            if (!english && size is 1 or 4) Save(main, theme.Replace(' ', '-') + "-Hover-" + size);
                        }
                        view.CloseInfo();
                        if (theme.StartsWith("Neon"))
                        {
                            var frames = (List<(AdornerLayer, NeonFrameAdorner)>)typeof(MainWindow).GetField("_neonFrames", flags)!.GetValue(main)!;
                            Assert(frames.All(x => x.Item2.Opacity == 1), "Closing hover restores neon frames");
                        }
                        view.ShowInfo(entry, view, false); Wait(view.Info!.Loading); view.ConnectionChanged(false); Pump();
                        Assert(view.Info?.IsOpen != true && view.HasOverlay, "Connection loss closes an existing info card instead of stacking unreadable overlays");
                        var continueLocal = UnifiedDialogAppearance.Children<Button>(view).Single(x => x.Content?.ToString() == (english ? "Continue offline" : "Offline weiter"));
                        continueLocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        var inventory = new TaskCompletionSource<ProgramInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
                        fake.InspectHandler = token => inventory.Task.WaitAsync(token); var refresh = view.RefreshAsync(); Pump();
                        Assert(view.HasLoadingOverlay, "Inspection shows central loading overlay");
                        var loadingLayer = (Grid)typeof(ProgramCatalogView).GetField("_connectionLayer", flags)!.GetValue(view)!;
                        var loadingCard = (Border)loadingLayer.Children[0];
                        var stop = UnifiedDialogAppearance.Children<Button>(loadingCard).Single();
                        Assert(stop.Width == 220 && stop.HorizontalAlignment == HorizontalAlignment.Center && stop.Height == 34, "Approved narrow centered loading action");
                        Assert(stop.Style == main.FindResource("UnifiedDialogDanger") && stop.Content is TextBlock { Foreground: SolidColorBrush foreground } && foreground.Color == Colors.White, "Red app-style stop action with readable white text");
                        var stopBounds = stop.TransformToAncestor(loadingCard).TransformBounds(new Rect(stop.RenderSize));
                        Assert(stopBounds.Left >= 0 && stopBounds.Right <= loadingCard.ActualWidth + 1 && stopBounds.Bottom <= loadingCard.ActualHeight + 1, "Stop action fits inside loading card");
                        Assert(loadingCard.ActualHeight >= Math.Min(190, loadingLayer.ActualHeight - 16) - 1, "Approved loading card height in available space");
                        if (!english && theme == "Standard" && size == 1)
                        {
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 4; Pump();
                            Assert(view.HasLoadingOverlay && loadingCard.ActualWidth == 600, "Pending loader expands when main window grows");
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump();
                            Assert(view.HasLoadingOverlay && loadingCard.ActualWidth == 560, "Pending loader fits compact after resize");
                        }
                        if (!english && size is 1 or 4 && (theme == "Standard" || theme == "Neon Pink")) Save(main, theme.Replace(' ', '-') + "-Laden-" + size);
                        view.ConnectionChanged(false); Pump(); Wait(refresh);
                        Assert(view.HasOverlay && !view.HasLoadingOverlay, "Connection loss replaces spinner with offline state");
                        foreach (var button in UnifiedDialogAppearance.Children<Button>(view).Where(x => x.Content?.ToString() == (english ? "Continue offline" : "Offline weiter")))
                        {
                            var bounds = button.TransformToAncestor(view).TransformBounds(new Rect(button.RenderSize));
                            Assert(bounds.Top >= 0 && bounds.Bottom <= view.ActualHeight + 1 && bounds.Right <= view.ActualWidth + 1, "Offline action visible even in compact");
                        }
                        if (!english && size is 1 or 4 && (theme == "Standard" || theme == "Neon Pink")) Save(main, theme.Replace(' ', '-') + "-Offline-" + size);
                        var local = UnifiedDialogAppearance.Children<Button>(view).Single(x => x.Content?.ToString() == (english ? "Continue offline" : "Offline weiter")); local.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        Assert(!view.HasOverlay, "Offline continuation restores local catalog");
                        inventory.TrySetResult(new ProgramInventory(true, new Dictionary<string, string?> { [entry.Id] = "9.9" })); Pump(); Assert(!view.Inventory.Installed.ContainsKey(entry.Id), "Cancelled inventory response cannot replace retained results");
                        fake.InspectHandler = null; fake.ResolveHandler = null; Wait(view.RefreshAsync());
                        Assert(!view.HasLoadingOverlay && !view.HasOverlay, "Successful retry clears overlay");
                        fake.Installed[entry.Id] = null; Wait(view.RefreshAsync()); Assert(!view.ShowManualMark(entry.Id) && view.IsInstalled(entry.Id), "Known installation hides switch even without version"); fake.Installed.Clear(); Wait(view.RefreshAsync());
                        var cancelPending = new TaskCompletionSource<ProgramInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
                        fake.InspectHandler = token => cancelPending.Task.WaitAsync(token); var cancelling = view.RefreshAsync(); Pump();
                        loadingCard = (Border)loadingLayer.Children[0];
                        UnifiedDialogAppearance.Children<Button>(loadingCard).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(cancelling);
                        Assert(!view.HasOverlay && !view.HasLoadingOverlay, "Approved stop action cancels inspection and restores catalog");
                        fake.InspectHandler = null;
                    }
                    Console.WriteLine("PASS online states / manual gating / neon: " + theme + " / DE+EN / compact+standard+large");
                }
                finally { main.Close(); Pump(); }
            }
            Console.WriteLine("PASS " + checks + " focused online checks. No physical network changes or installers."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { OnlineConnection.NetworkForTests = null; }
    }
}
