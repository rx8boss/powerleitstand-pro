using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramReinstallFlowTests
{
    internal static int Run(App app, string output)
    {
        int checks = 0, installs = 0; string current = "1.0"; TaskCompletionSource<int>? installerGate = null;
        var commands = new List<ProcessStartInfo>();
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Until(Func<bool> condition)
        { var timer = Stopwatch.StartNew(); while (!condition() && timer.Elapsed.TotalSeconds < 60) { Pump(); Thread.Sleep(5); } if (!condition()) throw new TimeoutException("Reinstall flow fixture"); Pump(); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var service = new ProgramInstallService(query: (start, token) =>
        {
            token.ThrowIfCancellationRequested(); string verb = start.ArgumentList[0];
            if (verb == "search") return Task.FromResult(new WinGetResult(0, "Reinstall Fixture  FixturePublisher.ReinstallFixture  2.0\n"));
            if (verb == "list") return Task.FromResult(new WinGetResult(0, current == "2.0" ? "" : "Reinstall Fixture  FixturePublisher.ReinstallFixture  " + current + "  2.0\n"));
            if (verb == "export")
            {
                string file = start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1];
                File.WriteAllText(file, JsonSerializer.Serialize(new { Sources = new[] { new { SourceDetails = new { Name = "winget" }, Packages = new[] { new { PackageIdentifier = "FixturePublisher.ReinstallFixture", Version = current } } } } }));
                return Task.FromResult(new WinGetResult(0, ""));
            }
            int versionIndex = start.ArgumentList.IndexOf("--version");
            return Task.FromResult(new WinGetResult(0, "Found Reinstall Fixture [FixturePublisher.ReinstallFixture]\nVersion: " + (versionIndex < 0 ? "2.0" : start.ArgumentList[versionIndex + 1]) + "\n"));
        }, install: async (start, started) =>
        {
            commands.Add(start); installs++; started();
            if (installerGate is not null) await installerGate.Task;
            current = start.ArgumentList[start.ArgumentList.IndexOf("--version") + 1]; return 0;
        });
        var main = new MainWindow(Path.Combine(output, "IsolatedSettings"), new AppSettings { SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons = false; view.Service = service;
            var search = service.SearchAsync("Reinstall", default); Wait(search); var entry = search.Result.Single();
            ((Dictionary<string, ProgramEntry>)typeof(ProgramCatalogView).GetField("_sessionEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!)[entry.Id] = entry;
            void Scan() { var scan = service.InspectAsync(default); Wait(scan); typeof(ProgramCatalogView).GetProperty("Inventory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, scan.Result); }
            Scan(); Assert(view.Inventory.HasUpdate(entry.Id) && view.CanReinstall(entry), "Discovered installed program has independent update and reinstall paths");
            view.ConfirmForTests = _ => false; Wait(view.InstallEntriesAsync([entry], allowReinstall: true));
            Assert(installs == 0 && !view.IsWorking, "Cancelled reinstallation executes no installer and releases queue");
            Assert(view.OperationPanel is null && !view.HasOverlay, "Cancelled reinstallation closes preparation instead of showing an empty completion card");
            view.ConfirmForTests = _ => true; installerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var same = view.InstallEntriesAsync([entry], allowReinstall: true); Until(() => installs == 1);
            Assert(view.IsWorking && !view.CanChooseProgramFile, "Selections remain locked until the installer finishes");
            installerGate.SetResult(0); Wait(same); installerGate = null;
            Assert(current == "1.0" && view.Inventory.HasUpdate(entry.Id) && !view.IsWorking, "Same-version reinstall does not silently consume available update");
            Assert(commands.Single().ArgumentList[0] == "install" && commands.Single().ArgumentList.Contains("--force"), "Reinstall uses force-install route, not upgrade route");
            view.ConfirmForTests = null;
            var latest = view.InstallEntriesAsync([entry], allowReinstall: true);
            var host = (Grid)main.FindName("Root");
            Until(() => UnifiedDialogAppearance.Children<ComboBox>(host).Any(b => b.Name == "ProgramReinstallVersionChoice"));
            var versions = UnifiedDialogAppearance.Children<ComboBox>(host).Single(b => b.Name == "ProgramReinstallVersionChoice");
            Assert(versions.Items.Count == 2, "Confirmation offers same and latest available versions"); versions.SelectedIndex = 1; Pump();
            UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Content is TextBlock t && t.Text == "↻ Neu installieren").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(latest);
            Assert(installs == 2 && current == "2.0" && !view.Inventory.HasUpdate(entry.Id) && !view.IsWorking, "Explicit latest choice propagates through execution and final verification");
            Assert(commands[1].ArgumentList.Contains("2.0") && commands[1].ArgumentList.Contains("--force"), "Selected target version reaches real command boundary");
            current = "1.0"; Scan();
            view.ConfirmForTests = _ => { current = "1.1"; return true; };
            Wait(view.InstallEntriesAsync([entry], allowReinstall: true));
            Assert(installs == 2 && !view.IsWorking, "Changed installed version after confirmation prevents stale reinstall");
            Console.WriteLine("PASS " + checks + " end-to-end reinstallation selection/queue/locking/version/revalidation checks; controlled runner only, no third-party installation.");
            return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
