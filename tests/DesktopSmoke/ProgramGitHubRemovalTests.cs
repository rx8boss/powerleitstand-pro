using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramGitHubRemovalTests
{
    internal static int Run(App app, string output, bool logicOnly = false)
    {
        int checks = 0; void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var ventoy = ProgramCatalog.Entries.Single(e => e.Id == "extra-ventoy-ventoy");
        var dlss = ProgramCatalog.Entries.Single(e => e.Id == "dlss-swapper");
        Assert(ProgramVersions.GithubRepository(ventoy) == "ventoy/Ventoy", "Ventoy official repository");
        var live = ProgramLocalInventory.Read([ventoy], CancellationToken.None)[ventoy.Id];
        Assert(live.Version == File.ReadAllText(Path.Combine(Path.GetDirectoryName(live.Path!)!, "ventoy", "version")).Trim(), "Ventoy uses actual package version, not stale EXE resources");
        string fixture = Path.Combine(output, "Fixtures", "Ventoy"); Directory.CreateDirectory(Path.Combine(fixture, "ventoy"));
        string executable = Path.Combine(fixture, "Ventoy2Disk.exe"); File.Copy(live.Path!, executable, true);
        Assert(ProgramLocalInventory.PortableVersion(ventoy.Id, executable) is null, "Missing Ventoy manifest is unavailable, not EXE resource version");
        File.WriteAllText(Path.Combine(fixture, "ventoy", "version"), "1.0.0");
        File.WriteAllText(Path.Combine(fixture, "Ventoy2Disk.ini"), "user configuration");
        string zip = Path.Combine(output, "Ventoy-Test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] bytes) { using var stream = archive.CreateEntry("ventoy-2.0.0/" + name).Open(); stream.Write(bytes); }
            Add("Ventoy2Disk.exe", File.ReadAllBytes(executable)); Add("ventoy/version", Encoding.UTF8.GetBytes("2.0.0"));
            Add("boot/boot.img", [1, 2, 3]); Add("Ventoy2Disk.ini", Encoding.UTF8.GetBytes("must not overwrite"));
            Add("plugin/ventoy.json", Encoding.UTF8.GetBytes("must not overwrite"));
        }
        var update = new GitHubProgramUpdate(ventoy.Id, "ventoy/Ventoy", "1.0.0", "2.0.0", executable, "https://github.com/ventoy/Ventoy/releases/download/v2.0.0/ventoy-2.0.0-windows.zip", ProgramPortableUpdates.Hash(zip), new FileInfo(zip).Length, "VentoyArchive", ProgramPortableUpdates.Hash(executable));
        var payload = ProgramGitHubUpdates.VentoyPayload(zip, "2.0.0");
        Assert(payload.Count == 3 && payload.All(p => !p.Relative.EndsWith(".ini") && !p.Relative.StartsWith("plugin/")), "Ventoy payload preserves user settings and plugins");
        bool failed = false;
        try { ProgramGitHubUpdates.ApplyVentoyArchive(update, zip, Path.Combine(output, "Backups"), n => { if (n == 2) throw new IOException("fixture failure"); }); }
        catch (IOException) { failed = true; }
        Assert(failed && File.ReadAllText(Path.Combine(fixture, "ventoy", "version")) == "1.0.0", "Partial Ventoy replacement rolls back package version");
        ProgramGitHubUpdates.ApplyVentoyArchive(update, zip, Path.Combine(output, "Backups"));
        Assert(ProgramLocalInventory.PortableVersion(ventoy.Id, executable) == "2.0.0" && File.ReadAllText(Path.Combine(fixture, "Ventoy2Disk.ini")) == "user configuration", "Ventoy transaction verifies version and preserves configuration");
        string assetJson = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v2.0.0", assets = new[] { new { name = "DLSS.Swapper-2.0.0-installer.exe", digest = "sha256:" + new string('a', 64), size = 120, browser_download_url = "https://github.com/beeradmoore/dlss-swapper/releases/download/v2.0.0/DLSS.Swapper-2.0.0-installer.exe" } } });
        var dlssProof = new ProgramInstallation("1.0.0", "Registry", executable);
        var plan = ProgramGitHubUpdates.Parse(dlss, dlssProof, assetJson, "hash");
        Assert(plan is { Kind: "NSIS", Version: "2.0.0" }, "DLSS installed variant selects official installer");
        Assert(ProgramGitHubUpdates.Parse(dlss, dlssProof with { Version = "2.0.0" }, assetJson, "hash") is null,
            "Same GitHub version is not incorrectly offered as update");
        Assert(ProgramGitHubUpdates.Parse(dlss, dlssProof with { Version = "2.0.0" }, assetJson, "hash", reinstall: true) is { Version: "2.0.0" },
            "Explicit reinstallation may select same confirmed GitHub version");
        Assert(ProgramGitHubUpdates.Parse(dlss, dlssProof with { Version = "3.0.0" }, assetJson, "hash", reinstall: true) is null,
            "Reinstallation cannot silently downgrade GitHub installation");
        Assert(ProgramGitHubUpdates.Parse(dlss, dlssProof with { Source = "Portable" }, assetJson, "hash") is null, "DLSS portable never switched to installer");
        Assert(ProgramGitHubUpdates.Parse(dlss, dlssProof, assetJson.Replace("https://github.com/", "https://example.invalid/"), "hash") is null, "Foreign download rejected");
        Assert(ProgramGitHubUpdates.Parse(dlss, dlssProof, assetJson.Replace("sha256:", "md5:"), "hash") is null, "Missing verified digest rejected");
        Assert(ProgramGitHubUpdates.ProductMatches("dlss-swapper", "DLSS Swapper.exe", "DLSS Swapper", "DLSS Swapper")
            && !ProgramGitHubUpdates.ProductMatches("dlss-swapper", "uninstall.exe", "DLSS Swapper", "DLSS Swapper"), "Installed executable identity verified before direct update");
        var powershell = ProgramCatalog.Entries.Single(e => e.Id == "powershell");
        string psName = ProgramGitHubUpdates.AssetName(powershell.Id, executable, "2.0.0")!;
        string psJson = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v2.0.0", assets = new[] { new { name = psName, digest = "sha256:" + new string('b', 64), size = 120, browser_download_url = "https://github.com/PowerShell/PowerShell/releases/download/v2.0.0/" + psName } } });
        Assert(ProgramGitHubUpdates.Parse(powershell, dlssProof, psJson, "hash") is { Kind: "MSI" }, "PowerShell MSI architecture-specific recipe");
        Assert(ProgramLocalInventory.MatchesPortable("stability-matrix", "StabilityMatrix.Avalonia", "StabilityMatrix.Avalonia"), "Official Stability Matrix product metadata");
        string stabilityJson = JsonSerializer.Serialize(new { draft = false, prerelease = false, tag_name = "v2.0.0", assets = new[] { new { name = "StabilityMatrix-win-x64.zip", digest = "sha256:" + new string('c', 64), size = 120, browser_download_url = "https://github.com/LykosAI/StabilityMatrix/releases/download/v2.0.0/StabilityMatrix-win-x64.zip" } } });
        Assert(ProgramPortableUpdates.ParseGithub("stability-matrix", stabilityJson, executable, "1.0.0", "hash") is { ZipMember: "StabilityMatrix.exe", Version: "2.0.0" }, "Stability Matrix uses exact official x64 archive member");
        Assert(ProgramPortableUpdates.ParseGithub("stability-matrix", stabilityJson.Replace("win-x64", "linux-x64"), executable, "1.0.0", "hash") is null, "Stability Matrix rejects wrong platform");
        var inventory = new ProgramInventory(true, new Dictionary<string, string?> { [ventoy.Id] = "1.0.0" }, Updates: new Dictionary<string, string> { [ventoy.Id] = "2.0.0" }, Evidence: new Dictionary<string, ProgramInstallation> { [ventoy.Id] = new("1.0.0", "Portable", executable, false) }, GitHubPackages: new Dictionary<string, GitHubProgramUpdate> { [ventoy.Id] = update });
        Assert(inventory.HasUpdate(ventoy.Id), "Verified GitHub package selectable for incompatible WinGet portable source");
        Assert(!ProgramRemovalService.SafeLeaf("..") && !ProgramRemovalService.SafeLeaf("microsoft") && !ProgramRemovalService.SafeLeaf("A/B") && ProgramRemovalService.SafeLeaf("DLSS Swapper"), "Removal scope excludes parent paths/shared folders");
        foreach (string command in new[] { "cmd.exe /c del file", "powershell.exe -command test", "https://example.com/uninstall.exe", "C:\\missing\\uninstall.exe" })
        {
            bool denied = false; try { ProgramRemovalService.ParseCommand(command); } catch (IOException) { denied = true; }
            Assert(denied, "Unverified uninstaller rejected");
        }
        var msi = ProgramRemovalService.ParseCommand("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}");
        Assert(msi.Arguments == "/x {12345678-1234-1234-1234-123456789012} /norestart", "MSI uninstall targets exact product code");

        if (logicOnly) { Console.WriteLine("PASS " + checks + " GitHub/Ventoy/removal logic checks; fixture files only."); return 0; }

        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        var previousReader = ProgramRemovalDialog.Reader;
        ProgramRemovalDialog.Reader = _ => [new("fixture", "DLSS Swapper", "beeradmoore", "1.2.6.1", @"C:\Program Files\DLSS Swapper\DLSS Swapper.exe", "Registry"), new("portable", "Ventoy", "Ventoy", "1.1.17", @"C:\Desktop\ventoy\Ventoy2Disk.exe", "Portable"), new("unknown", "Test ohne Versionsangabe", "Fixture", null, null, "Registry", Protected: true)];
        try
        {
            foreach (string theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            {
                var main = new MainWindow(Path.Combine(output, "Settings", theme)) { ShowActivated = false };
                try
                {
                    main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Service = new ProgramCatalogTests.Fake(); view.Assets.AllowOnlineIcons = false;
                    var themeBox = (ComboBox)main.FindName("ThemeBox"); themeBox.SelectedItem = themeBox.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                    foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                        var task = ProgramRemovalDialog.ShowAsync(view); Pump();
                        var host = (Grid)main.FindName("Root"); var overlay = host.Children.OfType<Grid>().Single(g => g.Name == "InstalledProgramsOverlay");
                        var watch = Stopwatch.StartNew(); while (!UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "DLSS Swapper") && watch.Elapsed.TotalSeconds < 10) { Pump(); Thread.Sleep(5); }
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "DLSS Swapper"), "Installed program list loaded");
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text.Contains(english ? "Version unavailable" : "Version nicht ermittelbar")), "Missing version explicitly labelled");
                        Assert(UnifiedDialogAppearance.Children<ScrollViewer>(overlay).All(s => s.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden), "Hidden scrollbar");
                        foreach (var button in UnifiedDialogAppearance.Children<Button>(overlay))
                        {
                            var rect = button.TransformToAncestor(overlay).TransformBounds(new Rect(button.RenderSize));
                            Assert(rect.Left >= -1 && rect.Top >= -1 && rect.Right <= overlay.ActualWidth + 1 && rect.Bottom <= overlay.ActualHeight + 1, "Removal action fits " + theme + "/" + english + "/" + size);
                        }
                        Assert(!view.InstallButton.IsEnabled && !view.InstalledButton.IsEnabled, "Background program actions locked");
                        if (!english && theme is "Standard" or "Neon Pink")
                        {
                            var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(main);
                            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(Path.Combine(output, theme.Replace(' ', '-') + "-Installiert-" + size + ".png")); png.Save(stream);
                        }
                        var close = UnifiedDialogAppearance.Children<Button>(overlay).Single(b => b.Content is TextBlock t && t.Text == (english ? "Close" : "Schließen")); close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(task);
                        Assert(!view.SelectionOpen && !host.Children.Contains(overlay), "Dialog closes and releases selection lock");
                    }
                }
                finally { main.Close(); Pump(); }
            }
        }
        finally { ProgramRemovalDialog.Reader = previousReader; }
        Console.WriteLine("PASS " + checks + " GitHub/Ventoy/removal checks. No real installer/uninstaller executed; no user data or registry keys removed."); return 0;
    }

    internal static int Probe(string output) => Task.Run(async () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var catalog = ProgramCatalog.Entries.Where(e => e.Id is "dlss-swapper" or "extra-ventoy-ventoy").ToArray();
        var local = ProgramLocalInventory.Read(catalog, timeout.Token);
        foreach (var entry in catalog)
        {
            var proof = local[entry.Id]; var plan = await ProgramGitHubUpdates.ResolveAsync(entry, proof, timeout.Token);
            if (plan is null) throw new Exception("Live GitHub update route not verified: " + entry.Id);
            File.WriteAllText(Path.Combine(output, entry.Id + "-Updateplan.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS official update route: " + entry.Name + " " + plan.InstalledVersion + " -> " + plan.Version + " / " + plan.Kind);
            if (plan.Kind == "VentoyArchive")
            {
                var bytes = await CatalogWeb.ReadAsync(new Uri(plan.Url), ProgramGitHubUpdates.Hosts, 32 * 1024 * 1024, timeout.Token);
                string zip = Path.Combine(output, "Ventoy-Official.zip"); File.WriteAllBytes(zip, bytes);
                if (!ProgramPortableUpdates.Hash(zip).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw new Exception("Live checksum mismatch");
                var files = ProgramGitHubUpdates.VentoyPayload(zip, plan.Version);
                Console.WriteLine("PASS official Ventoy archive checksum and structure: " + files.Count + " files. Not applied to installed program.");
            }
        }
        var inventory = await new ProgramInstallService().InspectAsync(timeout.Token);
        foreach (var entry in catalog)
            if (!inventory.HasUpdate(entry.Id) || inventory.GitHubPackages?.ContainsKey(entry.Id) != true)
                throw new Exception("Live inventory did not promote the confirmed GitHub update: " + entry.Id);
        Console.WriteLine("PASS full live inventory exposes DLSS and Ventoy in executable update selection. No installation.");
        return 0;
    }).GetAwaiter().GetResult();
}
