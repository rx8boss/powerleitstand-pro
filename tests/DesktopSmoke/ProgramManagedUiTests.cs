using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramManagedUiTests
{
    internal static int Run(App app, string output)
    {
        int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Until(Func<bool> ready) { var timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed.TotalSeconds < 35) { Pump(); Thread.Sleep(4); } if (!ready()) throw new TimeoutException("Managed UI fixture"); Pump(); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var main = new MainWindow(Path.Combine(output, "IsolatedSettings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons = false; var service = new ManagedFake(output); view.Service = service;
            var entry = ProgramCatalog.Entries.Single(e => e.Id == "7-zip");
            foreach (var theme in ((ComboBox)main.FindName("ThemeBox")).Items.OfType<ComboBoxItem>().Where(t => t.Tag is string).ToArray())
            foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                ((ComboBox)main.FindName("ThemeBox")).SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Wait(view.RefreshAsync());
                Assert(view.Inventory.HasUpdate(entry.Id) && view.UpdateEntries.Any(e => e.Id == entry.Id), "Manager offer participates in normal update selection");
                var card = ProgramInfoCard.Open(view, entry, view, false)!; Wait(card.Loading);
                var texts = UnifiedDialogAppearance.Children<TextBlock>(card.Frame).ToArray();
                Assert(texts.Single(t => t.Name == "ProgramInfoInstalledVersion").Text == "1.0" && texts.Single(t => t.Name == "ProgramInfoAvailableVersion").Text == "2.0", "Installed and available managed versions shown separately");
                Assert(UnifiedDialogAppearance.Children<Button>(card.Frame).Single(b => b.Name == "ProgramInfoUpdateAction").Visibility == Visibility.Visible, "Existing update action works for confirmed manager source");
                Assert(texts.Any(t => t.Text.Contains(english ? "Package version · Scoop" : "Paketversion · Scoop")), "Manager package-version numbering explained in app language");
                Assert(card.Frame.ActualWidth == (size == 1 ? 420 : 580) && card.Frame.ActualHeight <= 540, "Original info-frame dimensions preserved"); card.Close();
                var offered = service.Package;
                var alternative = offered with { Target = offered.Target with { Scope = "Machine", Root = Path.Combine(output, "GlobalScoop") } };
                var selection = ProgramSelectionDialog.ShowAsync(view, [new(entry, offered.Package(), ReinstallOptions: [offered.Package(), alternative.Package()])]); Pump();
                var host = (Grid)main.FindName("Root"); var chooser = UnifiedDialogAppearance.Children<ComboBox>(host).Single(c => c.Name == "ProgramReinstallVersionChoice");
                chooser.SelectedIndex = 1; Pump();
                Assert(UnifiedDialogAppearance.Children<TextBlock>(host).Any(t => t.Text.Contains(english ? "All users · administrator rights required" : "Alle Benutzer · Administratorrechte erforderlich")), "Choosing copy updates source and permission details");
                var cancel = UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Content is TextBlock t && t.Text == (english ? "Cancel" : "Abbrechen"));
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(selection);
                Assert(selection.Result is null && !view.SelectionOpen && service.Executions == 0, "Cancel never starts a package command");
            }
            view.ConfirmForTests = _ => true; service.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = view.InstallEntriesAsync([entry]); Until(() => service.Executions == 1);
            Assert(view.IsWorking && !operation.IsCompleted, "Execution keeps the normal shared selection lock");
            int selected = view.Selected.Count; view.Select("git", true); Assert(view.Selected.Count == selected, "Cannot change selection during execution");
            var panelTexts = UnifiedDialogAppearance.Children<TextBlock>((Grid)main.FindName("Root")).Select(t => t.Text).ToArray();
            Assert(panelTexts.Any(t => t.Contains("Scoop") && t.Contains("2.0")), "Existing in-app progress view identifies manager and target version");
            view.RequestStop(); Pump(); Assert(view.IsWorking && !operation.IsCompleted, "Queue stop waits for the running installer instead of terminating or unlocking it");
            service.Gate.TrySetResult(); Wait(operation);
            Assert(!view.IsWorking && service.Executions == 1 && !view.Inventory.HasUpdate(entry.Id), "Independent post-operation inventory verifies result before unlock");
            Assert(view.Inventory.Evidence![entry.Id].ManagedCopies!.Single().PackageVersion == "2.0", "Package registration re-read after controlled installation");
            Console.WriteLine($"PASS {checks} managed info/selection/progress checks; seven designs, DE/EN, compact/large; controlled process only."); return 0;
        }
        finally { main.Close(); Pump(); }
    }
    private sealed class ManagedFake : IProgramInstallService, IProgramStepSource
    {
        private readonly string _root, _oldFile, _newFile; private bool _done;
        internal int Executions; internal TaskCompletionSource? Gate;
        public event Action<ProgramStepEvent>? StepChanged;
        internal ProgramManagedPackage Package { get; }
        internal ManagedFake(string output)
        {
            _root = Path.Combine(output, "ManagedFixture"); _oldFile = Path.Combine(_root, "apps", "7zip", "1.0", "7zFM.exe"); _newFile = Path.Combine(_root, "apps", "7zip", "2.0", "7zFM.exe");
            foreach (string file in new[] { _oldFile, _newFile }) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), file, true); }
            Package = new(new("Scoop", _root, Path.Combine(_root, "scoop.ps1"), "7zip", "User", "main", "64bit", "1.0", Path.GetDirectoryName(_oldFile), "fixture", _oldFile), "2.0", "https://example.invalid/fixture", "fixture", "fixture", false, "Fixture license", "—");
        }
        public Task<ProgramInventory> InspectAsync(CancellationToken token)
        {
            string version = _done ? "2.0" : "1.0", file = _done ? _newFile : _oldFile;
            var location = new ProgramPathEvidence(Path.GetDirectoryName(file), file, "Scoop package file", "User", version);
            var copy = new ProgramManagedCopy("Scoop", "7zip", version, Path.GetDirectoryName(file)!, "User", location, _root);
            var proof = new ProgramInstallation(version, "Scoop", file, false, ManagedCopies: [copy], LocationEvidence: location);
            return Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?> { ["7-zip"] = version },
                Updates: _done ? [] : new Dictionary<string, string> { ["7-zip"] = "2.0" }, UpdateCheckComplete: true,
                Evidence: new Dictionary<string, ProgramInstallation> { ["7-zip"] = proof },
                ManagedPackages: _done ? new Dictionary<string, IReadOnlyList<ProgramManagedPackage>>() : new Dictionary<string, IReadOnlyList<ProgramManagedPackage>> { ["7-zip"] = [Package] }));
        }
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token) => Task.FromResult<ProgramPackage?>(Package.Package());
        public async Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started)
        { Executions++; started(); StepChanged?.Invoke(new(ProgramStep.Download, ProgramStepState.Active)); StepChanged?.Invoke(new(ProgramStep.Download, ProgramStepState.Done)); StepChanged?.Invoke(new(ProgramStep.Install, ProgramStepState.Active)); if (Gate is not null) await Gate.Task; _done = true; return 0; }
        public Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started) => throw new Exception("Unexpected new install in update fixture");
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token) => Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public void OpenWebsite(ProgramEntry entry) => throw new Exception("No external site during controlled execution");
    }
}
