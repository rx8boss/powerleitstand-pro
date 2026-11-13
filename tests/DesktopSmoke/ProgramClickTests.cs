using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramClickTests
{
    internal static int Run(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic; int count = 0;
        void Assert(bool value, string reason) { if (!value) throw new Exception(reason); count++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 25) { Pump(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); Pump(); }
        void Pause(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(8); } }
        void Save(Window window, string name)
        {
            Pump(); window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            var fake = new ProgramCatalogTests.Fake(); fake.Installed["google-chrome"] = "1.0"; fake.Updates["google-chrome"] = "2.0"; fake.Installed["winrar"] = "7.0"; fake.Updates["winrar"] = "7.1";
            view.Service = fake; Wait(view.RefreshAsync());
            var entry = ProgramCatalog.Entries.Single(x => x.Id == "notepad");
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(x => x.Tag is string).ToArray())
            foreach (bool english in new[] { false, true })
            foreach (int size in new[] { 1, 2, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; view.SetLanguage(english); view.ClearSearch(); view.SetCategory("tools"); Pump();
                var tile = view.Tiles.Single(t => t.Tag?.ToString() == entry.Id);
                var tileButton = UnifiedDialogAppearance.Children<Button>(tile).First();
                bool Inside(DependencyObject? hit, DependencyObject target)
                { while (hit is not null) { if (ReferenceEquals(hit, target)) return true; hit = VisualTreeHelper.GetParent(hit); } return false; }
                Assert(Inside(tile.InputHitTest(new Point(tile.ActualWidth - 4, tile.ActualHeight - 4)) as DependencyObject, tileButton), "empty tile area belongs to info button");
                var tileCheck = UnifiedDialogAppearance.Children<CheckBox>(tile).Single();
                var checkPoint = tileCheck.TranslatePoint(new Point(tileCheck.ActualWidth / 2, tileCheck.ActualHeight / 2), tile);
                Assert(Inside(tile.InputHitTest(checkPoint) as DependencyObject, tileCheck), "checkbox remains independent above full tile button");
                tileCheck.IsChecked = true; Assert(view.Info is null && view.Selected.Contains(entry.Id), "checkbox selects without opening info"); tileCheck.IsChecked = false;
                tile.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                Assert(view.Info is null, "hover never opens program info");
                var open = UnifiedDialogAppearance.Children<Button>(tile).First(); open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Assert(view.Info?.EntryId == entry.Id, "click opens matching app info"); var info = view.Info!; Wait(info.Loading);
                var host = (Grid)main.FindName("Root"); var position = info.Frame.TranslatePoint(new Point(0, 0), host); var center = position.Y + info.Frame.ActualHeight / 2;
                Assert(position.Y >= -1 && position.Y + info.Frame.ActualHeight <= host.ActualHeight + 1, "original sized info bounded inside app");
                Assert(Math.Abs(center - host.ActualHeight / 2) < 2, "info centered in app");
                Assert(Math.Abs(info.Frame.ActualWidth - (view.Compact ? 420 : 580)) < 1, "approved wider reference layout applied");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Any(t => t.Name == "ProgramInfoDescription" && t.Text.Length > 0), "full info fields retained");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Any(t => t.Text == (english ? "Determined by the installer" : "Vom Installer festgelegt")), "unknown default path labelled honestly");
                var website = UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b => b.Name == "ProgramInfoWebsite");
                Assert(((TextBlock)website.Content).Text == (english ? "↗ Website" : "↗ Webseite"), "website caption stays short");
                var scroll = UnifiedDialogAppearance.Children<ScrollViewer>(info.Frame).First(); Assert(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "scrollbars remain invisible");
                Assert(((DispatcherTimer)typeof(ProgramInfoCard).GetField("_leave", flags)!.GetValue(info)!).Interval == TimeSpan.FromSeconds(20), "idle interval exactly twenty seconds");
                if (theme.Tag?.ToString() == "Standard" && !english && size != 2) Save(main, "App-Info-" + size);
                var other = ProgramCatalog.Entries.Single(e => e.Id == "winrar"); view.ShowInfo(other, view, false); Wait(view.Info!.Loading); Assert(!info.IsOpen && view.Info.EntryId == other.Id, "another app replaces info");
                view.CloseInfo(); view.Select("google-chrome", true); Assert(view.UpdateButton.Visibility == Visibility.Visible && view.UpdateButton.IsEnabled, "update button visible for confirmed updates");
                view.Selected.Clear(); view.Select("google-chrome", true); Assert(!view.InstallButton.IsEnabled, "install button excludes installed update selections"); view.Select("7-zip", true); Assert(view.InstallButton.IsEnabled, "install button accepts new app selection");
                var choose = ProgramUpdatesDialog.ShowAsync(view, view.UpdateEntries.ToArray()); Pump();
                var overlay = UnifiedDialogAppearance.Children<Grid>(main).Single(g => g.Name == "ProgramUpdatesOverlay");
                var checks = UnifiedDialogAppearance.Children<CheckBox>(overlay).ToArray(); Assert(checks.Length == 2 && checks.Count(c => c.IsChecked == true) == 1, "updates have independent selectable rows");
                var start = UnifiedDialogAppearance.Children<Button>(overlay).Single(b => b.Name == "ProgramUpdatesStart");
                foreach (var check in checks) check.IsChecked = false; Assert(!start.IsEnabled, "no selection disables update start");
                checks.Single(c => c.Tag?.ToString() == "winrar").IsChecked = true; Assert(start.IsEnabled, "one selected update enables start");
                if (theme.Tag?.ToString() == "Standard" && !english && size != 2) Save(main, "Update-Auswahl-" + size);
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(choose); Assert(choose.Result is { Length: 1 } && choose.Result[0].Id == "winrar", "only explicit chosen updates returned");
                Assert(!view.SelectionOpen && fake.Started.Count == 0, "closing selection starts no installer");
                view.SetQuery("Firefox"); Pump(); Assert(view.VisibleEntries.Any(e => e.Id == "firefox"), "search crosses previous tools category");
                Assert(((Grid)typeof(ProgramCatalogView).GetField("_tabs", flags)!.GetValue(view)!).Visibility == Visibility.Collapsed, "tabs hidden while query exists");
                view.ClearSearch(); Pump(); Assert(((Grid)typeof(ProgramCatalogView).GetField("_tabs", flags)!.GetValue(view)!).Visibility == Visibility.Visible, "clear restores tabs");
                Console.WriteLine("PASS " + theme.Tag + " " + (english ? "EN" : "DE") + " size=" + size);
            }
            view.SetLanguage(false); view.ClearSearch(); view.SetCategory("all");
            fake.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); view.ConfirmForTests = _ => true;
            var chrome = ProgramCatalog.Entries.Single(e => e.Id == "google-chrome"); var work = view.InstallEntriesAsync([chrome]); Pump();
            Assert(view.IsWorking, "update executing retains work lock"); var selectedBefore = view.Selected.ToArray(); view.Select("notepad", true); Assert(view.Selected.SetEquals(selectedBefore), "selection guard rejects changes during work");
            fake.Pending.SetResult(0); Wait(work); Assert(!view.IsWorking && !view.Inventory.HasUpdate(chrome.Id), "update refresh removes completed update from pending list");
            fake.Updates.Clear(); Wait(view.RefreshAsync()); Assert(view.UpdateButton.Visibility == Visibility.Collapsed, "no updates hides update button");
            view.ShowInfo(entry, view, false); Wait(view.Info!.Loading); var idle = view.Info!;
            Pause(11000); idle.Frame.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Pause(10500); Assert(idle.IsOpen, "interaction resets timeout rather than closing at original twenty seconds");
            Pause(10500); Assert(!idle.IsOpen, "twenty idle seconds closes info even after interaction");
            Console.WriteLine("PASS " + count + " click/update/layout checks; no real installation"); return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
