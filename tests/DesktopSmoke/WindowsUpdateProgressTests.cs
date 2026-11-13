using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class WindowsUpdateProgressTests
{
    internal static int Run(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        int checks = 0; void Assert(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var errors = new List<Exception>(); DispatcherUnhandledExceptionEventHandler capture = (_, e) => errors.Add(e.Exception);
        app.DispatcherUnhandledException += capture;
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        settings.Theme = "Standard";
        settings.SizeIndex = 1;
        var main = new MainWindow(Path.Combine(output, "Isolated"), settings) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 15) { Pump(); Thread.Sleep(3); } if (!task.IsCompleted) throw new TimeoutException("Windows-update progress fixture timed out"); task.GetAwaiter().GetResult(); Pump(); }
        Rect Bounds(FrameworkElement value) => value.TransformToAncestor(main).TransformBounds(new Rect(value.RenderSize));
        void Save(string name, FrameworkElement? visual = null)
        {
            Pump(); visual ??= main;
            var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name + ".png")); png.Save(stream);
        }
        Task? activeInstall = null;
        TaskCompletionSource? helperExit = null;
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            var service = new WindowsUpdateTests.FakeService(); page.Service = service; page.ConfirmForTests = _ => true;
            var bar = (AnimatedProgressBar)page.FindName("OperationProgress"); var text = (TextBlock)page.FindName("OperationText");
            var cancel = (Button)page.FindName("CancelOperationButton"); var install = (Button)page.FindName("InstallUpdatesButton");
            var dialog = (Border)page.FindName("OperationSurface");
            var collapse = (Button)page.FindName("CollapseOperationButton");
            var reopen = (Button)page.FindName("ReopenOperationButton");
            var operationRows = (StackPanel)page.FindName("OperationRows");
            string Step(string name) => ((TextBlock)page.FindName(name + "OperationStep")).Text;
            void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump(); }
            var searchBar = (AnimatedProgressBar)page.FindName("SearchProgress");
            var searchTitle = (TextBlock)page.FindName("SearchTitle");
            var themes = (ComboBox)main.FindName("ThemeBox");
            Action<WindowsUpdateEvent>? previousReport = null;
            void Snap() { bar.IsAnimationSuspended = true; bar.IsAnimationSuspended = false; Pump(); }
            void Select() => UnifiedDialogAppearance.Children<CheckBox>(page).First(c => c.Tag is string).IsChecked = true;
            themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == "Standard");
            page.SetLanguage(false);
            var sizes = (ComboBox)main.FindName("SizeBox");
            sizes.SelectedIndex = 1;
            var searchSurface = (Border)page.FindName("SearchSurface");
            var stopSearch = (Button)page.FindName("CancelSearchButton");
            var firstOpen = (CheckBox)main.FindName("AutomaticWindowsUpdateSearch");
            var drivers = (StackPanel)page.FindName("DriverRows");
            Assert(settings.CheckUpdatesOnFirstOpen && firstOpen.IsChecked == true && page.FindName("FirstOpenUpdateRow") is null,
                "The default-on setting appears in Options only");
            var options = (Border)main.FindName("SettingsPanel");
            var settingsOverlay = typeof(MainWindow).GetMethod("SetSettingsOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (int size in new[] { 1, 4 })
            {
                sizes.SelectedIndex = size;
                settingsOverlay.Invoke(main, [true, false]); Pump();
                var bounds = Bounds(options);
                Assert(bounds.Top >= 90 && bounds.Bottom <= main.ActualHeight - 43, "The complete settings panel fits both sizes");
                var updates = (Button)main.FindName("CheckUpdatesButton");
                Assert(Grid.GetRow(updates) == Grid.GetRow(firstOpen) + 1 && Bounds(updates).Top > Bounds(firstOpen).Top,
                    "The main app update action is below the automatic search");
                Save(size == 1 ? "Optionen-Kompakt" : "Optionen-Gross");
                settingsOverlay.Invoke(main, [false, false]); Pump();
            }
            main.ShowRepairPage(); Pump();
            page.ConfigureFirstOpenCheck(() => settings.CheckUpdatesOnFirstOpen, runOnOpen: true);
            int before = service.Searches;
            int installsBefore = service.Installs;
            var pending = new TaskCompletionSource<WindowsUpdateSearch>();
            bool? scope = false;
            CancellationToken cancellation = default;
            service.OnSearch = (mode, token) => { scope = mode; cancellation = token; return pending.Task; };
            Pump();
            Assert(service.Searches == before, "Hidden page does not query Windows Update");
            main.ShowWindowsUpdatesPage(); Pump();
            Assert(scope is null && service.Searches == before + 1 && searchSurface.IsVisible, "First visible opening starts one combined search");
            Assert(searchTitle.Text == "Windows- und Treiberupdates werden gesucht …", "The search title contains only the active search");
            Assert(searchBar.IsIndeterminate && searchBar.IsRunning && searchBar.PercentageText.Length == 0 && stopSearch.IsEnabled, "The combined search animates and can be stopped");
            var overviewScroll = (ScrollViewer)page.FindName("PageScroll");
            Assert(overviewScroll.VerticalOffset == 0, "Automatic search keeps the page header visible");
            Thread.Sleep(350);
            Save("Seite4-Updatesuche-Kompakt");
            sizes.SelectedIndex = 4;
            Save("Seite4-Updatesuche-Gross");
            Assert(Bounds(searchBar).Right <= main.ActualWidth && Bounds(stopSearch).Bottom < main.ActualHeight, "Combined search fits the large window");
            pending.SetResult(new([WindowsUpdateTests.FakeService.Item(1), WindowsUpdateTests.FakeService.Item(3, true)], false));
            Pump();
            Assert(page.SoftwareItems.Count == 1 && !page.SoftwareItems[0].Driver && drivers.Children.Count == 2 && !searchSurface.IsVisible, "Combined result separates Windows and driver entries");
            main.ShowRepairPage(); main.ShowWindowsUpdatesPage(); Pump();
            Assert(service.Searches == before + 1 && service.Installs == installsBefore && !page.IsWorking, "Reopening does not repeat the automatic search or install anything");

            firstOpen.IsChecked = false;
            firstOpen.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            Assert(!restored.CheckUpdatesOnFirstOpen, "Switch saves the disabled setting");
            main.ShowRepairPage(); Pump();
            page.ConfigureFirstOpenCheck(() => restored.CheckUpdatesOnFirstOpen, runOnOpen: true);
            main.ShowWindowsUpdatesPage(); Pump();
            Assert(service.Searches == before + 1 && !page.IsWorking, "Disabled automatic search stays idle on opening");
            pending = new();
            var manual = page.SearchAsync(true); Pump();
            Assert(scope == true && service.Searches == before + 2, "Manual driver search remains available");
            stopSearch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert(cancellation.IsCancellationRequested && page.IsWorking && !stopSearch.IsEnabled, "Stop requests cancellation and retains the lock until Windows completes");
            pending.SetResult(new([], false)); Wait(manual);
            Assert(!page.IsWorking && !main.IsBusy && drivers.Children.Count == 2, "Cancellation retains preceding results and releases the lock");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(x => x.Tag is string).ToArray())
            {
                themes.SelectedItem = theme; Pump();
                var programs = page.ShowPrograms(inspect: false); programs.Assets.AllowOnlineIcons = false;
                var entry = ProgramCatalog.Entries.First(x => x.PackageId == "7zip.7zip");
                using (var reference = new ProgramProgressPanel(programs, [entry]))
                {
                    Pump(); Assert(bar.Height == reference.Progress.Height, "Windows and program bars use identical approved height");
                    Assert(((SolidColorBrush)bar.Foreground).Color == ((SolidColorBrush)reference.Progress.Foreground).Color
                        && ((SolidColorBrush)bar.BorderBrush).Color == ((SolidColorBrush)reference.Progress.BorderBrush).Color,
                        "Progress accent and frame match actual program bar for " + theme.Tag);
                }
                page.ShowUpdateOverview(); Pump();
                foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                    service.OnSearch = null; Wait(page.SearchAsync(null));
                    var exit = helperExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Action<WindowsUpdateEvent>? report = null;
                    service.OnRun = (_, progress, _) => { report = progress; return exit.Task; };
                    Select();
                    var driverKey = WindowsUpdateTests.FakeService.Item(3, true).Key;
                    UnifiedDialogAppearance.Children<CheckBox>(page).Single(box => Equals(box.Tag, driverKey)).IsChecked = true;
                    var operation = activeInstall = page.InstallAsync(); Pump();
                    Assert(page.IsWorking && main.IsBusy && text.Text == (english ? "Preparing update installation…" : "Installationsauftrag wird vorbereitet…"), "Install click immediately shows preparation, not general search");
                    Assert(dialog.IsVisible && operationRows.Children.Count == 2 && Step("Validate").EndsWith("…") && Step("Download").EndsWith("·"),
                        "Centered dialog immediately displays the approved software and driver with their active phase");
                    Assert(bar.IsIndeterminate && bar.PercentageText.Length == 0 && bar.IsRunning, "No invented percentage while waiting for helper/UAC");
                    if (previousReport is not null)
                    {
                        previousReport(new("install", Percent: 99)); Pump();
                        Assert(text.Text.Contains(english ? "Preparing" : "vorbereitet"), "Callback from preceding install cannot modify new operation");
                    }
                    report!(new("search")); Pump();
                    Assert(text.Text == (english ? "Checking selected updates before installation…" : "Ausgewählte Updates werden vor der Installation geprüft…"), "Legacy helper search maps to selection validation during install");
                    report(new("validate", Percent: 24)); Pump();
                    Assert(bar.IsIndeterminate && bar.PercentageText.Length == 0, "Validation cannot display a fabricated installation percentage");
                    if (!english && size == 1 && theme.Tag?.ToString() == "Standard")
                    {
                        Thread.Sleep(300); Save("WindowsUpdate-Dialog-Pruefen-Kompakt");
                    }
                    report(new("download")); Pump();
                    Assert(text.Text.Contains(english ? "Downloading" : "heruntergeladen") && bar.IsIndeterminate, "Download transition visible before first measurement");
                    report(new("download", Percent: 42)); Snap();
                    Assert(!bar.IsIndeterminate && bar.PercentageText == "42 %" && bar.Value == 42, "Measured download percentage appears inside bar");
                    report(new("download", Percent: 100)); Snap(); Assert(bar.PercentageText == "100 %", "Measured download completion supported");
                    report(new("install")); Pump();
                    Assert(text.Text.Contains(english ? "Installing" : "installiert") && bar.IsIndeterminate && bar.PercentageText.Length == 0 && bar.Value == 0, "Installation never inherits preceding 100 percent download");
                    Assert(Step("Validate").EndsWith("✓") && Step("Download").EndsWith("✓") && Step("Install").EndsWith("…"), "Confirmed steps and active installation use the correct indicators");
                    report(new("install", Percent: 0)); Snap(); Assert(bar.PercentageText == "0 %", "Genuine measured zero is displayed");
                    foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                    { report(new("install", Percent: invalid)); Pump(); Assert(bar.IsIndeterminate && bar.PercentageText.Length == 0, "Invalid measurement safely uses waiting animation"); }
                    report(new("install", Percent: -10)); Snap(); Assert(bar.Value == 0 && bar.PercentageText == "0 %", "Finite lower bound clamped safely");
                    report(new("install", Percent: 110)); Snap(); Assert(bar.Value == 100 && bar.PercentageText == "100 %", "Finite upper bound clamped safely");
                    report(new("install", Percent: 68)); Snap();
                    report(new("download", Percent: 99)); Pump();
                    Assert(text.Text.Contains(english ? "Installing" : "installiert") && bar.Value == 68, "Out-of-order older phase does not overwrite installation");
                    int searches = service.Searches;
                    Wait(page.SearchAsync(null));
                    Assert(service.Searches == searches && bar.PercentageText == "68 %" && main.IsBusy, "Search cannot overwrite a running installation");
                    Assert(cancel.IsEnabled && !install.IsEnabled && !((Button)page.FindName("SearchUpdatesButton")).IsEnabled, "Stop remains usable while new update actions stay locked");
                    Assert(UnifiedDialogAppearance.Children<CheckBox>(page).Where(c => c.Tag is string).All(c => !c.IsEnabled), "Selection cannot change during installation");
                    var barBounds = Bounds(bar); var cancelBounds = Bounds(cancel);
                    Assert(barBounds.Left >= 0 && barBounds.Right <= main.ActualWidth && barBounds.Bottom <= cancelBounds.Top && cancelBounds.Bottom < main.ActualHeight, "Bar and red stop button fit compact/large window");
                    var pageBounds = Bounds(page); var dialogBounds = Bounds(dialog);
                    Assert(dialogBounds.Top >= pageBounds.Top && dialogBounds.Bottom <= pageBounds.Bottom &&
                        Math.Abs((dialogBounds.Left + dialogBounds.Right) / 2 - (pageBounds.Left + pageBounds.Right) / 2) <= 2,
                        "Dialog stays centered inside the update page");
                    Assert(bar.HasMotion == SystemParameters.ClientAreaAnimation, "Active measurement follows Windows animation preference");
                    if (!english && size is 1 or 4 && theme.Tag?.ToString() == "Standard")
                    {
                        bar.IsAnimationSuspended = true; Save("WindowsUpdate-Dialog-Installation-" + size); bar.IsAnimationSuspended = false;
                    }
                    Click(collapse);
                    Assert(!dialog.IsVisible && reopen.IsVisible && !bar.HasMotion && main.IsBusy, "Collapsing keeps the operation active");
                    Click(reopen);
                    Assert(dialog.IsVisible && !reopen.IsVisible && bar.Value == 68, "Reopening shows the same measured progress");
                    main.ShowRepairPage(); Pump(); Assert(!bar.HasMotion, "Hidden update page stops decorative animation");
                    main.ShowWindowsUpdatesPage(); Pump(); Assert(bar.HasMotion == SystemParameters.ClientAreaAnimation && bar.Value == 68, "Returning to update page resumes same measured progress");
                    report(new("item", Key: WindowsUpdateTests.FakeService.Item(1).Key, Code: 2)); Pump();
                    report(new("item", Key: driverKey, Code: 2)); Pump();
                    Assert(bar.PercentageText == "68 %", "Per-item results do not erase most recent measurement");
                    Click(collapse);
                    report(new("done", Code: 2)); Pump();
                    string finished = text.Text;
                    Assert(main.IsBusy && page.IsWorking && !bar.HasMotion, "Terminal report stops animation but lock waits for real helper completion");
                    report(new("install", Percent: 13)); Pump(); Assert(text.Text == finished, "Late sample cannot overwrite terminal result");
                    exit.SetResult(); Wait(operation);
                    Assert(!page.IsWorking && !main.IsBusy && bar.Visibility == Visibility.Collapsed && !bar.HasMotion, "Real completion releases lock and stops hidden animation");
                    Assert(text.Text.Contains(english ? "completed" : "abgeschlossen"), "Installation keeps its success message");
                    Assert(dialog.IsVisible && !reopen.IsVisible && Step("Finish").EndsWith("✓")
                        && UnifiedDialogAppearance.Children<TextBlock>(operationRows).Count(x => x.Text == (english ? "Installed" : "Installiert")) == 2,
                        "Completion reopens the result with both original entries even after the update rows are removed");
                    if (!english && size == 1 && theme.Tag?.ToString() == "Standard") Save("WindowsUpdate-Dialog-Ergebnis-Kompakt");
                    Click(collapse);
                    Assert(!dialog.IsVisible && !reopen.IsVisible, "Closing the result clears its presentation");
                    previousReport = report; previousReport(new("error", "stale")); Pump();
                    Assert(text.Text == finished, "Callback after ended operation is ignored");
                }
            }
            foreach (bool english in new[] { false, true })
            {
                page.SetLanguage(english); service.OnSearch = null; Wait(page.SearchAsync(false));
                var exit = helperExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<WindowsUpdateEvent>? report = null;
                service.OnRun = async (_, progress, token) => { report = progress; await exit.Task; token.ThrowIfCancellationRequested(); };
                Select(); var operation = activeInstall = page.InstallAsync(); report!(new("install", Percent: 31)); Snap();
                Click(cancel);
                Assert(main.IsBusy && !operation.IsCompleted && !cancel.IsEnabled, "Stop request alone cannot release installation lock");
                report(new("install", Percent: 32)); Pump();
                Assert(text.Text.Contains(english ? "Stop requested" : "Beenden angefordert"), "Further real progress cannot hide stop request");
                exit.SetResult(); Wait(operation);
                Assert(text.Text.Contains(english ? "Operation stopped" : "Vorgang beendet") && !main.IsBusy && !bar.HasMotion, "Actual stopped process releases lock and retains stop explanation");

                service.OnSearch = null; Wait(page.SearchAsync(false));
                exit = helperExit = new(TaskCreationOptions.RunContinuationsAsynchronously); service.OnRun = (_, progress, _) => { report = progress; return exit.Task; };
                Select(); operation = activeInstall = page.InstallAsync(); report!(new("install", Percent: 59)); report(new("error", "Fixture update failure", RebootRequired: true));
                exit.SetResult(); Wait(operation);
                Assert(text.Text.Contains("Fixture update failure") && text.Text.Contains(english ? "restart" : "Neustart"), "Failure preserves its explanation and reboot requirement");

                var searchDone = new TaskCompletionSource<WindowsUpdateSearch>(TaskCreationOptions.RunContinuationsAsynchronously);
                service.OnSearch = (_, _) => searchDone.Task; var search = page.SearchAsync(false); Pump();
                Assert(searchTitle.Text.Contains(english ? "Checking Windows updates" : "Windows-Updates werden gesucht") && searchBar.PercentageText.Length == 0, "Ordinary search retains its correct caption and unknown progress");
                previousReport!(new("install", Percent: 99)); Pump();
                Assert(searchTitle.Text.Contains(english ? "Checking Windows updates" : "Windows-Updates werden gesucht"), "Old installer callback cannot overwrite a fresh ordinary search");
                searchDone.SetResult(new([], false)); Wait(search);
            }

            page.SetLanguage(false);
            themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == "Standard");
            sizes.SelectedIndex = 1;
            var updatesToInstall = Enumerable.Range(1, 5).Select(index => WindowsUpdateTests.FakeService.Item(index) with
            {
                Title = "Windows-Sicherheitsupdate " + index + " · Ausgewählter Testeintrag für ein längeres Update-Paket",
                Eula = ""
            }).ToArray();
            var confirmation = page.CreateInstallConfirmation(updatesToInstall);
            try
            {
                confirmation.ShowActivated = false; confirmation.Show(); Pump();
                Assert(UnifiedDialogAppearance.Children<Button>(confirmation).Any(button => button.IsDefault && Equals(button.Content, "5 Updates installieren")),
                    "Confirmation names the installation action and its item count");
                Assert(!UnifiedDialogAppearance.Children<TextBlock>(confirmation).Any(label => label.Text.Contains("Lizenzbedingungen")),
                    "Confirmation omits an empty licence notice");
                Save("WindowsUpdate-Bestaetigung", confirmation);
            }
            finally { confirmation.Close(); }
            service.OnSearch = (_, _) => Task.FromResult(new WindowsUpdateSearch(updatesToInstall, false));
            Wait(page.SearchAsync(false));
            foreach (var box in UnifiedDialogAppearance.Children<CheckBox>(page).Where(box => box.Tag is string)) box.IsChecked = true;
            Action<WindowsUpdateEvent>? reportError = null;
            helperExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            service.OnRun = (_, progress, _) => { reportError = progress; return helperExit.Task; };
            activeInstall = page.InstallAsync();
            reportError!(new("validate")); Pump();
            Assert(operationRows.Children.Count == 5 && dialog.IsVisible, "Five selected updates appear in the compact installer");
            var listScroll = (ScrollViewer)page.FindName("OperationListScroll");
            var listHint = (TextBlock)page.FindName("OperationListHint");
            Assert(listScroll.ScrollableHeight > 0 && listHint.IsVisible, "Long titles can be scrolled and the list indicates this");
            listScroll.ScrollToEnd(); Pump();
            Assert(listHint.Text.StartsWith('↑') && cancel.IsVisible && collapse.IsVisible, "The last update is reachable without moving the actions");
            listScroll.ScrollToTop(); Pump();
            Thread.Sleep(300); Save("WindowsUpdate-Installer");
            reportError(new("install"));
            reportError(new("error", "0x80242008"));
            helperExit.SetResult(); Wait(activeInstall);
            var openWindowsUpdate = (Button)page.FindName("OpenWindowsUpdateButton");
            Uri? opened = null;
            page.OpenExternalLink = uri => opened = uri;
            Click(openWindowsUpdate);
            Assert(text.Text.Contains("Windows hat die Installation abgebrochen") && ((Border)page.FindName("OperationList")).Visibility == Visibility.Collapsed
                && opened?.OriginalString == "ms-settings:windowsupdate", "Cancellation presents one explanation and the Windows Update entry point");
            Save("WindowsUpdate-Abbruch");
        }
        finally
        {
            helperExit?.TrySetResult();
            if (activeInstall is { IsCompleted: false }) Wait(activeInstall);
            main.Close(); Pump(); app.DispatcherUnhandledException -= capture;
        }
        Assert(errors.Count == 0, "No dispatcher exceptions were suppressed by outer host: " + errors.FirstOrDefault());
        Console.WriteLine("PASS " + checks + " Windows-update phase, progress, first-open and UI checks; seven designs, DE/EN, compact/large; fixture services only");
        return 0;
    }
}
