using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramReinstallUiTests
{
    internal static int Run(App app, string output, bool largeImageOnly = false)
    {
        int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 25) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException("Reinstallation UI fixture"); task.GetAwaiter().GetResult(); Pump(); }
        var main = new MainWindow(Path.Combine(output, "IsolatedSettings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        var previousReader = ProgramRemovalDialog.Reader;
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons = false;
            var entry = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
            var service = new ProgramInstallService(query: (start, _) => Task.FromResult(new WinGetResult(0,
                "Found VLC [VideoLAN.VLC]\nVersion: 3.0.22\n")), install: (_, _) => throw new Exception("Real installer must never launch in layout matrix"));
            view.Service = service;
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(x => x.Tag is string).ToArray())
            {
                if (largeImageOnly && theme.Tag?.ToString() != "Standard") continue;
                themes.SelectedItem = theme;
                foreach (bool english in largeImageOnly ? new[] { false } : new[] { false, true }) foreach (int size in largeImageOnly ? new[] { 4 } : new[] { 1, 4 })
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                    foreach (string state in largeImageOnly ? new[] { "update" } : new[] { "update", "current", "unknown", "absent" })
                    {
                        var installed = new Dictionary<string, string?>(); var ev = new Dictionary<string, ProgramInstallation>();
                        if (state != "absent") { installed[entry.Id] = state == "unknown" ? null : state == "current" ? "3.0.22" : "3.0.21"; ev[entry.Id] = new(installed[entry.Id], "Registry"); }
                        var inventory = new ProgramInventory(true, installed, Evidence: ev,
                            Updates: state == "update" ? new Dictionary<string, string> { [entry.Id] = "3.0.22" } : null);
                        Set(view, "Inventory", inventory); SetField(service, "_lastEvidence", ev);
                        ((Dictionary<string, string>)typeof(ProgramInstallService).GetField("_installedPackageIds", flags)!.GetValue(service)!)[entry.Id] = entry.PackageId!;
                        SetField(service, "_recentExport", new Dictionary<string, string?> { [entry.PackageId!] = installed.GetValueOrDefault(entry.Id) });
                        view.ShowInfo(entry, view, false); var info = view.Info!; Wait(info.Loading);
                        var buttons = UnifiedDialogAppearance.Children<Button>(info.Frame).ToArray();
                        var update = buttons.Single(b => b.Name == "ProgramInfoUpdateAction"); var reinstall = buttons.Single(b => b.Name == "ProgramInfoStatusReinstall"); var remove = buttons.Single(b => b.Name == "ProgramInfoUninstallAction");
                        Assert(update.Visibility == (state == "update" ? Visibility.Visible : Visibility.Collapsed), "Only a verified actionable update is offered");
                        Assert(reinstall.Visibility == (state == "absent" ? Visibility.Collapsed : Visibility.Visible), "Reinstallation visible for every detected installation, including unknown version");
                        Assert(remove.Visibility == (state == "absent" ? Visibility.Collapsed : Visibility.Visible), "Uninstall absent for uninstalled program");
                        Assert(((SolidColorBrush)update.BorderBrush).Color.G > ((SolidColorBrush)update.BorderBrush).Color.R * 2, "Update has green semantic accent");
                        Assert(remove.Style == (Style)main.FindResource("UnifiedDialogDanger"), "Uninstall uses red app danger style");
                        Assert(((TextBlock)reinstall.Content).Text == (english ? "Reinstall" : "Neu installieren"), "Reinstallation caption localized");
                        Assert(Math.Abs(info.Frame.ActualWidth - (size == 1 ? 420 : 580)) < 1 && info.Frame.ActualHeight <= 540, "Approved original outer dimensions preserved");
                        foreach (var button in buttons.Where(b => b.Visibility == Visibility.Visible))
                        { var p = button.TranslatePoint(new Point(), info.Frame); Assert(p.X >= -1 && p.X + button.ActualWidth <= info.Frame.ActualWidth + 1, "Visible button fits horizontally"); }
                        if (state != "absent")
                        {
                            var bottom = buttons.Single(b => b.Name == "ProgramInfoWebsite").TranslatePoint(new Point(), info.Frame).Y;
                            foreach (var button in new[] { reinstall, remove })
                            { var position = button.TranslatePoint(new Point(), info.Frame); Assert(position.Y >= 0 && position.Y + button.ActualHeight < bottom, "Fixed installed actions are fully visible above footer without scrolling"); }
                        }
                        if (!english && theme.Tag?.ToString() == "Standard" && state == "update") Save(main, Path.Combine(output, "App-Dialog-Neuinstallation-" + size + ".png"));
                        info.Close();
                    }
                }
            }
            // Targeted uninstall UI must never offer an unrelated application's uninstaller.
            ProgramRemovalDialog.Reader = _ => [new("vlc-record", "VLC", "VideoLAN", "3.0.21", null, "Registry", CatalogId: entry.Id),
                new("other-record", "Other App", "Other Publisher", "1.0", null, "Registry")];
            var removal = ProgramRemovalDialog.ShowAsync(view, entry); Pump();
            var host = (Grid)main.FindName("Root"); var overlay = host.Children.OfType<Grid>().Single(g => g.Name == "InstalledProgramsOverlay");
            var timer = Stopwatch.StartNew(); while (!UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "VLC") && timer.Elapsed.TotalSeconds < 15) { Pump(); Thread.Sleep(5); }
            Assert(UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "VLC") && !UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "Other App"), "Uninstall dialog only exposes matching installation");
            UnifiedDialogAppearance.Children<Button>(overlay).Single(b => b.Content is TextBlock t && t.Text == (view.English ? "Close" : "Schließen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(removal);
            Console.WriteLine("PASS " + checks + " reinstallation/update/uninstall dialog checks; fixture data, seven themes, DE/EN, compact/large; no uninstall or installer executed.");
            return 0;
        }
        finally { ProgramRemovalDialog.Reader = previousReader; main.Close(); Pump(); }
    }
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Save(Window window, string path)
    {
        // The execution host has no capturable interactive desktop. Export the actual WPF
        // visual as XPS; rasterize independently, without pretending this is a live screenshot.
        using var document = new System.Windows.Xps.Packaging.XpsDocument(Path.ChangeExtension(path, ".xps"), FileAccess.ReadWrite);
        var ticket = new System.Printing.PrintTicket { PageMediaSize = new System.Printing.PageMediaSize(window.ActualWidth, window.ActualHeight) };
        System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(document).Write((Visual)window.Content, ticket);
    }
}
