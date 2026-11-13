using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramProgressTests
{
    internal static int Run(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        int count = 0; var flags = BindingFlags.Instance | BindingFlags.NonPublic; ProgramCatalogTests.Fake? fake = null; Task? running = null; ProgramCatalogView? activeView = null;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); count++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 25) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException("Fixture did not finish"); task.GetAwaiter().GetResult(); Pump(); }
        void Save(Window main, string name)
        {
            Pump(); main.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
        try
        {
            var fixtureStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true }; fixtureStart.ArgumentList.Add("--hidden-child-fixture"); int started = 0;
            var process = ProgramInstallService.RunHiddenAsync(fixtureStart, () => started++); Wait(process);
            Assert(started == 1 && process.Result.ExitCode == 7, "hidden process preserves start callback and failure exit code");
            Assert(process.Result.Output.Length <= 65537 && process.Result.Output.Contains("fixture stderr"), "both pipes drained without deadlock and retained output bounded");
            Assert(fixtureStart.RedirectStandardError && fixtureStart.RedirectStandardOutput && !fixtureStart.UseShellExecute && fixtureStart.CreateNoWindow, "hidden start redirects streams without a console");
            var backend = new ProgramInstallService((_, _) => Task.FromResult(new WinGetResult(0, "")), (_, _) => Task.FromResult(0));
            var entry = ProgramCatalog.Entries.Single(x => x.Id == "firefox"); var command = backend.Command(entry, true);
            Assert(!command.UseShellExecute && command.CreateNoWindow && command.ArgumentList.Contains("--disable-interactivity"), "program WinGet has no CLI console/prompt");
            Assert(command.ArgumentList.Contains("--silent") && !command.ArgumentList.Contains("--interactive") && !command.ArgumentList.Any(x => x.StartsWith("--accept") || x.Contains("ignore-security")), "automatic install without implicit agreement or security bypass");
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); activeView = view; view.Assets.AllowOnlineIcons = false;
            fake = new ProgramCatalogTests.Fake(); view.Service = fake; view.ConfirmForTests = _ => true;
            var installPath = Path.Combine(output, "InstalledFixture", "Firefox"); Directory.CreateDirectory(installPath);
            fake.InspectHandler = _ => Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?>(fake.Installed), Updates: new Dictionary<string, string>(fake.Updates), UpdateCheckComplete: true,
                Evidence: fake.Installed.ToDictionary(pair => pair.Key, pair => new ProgramInstallation(pair.Value, "Registry", pair.Key == "firefox" ? installPath : null))));
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(t => t.Tag is string).ToArray())
            foreach (bool english in new[] { false, true })
            foreach (int size in new[] { 1, 2, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); fake.Installed.Clear(); fake.Started.Clear(); fake.Updates.Clear(); view.Selected.Clear(); Wait(view.RefreshAsync()); view.SetCategory("all");
                Pump(); var back = (Button)typeof(ProgramCatalogView).GetField("_back", flags)!.GetValue(view)!;
                var arrow = UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(back).Single(); var geometry = arrow.Data.Bounds;
                var arrowCenter = arrow.TranslatePoint(new Point(geometry.Left + geometry.Width / 2, geometry.Top + geometry.Height / 2), back);
                Assert(Math.Abs(arrowCenter.X - back.ActualWidth / 2) < 1 && Math.Abs(arrowCenter.Y - back.ActualHeight / 2) < 1, "back arrow centered inside unchanged button dimensions");
                view.Select("firefox", true); view.Select("7-zip", true); fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var work = view.InstallAsync(); running = work; Pump(); var panel = view.OperationPanel!;
                Assert(view.IsWorking && main.IsBusy && panel is { Expanded: true }, "operation owns lock and expanded app progress");
                Assert(panel.Progress.IsRunning && panel.Progress.IsIndeterminate && panel.Progress.PercentageText == "", "unmeasured WinGet progress never invents a percentage");
                var host = (Grid)main.FindName("Root"); var position = panel.Frame.TranslatePoint(new Point(0, 0), host);
                Assert(position.X >= 0 && position.Y >= 80 && position.X + panel.Frame.ActualWidth <= host.ActualWidth + 1 && position.Y + panel.Frame.ActualHeight <= host.ActualHeight + 1, "floating panel within app bounds");
                Assert(panel.Frame.Background.Opacity == 1 && (panel.Frame.Background is SolidColorBrush { Color.A: 255 }
                    || panel.Frame.Background is GradientBrush gradient && gradient.GradientStops.All(stop => stop.Color.A == 255)), "panel opaque in every theme");
                panel.Measurement(62); Assert(!panel.Progress.IsIndeterminate && panel.Progress.Value == 62, "measured progress supplied explicitly"); panel.Measurement(null);
                if (!english && size is 1 or 4 && theme.Tag?.ToString() is "Standard" or "Neon Blau") Save(main, "Fortschritt-" + theme.Tag + "-" + size);
                var before = view.Selected.ToArray(); view.Select("notepad", true); Assert(view.Selected.SetEquals(before), "selection changes rejected during actual operation");
                panel.Collapse(); Assert(!panel.Expanded && view.IsWorking, "collapse does not end or unlock operation");
                var reopen = UnifiedDialogAppearance.Children<Button>(main).Single(b => b.Name == "ProgramProgressReopen"); Assert(reopen.IsVisible, "collapsed progress remains reachable");
                page.ShowUpdateOverview(); Pump(); Assert(page.IsVisible && view.IsWorking, "overview navigation allowed during operation");
                ((Button)page.FindName("ProgramsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(view.IsVisible && view.IsWorking, "program view remains reachable without starting another operation");
                ((Button)main.FindName("PagePrevious")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(!page.IsVisible && view.IsWorking && reopen.IsVisible, "other app pages remain navigable with collapsed operation");
                ((Button)main.FindName("PageNext")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(page.IsVisible && view.IsWorking, "returning to page four preserves operation");
                reopen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(panel.Expanded && view.HasOverlay, "reopen restores panel and correct neon layering");
                panel.Place(-5000, -5000); Pump(); var moved = panel.Frame.TranslatePoint(new Point(0, 0), host); Assert(moved.X >= 0 && moved.Y >= 0, "drag placement clamps negative coordinates");
                panel.Place(5000, 5000); Pump(); moved = panel.Frame.TranslatePoint(new Point(0, 0), host); Assert(moved.X + panel.Frame.ActualWidth <= host.ActualWidth + 1 && moved.Y + panel.Frame.ActualHeight <= host.ActualHeight + 1, "drag placement clamps far edge");
                var stop = UnifiedDialogAppearance.Children<Button>(panel.Frame).Single(b => b.Name == "ProgramProgressStop"); stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Assert(!work.IsCompleted && view.IsWorking && !stop.IsEnabled, "stop waits for current installer and keeps locks");
                panel.Collapse();
                fake.Pending.SetResult(0); Wait(work); Assert(fake.Started.Count == 1 && !view.IsWorking && !main.IsBusy, "stop skips next installer and unlocks only after exit and inspection");
                Assert(panel.Expanded, "completion reopens collapsed progress automatically");
                Assert(UnifiedDialogAppearance.Children<Button>(panel.Frame).Where(b => b.Name is "ProgramProgressCollapse" or "ProgramProgressMinimize").All(b => !b.IsVisible), "completion removes collapse/minimize controls");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t => t.Text == (english ? "Operation finished" : "Vorgang beendet")), "completion clearly named as finished");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t => t.Text == (english ? "Installation path: " : "Installationspfad: ") + installPath), "confirmed full installation directory displayed");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t => t.Text == (english ? "Not run" : "Nicht ausgeführt")), "waiting item marked not run after queue stop");
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(!panel.Expanded && !view.HasOverlay, "finished progress closes and restores underlying view");
                Console.WriteLine("PASS app-progress " + theme.Tag + " " + (english ? "EN" : "DE") + " size=" + size);
            }
            page.SetLanguage(false); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(t => t.Tag?.ToString() == "Standard"); ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1;
            fake.Installed.Clear(); fake.Updates.Clear(); fake.Started.Clear(); view.Selected.Clear(); Wait(view.RefreshAsync()); view.Select("firefox", true); view.Select("7-zip", true);
            fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); var allWork = view.InstallAsync(); running = allWork; Pump(); var completedPanel = view.OperationPanel!; completedPanel.Collapse(); main.WindowState = WindowState.Minimized; Pump();
            fake.Pending.SetResult(0); Wait(allWork);
            Assert(fake.Started.Count == 2 && !view.IsWorking && completedPanel.Expanded && main.WindowState == WindowState.Normal, "successful full queue automatically restores a minimized owner and its result panel");
            Assert(!completedPanel.Progress.IsRunning && !completedPanel.Progress.IsIndeterminate && completedPanel.Progress.Value == 100, "only a fully verified successful queue shows completed progress");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(completedPanel.Frame).Any(t => t.Text == "Installationspfad nicht ermittelbar"), "missing installation path explicitly unknown instead of fabricated");
            Save(main, "Abschluss-Standard-1"); ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 4; Pump(); Save(main, "Abschluss-Standard-4"); completedPanel.Dispose();
            fake.Installed.Clear(); fake.Started.Clear(); view.Selected.Clear(); Wait(view.RefreshAsync()); main.WindowState = WindowState.Maximized; Pump();
            view.Select("firefox", true); fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); var maximizedWork = view.InstallAsync(); running = maximizedWork; Pump(); view.OperationPanel!.Collapse(); main.WindowState = WindowState.Minimized; Pump();
            fake.Pending.SetResult(0); Wait(maximizedWork); Assert(main.WindowState == WindowState.Maximized && view.OperationPanel!.Expanded, "completion preserves maximized state instead of changing original window size"); view.OperationPanel!.Dispose(); main.WindowState = WindowState.Normal; Pump();
            Console.WriteLine("PASS " + count + " progress/process/navigation checks. Controlled helper only; no installation."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); throw; }
        finally { fake?.Pending?.TrySetResult(0); if (running is { IsCompleted: false }) Wait(running); activeView?.OperationPanel?.Dispose(); main.Close(); Pump(); }
    }
}
