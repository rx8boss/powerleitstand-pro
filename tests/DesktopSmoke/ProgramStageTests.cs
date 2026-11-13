using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramStageTests
{
    private sealed class Fake : IProgramInstallService, IProgramStepSource, IProgramDownloadSource
    {
        public event Action<ProgramStepEvent>? StepChanged;
        public event Action<ProgramDownload>? DownloadChanged;
        internal void Download(long received, long? total) => DownloadChanged?.Invoke(new(received, total));
        internal readonly Dictionary<string, string?> Installed = [];
        internal bool Detect = true;
        internal Dictionary<string, ProgramInstallation>? Evidence;
        internal readonly List<string> Started = [];
        internal readonly Dictionary<string, TaskCompletionSource<int>> Gates = [];
        internal void Emit(ProgramStep step, ProgramStepState state) => StepChanged?.Invoke(new(step, state));
        public Task<ProgramInventory> InspectAsync(CancellationToken token) => Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?>(Installed), Evidence: Evidence, InstalledSources: Evidence?.ToDictionary(p => p.Key, p => p.Value.Source)));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry e, CancellationToken t) => Task.FromResult<ProgramPackage?>(e.PackageId is null ? null : new(e.PackageId, "1.2.3", "Fixture", DateTime.UtcNow));
        public async Task<int> InstallAsync(ProgramEntry e, ProgramPackage p, Action started)
        { Started.Add(e.Id); started(); int code = await Gates[e.Id].Task; if (code == 0 && Detect) Installed[e.Id] = "1.2.3"; return code; }
        public Task<int> UpgradeAsync(ProgramEntry e, ProgramPackage p, Action started) => InstallAsync(e, p, started);
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string q, CancellationToken t) => Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public void OpenWebsite(ProgramEntry e) => throw new Exception("Unexpected website");
    }
    internal static int Run(App app, string output)
    {
        int count = 0;
        void Assert(bool value, string why) { if (!value) throw new Exception(why); count++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var time = Stopwatch.StartNew(); while (!task.IsCompleted && time.Elapsed.TotalSeconds < 35) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        foreach (bool de in new[] { false, true })
        {
            var events = new List<ProgramStepEvent>(); var parser = new ProgramWinGetSteps(events.Add);
            parser.Line("99% downloading"); parser.Line("Successfully installed"); Assert(events.Count == 0, "unrelated output cannot complete stages");
            parser.Line(de ? "Download läuft https://example.invalid/file.exe" : "Downloading https://example.invalid/file.exe");
            parser.Line(de ? "Der Installer-Hash wurde erfolgreich überprüft" : "Successfully verified installer hash");
            parser.Line(de ? "Paketinstallation wird gestartet..." : "Starting package install...");
            Assert(events.SequenceEqual(new[] { new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Active), new(ProgramStep.Download, ProgramStepState.Done), new(ProgramStep.Install, ProgramStepState.Active) }), "live milestones precede success");
            parser.Line(de ? "Erfolgreich installiert" : "Successfully installed"); parser.Line(de ? "Erfolgreich installiert" : "Successfully installed");
            Assert(events.Count == 4 && events[^1] == new ProgramStepEvent(ProgramStep.Install, ProgramStepState.Done), "success once only");
        }
        {
            var events = new List<ProgramStepEvent>(); var parser = new ProgramWinGetSteps(events.Add);
            parser.Line("(1/2) Found Dependency [Vendor.Dependency] Version 1.0"); parser.Line("Successfully verified installer hash"); parser.Line("Starting package install..."); parser.Line("Successfully installed");
            Assert(events.Count == 0, "dependency success is never attributed to main program");
            parser = new ProgramWinGetSteps(events.Add); parser.Line("Starting package install...");
            Assert(events[0] == new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Unknown), "no false download tick for streaming/cached/unreported downloads");
            var start = new ProcessStartInfo(Environment.ProcessPath!); start.ArgumentList.Add("--step-child-fixture");
            bool observedBeforeExit = false; Task<WinGetResult>? work = null; var seen = new List<ProgramStepEvent>(); var tracker = new ProgramWinGetSteps(e => { seen.Add(e); if (e.Step == ProgramStep.Install && e.State == ProgramStepState.Active) observedBeforeExit = work is { IsCompleted: false }; });
            work = ProgramInstallService.RunHiddenAsync(start, () => { }, tracker.Line); Wait(work);
            Assert(observedBeforeExit && seen.Count == 4 && work.Result.ExitCode == 0, "actual hidden child streams milestones before exit");
        }
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false }; Fake? fake = null; Task? running = null; ProgramCatalogView? view = null;
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; view.ConfirmForTests = _ => true;
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(t => t.Tag is string).ToArray())
            foreach (bool english in new[] { false, true })
            foreach (int size in new[] { 1, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                fake = new(); fake.Gates["firefox"] = new(TaskCreationOptions.RunContinuationsAsynchronously); fake.Gates["7-zip"] = new(TaskCreationOptions.RunContinuationsAsynchronously); view.Service = fake; Wait(view.RefreshAsync());
                view.Select("firefox", true); view.Select("7-zip", true); running = view.InstallAsync(); Pump(); var panel = view.OperationPanel!;
                Assert(fake.Started.SequenceEqual(new[] { "firefox" }), "only first program starts");
                Assert(panel.StepState("firefox", ProgramStep.Check) == ProgramStepState.Done && panel.StepState("firefox", ProgramStep.Download) == ProgramStepState.Waiting, "checking complete without false download");
                fake.Emit(ProgramStep.Download, ProgramStepState.Active); Pump(); Assert(panel.StepState("firefox", ProgramStep.Download) == ProgramStepState.Active, "download visible live");
                fake.Download(5242880, 10485760); Pump(); Assert(!panel.Progress.IsIndeterminate && panel.Progress.Value == 50 && panel.Progress.ShowPercentage, "observed download bytes drive measured progress");
                fake.Download(6000000, null); Pump(); Assert(panel.Progress.IsIndeterminate && !panel.Progress.ShowPercentage, "missing total hides stale percent while animation continues");
                fake.Emit(ProgramStep.Download, ProgramStepState.Done); fake.Emit(ProgramStep.Install, ProgramStepState.Active); Pump();
                Assert(panel.StepState("firefox", ProgramStep.Download) == ProgramStepState.Done && panel.StepState("firefox", ProgramStep.Install) == ProgramStepState.Active && panel.StepState("firefox", ProgramStep.Finish) == ProgramStepState.Waiting, "download tick before installation and final verification");
                var texts = UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).ToArray();
                Assert(texts.Any(t => t.Text == "Download ✓" && t.Foreground is SolidColorBrush b && b.Color.G > b.Color.R), "actual green download checkmark");
                Assert(texts.Any(t => t.Text == (english ? "Install …" : "Installation …")), "queue status follows current stage instead of stale launch caption");
                if (!english && theme.Tag?.ToString() is "Standard" or "Neon Blau") Save("Installation-" + theme.Tag + "-" + size);
                fake.Emit(ProgramStep.Install, ProgramStepState.Done); fake.Gates["firefox"].SetResult(0);
                var watch = Stopwatch.StartNew(); while (fake.Started.Count < 2 && watch.Elapsed.TotalSeconds < 5) { Pump(); Thread.Sleep(5); }
                Assert(fake.Started.Count == 2 && Enum.GetValues<ProgramStep>().All(s => panel.StepState("firefox", s) == ProgramStepState.Done), "first result retained below while second starts");
                Assert(panel.StepState("7-zip", ProgramStep.Download) == ProgramStepState.Waiting, "next program has independent stages");
                fake.Emit(ProgramStep.Download, ProgramStepState.Active); fake.Emit(ProgramStep.Download, ProgramStepState.Done); fake.Emit(ProgramStep.Install, ProgramStepState.Active); fake.Gates["7-zip"].SetResult(0); Wait(running);
                Assert(Enum.GetValues<ProgramStep>().All(s => panel.StepState("7-zip", s) == ProgramStepState.Done), "completion after success and independent inspection");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Count(t => t.Text.EndsWith("✓")) >= 12, "all per-program steps retained with ticks");
                var host = (Grid)main.FindName("Root"); var rect = panel.Frame.TransformToAncestor(host).TransformBounds(new Rect(panel.Frame.RenderSize));
                Assert(rect.Left >= 0 && rect.Top >= 0 && rect.Bottom <= host.ActualHeight + 1 && rect.Right <= host.ActualWidth + 1, "progress fits compact/large");
                if (!english && theme.Tag?.ToString() is "Standard" or "Neon Blau") Save("Fertig-" + theme.Tag + "-" + size);
                panel.Dispose(); Console.WriteLine("PASS stages " + theme.Tag + " " + english + " " + size);
            }
            foreach (int code in new[] { 1602, 3010, 0 })
            {
                fake = new() { Detect = false }; fake.Gates["firefox"] = new(TaskCreationOptions.RunContinuationsAsynchronously); view.Service = fake; Wait(view.RefreshAsync()); view.Select("firefox", true);
                running = view.InstallAsync(); Pump(); var panel = view.OperationPanel!;
                fake.Emit(ProgramStep.Download, ProgramStepState.Active); fake.Emit(ProgramStep.Download, ProgramStepState.Done); fake.Emit(ProgramStep.Install, ProgramStepState.Active); fake.Gates["firefox"].SetResult(code); Wait(running);
                Assert(panel.StepState("firefox", ProgramStep.Download) == ProgramStepState.Done && panel.StepState("firefox", ProgramStep.Finish) != ProgramStepState.Done, "cancel/reboot/missing evidence never gets final success tick " + code);
                if (code != 0) Assert(panel.StepState("firefox", ProgramStep.Install) != ProgramStepState.Done, "nonzero result cannot complete installation");
                panel.Dispose();
            }
            fake = new(); fake.Gates["firefox"] = new(TaskCreationOptions.RunContinuationsAsynchronously); view.Service = fake; Wait(view.RefreshAsync()); view.Select("firefox", true); running = view.InstallAsync(); Pump();
            fake.Gates["firefox"].SetResult(0); Wait(running);
            Assert(view.OperationPanel!.StepState("firefox", ProgramStep.Download) == ProgramStepState.Unknown && view.OperationPanel.StepState("firefox", ProgramStep.Finish) == ProgramStepState.Done, "successful exit never invents a missing download report");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(view.OperationPanel.Frame).Any(t => t.Text.Contains("Stage not reported individually", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("Schritt nicht einzeln gemeldet")), "unreported stages explained in result");
            view.OperationPanel.Dispose();
            fake = new(); fake.Gates["firefox"] = new(TaskCreationOptions.RunContinuationsAsynchronously); fake.Gates["7-zip"] = new(TaskCreationOptions.RunContinuationsAsynchronously); view.Service = fake; Wait(view.RefreshAsync()); view.Select("firefox", true); view.Select("7-zip", true); running = view.InstallAsync(); Pump();
            view.RequestStop(); fake.Emit(ProgramStep.Download, ProgramStepState.Done); fake.Emit(ProgramStep.Install, ProgramStepState.Active); Pump();
            Assert(UnifiedDialogAppearance.Children<TextBlock>(view.OperationPanel!.Frame).Any(t => t.Text.StartsWith("Stop requested") || t.Text.StartsWith("Stop angefordert")), "late stage reports preserve stop notice");
            fake.Gates["firefox"].SetResult(0); Wait(running); Assert(fake.Started.Count == 1 && view.OperationPanel.StepState("7-zip", ProgramStep.Finish) == ProgramStepState.Skipped, "stopped queue skips next program without success ticks"); view.OperationPanel.Dispose();
            var impostorRoot = Path.Combine(output, "WrongProduct"); Directory.CreateDirectory(impostorRoot); File.Copy(Environment.ProcessPath!, Path.Combine(impostorRoot, "MelonLoader.Installer.exe"), true);
            var impostors = new Dictionary<string, ProgramInstallation>(); ProgramLocalInventory.ReadPortable(ProgramCatalog.Entries, impostors, CancellationToken.None, new[] { impostorRoot }); Assert(!impostors.ContainsKey("melonloader"), "renamed unrelated EXE is not a detected portable setup");
            // Local real files only; never launch these executables.
            var local = new Dictionary<string, ProgramInstallation>(); ProgramLocalInventory.ReadPortable(ProgramCatalog.Entries, local, CancellationToken.None);
            Assert(local.TryGetValue("geek-uninstaller", out var geek) && geek.Source == "Portable" && File.Exists(geek.Path), "observed Geek desktop EXE detected");
            Assert(local.TryGetValue("melonloader", out var melon) && melon.Source == "Portable-Setup" && melon.Version is null && melon.Variant!.StartsWith("MelonLoader Installer 4.4.0"), "observed Melon installer not confused with loader version");
            Assert(!ProgramLocalInventory.MatchesPortable("melonloader", "MelonLoader.Installer", "Other publisher"), "filename/product cannot override mismatched publisher");
            Assert(ProgramLocalInventory.MatchesPortable("melonloader", "MelonLoader.Installer", "discord.gg/2Wn3N2P"), "observed Melon product metadata");
            fake = new() { Evidence = local }; foreach (var pair in local) fake.Installed[pair.Key] = pair.Value.Version;
            view.Service = fake; page.SetLanguage(false); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(t => t.Tag?.ToString() == "Standard"); ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Wait(view.RefreshAsync());
            Assert(view.InstallationStatus("melonloader") == "Einrichtungs-EXE vorhanden" && view.InstallationStatus("geek-uninstaller").StartsWith("Portable App vorhanden"), "portable/setup states labelled distinctly");
            Assert(!view.ShowManualMark("melonloader") && !view.Inventory.HasUpdate("melonloader"), "known setup is neither uncertain install nor WinGet upgrade");
            view.ShowInfo(ProgramCatalog.Entries.Single(e => e.Id == "melonloader"), view, true); Pump(); Save("Portable-Melon-1"); view.CloseInfo();
            Console.WriteLine("PASS " + count + " stage/stream/portable checks; controlled child and fixtures only, no real installation."); return 0;
        }
        finally { if (fake is not null) foreach (var gate in fake.Gates.Values) gate.TrySetResult(0); if (running is { IsCompleted: false }) Wait(running); view?.OperationPanel?.Dispose(); main.Close(); Pump(); }
        void Save(string name)
        {
            Pump(); main.UpdateLayout(); var image = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(main); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var f = File.Create(Path.Combine(output, name + ".png")); encoder.Save(f);
        }
    }
}
