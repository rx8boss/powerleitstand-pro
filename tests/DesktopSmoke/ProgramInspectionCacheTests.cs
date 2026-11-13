using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramInspectionCacheTests
{
    private static readonly ProgramEntry Firefox = ProgramCatalog.Entries.Single(e => e.Id == "firefox");
    private static ProgramInventory Snapshot(DateTime now, string version = "1.0", string? target = "2.0", string source = "Registry", string? path = null)
    {
        var proof = new ProgramInstallation(version, source, path ?? @"I:\Fixture\firefox.exe");
        return new(true, new Dictionary<string, string?> { [Firefox.Id] = version },
            Updates: target is null ? [] : new Dictionary<string, string> { [Firefox.Id] = target }, UpdateCheckComplete: true,
            Evidence: new Dictionary<string, ProgramInstallation> { [Firefox.Id] = proof }, CheckedUtc: now);
    }
    internal static int Run(App app, string output, bool ui)
    {
        int count = 0; void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        if (ui) return Ui(app, output);
        Task.Run(async () =>
        {
            var now = DateTime.UtcNow; string file = Path.Combine(output, "Cache", "inventory.json");
            var cache = new ProgramInspectionCache(file); var original = Snapshot(now);
            Assert(cache.Read([Firefox], now) is null, "Missing cache is a cold start");
            Assert(cache.Save(original, [Firefox]), "Healthy complete scan persists");
            var document = cache.Read([Firefox], now)!; var display = ProgramInspectionCache.Display(document);
            Assert(display.IsStale && display.FromCache && !display.Complete && !display.UpdateCheckComplete, "Saved display is never a fresh completed scan");
            Assert(display.Installed[Firefox.Id] == "1.0" && display.PendingUpdates![Firefox.Id] == "2.0", "Installed version and candidate survive restart");
            Assert(!display.HasUpdate(Firefox.Id) && display.HasUpdateNotice(Firefox.Id), "Candidate has a badge but no executable update");
            Assert(display.GitHubPackages is null && display.PortableUpdates is null && display.ManagedPackages is null, "No executable offers are restored");
            Assert(display.CheckedUtc == now, "Online-check timestamp survives exactly");
            var bytes = File.ReadAllBytes(file);
            foreach (var bad in new[] { original with { Complete = false }, original with { UpdateCheckComplete = false }, original with { StoreCheckComplete = false },
                original with { IsPartial = true }, original with { IsStale = true }, original with { UnverifiedUpdateIds = [Firefox.Id] }, original with { Error = "fail" }, original with { OnlineError = "offline" }, original with { StoreError = "fail" } })
            {
                Assert(!cache.Save(bad, [Firefox]), "Incomplete/error snapshot cannot replace last good scan");
                Assert(File.ReadAllBytes(file).SequenceEqual(bytes), "Last good file remains unchanged");
            }
            Assert(cache.Read([Firefox with { PackageId = "Other.Firefox" }], now)!.Rows.Length == 0, "Changed package identity invalidates its row");
            Assert(cache.Read([], now)!.Rows.Length == 0, "Removed catalog entries are not resurrected");
            foreach (var invalid in new[] { document with { Schema = 2 }, document with { CheckedUtc = now.AddMinutes(10) },
                document with { CheckedUtc = now.AddDays(-31) }, document with { Rows = [document.Rows[0], document.Rows[0]] },
                document with { Rows = [document.Rows[0] with { Source = "bad\nsource" }] }, document with { Rows = [document.Rows[0] with { Version = "not a version" }] } })
            {
                Assert(cache.Write(invalid) && cache.Read([Firefox], now) is null, "Invalid schema, date or data rejected");
            }
            File.WriteAllText(file, "{broken"); Assert(cache.Read([Firefox], now) is null, "Corrupt cache falls back without a crash");
            File.WriteAllBytes(file, new byte[ProgramInspectionCache.MaxBytes + 1]); Assert(cache.Read([Firefox], now) is null, "Oversized cache is not parsed");
            cache.Save(original, [Firefox]);
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { Assert(!cache.Save(original with { CheckedUtc = now.AddSeconds(1) }, [Firefox]) && cache.LastError is not null, "Cache I/O failure never crashes inspection"); }
            Assert(!Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Any(), "Failed write cleans up its temporary file");
            cache.Invalidate(Firefox.Id, [Firefox]); Assert(cache.Read([Firefox], now)!.Rows.Length == 0, "Changing an installation invalidates the saved candidate");
            cache.Save(original, [Firefox]);
            int queries = 0, localReads = 0; string localVersion = "2.0"; string localPath = @"I:\Fixture\firefox.exe"; string localSource = "Registry";
            var service = new ProgramInstallService((_, _) => { queries++; throw new Exception("Local check must not query online"); }, cacheFile: file,
                localReader: (entries, _) => { localReads++; Assert(entries.Length == 1 && entries[0].Id == Firefox.Id, "Only candidates are read locally");
                    return Task.FromResult(new Dictionary<string, ProgramInstallation> { [Firefox.Id] = new(localVersion, localSource, localPath) }); });
            var updated = await service.RefreshKnownAsync(display, CancellationToken.None);
            Assert(queries == 0 && localReads == 1, "External updates are checked without a WinGet/network query");
            Assert(updated.Installed[Firefox.Id] == "2.0" && !updated.HasUpdateNotice(Firefox.Id), "External update removes candidate");
            Assert(cache.Read([Firefox], now)!.Rows[0].Target is null, "Removed badge stays removed after restart");
            foreach (var version in new[] { "3.0", "2.0.0" })
            { localVersion = version; cache.Save(original, [Firefox]); var result = await service.RefreshKnownAsync(display, CancellationToken.None); Assert(!result.HasUpdateNotice(Firefox.Id), "Same/newer comparable version clears candidate"); }
            foreach (var version in new[] { "1.0", "vendor-beta", "" })
            { localVersion = version; cache.Save(original, [Firefox]); var result = await service.RefreshKnownAsync(display, CancellationToken.None); Assert(result.HasUpdateNotice(Firefox.Id) && !result.HasUpdate(Firefox.Id), "Older/uncomparable version stays unconfirmed"); }
            localVersion = "2.0"; localPath = @"I:\OtherCopy\firefox.exe";
            var other = await service.RefreshKnownAsync(display, CancellationToken.None); Assert(other.HasUpdateNotice(Firefox.Id) && other.Installed[Firefox.Id] == "1.0", "Other copy cannot clear the candidate");
            localPath = @"I:\Fixture\firefox.exe"; localSource = "Windows-App";
            other = await service.RefreshKnownAsync(display, CancellationToken.None); Assert(other.HasUpdateNotice(Firefox.Id), "Other channel cannot clear the candidate");
            var scoped = original with { Evidence = new Dictionary<string, ProgramInstallation> { [Firefox.Id] = new("1.0", "Registry", localPath, LocationEvidence: new(null, localPath, "Registry", "User")) } };
            cache.Save(scoped, [Firefox]);
            var differentScope = new ProgramInstallService(cacheFile: file, localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation> {
                [Firefox.Id] = new("2.0", "Registry", localPath, LocationEvidence: new(null, localPath, "Registry", "Machine")) }));
            Assert((await differentScope.RefreshKnownAsync(ProgramInspectionCache.Display(cache.Read([Firefox], now)!), CancellationToken.None)).HasUpdateNotice(Firefox.Id), "Scope change is uncertain");
            cache.Save(original, [Firefox]);
            var absent = new ProgramInstallService(cacheFile: file, localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation>()));
            Assert((await absent.RefreshKnownAsync(display, CancellationToken.None)).HasUpdateNotice(Firefox.Id), "Missing local evidence is not proof of update completion");
            Assert(ProgramInspectionCache.Identity(new("1", "Scoop", ManagedCopies: [new("Scoop", "firefox", "1", @"I:\scoop\apps\firefox\1", "User", ManagerRoot: @"I:\scoop")]))
                == ProgramInspectionCache.Identity(new("2", "Scoop", ManagedCopies: [new("Scoop", "firefox", "2", @"I:\scoop\apps\firefox\2", "User", ManagerRoot: @"I:\scoop")])), "Managed identity survives its version directory changing");
            Assert(!ProgramInspectionCache.Identity(new("1", "Scoop", ManagedCopies: [new("Scoop", "firefox", "1", "a", "User", ManagerRoot: "a")]))
                .Equals(ProgramInspectionCache.Identity(new("1", "Scoop", ManagedCopies: [new("Scoop", "firefox", "1", "b", "User", ManagerRoot: "b")]))), "Different manager roots stay separate");
            var partial = ProgramCatalogView.PreserveLastCandidates(display, new(false, new Dictionary<string, string?>(), OnlineError: "offline"));
            Assert(partial.IsStale && partial.HasUpdateNotice(Firefox.Id) && !partial.HasUpdate(Firefox.Id), "Offline/partial result retains candidate without authorizing it");
            var complete = ProgramCatalogView.PreserveLastCandidates(display, Snapshot(now.AddSeconds(1), "2.0", null));
            Assert(!complete.IsStale && !complete.HasUpdateNotice(Firefox.Id) && complete.Complete, "Healthy full result replaces old saved candidates");
            var optionalFailure = ProgramCatalogView.PreserveLastCandidates(display, Snapshot(now.AddSeconds(1), "1.0", null) with { UnverifiedUpdateIds = [Firefox.Id] });
            Assert(optionalFailure.HasUpdateNotice(Firefox.Id) && !optionalFailure.HasUpdate(Firefox.Id) && !optionalFailure.UpdateSourcesComplete,
                "Optional provider failure retains its saved candidate as unconfirmed");
            var currentDespiteFailure = ProgramCatalogView.PreserveLastCandidates(display, Snapshot(now.AddSeconds(1), "2.0", null) with { UnverifiedUpdateIds = [Firefox.Id] });
            Assert(!currentDespiteFailure.HasUpdateNotice(Firefox.Id), "Confirmed current local version can clear old candidate despite optional online failure");
            var exportFailed = ProgramCatalogView.PreserveLastCandidates(display, Snapshot(now.AddSeconds(1)) with { Complete = false, Error = "Export timeout" });
            Assert(exportFailed.HasUpdate(Firefox.Id) && !exportFailed.IsStale && !exportFailed.Complete,
                "A failed export does not withhold an independently confirmed update");
            var missingCandidate = ProgramCatalogView.PreserveLastCandidates(display, new(false, new Dictionary<string, string?>(), UpdateCheckComplete: true, Error: "Export timeout"));
            Assert(missingCandidate.HasUpdateNotice(Firefox.Id) && missingCandidate.UnverifiedInstalledIds?.Contains(Firefox.Id) == true
                && !new ProgramInstallService().ReinstallCapability(Firefox, missingCandidate).Automatic,
                "Retained missing candidate remains unverified and cannot authorize a reinstall");
            cache.Save(original, [Firefox]); var goodBytes = File.ReadAllBytes(file);
            var incomplete = Snapshot(now.AddSeconds(2), "1.0", "3.0") with { Complete = false, Error = "Export timed out" };
            Assert(cache.SaveConfirmedParts(incomplete, [Firefox]), "Verified parts survive an unrelated export timeout");
            Assert(File.ReadAllBytes(file).SequenceEqual(goodBytes), "Partial scan leaves the complete snapshot untouched");
            var partialDisplay = ProgramInspectionCache.Display(cache.Read([Firefox], now.AddSeconds(3))!);
            Assert(partialDisplay.SavedPartial && partialDisplay.IsStale && partialDisplay.PendingUpdates![Firefox.Id] == "3.0"
                && !partialDisplay.HasUpdate(Firefox.Id), "Partial saved data remain labeled and non-executable");
            cache.Invalidate(Firefox.Id, [Firefox]);
            Assert(cache.Read([Firefox], now.AddSeconds(3))!.Rows.Length == 0, "Changed installation is invalidated in complete and partial files");
            cache.Save(original, [Firefox]);
            Assert(!File.Exists(file + ".partial"), "A later complete scan clears its superseded partial file");
            Assert(!new ProgramInstallService().ReinstallCapability(Firefox, display).Automatic, "Cache alone cannot authorize reinstall");
            foreach (var test in new[] { ("2.0", "2.0", true), ("3.0", "2.0", true), ("1.0", "2.0", false), ("x-2", "x-3", false) })
                Assert(ProgramInstallService.CurrentEnough(test.Item1, test.Item2) == test.Item3, "Version comparison conservatively handles opaque values");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int scanQueries = 0;
            async Task<WinGetResult> Query(ProcessStartInfo start, CancellationToken token)
            {
                Interlocked.Increment(ref scanQueries);
                if (start.ArgumentList[0] == "list") return new(0, "Firefox  Mozilla.Firefox  1.0  2.0  winget");
                if (start.ArgumentList[0] == "export")
                { await File.WriteAllTextAsync(start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1], "{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Mozilla.Firefox\",\"Version\":\"1.0\"}]}]}", token); return new(0, ""); }
                throw new Exception("No installers in tests");
            }
            var single = new ProgramInstallService(Query, (_, _) => throw new Exception("No installer"), cacheFile: Path.Combine(output, "Single.json"),
                localReader: async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return new(); });
            var first = single.InspectAsync(CancellationToken.None); await entered.Task;
            using var followerCancel = new CancellationTokenSource(); var follower = single.InspectAsync(followerCancel.Token); followerCancel.Cancel();
            bool canceled = false; try { await follower; } catch (OperationCanceledException) { canceled = true; }
            var others = Enumerable.Range(0, 10).Select(_ => single.InspectAsync(CancellationToken.None)).ToArray(); release.SetResult();
            await first; await Task.WhenAll(others);
            Assert(canceled && scanQueries == 2, "Concurrent refreshes share one scan; canceling a follower does not cancel its owner");
            await single.InspectAsync(CancellationToken.None); Assert(scanQueries == 4, "A later manual check is fresh");
            Assert((await single.ReadSavedAsync(CancellationToken.None))?.PendingUpdates?.ContainsKey(Firefox.Id) == true, "Actual inspection result is persisted through service wrapper");
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelService = new ProgramInstallService(Query, cacheFile: Path.Combine(output, "Cancelled.json"), localReader: async (_, token) => { waiting.TrySetResult(); await Task.Delay(10000, token); return new(); });
            using var owner = new CancellationTokenSource(); var canceledScan = cancelService.InspectAsync(owner.Token); await waiting.Task; owner.Cancel();
            try { await canceledScan; } catch (OperationCanceledException) { }
            Assert(!File.Exists(Path.Combine(output, "Cancelled.json")), "Interrupted scan is never persisted as current");
            var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var listRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var powerToys = ProgramCatalog.Entries.Single(e => e.Id == "powertoys");
            var early = new ProgramInstallService(async (start, token) =>
            {
                if (start.ArgumentList[0] == "list") await listRelease.Task.WaitAsync(token);
                return await Query(start, token);
            }, github: (_, _) => { releaseStarted.TrySetResult(); return Task.FromResult<ProgramPackage?>(null); },
                localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation> { [powerToys.Id] = new("1.0", "Registry") }));
            var earlyScan = early.InspectAsync(CancellationToken.None);
            await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!earlyScan.IsCompleted, "GitHub lookup starts while WinGet list is still pending");
            listRelease.SetResult(); await earlyScan;
            var failure = new ProgramInstallService(Query, github: (_, _) => throw new System.Net.Http.HttpRequestException("Provider unavailable"),
                localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation> { [powerToys.Id] = new("1.0", "Registry") }));
            var failedProvider = await failure.InspectAsync(CancellationToken.None);
            Assert(failedProvider.UnverifiedUpdateIds?.Contains(powerToys.Id) == true && !failedProvider.UpdateSourcesComplete,
                "Caught GitHub error is reported as incomplete rather than no updates");
            Assert(failedProvider.HasUpdate(Firefox.Id), "One failed provider does not withhold an independently verified WinGet update");
        }).GetAwaiter().GetResult();
        Console.WriteLine("PASS " + count + " saved/background inspection checks"); return 0;
    }

    private static int Ui(App app, string output)
    {
        int count = 0; void Assert(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task)
        { var clock = Stopwatch.StartNew(); while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 25) { Pump(); Thread.Sleep(3); } if (!task.IsCompleted) throw new TimeoutException("Cache UI test"); task.GetAwaiter().GetResult(); Pump(); }
        var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons = false; Pump(); string file = Path.Combine(output, "UiCache.json"); var cache = new ProgramInspectionCache(file);
            cache.Save(Snapshot(DateTime.UtcNow), [Firefox]); int queries = 0; string localVersion = "2.0";
            var service = new ProgramInstallService((_, _) => { queries++; throw new Exception("No query while opening warm page"); }, cacheFile: file,
                localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation> { [Firefox.Id] = new(localVersion, "Registry", @"I:\Fixture\firefox.exe") }));
            view.Service = service; Wait(view.OpenAsync());
            Assert(queries == 0 && view.Inventory.IsStale && !view.HasLoadingOverlay, "Warm opening immediately displays saved data without scan/loading overlay");
            Assert(view.UpdateEntries.Any(e => e.Id == Firefox.Id) && view.UpdateButton.IsEnabled, "Saved candidates remain listed with Check-before-update path");
            Assert(!view.Inventory.HasUpdate(Firefox.Id), "Saved candidate does not unlock executable update checkbox");
            var status = UnifiedDialogAppearance.Children<TextBlock>(view).Single(t => t.Name == "ProgramInspectionStatus");
            Assert(status.Text.Contains("Letzter Prüfstand") && !status.Text.Contains("Updates geprüft"), "Saved state has an explicit timestamped caption");
            Wait(view.OpenAsync()); Assert(queries == 0, "Repeated page open does not trigger full scan");
            Wait(view.CheckInBackgroundAsync()); Assert(view.Inventory.HasUpdateNotice(Firefox.Id), "No scan before minimum delay");
            view.Select("google-chrome", true); Wait(view.CheckInBackgroundAsync(DateTime.UtcNow.AddMinutes(3)));
            Assert(view.Inventory.HasUpdateNotice(Firefox.Id), "Selected user actions pause background checks"); view.Select("google-chrome", false);
            Wait(view.CheckInBackgroundAsync(DateTime.UtcNow.AddMinutes(3)));
            Assert(!view.Inventory.HasUpdateNotice(Firefox.Id) && !view.HasLoadingOverlay && queries == 0, "Targeted external-update detection removes badge without global overlay or online scan");
            Assert(!view.UpdateEntries.Any(), "Empty candidate list hides Updates button");
            cache.Save(Snapshot(DateTime.UtcNow.AddMinutes(-20)), [Firefox]);
            // Use a separate view lifecycle to model a restart and delayed online refresh.
            main.Close(); Pump();
        }
        finally { main.Close(); Pump(); }
        var secondMain = new MainWindow(Path.Combine(output, "SecondSettings")) { ShowActivated = false };
        try
        {
            secondMain.Show(); secondMain.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)secondMain.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; Pump();
            string file = Path.Combine(output, "UiCache.json"); int queries = 0;
            var blocked = new TaskCompletionSource<WinGetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Service = new ProgramInstallService(async (start, token) => { queries++; return await blocked.Task.WaitAsync(token); }, cacheFile: file,
                localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation>()));
            Wait(view.OpenAsync()); var prior = view.Inventory;
            var background = view.CheckInBackgroundAsync(DateTime.UtcNow.AddMinutes(1)); Pump();
            Assert(view.BackgroundChecking && !view.HasLoadingOverlay && view.Inventory == prior, "Delayed remote refresh leaves cached tiles usable");
            Wait(view.PauseBackgroundInspectionAsync()); Wait(background);
            Assert(ReferenceEquals(view.Inventory, prior), "Canceled late scan cannot replace the displayed snapshot");
            var now = DateTime.UtcNow.AddMinutes(2); var before = queries; Wait(view.CheckInBackgroundAsync(now));
            Assert(queries == before, "Online retry is throttled after a canceled attempt");
            foreach (bool english in new[] { false, true })
            {
                view.SetLanguage(english); Pump();
                var status = UnifiedDialogAppearance.Children<TextBlock>(view).Single(t => t.Name == "ProgramInspectionStatus");
                Assert(status.Text.Contains(english ? "Last checked" : "Letzter Prüfstand"), "Cached status is bilingual");
                foreach (var size in new[] { (900d, 650d), (1500d, 900d) })
                {
                    secondMain.Width = size.Item1; secondMain.Height = size.Item2; secondMain.UpdateLayout(); Pump();
                    Assert(status.ActualHeight > 0 && !view.HasLoadingOverlay, "Compact/large cached view keeps footer visible without overlay");
                    if (!english)
                    {
                        var image = new RenderTargetBitmap((int)secondMain.ActualWidth, (int)secondMain.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(secondMain);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(output, size.Item1 < 1000 ? "Gespeicherte-Updates-Kompakt.png" : "Gespeicherte-Updates-Gross.png")); png.Save(stream);
                    }
                }
            }
            var chatgpt = ProgramCatalog.Entries.Single(e => e.Id == "chatgpt");
            view.Service = new ProgramCatalogTests.Fake();
            foreach (bool english in new[] { false, true })
            {
                view.SetLanguage(english);
                var savedStore = new ProgramInventory(false, new Dictionary<string, string?> { [chatgpt.Id] = "1.0" },
                    Evidence: new Dictionary<string, ProgramInstallation> { [chatgpt.Id] = new("1.0", "Windows-App") },
                    StoreCheckComplete: false, IsStale: true, FromCache: true, CheckedUtc: DateTime.UtcNow);
                typeof(ProgramCatalogView).GetProperty("Inventory", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, savedStore);
                view.ShowInfo(chatgpt, view, true); Pump();
                var texts = UnifiedDialogAppearance.Children<TextBlock>(view.Info!.Frame).Select(t => t.Text).ToArray();
                Assert(!texts.Any(t => t.Contains("Store-Prüfung läuft") || t.Contains("Store check is still running")), "Cached Store details do not pretend a check is running");
                Assert(texts.Any(t => t.Contains(english ? "Saved state" : "Gespeicherter Stand")), "Store details explicitly label saved evidence");
                view.CloseInfo(); Pump();
            }
            void HoldBackground(Task task) => typeof(ProgramCatalogView).GetField("_backgroundTask", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, task);
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); HoldBackground(drain.Task);
            var action = view.InstallEntriesAsync([]); Pump();
            var repeatedAction = view.InstallEntriesAsync([]);
            Assert(view.SelectionOpen && !action.IsCompleted && repeatedAction.IsCompleted, "Repeated install click cannot enter while background readers drain");
            drain.SetResult(); Wait(action); Assert(!view.SelectionOpen && !view.IsWorking, "Empty queued action releases its pending-action lock");
            drain = new(TaskCreationOptions.RunContinuationsAsynchronously); HoldBackground(drain.Task);
            var check = view.RefreshAsync(); Pump(); var repeatedCheck = view.RefreshAsync();
            Assert(!check.IsCompleted && repeatedCheck.IsCompleted, "Repeated manual check cannot create a second owner while background readers drain");
            drain.SetResult(); Wait(check); Assert(!view.BackgroundChecking, "Drained readers cannot remain marked as running");
        }
        finally { secondMain.Close(); Pump(); }
        Console.WriteLine("PASS " + count + " saved/background UI checks"); return 0;
    }

    internal static int Live(string output, string? baselineAssembly)
    {
        return Task.Run(async () =>
        {
            var phases = new Dictionary<string, double>(); var clock = Stopwatch.StartNew();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            object result;
            if (baselineAssembly is not null)
            {
                var context = new AssemblyLoadContext("ReadOnlyBaseline", true);
                context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
                var assembly = context.LoadFromAssemblyPath(baselineAssembly); var type = assembly.GetType("PowerControlProWpf.ProgramInstallService", true)!;
                var constructor = type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Single();
                var service = constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());
                var task = (Task)type.GetMethod("InspectAsync")!.Invoke(service, [limit.Token])!; await task;
                result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            else result = await new ProgramInstallService(cacheFile: Path.Combine(output, "LiveCache.json"),
                inspectionTiming: (phase, elapsed) => { lock (phases) phases[phase] = elapsed.TotalMilliseconds; }).InspectAsync(limit.Token);
            var properties = result.GetType().GetProperties().Where(p => p.Name is "Complete" or "UpdateCheckComplete" or "StoreCheckComplete" or "Error" or "OnlineError" or "CheckedUtc")
                .ToDictionary(p => p.Name, p => p.GetValue(result));
            properties["TotalMilliseconds"] = clock.Elapsed.TotalMilliseconds; properties["Phases"] = phases;
            if (baselineAssembly is null)
            {
                var readClock = Stopwatch.StartNew();
                var saved = await new ProgramInstallService(cacheFile: Path.Combine(output, "LiveCache.json")).ReadSavedAsync(CancellationToken.None);
                properties["WarmReadMilliseconds"] = readClock.Elapsed.TotalMilliseconds;
                properties["SavedRows"] = saved?.Installed.Count; properties["SavedCandidates"] = saved?.PendingUpdates?.Count;
                properties["SavedPartial"] = saved?.SavedPartial; properties["SavedDisplayOnly"] = saved?.IsStale;
            }
            File.WriteAllText(Path.Combine(output, "Live-Timing.json"), JsonSerializer.Serialize(properties, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Read-only inventory seconds: " + clock.Elapsed.TotalSeconds.ToString("F2")); return 0;
        }).GetAwaiter().GetResult();
    }
}
