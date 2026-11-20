using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerControlProWpf;

internal static class AppUninstallTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        const string directory = @"C:\Test\WinGet\Packages\PowerLeitstand [Test]";
        const string executable = directory + @"\PowerLeitstand-PRO.exe";
        const string source = "Microsoft.Winget.Source_8wekyb3d8bbwe";
        Assert(AppUninstall.Matches(executable, executable, directory, AppUninstall.PackageId, source, "portable"), "Exact portable identity.");
        Assert(!AppUninstall.Matches(executable, @"C:\Other\PowerLeitstand-PRO.exe", directory, AppUninstall.PackageId, source, "portable"), "Other copy denied.");
        Assert(!AppUninstall.Matches(executable, executable, @"C:\Test", AppUninstall.PackageId, source, "portable"), "Unrelated install directory denied.");
        Assert(!AppUninstall.Matches(executable, executable, directory, "Other.PowerLeitstandPRO", source, "portable"), "Other package denied.");
        Assert(!AppUninstall.Matches(executable, executable, directory, AppUninstall.PackageId, "OtherSource", "portable"), "Other source denied.");
        Assert(!AppUninstall.Matches(executable, executable, directory, AppUninstall.PackageId, source, "msi"), "Other installer type denied.");
        Assert(!AppUninstall.Matches(@"\\server\PowerLeitstand-PRO.exe", @"\\server\PowerLeitstand-PRO.exe", @"\\server\", AppUninstall.PackageId, source, "portable"), "Network path denied.");
        var registration = new AppUninstall.Registration(AppUninstall.ProductCode, RegistryHive.CurrentUser, RegistryView.Registry64);
        var worker = AppUninstall.CreateWorker(executable, registration, 123, 456L, true, false);
        Assert(!worker.UseShellExecute && worker.CreateNoWindow && worker.ArgumentList.Contains("-EncodedCommand"), "Worker uses argument list.");
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(worker.ArgumentList.Last()));
        string payload = script.Split('\'')[1];
        using var json = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = json.RootElement;
        Assert(root.GetProperty("Executable").GetString() == executable && root.GetProperty("StartTicks").GetString() == "456", "Executable and start time round-trip through the worker arguments.");
        Assert(root.GetProperty("Keep").GetBoolean(), "Keep data is passed to the worker.");
        var arguments = root.GetProperty("Command").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert(arguments.Contains(AppUninstall.ProductCode) && arguments.Contains("user") && arguments.Contains("--preserve") && !arguments.Contains("--purge"), "Only the scoped portable package is selected.");

        var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Screenshot(Window window, string fileName)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, fileName));
            png.Save(file);
        }
        try
        {
            main.Show();
            Pump();
            Click((Button)main.FindName("SettingsHeaderButton"));
            Pump();
            var removal = (Button)main.FindName("AppUninstallButton");
            var updates = (Button)main.FindName("CheckUpdatesButton");
            Assert(removal.IsVisible && Grid.GetRow(removal) < Grid.GetRow(updates), "App uninstall is in settings above the bottom update action.");
            var settings = (Border)main.FindName("SettingsPanel");
            var done = (Button)main.FindName("DoneButton");
            var sizes = (ComboBox)main.FindName("SizeBox");
            foreach (int size in new[] { 2, 1 })
            {
                sizes.SelectedIndex = size;
                Pump();
                var bounds = done.TransformToAncestor(settings).TransformBounds(new Rect(done.RenderSize));
                Assert(bounds.Bottom <= settings.ActualHeight - 2, "The bottom settings action fits.");
            }
            Screenshot(main, "Optionen-Deinstallation.png");
            bool opened = false;
            app.Dispatcher.BeginInvoke(() =>
            {
                var dialog = app.Windows.OfType<AppUninstallWindow>().Single();
                opened = true;
                var action = UnifiedDialogAppearance.Children<Button>(dialog.Content).Single(x => x.Name == "ConfirmAppUninstall");
                Assert(action.Content is TextBlock { Text: "Ordner öffnen" }, "Manual copy offers its folder.");
                Screenshot(dialog, "App-Deinstallation-Manuell.png");
                dialog.Close();
            }, DispatcherPriority.ApplicationIdle);
            Click(removal);
            Assert(opened, "Settings action opened the dialog.");

            var simulated = new AppUninstallWindow(main, ThemePalette.For("Standard"), false, executable, registration);
            simulated.Loaded += (_, _) => app.Dispatcher.BeginInvoke(() =>
            {
                var keep = UnifiedDialogAppearance.Children<CheckBox>(simulated.Content).Single();
                Assert(keep.IsChecked == true && simulated.KeepData, "Keep settings and logs is selected by default.");
                Screenshot(simulated, "App-Deinstallation-WinGet.png");
                keep.IsChecked = false;
                Assert(UnifiedDialogAppearance.Children<TextBlock>(simulated.Content)
                    .Any(x => x.IsVisible && x.Text.Contains("settings.json") && x.Text.EndsWith("logs")), "Opting out shows the exact data paths.");
                Click(UnifiedDialogAppearance.Children<Button>(simulated.Content).Single(x => x.Name == "ConfirmAppUninstall"));
            }, DispatcherPriority.ApplicationIdle);
            Assert(simulated.ShowDialog() == true && !simulated.KeepData, "Confirmation captures the explicit data choice.");
            Console.WriteLine($"PASS {checks} app uninstall checks. No installed copy was removed.");
            return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
