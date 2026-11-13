using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramSelectionCancelTests
{
    internal static int Run(App app, string output)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        void Until(Func<bool> condition)
        { var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed.TotalSeconds < 30) { Pump(); Thread.Sleep(5); } if (!condition()) throw new TimeoutException("Selection cancellation fixture"); Pump(); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); Pump(); }
        void Save(Window window, string name)
        { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file); }
        var main = new MainWindow(Path.Combine(output, "Fixture"), new AppSettings { SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            var fake = new ProgramCatalogTests.Fake(); view.Service = fake; view.PortableService = new VlcZipTests.NoPortable(); view.Assets.AllowOnlineIcons = false;
            var gimp = ProgramCatalog.Entries.Single(e => e.Id == "gimp"); var vlc = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
            fake.ResolveHandler = (entry, _) => Task.FromResult<ProgramPackage?>(new(entry.PackageId!, "2.0", "Fixture", DateTime.UtcNow, VersionSource: "WinGet",
                TargetProof: new(entry.PackageId!, "2.0", "fixture", "nullsoft", "fixture")));
            var host = (Grid)main.FindName("Root");
            foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 }) foreach (string action in new[] { "cancel", "close", "escape" })
            {
                ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Wait(view.RefreshAsync());
                view.Selected.Clear(); view.Select(gimp.Id, true); if (action == "cancel") view.Select(vlc.Id, true);
                var before = view.Selected.ToArray(); var work = view.InstallAsync();
                Until(() => view.SelectionOpen && UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).Any());
                var panel = view.OperationPanel!;
                var picker = UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).First();
                UnifiedDialogAppearance.Children<CheckBox>(picker).Single().IsChecked = true;
                UnifiedDialogAppearance.Children<TextBox>(picker).Single().Text = Path.Combine(output, "NotInstalled", action + size);
                Assert(view.IsWorking && main.IsBusy, "selection owns the busy state before " + action);
                var layer = host.Children.OfType<Grid>().Single(x => Panel.GetZIndex(x) == 6000);
                if (action == "escape")
                    main.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                else
                    UnifiedDialogAppearance.Children<Button>(layer).Single(b => action == "cancel"
                        ? b.Content is TextBlock t && t.Text == (english ? "Cancel" : "Abbrechen")
                        : AutomationProperties.GetName(b) == (english ? "Close" : "Schließen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Wait(work);
                if (!english && action == "cancel") Save(main, "Nach-Abbruch-" + size);
                Assert(fake.Started.Count == 0 && fake.Websites == 0, "selection cancellation never starts an installer or publisher route");
                Assert(!view.IsWorking && !main.IsBusy && !view.SelectionOpen, "selection cancellation releases all operation and selection locks");
                Assert(view.OperationPanel is null && !view.HasOverlay, "selection cancellation removes progress instead of showing a stopped completion card");
                Assert(!panel.Progress.IsRunning && !panel.Expanded, "cancelled preparation disposes the progress bar and panel");
                Assert(!UnifiedDialogAppearance.Children<Canvas>(host).Any(c => c.Name == "ProgramProgressLayer"), "no progress/reopen layer remains");
                Assert(view.Selected.SetEquals(before), "unexecuted selection remains selected");
                view.Select(vlc.Id, true); Assert(view.Selected.Contains(vlc.Id), "selection immediately usable again");
            }
            // Multiple updates still use the confirmation dismissal route.
            fake.Installed[gimp.Id] = fake.Installed[vlc.Id] = "1.0"; fake.Updates[gimp.Id] = fake.Updates[vlc.Id] = "2.0"; Wait(view.RefreshAsync());
            bool updateConfirmation = false; view.ConfirmForTests = _ => { updateConfirmation = true; return false; }; Wait(view.InstallEntriesAsync([gimp,vlc]));
            Assert(updateConfirmation && fake.Started.Count == 0 && view.OperationPanel is null && !view.HasOverlay, "cancelled update confirmation leaves neither installer nor completion dialog");
            fake.Installed.Clear(); fake.Updates.Clear(); Wait(view.RefreshAsync());
            // Stopping an installer that has really started must retain its running/result UI.
            view.ConfirmForTests = _ => true; fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = view.InstallEntriesAsync([gimp]); Until(() => fake.Started.Count == 1);
            var runningPanel = view.OperationPanel!; view.RequestStop(); Pump();
            Assert(!running.IsCompleted && view.IsWorking && runningPanel.Expanded, "active installer is not dismissed by stop request");
            fake.Pending.SetResult(0); Wait(running);
            Assert(!view.IsWorking && !main.IsBusy && view.OperationPanel == runningPanel && runningPanel.Expanded, "actual installer completion still keeps its result card");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(runningPanel.Frame).Any(t => t.Text is "Vorgang beendet" or "Operation finished"), "actual stopped operation retains completion summary");
            runningPanel.Dispose();
        }
        finally { main.Close(); }
        Console.WriteLine($"PASS {checks} selection cancel/X/Esc, update cancellation, progress cleanup and active-installer preservation checks; fixture installers only"); return 0;
    }
}
