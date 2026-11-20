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
        var worker = AppUninstall.CreateWorker(executable, registration, 123, 456L, [], false, new string('A', 64));
        Assert(!worker.UseShellExecute && worker.CreateNoWindow && worker.ArgumentList.Contains("-EncodedCommand"), "Worker uses argument list.");
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(worker.ArgumentList.Last()));
        string payload = script.Split('\'')[1];
        using var json = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
        var root = json.RootElement;
        Assert(root.GetProperty("Executable").GetString() == executable && root.GetProperty("StartTicks").GetString() == "456", "Executable and start time round-trip through the worker arguments.");
        Assert(root.GetProperty("Groups").GetArrayLength() == 0, "Keep data is passed to the worker.");
        var arguments = root.GetProperty("Command").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert(arguments.Contains(AppUninstall.ProductCode) && arguments.Contains("user") && arguments.Contains("--preserve") && !arguments.Contains("--purge"), "Only the scoped portable package is selected.");

        foreach (string scenario in new[] { "Desktop", "Downloads", "changed", "locked", "linked", "winget" })
        {
            var test = Directory.CreateDirectory(Path.Combine(output, scenario + "-" + Guid.NewGuid().ToString("N"))).FullName;
            var copy = Path.Combine(test, "PowerLeitstand-PRO.exe");
            var other = Path.Combine(test, "second.exe");
            File.WriteAllText(copy, "disposable test executable"); File.WriteAllText(other, "keep");
            var local = Path.Combine(test, AppUninstall.DataFolderName);
            string Owned(string path) => Path.Combine(local, path);
            foreach (var leaf in new[] { "logs", "Backups", "sound-cache" })
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Owned(leaf)).FullName, "sample.txt"), "owned");
            File.WriteAllText(Owned("settings.json"), "{}");
            var groups = scenario switch { "Desktop" => new[] { "logs" }, "Downloads" => [],
                "linked" => ["logs", "settings"], _ => ["logs", "backups", "settings"] };
            var start = AppUninstall.CreateWorker(copy, scenario == "winget" ? registration : null, int.MaxValue, 0, groups, true, ProgramPortableUpdates.Hash(copy));
            string isolated = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1]));
            string fixturePath = "'" + test.Replace("'", "''") + "'";
            isolated = isolated.Replace("[Environment]::GetFolderPath('LocalApplicationData')", fixturePath);
            Assert(!isolated.Contains("GetFolderPath") && isolated.Contains("Join-Path (" + fixturePath + ") $payload.Root"), "Cleanup is confined to the disposable profile");
            if (scenario == "winget")
            {
                int begin = isolated.IndexOf("function Read-Registration", StringComparison.Ordinal);
                int end = isolated.IndexOf("function Get-SafeAttributes", StringComparison.Ordinal);
                isolated = isolated[..begin] + """
                    function Read-Registration {
                        if ($script:done) { return }
                        $key = [pscustomobject]@{}
                        $key | Add-Member ScriptMethod Dispose {}
                        $key | Add-Member ScriptMethod GetValue { param($field)
                            switch ($field) {
                                'WinGetPackageIdentifier' {$payload.PackageId} 'WinGetSourceIdentifier' {$payload.SourceId}
                                'WinGetInstallerType' {'portable'} 'PortableTargetFullPath' {$payload.Executable}
                                'InstallLocation' {[IO.Path]::GetDirectoryName($payload.Executable)}
                            }
                        }
                        $key
                    }

                    """ + isolated[end..];
                isolated = isolated.Replace("& $winget @arguments | Out-Null", "$script:done=$true; $LASTEXITCODE=0");
                Assert(!isolated.Contains("OpenBaseKey") && !isolated.Contains("& $winget"), "Package manager and registry replaced in fixture");
            }
            isolated = System.Text.RegularExpressions.Regex.Replace(isolated, @"(?m)^.*\[Windows.MessageBox\]::Show.*$", _ => "Write-Output ($message + [string]$_.Exception)");
            if (scenario == "changed") File.AppendAllText(copy, "replacement");
            using var locked = scenario == "locked" ? File.Open(copy, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            if (scenario == "linked")
            {
                var junction = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                junction.Arguments = "/d /c mklink /J \"" + Owned(@"logs\redirect") + "\" \"" + Owned("Backups") + "\"";
                using var created = System.Diagnostics.Process.Start(junction)!; created.WaitForExit();
                Assert(created.ExitCode == 0, "Disposable junction created");
            }
            start.ArgumentList[^1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(isolated));
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = Task.Run(() => WinGetService.QueryAsync(start, limit.Token)).GetAwaiter().GetResult();
            bool failed = scenario is "changed" or "locked" or "linked";
            Assert((result.ExitCode == 0) == !failed, scenario + " worker exit " + result.ExitCode + " " + result.Output);
            Assert(File.Exists(copy) == (scenario is "changed" or "locked") && File.Exists(other), scenario + " exact target");
            Assert(File.Exists(Owned(@"logs\sample.txt")) == (failed || !groups.Contains("logs")), scenario + " log scope");
            Assert(File.Exists(Owned("settings.json")) == (scenario is "changed" or "locked" || !groups.Contains("settings")), scenario + " settings scope");
            Assert(File.Exists(Owned(@"Backups\sample.txt")) == (failed || !groups.Contains("backups")), scenario + " backup scope");
        }

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
                Assert(action.Content is TextBlock { Text: "Deinstallieren" }, "Manual copy offers confirmed removal.");
                Screenshot(dialog, "App-Deinstallation-Manuell.png");
                dialog.Close();
            }, DispatcherPriority.ApplicationIdle);
            Click(removal);
            Assert(opened, "Settings action opened the dialog.");

            var simulated = new AppUninstallWindow(main, ThemePalette.For("Standard"), false, executable, registration);
            simulated.Loaded += (_, _) => app.Dispatcher.BeginInvoke(() =>
            {
                var choices = UnifiedDialogAppearance.Children<CheckBox>(simulated.Content).ToArray();
                Assert(choices.Length == 3 && choices.All(x => x.IsChecked == false), "Data removal requires explicit selection.");
                Screenshot(simulated, "App-Deinstallation-WinGet.png");
                choices.Single(x => Equals(x.Tag, "logs")).IsChecked = true;
                Click(UnifiedDialogAppearance.Children<Button>(simulated.Content).Single(x => x.Name == "ConfirmAppUninstall"));
            }, DispatcherPriority.ApplicationIdle);
            Assert(simulated.ShowDialog() == true && simulated.DataGroups.SequenceEqual(["logs"]), "Confirmation captures the explicit data choice.");
            Console.WriteLine($"PASS {checks} app uninstall checks. No installed copy was removed.");
            return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
