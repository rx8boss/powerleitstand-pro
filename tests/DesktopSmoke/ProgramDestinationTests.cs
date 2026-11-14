using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramDestinationTests
{
    private static ProgramPackage Package(ProgramEntry entry) => new(entry.PackageId!, "2.0", "", DateTime.UtcNow, VersionSource: "WinGet",
        TargetProof: new(entry.PackageId!, "2.0", "fixture-hash", "nullsoft", "fixture"));

    internal static async Task<int> Logic(string output)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var empty = new ProgramInventory(true, new Dictionary<string, string?>());
        foreach (var entry in ProgramCatalog.Entries)
        {
            Assert(ProgramDestinationPolicy.Restriction(new(entry, null), empty, false) is not null, "publisher route has an explicit destination explanation: " + entry.Id);
            if (entry.PackageId is null) continue;
            var package = Package(entry);
            foreach (bool english in new[] { false, true })
            {
                Assert(ProgramDestinationPolicy.Restriction(new(entry, package), empty, english) is null, "verified new-install destination available: " + entry.Id);
                Assert(ProgramDestinationPolicy.Restriction(new(entry, package with { TargetProof = null }), empty, english) is not null, "unconfirmed support never pretends to accept a destination: " + entry.Id);
                Assert(ProgramDestinationPolicy.Restriction(new(entry, package, Reinstall: true), empty, english) is not null, "reinstall never implies relocation: " + entry.Id);
                Assert(ProgramDestinationPolicy.Restriction(new(entry, package), empty, english, existingInstallation: true) is not null, "manual installed mark retains existing location: " + entry.Id);
                Assert(ProgramDestinationPolicy.Restriction(new(entry, package with { Version = "3.0" }), empty, english) is not null, "proof bound to version: " + entry.Id);
            }
        }
        Assert(ProgramInstallTargets.Overlapping([@"I:\Programs\First", @"i:\programs\first\Nested"]), "nested destinations conflict");
        Assert(ProgramInstallTargets.Overlapping([@"I:\Programs\First\", @"i:\programs\first"]), "same destination conflicts across casing and trailing slash");
        Assert(!ProgramInstallTargets.Overlapping([@"I:\Programs\First", @"I:\Programs\First-Other"]), "directory-boundary comparison keeps siblings distinct");
        Assert(!ProgramInstallTargets.Overlapping([@"I:\Programs\First", @"D:\Programs\First"]), "different drives remain independent");
        var installed = new ProgramInventory(true, new Dictionary<string, string?> { ["vlc"] = "1.0" });
        var vlc = ProgramCatalog.Entries.Single(e => e.Id == "vlc"); var candidate = Package(vlc);
        Assert(ProgramDestinationPolicy.Restriction(new(vlc, candidate), installed, false) is not null, "installed evidence prevents relocation");
        ProcessStartInfo? captured = null; int starts = 0, validations = 0; bool changed = false;
        var service = new ProgramInstallService(query: (_, _) => Task.FromResult(new WinGetResult(0, "{\"Sources\":[]}")),
            install: (start, started) => { captured = start; starts++; started(); return Task.FromResult(0); },
            targetResolver: (p, token) => { validations++; token.ThrowIfCancellationRequested(); return Task.FromResult(changed ? p.TargetProof! with { ManifestHash = "changed" } : p.TargetProof); });
        string target = Path.Combine(output, "Selected Drive", "VLC");
        int callbacks = 0;
        Assert(await service.InstallAtAsync(vlc, candidate, target, () => callbacks++) == 0 && starts == 1 && callbacks == 1 && validations == 1,
            "confirmed destination executes exactly one installer after fresh proof");
        Assert(captured!.ArgumentList[captured.ArgumentList.IndexOf("--location") + 1] == target && captured.ArgumentList.Contains("--version")
            && !captured.UseShellExecute && captured.CreateNoWindow, "selected complete path with spaces stays one argument in hidden pinned installation");
        async Task Blocked(ProgramPackage package, string path, string why)
        { int before = starts; try { await service.InstallAtAsync(vlc, package, path, () => { }); } catch (IOException) { Assert(starts == before, why); return; } throw new Exception("Expected blocked installation: " + why); }
        changed = true; await Blocked(candidate, target, "changed manifest prevents execution"); changed = false;
        await Blocked(candidate with { TargetProof = null }, target, "missing proof prevents execution");
        await Blocked(candidate with { VersionSource = "Publisher" }, target, "another route cannot reuse WinGet destination proof");
        await Blocked(candidate with { Version = "3.0" }, target, "wrong version prevents execution");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int beforeCancelled = starts;
        try { await service.InstallAtAsync(vlc, candidate, target, () => { }, cancelled.Token); throw new Exception("Cancelled request started"); }
        catch (OperationCanceledException) { Assert(starts == beforeCancelled, "cancel before validation never starts installer"); }
        using var during = new CancellationTokenSource(); bool lateStarted = false;
        var waiting = new ProgramInstallService(query: (_, _) => Task.FromResult(new WinGetResult(0, "")), install: (_, _) => { lateStarted = true; return Task.FromResult(0); },
            targetResolver: async (_, token) => { await Task.Delay(Timeout.Infinite, token); return candidate.TargetProof; });
        var pending = waiting.InstallAtAsync(vlc, candidate, target, () => { }, during.Token); during.Cancel();
        try { await pending; throw new Exception("Cancelled validation completed"); }
        catch (OperationCanceledException) { Assert(!lateStarted, "cancel during manifest validation never starts installer"); }
        await Blocked(candidate, @"relative\invalid", "invalid destination prevents execution");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "preserve.txt"), "user file");
        await Blocked(candidate, target, "occupied destination prevents execution");
        Assert(File.ReadAllText(Path.Combine(target, "preserve.txt")) == "user file", "existing target content preserved");
        var adw = ProgramCatalog.Entries.Single(e => e.Id == "adwcleaner");
        var portableRoot = Path.Combine(output, "AdwCleanerFixture");
        Directory.CreateDirectory(portableRoot);
        string executable = Path.Combine(portableRoot, "adwcleaner_8.8.1.exe");
        File.Copy(Environment.ProcessPath!, executable, true);
        var found = ProgramLocalInventory.AdwCleanerExecutable("8.8.1.639", [portableRoot], _ => ("AdwCleaner", "Malwarebytes Inc.", "8.8.1.639"));
        Assert(found == executable, "The WinGet package folder resolves the versioned portable executable");
        Assert(ProgramLocalInventory.AdwCleanerExecutable("8.8.1.639", [portableRoot], _ => ("AdwCleaner", "Other", "8.8.1.639")) is null,
            "A different publisher cannot become the shortcut target");
        var link = "AdwCleaner-Fixture-" + Guid.NewGuid().ToString("N");
        Assert(ProgramCatalogView.SaveDesktopShortcut(link, found, portableRoot) == true
            && ProgramPathResolver.ReadShortcut(Path.Combine(portableRoot, link + ".lnk")) == executable, "Portable package resolution creates a working link");
        File.Copy(executable, Path.Combine(portableRoot, "adwcleaner.exe"), true);
        Assert(ProgramLocalInventory.AdwCleanerExecutable("8.8.1.639", [portableRoot], _ => ("AdwCleaner", "Malwarebytes", "8.8.1.639")) is null, "Two matching copies stay ambiguous");

        Assert(ProgramLocalInventory.AdwCleanerExecutable("8.8.1.639", [portableRoot], _ => ("AdwCleaner", "Malwarebytes", "8.8.1.639"), executable) == executable,
            "The exact registered WinGet target resolves a portable alias without guessing");
        string adminManifest = $"""
            PackageIdentifier: {vlc.PackageId}
            PackageVersion: 2.0
            ManifestType: installer
            InstallerType: wix
            Scope: machine
            ElevationRequirement: elevationRequired
            Installers:
            - Architecture: x64
            """;
        var machineProof = ProgramInstallTargets.ParseManifest(vlc.PackageId!, "2.0", adminManifest)!;
        Assert(machineProof.RequiresMachineScope && !machineProof.SupportsLocation, "An administrator-only installer can be detected without custom path support");
        Assert(ProgramInstallTargets.ParseManifest(vlc.PackageId!, "2.0", adminManifest.Replace("elevationRequired", "elevationProhibited"))?.RequiresMachineScope == false,
            "Elevated batch excludes elevation-prohibited installers");
        var machinePackage = candidate with { TargetProof = machineProof };
        Assert(ProgramInstallElevation.Applies(new(vlc, machinePackage), empty, false)
            && !ProgramInstallElevation.Applies(new(adw, Package(adw)), empty, false)
            && !ProgramInstallElevation.Applies(new(vlc, machinePackage), empty, true), "Only supported machine installations use the elevated session");
        var request = new ProgramInstallElevation.Request(vlc.Id, vlc.PackageId!, "2.0", machineProof.ManifestHash, null);
        var verifiedCommand = await ProgramInstallElevation.VerifyCommandAsync(request, Environment.ProcessPath!, default, (_, _) => Task.FromResult<ProgramInstallTargetProof?>(machineProof));
        Assert(verifiedCommand.ArgumentList.Contains("--scope") && verifiedCommand.ArgumentList.Contains("machine") && verifiedCommand.ArgumentList.Contains("--exact")
            && verifiedCommand.ArgumentList.Contains("--no-upgrade") && !verifiedCommand.ArgumentList.Contains("--force"), "Elevated command pins the verified package and installation scope");
        bool rejected = false;
        try { await ProgramInstallElevation.VerifyCommandAsync(request with { Package = "Unrelated.Package" }, Environment.ProcessPath!, default); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "Elevated helper rejects a package that differs from the catalog entry");
        Console.WriteLine($"PASS {checks} catalog-wide destination policy, selected-path execution, revalidation and conflict checks; controlled installer only"); return 0;
    }

    internal static int Ui(App app, string output, string theme)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 30) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException("Destination UI fixture"); task.GetAwaiter().GetResult(); Pump(); }
        void Save(Window window, string name)
        { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file); }
        var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            var view = page.ShowPrograms(false); view.Service = new ProgramCatalogTests.Fake(); view.Assets.AllowOnlineIcons = false;
            var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
            var vlc = ProgramCatalog.Entries.Single(e => e.Id == "vlc"); var firefox = ProgramCatalog.Entries.Single(e => e.Id == "firefox");
            var gimp = ProgramCatalog.Entries.Single(e => e.Id == "gimp");
            foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                var host = (Grid)main.FindName("Root"); var package = Package(vlc);
                var dialog = ProgramSelectionDialog.ShowAsync(view, [new(vlc, package), new(firefox, Package(firefox) with { TargetProof = null })]); Pump();
                var pickers = UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).ToArray();
                Assert(pickers.Length == 2, "every program has an installation-location section");
                var supported = pickers.Single(p => UnifiedDialogAppearance.Children<CheckBox>(p).Single().IsEnabled);
                var fixedLocation = pickers.Single(p => !UnifiedDialogAppearance.Children<CheckBox>(p).Single().IsEnabled);
                Assert(UnifiedDialogAppearance.Children<TextBox>(fixedLocation).Single().IsReadOnly, "fixed installer location explicitly read-only");
                UnifiedDialogAppearance.Children<CheckBox>(supported).Single().IsChecked = true; Pump();
                string target = Path.Combine(output, "Chosen-" + size + (english ? "-EN" : "-DE"), "VLC");
                string example = Path.Combine(Path.GetPathRoot(output)!, "Programme", "VLC");
                try { ProgramInstallTargets.Validate(example); target = example; } catch (IOException) { }
                var path = UnifiedDialogAppearance.Children<TextBox>(supported).Single(); path.Text = target; Pump();
                var start = UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Content is TextBlock t && t.Text == (english ? "↓ Start selected" : "↓ Auswahl starten"));
                Assert(start.IsEnabled && supported.IsValid, "selected valid path enables batch with a standard-path sibling");
                path.Text = "invalid"; Pump(); Assert(!start.IsEnabled, "invalid path blocks entire batch");
                path.Text = target; Pump();
                var rect = start.TransformToAncestor(host).TransformBounds(new Rect(start.RenderSize));
                Assert(rect.Right <= host.ActualWidth + 1 && rect.Bottom <= host.ActualHeight + 1 && rect.Top >= 0, "confirmation remains inside compact and large windows");
                if (!english) Save(main, "Installationsort-" + size);
                var shortcuts = UnifiedDialogAppearance.Children<System.Windows.Controls.CheckBox>(host).Where(x => x.Name == "ProgramDesktopShortcut").ToArray();
                Assert(shortcuts.Length == 2 && shortcuts.All(x => x.IsChecked == false), "Each new installation has an opt-in shortcut");
                shortcuts[0].IsChecked = true;
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(dialog);
                Assert(dialog.Result![0].DesktopShortcut && !dialog.Result[1].DesktopShortcut, "Desktop shortcut choice is independent per program");
                Assert(dialog.Result![0].TargetPath == path.Text && dialog.Result[1].TargetPath is null, "each program retains its independent exact destination");
                var updates = new ProgramInventory(true, new Dictionary<string, string?> { [vlc.Id] = "1.0", [firefox.Id] = "1.0" },
                    Updates: new Dictionary<string, string> { [vlc.Id] = "2.0", [firefox.Id] = "2.0" }, UpdateCheckComplete: true);
                foreach (string selection in new[] { "single-update", "multiple-updates", "mixed", "reinstall", "cancel-update" })
                {
                    ProgramSelection[] options = selection switch
                    {
                        "multiple-updates" => [new(vlc, Package(vlc)), new(firefox, Package(firefox))],
                        "mixed" => [new(vlc, Package(vlc)), new(gimp, Package(gimp))],
                        "reinstall" => [new(vlc, Package(vlc), Reinstall: true)],
                        _ => [new(vlc, Package(vlc))]
                    };
                    var updateDialog = ProgramSelectionDialog.ShowAsync(view, options, checkedInventory: updates); Pump();
                    var destinations = UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).ToArray();
                    Assert(destinations.Length == (selection is "mixed" or "reinstall" ? 1 : 0), "only new installations and reinstalls retain destination sections: " + selection);
                    Assert(!UnifiedDialogAppearance.Children<TextBox>(host).Any(t => t.Name == "ProgramDestinationPath" && selection is "single-update" or "multiple-updates" or "cancel-update"), "updates never expose a destination field");
                    var confirm = UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Content is TextBlock t && t.Text == (selection == "reinstall"
                        ? english ? "↻ Reinstall" : "↻ Neu installieren" : english ? "↓ Start selected" : "↓ Auswahl starten"));
                    Assert(confirm.IsEnabled, "an update without a destination remains confirmable: " + selection);
                    var bounds = confirm.TransformToAncestor(host).TransformBounds(new Rect(confirm.RenderSize));
                    Assert(bounds.Bottom <= host.ActualHeight + 1 && bounds.Top >= 0, "update confirmation remains in compact/large window");
                    if (!english && selection == "single-update") Save(main, "Update-ohne-Pfad-" + size);
                    var finish = selection == "cancel-update" ? UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Name == "ProgramSelectionCancel") : confirm;
                    finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(updateDialog);
                    Assert(selection == "cancel-update" ? updateDialog.Result is null : updateDialog.Result!.Count == options.Length && updateDialog.Result.All(c => c.TargetPath is null),
                        "updates retain their installed location, new installs default to installer location, and cancellation stays available");
                }
                var panel = new ProgramProgressPanel(view, [vlc]); panel.Current(vlc, english ? "Installation" : "Installation", package); Pump();
                Assert(!UnifiedDialogAppearance.Children<FrameworkElement>(panel.Frame).Any(x => x.Name == "ProgramOperationSpinner"), "operation panel has no redundant loading spinner");
                Assert(panel.Progress.IsVisible && panel.Progress.IsRunning, "animated progress bar is the only activity indicator");
                panel.Step(vlc.Id, new(ProgramStep.Check, ProgramStepState.Done)); panel.Step(vlc.Id, new(ProgramStep.Download, ProgramStepState.Done)); panel.Step(vlc.Id, new(ProgramStep.Install, ProgramStepState.Active));
                panel.Measurement(47); Pump(); Assert(panel.Progress.Value == 47 && panel.Progress.ShowPercentage, "progress bar preserves actual measured progress");
                var stop = UnifiedDialogAppearance.Children<Button>(panel.Frame).Single(b => b.Name == "ProgramProgressStop");
                rect = stop.TransformToAncestor(host).TransformBounds(new Rect(stop.RenderSize));
                Assert(rect.Bottom <= host.ActualHeight + 1 && rect.Top >= 0, "progress bar leaves queue stop inside the smallest window");
                if (!english) { var animation = Stopwatch.StartNew(); while (animation.ElapsedMilliseconds < 400) { Pump(); Thread.Sleep(10); } Save(main, "Ladebalken-" + size); }
                panel.Collapse(); Pump(); panel.Expand(); Pump();
                panel.StopRequested(); Pump(); Assert(panel.Progress.IsRunning, "stop request keeps the progress bar active until the operation ends");
                panel.Finish(true); Pump(); Assert(!panel.Progress.IsRunning && !panel.Progress.IsVisible, "finished/stopped operation removes loading state");
                panel.Step(vlc.Id, new(ProgramStep.Install, ProgramStepState.Active)); Assert(!panel.Progress.IsRunning, "late phase does not restart finished progress");
                panel.Dispose(); Assert(!panel.Progress.IsRunning, "disposal stops progress animation");
            }
        }
        finally { main.Close(); }
        Console.WriteLine($"PASS {checks} destination sections, compact/large DE/EN layout and progress-bar-only checks in {theme}; no real installer executed"); return 0;
    }

    internal static int Flow(App app, string output)
    {
        Directory.CreateDirectory(output); int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        void Until(Func<bool> condition) { var watch = Stopwatch.StartNew(); while (!condition() && watch.Elapsed.TotalSeconds < 45) { Pump(); Thread.Sleep(5); } if (!condition()) throw new TimeoutException("Destination queue fixture"); Pump(); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var installed = new Dictionary<string, string?>(); var paths = new Dictionary<string, ProgramInstallation>(); var entriesByPackage = new Dictionary<string, ProgramEntry>();
        var commands = new List<ProcessStartInfo>(); int validations = 0; bool ignoreRequestedPath = false;
        var service = new ProgramInstallService(query: (start, token) =>
        {
            token.ThrowIfCancellationRequested(); string verb = start.ArgumentList[0];
            if (verb == "search") return Task.FromResult(new WinGetResult(0, start.ArgumentList.Contains("Different")
                ? "Different Destination Fixture  FixturePublisher.DestinationDifferent  2.0\n" : "Online Destination Fixture  FixturePublisher.DestinationFixture  2.0\n"));
            if (verb == "show") { string id = start.ArgumentList[start.ArgumentList.IndexOf("--id") + 1]; return Task.FromResult(new WinGetResult(0, "Found Fixture [" + id + "]\nVersion: 2.0\n")); }
            if (verb == "export")
            {
                string file = start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1];
                File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { Sources = new[] { new { SourceDetails = new { Name = "winget" }, Packages = installed.Select(p => new { PackageIdentifier = p.Key, Version = p.Value }).ToArray() } } }));
            }
            return Task.FromResult(new WinGetResult(0, ""));
        }, install: (start, started) =>
        {
            commands.Add(start); started(); string id = start.ArgumentList[start.ArgumentList.IndexOf("--id") + 1];
            string target = start.ArgumentList[start.ArgumentList.IndexOf("--location") + 1];
            if (ignoreRequestedPath) target = Path.Combine(output, "DifferentActualLocation");
            Directory.CreateDirectory(target); installed[id] = "2.0"; paths[entriesByPackage[id].Id] = new("2.0", "Registry", target); return Task.FromResult(0);
        }, localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation>(paths)),
            targetResolver: (p, _) => { validations++; return Task.FromResult<ProgramInstallTargetProof?>(new(p.Id, p.Version!, "fixture", "nullsoft", "fixture")); });
        var main = new MainWindow(Path.Combine(output, "Fixture"), new AppSettings { SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Service = service; view.PortableService = new VlcZipTests.NoPortable(); view.Assets.AllowOnlineIcons = false; var host = (Grid)main.FindName("Root");
            var choices = new[] { ProgramCatalog.Entries.Single(e => e.Id == "vlc"), ProgramCatalog.Entries.Single(e => e.Id == "notepad") };
            foreach (var entry in choices) entriesByPackage[entry.PackageId!] = entry;
            void Confirm(string[] targets)
            {
                Until(() => UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).Count() == targets.Length);
                var pickers = UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).ToArray();
                for (int n = 0; n < pickers.Length; n++)
                { var toggle = UnifiedDialogAppearance.Children<CheckBox>(pickers[n]).Single(); Assert(toggle.IsEnabled, "freshly confirmed queue offers destination"); toggle.IsChecked = true; UnifiedDialogAppearance.Children<TextBox>(pickers[n]).Single().Text = targets[n]; }
                Pump();
                var start = UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Content is TextBlock t && t.Text == "↓ Auswahl starten");
                Assert(start.IsEnabled, "batch can start with independent valid destinations"); start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            var batch = view.InstallEntriesAsync(choices); var destinations = choices.Select(e => Path.Combine(output, "Selected", e.Id)).ToArray(); Confirm(destinations); Wait(batch);
            Assert(commands.Count == 2 && validations == 4, "two sequential installers each revalidate selected destination before execution");
            Assert(commands.Select(c => c.ArgumentList[c.ArgumentList.IndexOf("--location") + 1]).SequenceEqual(destinations), "selection paths survive complete multi-program queue");
            Assert(!view.IsWorking && view.Inventory.Installed.Keys.Contains("vlc") && view.Inventory.Installed.Keys.Contains("notepad"), "queue unlocks only after fresh inventory");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(view.OperationPanel!.Frame).Count(t => t.Text == "Bestätigt") == 2, "both selected locations confirmed at completion");
            view.OperationPanel.Dispose();
            var search = service.SearchAsync("Destination", default); Wait(search); var online = search.Result.Single(); entriesByPackage[online.PackageId!] = online;
            ((Dictionary<string, ProgramEntry>)typeof(ProgramCatalogView).GetField("_sessionEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!)[online.Id] = online;
            var onlineWork = view.InstallEntriesAsync([online]); string onlineTarget = Path.Combine(output, "Selected", "Online"); Confirm([onlineTarget]); Wait(onlineWork);
            Assert(commands.Count == 3 && view.Inventory.Installed.ContainsKey(online.Id) && paths[online.Id].Path == onlineTarget, "online-search entry uses same selected-destination route");
            view.OperationPanel!.Dispose();
            var different = service.SearchAsync("Different", default); Wait(different); var wrong = different.Result.Single(); entriesByPackage[wrong.PackageId!] = wrong; ignoreRequestedPath = true;
            ((Dictionary<string, ProgramEntry>)typeof(ProgramCatalogView).GetField("_sessionEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!)[wrong.Id] = wrong;
            var wrongWork = view.InstallEntriesAsync([wrong]); Confirm([Path.Combine(output, "RequestedButIgnored")]); Wait(wrongWork);
            Assert(commands.Count == 4 && !UnifiedDialogAppearance.Children<TextBlock>(view.OperationPanel!.Frame).Any(t => t.Text == "Bestätigt"), "installer ignoring requested destination never gets a verified completion");
            Assert(!view.IsWorking && paths[wrong.Id].Path == Path.Combine(output, "DifferentActualLocation"), "actual location retained without pretending it was moved");
            view.OperationPanel.Dispose();
        }
        finally { main.Close(); }
        Console.WriteLine($"PASS {checks} end-to-end multi-program, online-search and ignored-destination queue checks; fixture installers only"); return 0;
    }
}
