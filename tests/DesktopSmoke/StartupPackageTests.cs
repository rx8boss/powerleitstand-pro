using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class StartupPackageTests
{
    internal static int RunUi(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var (live, _) = Task.Run(() => StartupPackagedApps.ReadAsync(timeout.Token)).GetAwaiter().GetResult();
        var sample = live.First(item => item.HasNativeTask == false && item.Entry is not null && StartupPackagedApps.CanResolveTarget(item.Id));
        var native = live.First(item => item.HasNativeTask == true && item.Entry is not null);
        var main = new MainWindow(Path.Combine(output, "WindowFixture"), new AppSettings { SizeIndex = 1 }, loadUsersForTests: false)
        { ShowActivated = false };
        try
        {
            main.Show();
            main.ShowWindowsUpdatesPage();
            void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Pump();
            var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            string fixture = Path.Combine(output, "UiStartup");
            page.StartupInventory = new StartupApps(@"Software\PowerLeitstand PRO\Tests\NoSuchRun",
                userFolder: fixture, savedFolder: Path.Combine(output, "UiBackup"),
                packages: (_, _) => Task.FromResult<(IReadOnlyList<StartupPackagedApp>, bool)>(([sample, native], false)));
            var opened = page.OpenStartupAsync();
            var limit = DateTime.UtcNow.AddSeconds(24);
            while (!opened.IsCompleted && DateTime.UtcNow < limit) { Pump(); Thread.Sleep(10); }
            if (!opened.IsCompleted) throw new TimeoutException("Autostart panel did not finish loading.");
            opened.GetAwaiter().GetResult();
            Pump();
            var overlay = UnifiedDialogAppearance.Children<Grid>(page).Single(grid => grid.Name == "StartupOverlay");
            var toggle = UnifiedDialogAppearance.Children<CheckBox>(overlay).Single();
            if (!toggle.IsVisible || toggle.IsChecked != false || !AutomationProperties.GetName(toggle).StartsWith(sample.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("The packaged app switch is not available.");
            var icons = UnifiedDialogAppearance.Children<Image>(overlay).ToArray();
            var iconLimit = DateTime.UtcNow.AddSeconds(14);
            while (icons.All(image => image.Source is null) && DateTime.UtcNow < iconLimit) { Pump(); Thread.Sleep(10); }
            if (icons.All(image => image.Source is null))
                throw new InvalidOperationException("No local Windows app logo was displayed.");
            string? uri = null;
            page.OpenExternalLink = link => uri = link.AbsoluteUri;
            if (UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(text => text.Text == native.Name))
                throw new InvalidOperationException("A native app without a switch was displayed.");
            UnifiedDialogAppearance.Children<Button>(overlay).Single(button => button.Content as string == "Windows-Autostart öffnen")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (uri != "ms-settings:startupapps") throw new InvalidOperationException("Wrong Windows settings target.");
            var image = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(main);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, "Autostart-Windows-Apps-Kompakt.png"));
            png.Save(file);
            Console.WriteLine("PASS packaged startup dialog, local app logo, toggle and Windows settings link.");
            return 0;
        }
        finally { main.Close(); }
    }

    internal static async Task<int> RunAsync(string output)
    {
        int checks = 0;
        void Assert(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks++; }
        string fixture = Directory.CreateDirectory(Path.Combine(output, Guid.NewGuid().ToString("N"))).FullName;
        string manifest = Path.Combine(fixture, "AppxManifest.xml");
        File.WriteAllText(manifest, """<Package xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"><uap5:Extension Category="windows.startupTask"/></Package>""");
        Assert(StartupPackagedApps.HasNativeTask(manifest), "Native startup declaration recognized");
        File.WriteAllText(manifest, """<Package><Extension Category="windows.appExecutionAlias"/></Package>""");
        Assert(!StartupPackagedApps.HasNativeTask(manifest), "An app execution alias is not a startup task");
        Assert(!StartupPackagedApps.ValidId(@"Test.Test_123!App ..\Other"), "Malformed identity rejected");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var (live, partial) = await StartupPackagedApps.ReadAsync(timeout.Token);
        Console.WriteLine($"Windows package inventory: {live.Count} launchable apps; partial={partial}; {live.Count(item => item.HasNativeTask == true)} native tasks.");
        var candidate = live.FirstOrDefault(item => item.HasNativeTask == false && StartupPackagedApps.CanResolveTarget(item.Id));
        Assert(candidate is not null, "The current Windows session supplies a launchable packaged app without its own task");
        var selected = candidate!;
        var native = new StartupPackagedApp("Fixture.Native_123!App", "Native task fixture", true);
        var unknown = new StartupPackagedApp("Fixture.Unknown_123!App", "Unknown task fixture", null);
        var missing = new StartupPackagedApp("Fixture.Missing_123!App", "Missing launch target fixture", false);
        var sources = new[] { selected, native, unknown, missing };
        string folder = Path.Combine(fixture, "Startup"), backup = Path.Combine(fixture, "Backup");
        string registry = @"Software\PowerLeitstand PRO\Tests\PackagedStartup-" + Guid.NewGuid().ToString("N");
        var inventory = new StartupApps(registry + @"\Run", registry + @"\Saved", registry + @"\Approved",
            folder, backup, registry + @"\FolderApproved", (_, id) => Task.FromResult<(
                IReadOnlyList<StartupPackagedApp>, bool)>((sources.Where(item => id is null || item.Id == id).ToArray(), false)));
        var (rows, incomplete) = await inventory.ReadWithPackagesAsync(default);
        Assert(!incomplete && rows.Count == 1, "Only the app with a verified editable startup entry is displayed");
        Assert(!rows.Any(row => row.Package?.Id == native.Id), "Native startup tasks without a direct switch are hidden");
        Assert(!rows.Any(row => row.Package?.Id == unknown.Id), "Unknown manifest cannot authorize a shortcut");
        Assert(!rows.Any(row => row.Package?.Id == missing.Id), "An unresolvable app is hidden");
        var initial = rows.Single(row => row.Package?.Id == selected.Id);
        Assert(initial is { Enabled: false, CanToggle: true }, "An eligible Windows app can be switched on");
        var enabled = await inventory.SetAndReadAsync(initial, true);
        string shortcut = Path.Combine(folder, selected.LinkName);
        Assert(enabled is { Enabled: true, CanToggle: true } && StartupPackagedApps.ReadLinkAppId(shortcut) == selected.Id,
            "A native Shell Link points to the exact packaged app");
        Assert((await inventory.ReadWithPackagesAsync(default)).Apps.Count == 1, "Managed shortcut does not duplicate the package row");
        var disabled = await inventory.SetAndReadAsync(enabled, false);
        Assert(disabled is { Enabled: false, CanToggle: true } && !File.Exists(shortcut), "Off moves the link out of the startup folder");
        enabled = await inventory.SetAndReadAsync(disabled, true);
        Assert(enabled.Enabled == true && File.Exists(shortcut), "On restores the verified link");
        disabled = await inventory.SetAndReadAsync(enabled, false);
        StartupPackagedApps.CreateLink(Path.Combine(folder, "External shortcut.lnk"), selected.Id);
        Assert(!(await inventory.ReadWithPackagesAsync(default)).Apps.Any(row => row.Package?.Id == selected.Id),
            "An external shortcut prevents a duplicate package row");
        bool refused = false;
        try { await inventory.SetAndReadAsync(disabled, true); }
        catch (IOException) { refused = true; }
        Assert(refused && !File.Exists(shortcut), "An outdated toggle cannot bypass duplicate checks");
        Console.WriteLine($"PASS {checks} packaged startup checks. Links created only inside the isolated test folder.");
        return 0;
    }
}
