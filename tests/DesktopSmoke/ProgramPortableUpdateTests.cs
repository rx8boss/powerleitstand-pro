using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramPortableUpdateTests
{
    private sealed class PortableUiService(PortableUpdate update, string payload) : IProgramInstallService, IProgramStepSource, IProgramDownloadSource
    {
        public event Action<ProgramStepEvent>? StepChanged;
        public event Action<ProgramDownload>? DownloadChanged;
        internal readonly TaskCompletionSource<int> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _complete;
        internal void Emit(ProgramStep step, ProgramStepState state) => StepChanged?.Invoke(new(step, state));
        internal void Bytes(long current, long total) => DownloadChanged?.Invoke(new(current, total));
        public Task<ProgramInventory> InspectAsync(CancellationToken token) => Task.FromResult(new ProgramInventory(true,
            new Dictionary<string, string?> { [update.Id] = _complete ? update.Version : update.InstalledVersion },
            Updates: _complete ? new Dictionary<string, string>() : new Dictionary<string, string> { [update.Id] = update.Version }, UpdateCheckComplete: true,
            Evidence: new Dictionary<string, ProgramInstallation> { [update.Id] = new(_complete ? update.Version : update.InstalledVersion, "Portable", update.Path, false) },
            PortableUpdates: _complete ? new Dictionary<string, PortableUpdate>() : new Dictionary<string, PortableUpdate> { [update.Id] = update }));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token) => Task.FromResult<ProgramPackage?>(new(entry.PackageId!, update.Version, update.Url, DateTime.UtcNow, VersionSource: "Publisher", PortableUpdate: update));
        public Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started) => throw new Exception("Portable source cannot invoke normal installer");
        public async Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started)
        {
            started(); Emit(ProgramStep.Download, ProgramStepState.Active); int result = await Gate.Task;
            if (result == 0) { File.Copy(payload, update.Path, true); _complete = true; }
            return result;
        }
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token) => Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public void OpenWebsite(ProgramEntry entry) => throw new Exception("Unexpected website fallback");
    }
    internal static int Ui(App app, string output, bool previewOnly = false)
    {
        int cases = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); cases++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "geek-uninstaller");
        string root = Path.GetDirectoryName(output)!, payload = Path.Combine(output, "manufacturer-geek.exe");
        string archive = Path.Combine(root, "Herstellerquellen", "geek.zip");
        var recipe = ProgramPortableUpdates.ParseGeek(File.ReadAllText(Path.Combine(root, "Herstellerquellen", "geek.yaml")), "", "1.5.5.185", "", "1.6.0.200")!;
        ProgramPortableUpdates.ExtractPayload(recipe, archive, payload);
        var main = new MainWindow(Path.Combine(output, "Settings")) { ShowActivated = false }; PortableUiService? service = null; ProgramCatalogView? view = null; Task? running = null;
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; view.ConfirmForTests = _ => true;
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(t => t.Tag is string && (!previewOnly || t.Tag.ToString() is "Standard" or "Neon Blau")).ToArray())
            foreach (bool english in previewOnly ? new[] { false } : new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                string path = Path.Combine(output, "target.exe"); File.WriteAllText(path, "old fixture");
                var update = recipe with { Path = path, OriginalHash = ProgramPortableUpdates.Hash(path) };
                service = new(update, payload); view.Service = service; Wait(view.RefreshAsync());
                Assert(view.UpdateEntries.Any(e => e.Id == entry.Id), "portable update in common update selection");
                var card = ProgramInfoCard.Open(view, entry, view, false)!; Wait(card.Loading);
                Assert(card.Frame.Width == (view.Compact ? 340 : 390), "approved info card width unchanged");
                var info = UnifiedDialogAppearance.Children<TextBlock>(card.Frame).Select(t => t.Text).ToArray();
                Assert(info.Any(t => t.Contains(english ? "Publisher download" : "Hersteller-Download")) && !info.Any(t => t.Contains(english ? "Different source / variant" : "Anderer Bezugsweg / andere Variante")), "confirmed source replaces misleading mismatch warning");
                if (!english && theme.Tag?.ToString() == "Standard") Save("Portable-Info-" + size);
                card.Close(); view.Select(entry.Id, true); running = view.InstallAsync(); Pump(); var panel = view.OperationPanel!;
                Assert(view.IsWorking && panel.StepState(entry.Id, ProgramStep.Check) == ProgramStepState.Done && panel.StepState(entry.Id, ProgramStep.Download) == ProgramStepState.Active, "portable queue advances from check into real reported download");
                service.Bytes(5242880, 10485760); Pump();
                Assert(panel.Progress.Value == 50 && panel.Progress.ShowPercentage && !panel.Progress.IsIndeterminate, "measured portable progress in existing bar");
                if (previewOnly)
                {
                    var timer = Stopwatch.StartNew(); while (panel.Progress.PercentageText != "50 %" && timer.Elapsed.TotalSeconds < 2) { Pump(); Thread.Sleep(5); }
                    Assert(panel.Progress.PercentageText == "50 %", "animated visible percentage reaches actual measurement");
                }
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t => t.Text.Contains(english ? "Publisher · Portable" : "Hersteller · Portable")), "portable route is not labeled WinGet");
                if (!english && theme.Tag?.ToString() is "Standard" or "Neon Blau") Save("Portable-Download-" + theme.Tag + "-" + size);
                service.Emit(ProgramStep.Download, ProgramStepState.Done); service.Emit(ProgramStep.Install, ProgramStepState.Active); Pump();
                Assert(panel.Progress.IsIndeterminate && !panel.Progress.ShowPercentage, "replacement clears stale download percentage");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t => t.Text == (english ? "Replace …" : "Austausch …")), "replacement stage labeled accurately");
                service.Emit(ProgramStep.Install, ProgramStepState.Done); service.Gate.SetResult(0); Wait(running);
                Assert(Enum.GetValues<ProgramStep>().All(s => panel.StepState(entry.Id, s) == ProgramStepState.Done), "all four portable stages confirmed after reading actual target version");
                Assert(!view.Inventory.HasUpdate(entry.Id) && !view.Selected.Contains(entry.Id) && !view.IsWorking, "new inventory removes completed update selection and unlocks");
                var host = (Grid)main.FindName("Root"); var bounds = panel.Frame.TransformToAncestor(host).TransformBounds(new Rect(panel.Frame.RenderSize));
                Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Bottom <= host.ActualHeight + 1 && bounds.Right <= host.ActualWidth + 1, "portable progress fits compact and large");
                if (!english && theme.Tag?.ToString() is "Standard" or "Neon Blau") Save("Portable-Fertig-" + theme.Tag + "-" + size);
                panel.Dispose(); Console.WriteLine("PASS portable UI " + theme.Tag + " " + english + " " + size);
            }
            Console.WriteLine("PASS portable UI checks: " + cases); return 0;
        }
        finally { service?.Gate.TrySetResult(0); if (running is { IsCompleted: false }) Wait(running); view?.OperationPanel?.Dispose(); main.Close(); Pump(); }
        void Save(string name)
        { Pump(); main.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file); }
    }
    internal static int Run(App app, string output)
    {
        int cases = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); cases++; }
        void Fails(Action action, string message) { try { action(); } catch (IOException) { cases++; return; } throw new Exception(message); }
        var fixture = Path.Combine(output, "PortableFixtures"); Directory.CreateDirectory(fixture);
        string source = Path.Combine(fixture, "program.exe"), payload = Path.Combine(fixture, "payload.exe"), backups = Path.Combine(fixture, "Backups");
        string config = Path.Combine(fixture, "settings.ini"); File.WriteAllText(config, "user settings");
        File.WriteAllText(source, "old program"); File.WriteAllText(payload, "new program");
        var update = new PortableUpdate("geek-uninstaller", source, "1.0", "2.0", "https://geekuninstaller.com/geek.zip", "A".PadLeft(64, 'A'), null, "geek.exe", ProgramPortableUpdates.Hash(source));
        string? Version(string path) => File.ReadAllText(path) == "old program" ? "1.0" : File.ReadAllText(path) == "new program" ? "2.0" : null;
        var backup = ProgramPortableUpdates.Replace(update, payload, backups, Version);
        Assert(File.ReadAllText(source) == "new program" && File.ReadAllText(Path.Combine(backup, "previous.exe")) == "old program", "real atomic replacement and backup");
        Assert(File.ReadAllText(config) == "user settings" && File.Exists(Path.Combine(backup, "backup.json")), "settings preserved and backup metadata saved");
        Assert(!Directory.GetFiles(fixture, ".PowerLeitstand-*").Any() && !Directory.GetFiles(fixture, "*.bak").Any(), "no replacement leftovers beside original");
        File.WriteAllText(source, "old program");
        Fails(() => ProgramPortableUpdates.Replace(update, payload, backups, Version, _ => throw new IOException("post-commit fixture failure")), "rollback required");
        Assert(File.ReadAllText(source) == "old program" && !Directory.GetFiles(fixture, ".PowerLeitstand-*").Any(), "rollback restores exact original and cleans stage");
        File.WriteAllText(source, "changed program");
        Fails(() => ProgramPortableUpdates.Replace(update, payload, backups, Version), "changed original must fail");
        Assert(File.ReadAllText(source) == "changed program", "changed file untouched");
        File.WriteAllText(source, "old program"); File.WriteAllText(payload, "wrong version");
        Fails(() => ProgramPortableUpdates.Replace(update, payload, backups, Version), "wrong new product version must fail");
        File.WriteAllText(payload, "new program");
        using (var locked = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None))
            Fails(() => ProgramPortableUpdates.Replace(update, payload, backups, Version), "locked program must not be replaced");
        Assert(File.ReadAllText(source) == "old program", "locked target preserved");
        string manifest = "PackageIdentifier: GeekUninstaller.GeekUninstaller\nPackageVersion: 2.0\nInstallerType: zip\nNestedInstallerType: portable\nNestedInstallerFiles:\n- RelativeFilePath: geek.exe\nInstallers:\n- Architecture: x86\n  InstallerUrl: https://geekuninstaller.com/geek.zip\n  InstallerSha256: " + new string('A', 64);
        Assert(ProgramPortableUpdates.ParseGeek(manifest, source, "1.0", update.OriginalHash, "2.0") is not null, "exact Geek manufacturer zip with external checksum");
        foreach (string invalid in new[] { manifest.Replace("geek.zip", "other.zip"), manifest.Replace("NestedInstallerType: portable", "NestedInstallerType: exe"), manifest.Replace("PackageVersion: 2.0", "PackageVersion: 3.0"), manifest + "\nInstallerSha256: " + new string('B', 64), manifest.Replace("GeekUninstaller.GeekUninstaller", "Other.Vendor") })
            Assert(ProgramPortableUpdates.ParseGeek(invalid, source, "1.0", update.OriginalHash, "2.0") is null, "reject unverified Geek source/identity/duplicate field");
        string Release(string id, string tag, bool pre = false, string? digest = null, string? url = null) => JsonSerializer.Serialize(new
        {
            draft = false, prerelease = pre, tag_name = tag,
            assets = new[] { new { name = id == "melonloader" ? "MelonLoader.Installer.exe" : "CompactGUI.exe", size = 1234,
                digest = digest ?? "sha256:" + new string('b', 64), browser_download_url = url ?? $"https://github.com/{(id == "melonloader" ? "LavaGang/MelonLoader.Installer" : "IridiumIO/CompactGUI")}/releases/download/{tag}/{(id == "melonloader" ? "MelonLoader.Installer.exe" : "CompactGUI.exe")}" } }
        });
        Assert(ProgramPortableUpdates.ParseGithub("melonloader", Release("melonloader", "4.4.0"), source, "4.3.0", update.OriginalHash)?.Version == "4.4.0", "installer project rather than game loader project");
        Assert(ProgramPortableUpdates.ParseGithub("melonloader", Release("melonloader", "4.5.0-beta", true), source, "4.3.0", "") is null, "no prerelease updates");
        Assert(ProgramPortableUpdates.ParseGithub("melonloader", Release("melonloader", "4.5.0", digest: "invalid"), source, "4.3.0", "") is null, "no missing digest workaround");
        Assert(ProgramPortableUpdates.ParseGithub("melonloader", Release("melonloader", "4.5.0", url: "https://example.org/installer.exe"), source, "4.3.0", "") is null, "no third-party release redirect");
        string compact = "[" + Release("compactgui", "v4.0.0-beta.12") + "," + Release("compactgui", "v3.6.0") + "]";
        Assert(ProgramPortableUpdates.ParseGithub("compactgui", compact, source, "3.5.0", "")?.Version == "3.6.0", "beta tag excluded even if wrongly marked stable");
        Assert(ProgramPortableUpdates.CompactVariant(compact, "3.6.0.0", new string('b', 64)), "exact normal CompactGUI variant digest");
        Assert(!ProgramPortableUpdates.CompactVariant(compact, "3.6.0", new string('c', 64)), "renamed mono binary cannot change distribution");
        foreach (var id in new[] { "geek-uninstaller", "melonloader", "compactgui", "rufus", "autoruns", "cpu-z", "gpu-z", "obs-studio", "tukui-client" })
        {
            Assert(ProgramPortableUpdates.Eligible(id, new("1.0", "Portable", source, false)), "known portable candidate " + id);
            Assert(!ProgramPortableUpdates.Eligible(id, new("1.0", "Registry", source)), "installed program cannot be converted " + id);
            Assert(!ProgramPortableUpdates.Eligible(id, new("1.0", "Portable", source, false, ManagedCopies: [new("Scoop", "app", "1.0", fixture, "User")])), "manager-owned file not replaced " + id);
        }
        foreach (var id in new[] { "firefox", "vlc", "artcraft-launcher", "teamspeak" })
            Assert(!ProgramPortableUpdates.Eligible(id, new("1.0", "Portable", source, false)), "unverified/multifile adapter remains manual " + id);
        var zipPath = Path.Combine(fixture, "good.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("geek.exe").Open())) writer.Write("fixture executable");
            using (var writer = new StreamWriter(zip.CreateEntry("../escape.exe").Open())) writer.Write("must never extract");
        }
        var zipUpdate = update with { Sha256 = ProgramPortableUpdates.Hash(zipPath) };
        var extracted = Path.Combine(fixture, "extracted.exe"); ProgramPortableUpdates.ExtractPayload(zipUpdate, zipPath, extracted);
        Assert(File.ReadAllText(extracted) == "fixture executable" && !File.Exists(Path.Combine(output, "escape.exe")), "only exact member extracted; zip traversal ignored");
        Fails(() => ProgramPortableUpdates.ExtractPayload(zipUpdate with { Sha256 = new string('0', 64) }, zipPath, Path.Combine(fixture, "bad.exe")), "mismatched checksum stops extraction");
        using (var input = new MemoryStream(new byte[100])) using (var target = new MemoryStream())
        {
            var progress = new List<ProgramDownload>(); ProgramPortableUpdates.CopyDownloadAsync(input, target, 100, progress.Add, CancellationToken.None).GetAwaiter().GetResult();
            Assert(progress[0].Percent == 0 && progress[^1].Received == 100 && progress[^1].Percent == 100, "measured byte progress reaches exact total");
        }
        using (var input = new MemoryStream(new byte[100])) using (var target = new MemoryStream())
        {
            var progress = new List<ProgramDownload>(); ProgramPortableUpdates.CopyDownloadAsync(input, target, null, progress.Add, CancellationToken.None).GetAwaiter().GetResult();
            Assert(progress.All(p => p.Percent is null) && target.Length == 100, "unknown length stays indeterminate");
        }
        foreach (long total in new long[] { 50, 200 }) using (var input = new MemoryStream(new byte[100])) using (var target = new MemoryStream())
            Fails(() => ProgramPortableUpdates.CopyDownloadAsync(input, target, total, _ => { }, CancellationToken.None).GetAwaiter().GetResult(), "too much/truncated download is failure");
        using (var cancellation = new CancellationTokenSource()) using (var input = new MemoryStream(new byte[100])) using (var target = new MemoryStream())
        {
            cancellation.Cancel(); bool cancelled = false;
            try { ProgramPortableUpdates.CopyDownloadAsync(input, target, 100, _ => { }, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "download cancellation does not commit payload");
        }
        var steps = new List<ProgramStepEvent>(); var measurements = new List<ProgramDownload>(); var tracker = new ProgramWinGetSteps(steps.Add, "Vendor.App", measurements.Add);
        tracker.Line("(1/2) Found Runtime [Vendor.Runtime] Version 1.0"); tracker.Line("Downloading https://example.org/runtime"); tracker.Line("Successfully verified installer hash"); tracker.Line("Successfully installed");
        Assert(steps.Count == 0, "dependency completion cannot count for selected app");
        tracker.Line("(2/2) Found App [Vendor.App] Version 2.0"); tracker.Line("Downloading https://example.org/app"); tracker.Line("  4,5 MB / 9,0 MB"); tracker.Line("Successfully verified installer hash"); tracker.Line("Starting package install..."); tracker.Line("Successfully installed");
        Assert(steps.Count == 4 && steps[^1].State == ProgramStepState.Done, "selected package resumes after dependency group");
        Assert(measurements.Count == 1 && measurements[0].Percent == 50, "WinGet observed size pair shown only for target download");
        foreach (string bad in new[] { "99 %", "10 MB / 5 MB", "999999999999999999999999999 GB / 9 GB", "opaque output" }) Assert(ProgramWinGetSteps.TryBytes(bad) is null, "no invented progress " + bad);
        var inventory = new ProgramInventory(true, new Dictionary<string, string?> { [update.Id] = "1.0" }, Updates: new Dictionary<string, string> { [update.Id] = "2.0" },
            Evidence: new Dictionary<string, ProgramInstallation> { [update.Id] = new("1.0", "Portable", source, false) });
        Assert(!inventory.HasUpdate(update.Id), "unrelated WinGet update remains blocked for portable source");
        Assert((inventory with { PortableUpdates = new Dictionary<string, PortableUpdate> { [update.Id] = update } }).HasUpdate(update.Id), "separate confirmed portable route selectable");
        var official = Path.Combine(Path.GetDirectoryName(output)!, "Herstellerquellen");
        if (Directory.Exists(official))
        {
            var geek = ProgramPortableUpdates.ParseGeek(File.ReadAllText(Path.Combine(official, "geek.yaml")), source, "1.5.5.185", update.OriginalHash, "1.6.0.200")!;
            string exe = Path.Combine(fixture, "manufacturer-geek.exe"); ProgramPortableUpdates.ExtractPayload(geek, Path.Combine(official, "geek.zip"), exe);
            Assert(ProgramPortableUpdates.SameVersion(ProgramPortableUpdates.FileVersion("geek-uninstaller", exe) ?? "", "1.6.0.200"), "real manufacturer download checksum and actual EXE product version");
            var melon = ProgramPortableUpdates.ParseGithub("melonloader", File.ReadAllText(Path.Combine(official, "melon.json")), source, "4.4.0", update.OriginalHash);
            Assert(melon?.Version == "4.4.0" && !ProgramVersions.IsNewer("4.4.0", melon.Version), "already current Melon installer is no false game-loader upgrade");
            string desktopGeek = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "geek.exe");
            if (File.Exists(desktopGeek))
            {
                string originalHash = ProgramPortableUpdates.Hash(desktopGeek);
                string privateGeek = Path.Combine(fixture, "private-geek.exe"); File.Copy(desktopGeek, privateGeek, true);
                var fixtureUpdater = new ProgramPortableUpdates((uri, _) => Task.FromResult(File.ReadAllBytes(Path.Combine(official,
                    uri.Host == "geekuninstaller.com" ? "geek.html" : "geek.yaml"))),
                    async (_, target, report, token) =>
                    {
                        using var archive = File.OpenRead(Path.Combine(official, "geek.zip")); using var destination = File.Open(target, FileMode.CreateNew);
                        await ProgramPortableUpdates.CopyDownloadAsync(archive, destination, archive.Length, report, token);
                    }, Path.Combine(fixture, "LifecycleBackups"));
                var resolved = Task.Run(() => fixtureUpdater.ResolveAsync("geek-uninstaller", new("1.5.5.185", "Portable", privateGeek, false), CancellationToken.None)).GetAwaiter().GetResult();
                Assert(resolved?.Version == "1.6.0.200", "full source recipe resolves against real old private executable");
                var lifecycle = new List<ProgramStepEvent>(); var bytes = new List<ProgramDownload>();
                Task.Run(() => fixtureUpdater.ApplyAsync(resolved!, lifecycle.Add, bytes.Add, CancellationToken.None)).GetAwaiter().GetResult();
                Assert(ProgramPortableUpdates.SameVersion(ProgramPortableUpdates.FileVersion("geek-uninstaller", privateGeek) ?? "", "1.6.0.200"), "full portable apply validates new real product metadata without launching");
                Assert(lifecycle.SequenceEqual(new[] { new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Active), new(ProgramStep.Download, ProgramStepState.Done), new(ProgramStep.Install, ProgramStepState.Active), new(ProgramStep.Install, ProgramStepState.Done) }), "portable actual lifecycle stages ordered and verified");
                Assert(bytes[^1].Percent == 100 && ProgramPortableUpdates.Hash(desktopGeek) == originalHash, "real archive transfer completes; personal desktop original unchanged");
                Assert(!Directory.GetDirectories(Path.Combine(fixture, "LifecycleBackups"), "Download-*").Any(), "download staging cleaned after complete lifecycle");
            }
        }
        Console.WriteLine("PASS portable transactions/sources/progress: " + cases);
        return 0;
    }
}
