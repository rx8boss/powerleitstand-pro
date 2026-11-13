using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramVlcNormalTests
{
    internal static async Task<int> Live(string output)
    {
        Directory.CreateDirectory(output);
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
        var service = new ProgramInstallService(install: (_, _) => throw new Exception("Installer forbidden"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var result = await service.ResolveAsync(entry, timeout.Token);
        File.WriteAllText(Path.Combine(output, "VideoLAN-Live.json"), System.Text.Json.JsonSerializer.Serialize(result));
        var version = result?.Version ?? result?.OnlineVersion;
        if (version is null || result?.VersionSource is not ("Publisher" or "WinGet")) throw new Exception("Live normal VLC version source unconfirmed");
        Console.WriteLine("PASS live app VLC version query: " + version + " / " + result.VersionSource + "; metadata only, no executable download or installer"); return 0;
    }
    internal static int Run(App app, string output)
    {
        Directory.CreateDirectory(output); int checks = 0, installers = 0;
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
        var copies = ProgramVlcReinstallation.Read();
        Assert(copies.Count >= 2, "local test requires two real registered VLC copies; reads metadata only");
        var evidence = ProgramLocalInventory.Read([entry], default, includeManaged: false);
        Console.WriteLine("VLC: local registry and executable detection completed.");
        Assert(evidence["vlc"] is { Source: "Registry", Path: not null, Version: not null }, "normal website-installed VLC is recognized by the app's real local reader");
        File.WriteAllText(Path.Combine(output, "Local-VLC.json"), System.Text.Json.JsonSerializer.Serialize(copies));
        Assert(!ProgramCatalog.Entries.Any(e => e.Id == "vlc-zip") && ProgramCatalog.Entries.Count(e => e.Id == "vlc") == 1, "only the normal VLC catalog entry remains");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        var main = new MainWindow(Path.Combine(output, "Fixture"), new AppSettings { SizeIndex = 1 }) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background); main.UpdateLayout(); }
        void Until(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done() && watch.Elapsed.TotalSeconds < 40) { Pump(); Thread.Sleep(3); } if (!done()) throw new TimeoutException("VLC normal UI"); Pump(); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            var service = new ProgramInstallService(query: (start, _) => Task.FromResult(new WinGetResult(0, start.ArgumentList[0] == "show" ? "Found VLC [VideoLAN.VLC]\nVersion: " + evidence["vlc"].Version + "\n" : "{\"Sources\":[]}")),
                install: (_, _) => { installers++; throw new Exception("Real installer forbidden"); }, localReader: (_, _) => Task.FromResult(new Dictionary<string,ProgramInstallation>(evidence)),
                locationsFile: view.Assets.LocationsFile, vlcCopies: () => copies, vlcManifest: (_, _, _) => Task.FromResult<string?>(new string('A',64)));
            view.Service = service; Wait(view.RefreshAsync());
            Console.WriteLine("VLC: fixture inventory completed.");
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(i => i.Tag is string).ToArray())
            foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                Console.WriteLine("VLC UI: " + theme.Tag + " / " + english + " / " + size);
                var info = ProgramInfoCard.Open(view, entry, view, false)!; Wait(info.Loading);
                var picker = UnifiedDialogAppearance.Children<ComboBox>(info.Frame).Single(c => c.Name == "ProgramInfoVlcCopy");
                Assert(picker.IsVisible && picker.Items.Count == copies.Count && picker.SelectedItem is ComboBoxItem { Tag: VlcRegisteredCopy }, "copy picker shows real registered architectures and the active copy");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Any(t => t.Name == "ProgramInfoInstalledVersion" && t.Text == evidence["vlc"].Version), "real installed version displayed");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Any(t => t.Name == "ProgramInfoAvailableVersion" && t.Text == evidence["vlc"].Version), "WinGet version remains available on normal VLC");
                var reinstall = UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b => b.Name == "ProgramInfoStatusReinstall");
                var rect = reinstall.TransformToAncestor(main).TransformBounds(new Rect(reinstall.RenderSize));
                Assert(reinstall.IsVisible && reinstall.IsEnabled && rect.Bottom <= main.ActualHeight && rect.Top >= 0, "normal reinstall action remains visible and fits compact/large");
                if (theme.Tag?.ToString() == "Standard" && !english)
                {
                    var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96,96,PixelFormats.Pbgra32); bitmap.Render(main);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output,"VLC-" + size + ".png")); png.Save(file);
                }
                info.Close();
            }
            page.SetLanguage(false); var selected = ProgramInfoCard.Open(view, entry, view, false)!; Wait(selected.Loading);
            var choose = UnifiedDialogAppearance.Children<ComboBox>(selected.Frame).Single(c => c.Name == "ProgramInfoVlcCopy");
            var other = copies.First(c => c.Executable != evidence["vlc"].Path && c.Root != evidence["vlc"].Path);
            choose.SelectedItem = choose.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, other));
            Until(() => !view.SelectionOpen && view.Inventory.Evidence?.GetValueOrDefault("vlc")?.Path == other.Executable);
            Assert(view.Inventory.Installed["vlc"] == other.Version, "selecting a copy uses that copy's own installed version");
            view.CloseInfo(); Wait(view.RefreshAsync());
            Assert(view.Inventory.Evidence?["vlc"].Path == other.Executable && view.Inventory.Installed["vlc"] == other.Version, "private saved copy choice survives fresh inventory");
            Assert(installers == 0, "inspection and copy selection never execute an installer or modify VLC");
        }
        catch (Exception error) { Console.Error.WriteLine(error); throw; }
        finally { if (!main.IsBusy) main.Close(); }
        Console.WriteLine($"PASS {checks} normal VLC local detection, copy picker, saved path/version, DE/EN compact/large all-theme UI checks; fixture WinGet replies, no real installer"); return 0;
    }
}
