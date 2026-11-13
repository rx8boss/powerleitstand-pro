using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramManagerTests
{
    internal static int Run(App app, string output)
    {
        int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var entries = ProgramCatalog.Entries; ProgramEntry Entry(string id) => entries.Single(e => e.Id == id);
        Assert(ProgramManagedInventory.Matches(Entry("7-zip"), "7zip", "https://www.7-zip.org"), "7zip exact alias and official homepage");
        Assert(ProgramManagedInventory.Matches(Entry("git"), "git", "https://gitforwindows.org"), "Git for Windows homepage alias");
        Assert(!ProgramManagedInventory.Matches(Entry("firefox"), "firefox-beta", "https://www.firefox.com"), "different channel not stable Firefox");
        Assert(!ProgramManagedInventory.Matches(Entry("7-zip"), "7zip", "https://7-zip.org.example.invalid"), "host suffix not official");
        Assert(!ProgramManagedInventory.Matches(Entry("git"), "git", "https://gitforwindows.org@example.invalid"), "userinfo cannot spoof publisher");
        var scoop = Path.Combine(output, "ScoopUser"); var global = Path.Combine(output, "ScoopGlobal"); var choco = Path.Combine(output, "Chocolatey");
        foreach (var dir in new[] { scoop, global }) { Directory.CreateDirectory(Path.Combine(dir, "shims")); Directory.CreateDirectory(Path.Combine(dir, "apps", "scoop")); File.WriteAllText(Path.Combine(dir, "shims", "scoop.ps1"), "throw 'Must never execute'"); }
        Directory.CreateDirectory(Path.Combine(choco, "bin")); File.WriteAllText(Path.Combine(choco, "bin", "choco.exe"), "Not executable");
        string Scoop(string root, string id, string dir, string version, string url)
        { string path = Path.Combine(root, "apps", id, dir); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "install.json"), "{\"architecture\":\"64bit\",\"bucket\":\"main\"}"); File.WriteAllText(Path.Combine(path, "manifest.json"), JsonSerializer.Serialize(new { version, homepage = url, post_install = "throw 'Do not run'" })); return path; }
        string Choco(string id, string version, string url)
        { string path = Path.Combine(choco, "lib", id); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, id + ".nupkg"), "Fixture registration"); File.WriteAllText(Path.Combine(path, id + ".nuspec"), $"<package xmlns='http://schemas.microsoft.com/packaging/2015/06/nuspec.xsd'><metadata><id>{id}</id><version>{version}</version><projectUrl>{url}</projectUrl></metadata></package>"); return path; }
        var seven = Scoop(scoop, "7zip", "current", "26.01", "https://www.7-zip.org");
        Scoop(global, "7zip", "current", "25.01", "https://www.7-zip.org");
        var old = Scoop(scoop, "git", "9.0", "9.0", "https://gitforwindows.org"); var latest = Scoop(scoop, "git", "2.54.0", "2.54.0", "https://gitforwindows.org");
        File.SetLastWriteTimeUtc(Path.Combine(old, "install.json"), new DateTime(2020, 1, 1)); File.SetLastWriteTimeUtc(Path.Combine(latest, "install.json"), new DateTime(2021, 1, 1));
        var vlc = Choco("vlc", "3.0.21.20260101", "https://www.videolan.org/vlc/");
        var pending = Choco("firefox", "158.0", "https://www.firefox.com"); File.WriteAllText(Path.Combine(pending, ".chocolateyPending"), "");
        Choco("git", "2.54.0", "https://gitforwindows.org"); // meta package, not an app identity
        var invalid = Choco("winrar", "7.22", "https://www.win-rar.com"); File.WriteAllText(Path.Combine(invalid, "winrar.nuspec"), "<!DOCTYPE package [<!ENTITY x SYSTEM 'file:///C:/never-read'>]><package><metadata><id>&x;</id></metadata></package>");
        var roots = new[] { new ProgramManagerRoot("Scoop", scoop, "User"), new("Scoop", global, "Machine"), new("Chocolatey", choco, "Machine") };
        var before = Directory.GetFiles(output, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        var snapshot = ProgramManagedInventory.Read(entries, CancellationToken.None, roots);
        Assert(snapshot.Managers.All(m => m.Present) && snapshot.Managers.Count == 3, "managers located without executing their entry points");
        Assert(snapshot.Matches["7-zip"].Count == 2 && snapshot.Matches["7-zip"].Select(c => c.Scope).Distinct().Count() == 2, "user and global copies retained independently");
        Assert(snapshot.Matches["git"].Single().PackageVersion == "2.54.0", "fallback uses latest completed installation, not highest version");
        Assert(snapshot.Matches["vlc"].Single().PackageVersion == "3.0.21.20260101", "Chocolatey wrapper version kept as package version");
        Assert(!snapshot.Matches.ContainsKey("firefox") && !snapshot.Matches.ContainsKey("winrar"), "pending package and external XML entity rejected");
        Assert(before.All(f => File.ReadAllBytes(f.Key).SequenceEqual(f.Value)) && before.Count == Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length, "inventory writes no package files");
        var installed = new Dictionary<string, ProgramInstallation> { ["7-zip"] = new("26.00", "Registry", seven) }; ProgramManagedInventory.Merge(installed, snapshot);
        Assert(installed["7-zip"].Version == "26.00" && installed["7-zip"].Source == "Registry" && installed["7-zip"].ManagedCopies!.Count == 2, "existing product proof not overwritten by package version");
        Assert(installed["vlc"].Version is null && installed["vlc"].Path is null && installed["vlc"].Source == "Chocolatey", "package directory never invented as application directory");
        var inventory = new ProgramInventory(true, installed.ToDictionary(p => p.Key, p => p.Value.Version), Updates: new Dictionary<string, string> { ["7-zip"] = "99.0", ["vlc"] = "99.0" }, UpdateCheckComplete: true, Evidence: installed);
        Assert(!inventory.HasUpdate("7-zip") && !inventory.HasUpdate("vlc"), "no cross-manager WinGet update");
        File.WriteAllText(Path.Combine(seven, "install.json"), "not json"); Assert(!ProgramManagedInventory.Read(entries, CancellationToken.None, [roots[0]]).Matches.ContainsKey("7-zip"), "broken install marker cannot prove registered package");
        File.WriteAllText(Path.Combine(seven, "install.json"), "{}");
        File.WriteAllText(Path.Combine(latest, "manifest.json"), "{}"); Assert(!ProgramManagedInventory.Read(entries, CancellationToken.None, [roots[0]]).Matches.ContainsKey("git"), "invalid current metadata does not silently select old version");
        File.WriteAllText(Path.Combine(latest, "manifest.json"), JsonSerializer.Serialize(new { version = "2.54.0", homepage = "https://gitforwindows.org" }));
        using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); bool observed = false; try { ProgramManagedInventory.Read(entries, canceled.Token, roots); } catch (OperationCanceledException) { observed = true; } Assert(observed, "cancel observed before scan"); }
        var real = ProgramManagedInventory.Read(entries, CancellationToken.None);
        Assert(real.Managers.Any(m => m.Manager == "Scoop" && m.Present), "real Scoop presence detected read-only");
        bool HasLocalScoopVersion(string id) => real.Matches.TryGetValue(id, out var copies) && copies.Any(c => c.Manager == "Scoop"
            && ProgramVersions.ConfirmedVersion(c.PackageVersion) is not null
            && (File.Exists(Path.Combine(c.PackageDirectory, "scoop-manifest.json")) || File.Exists(Path.Combine(c.PackageDirectory, "manifest.json"))));
        Assert(HasLocalScoopVersion("7-zip"), "real 7zip version and manifest detected");
        Assert(HasLocalScoopVersion("git"), "real Git version and manifest detected");
        File.WriteAllText(Path.Combine(output, "Lokaler-Abgleich-Privat.json"), JsonSerializer.Serialize(real, new JsonSerializerOptions { WriteIndented = true }));
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            var fake = new ProgramCatalogTests.Fake { InspectHandler = _ => Task.FromResult(inventory) }; view.Service = fake;
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(t => t.Tag is string).ToArray())
            foreach (bool english in new[] { false, true })
            foreach (int size in new[] { 1, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Wait(view.RefreshAsync());
                Assert(view.InstallationStatus("vlc").Contains(english ? "package registered" : "Paket registriert"), "package registration explicit instead of product version claim");
                Assert(!view.ShowManualMark("vlc") && !view.Inventory.HasUpdate("7-zip"), "known package not manual/WinGet update selection");
                view.ShowInfo(Entry("7-zip"), view, true); Pump();
                var texts = UnifiedDialogAppearance.Children<TextBlock>((Grid)main.FindName("Root")).ToArray();
                Assert(texts.Any(t => t.Text.Contains("Scoop · 7zip") && t.Text.Contains(english ? "package version" : "Paketversion")), "source and package version shown in existing info card");
                Assert(texts.Any(t => t.Text.Contains(english ? "Multiple sources detected" : "Mehrere Bezugswege erkannt")), "source conflict explained");
                if (!english && theme.Tag?.ToString() is "Standard" or "Neon Blau")
                { main.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, "Paketquelle-" + theme.Tag + "-" + size + ".png")); encoder.Save(file); }
                view.CloseInfo();
                var checkedCopy = snapshot.Matches["7-zip"][0] with
                { Location = new(seven, Path.Combine(seven, "7zFM.exe"), "Scoop package file", "User", "26.00") };
                var checkedInstalled = new Dictionary<string, ProgramInstallation>();
                ProgramManagedInventory.Merge(checkedInstalled, new(new Dictionary<string, IReadOnlyList<ProgramManagedCopy>> { ["7-zip"] = [checkedCopy] }, []));
                var checkedInventory = new ProgramInventory(true, checkedInstalled.ToDictionary(p => p.Key, p => p.Value.Version), UpdateCheckComplete: true, Evidence: checkedInstalled);
                fake.InspectHandler = _ => Task.FromResult(checkedInventory); Wait(view.RefreshAsync());
                Assert(view.InstallationStatus("7-zip").Contains(english ? "Installed through Scoop" : "Installiert über Scoop") && view.InstallationStatus("7-zip").Contains("26.00"), "Confirmed managed file version displayed as installed in app language");
                Assert(!view.ShowManualMark("7-zip") && !view.Inventory.HasUpdate("7-zip"), "Confirmed managed file does not enable manual mark or cross-manager update");
                view.ShowInfo(Entry("7-zip"), view, true); Pump();
                texts = UnifiedDialogAppearance.Children<TextBlock>((Grid)main.FindName("Root")).ToArray();
                Assert(texts.Any(t => t.Name == "ProgramInfoInstalledVersion" && t.Text == "26.00"), "Program version visible in existing info version row");
                Assert(texts.Any(t => t.Text.Contains(english ? "package version 26.01" : "Paketversion 26.01") && t.Text.Contains(english ? "Program version: 26.00" : "Programmversion: 26.00")), "Package and program versions explicitly distinct inside existing details area");
                view.CloseInfo();
                checkedInstalled.Clear(); ProgramManagedInventory.Merge(checkedInstalled, snapshot);
                var multipleInventory = new ProgramInventory(true, checkedInstalled.ToDictionary(p => p.Key, p => p.Value.Version), UpdateCheckComplete: true, Evidence: checkedInstalled);
                fake.InspectHandler = _ => Task.FromResult(multipleInventory); Wait(view.RefreshAsync());
                Assert(view.InstallationStatus("7-zip").Contains(english ? "Multiple package installations" : "Mehrere Paketinstallationen"), "Multiple package copies explicitly identified in each design and size");
                Assert(view.Inventory.Installed["7-zip"] is null && view.Inventory.Evidence!["7-zip"].Path is null, "No arbitrary primary version/path displayed for multiple managed copies");
                fake.InspectHandler = _ => Task.FromResult(inventory);
            }
            Console.WriteLine("PASS " + checks + " managed inventory / source isolation / UI checks; read-only live Scoop and synthetic Chocolatey, no manager executed."); return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
