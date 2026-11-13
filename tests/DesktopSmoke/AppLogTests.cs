using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class AppLogTests
{
    internal static int Run(App app, string output, bool ui)
    {
        int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        if (ui)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
            void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            void Wait(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 30) { Pump(); Thread.Sleep(3); } if (!task.IsCompleted) throw new TimeoutException("Log UI fixture"); task.GetAwaiter().GetResult(); Pump(); }
            var settings = Path.Combine(output, "Settings"); var main = new MainWindow(settings, new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
            try
            {
                main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
                var fake = new Fake(); view.Service = fake; view.ConfirmForTests = _ => true;
                string errorFile = Path.Combine(settings, "Logs", "errors.log"), eventFile = Path.Combine(settings, "Logs", "events.log");
                Assert(!File.Exists(errorFile), "Routine startup and checks do not create an error file");
                var entry = ProgramCatalog.Entries.Single(e => e.Id == "7-zip");
                foreach (bool english in new[] { false, true })
                {
                    page.SetLanguage(english); fake.Done = false; Wait(view.RefreshAsync()); int before = File.Exists(errorFile) ? File.ReadAllLines(errorFile).Length : 0;
                    Wait(view.InstallEntriesAsync([entry]));
                    var errorLines = File.ReadAllLines(errorFile); var eventLines = File.ReadAllLines(eventFile);
                    Assert(errorLines.Length == before + 1 && errorLines.Last().Contains("7-Zip") && errorLines.Last().Contains("ERROR actual fixture failure"), "Diagnostic error reaches MainWindow errors.log once per operation");
                    Assert(eventLines.Any(l => l.Contains(english ? "installation/update verified" : "Installation/Update bestätigt")), "A later verified success remains visible without deleting earlier actual error");
                    Assert(!eventLines.Any(l => l.Contains("Updating files:") || l.Contains("deadbeef1234")), "Progress and commit rows do not reach normal event log");
                    Assert(eventLines.Count(l => l.Contains(english ? "Download verified." : "Download bestätigt.")) == 1, "Repeated phase reports are bundled within one operation");
                    Assert(!view.IsWorking && fake.Done, "Logging preserves operation completion and unlock");
                }
                fake.Done = false; fake.Cancel = true; Wait(view.RefreshAsync()); int count = File.ReadAllLines(errorFile).Length;
                Wait(view.InstallEntriesAsync([entry])); Assert(File.ReadAllLines(errorFile).Length == count && !view.IsWorking, "Administrator-dialog cancellation adds no error and releases selection");
                fake.Cancel = false; fake.Fail = true; Wait(view.RefreshAsync()); Wait(view.InstallEntriesAsync([entry]));
                Assert(File.ReadAllLines(errorFile).Length == count + 1 && File.ReadAllText(errorFile).Contains("Fixture exception"), "Caught installer exception becomes a separate actual error");
                main.RecordError(new("Users", "Edit", "Fixture user error", "0x00000005", "users-fixture"));
                Assert(File.ReadAllText(errorFile).Contains("Users · Edit · Code 0x00000005"), "Explicit non-program error preserves source, operation and code");
                int beforeRepair = File.ReadAllLines(errorFile).Length;
                var appendRepair = typeof(MainWindow).GetMethod("AppendRepairLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
                appendRepair.Invoke(main, ["If an error occurs, see details.", "INFO"]); appendRepair.Invoke(main, ["Fixture user cancellation", "ABBRUCH"]);
                Assert(File.ReadAllLines(errorFile).Length == beforeRepair, "Repair information and cancellation are not actual errors");
                appendRepair.Invoke(main, ["Fixture repair failure", "FEHLER"]); appendRepair.Invoke(main, ["Fixture repair failure", "FEHLER"]);
                Assert(File.ReadAllLines(errorFile).Length == beforeRepair + 1 && File.ReadAllLines(errorFile).Last().Contains("Windows-Reparatur"), "Repeated repair error reaches error file once per repair session");
                int beforeUpdate = File.ReadAllLines(errorFile).Length;
                typeof(WindowsUpdatesPage).GetMethod("Failure", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [new IOException("Fixture Windows Update failure")]);
                Assert(File.ReadAllLines(errorFile).Length == beforeUpdate + 1 && File.ReadAllLines(errorFile).Last().Contains("Windows Update"), "Caught Windows Update failure reaches shared error file");
                Console.WriteLine($"PASS {checks} event/error UI pipeline checks; controlled installer only, DE/EN; no real install."); return 0;
            }
            finally { main.Close(); Pump(); }
        }

        DateTimeOffset clock = new(2026, 10, 28, 16, 0, 0, TimeSpan.FromHours(1));
        var store = new AppLogStore(Path.Combine(output, "Basic"), clock: () => clock);
        store.Event("Started successfully"); Assert(!File.Exists(Path.Combine(output, "Basic", "errors.log")), "No error file for ordinary events");
        var error = new AppLogError("7-Zip", "Update", "Actual failure", "0x00000005", "operation-1");
        string first = store.Error(error)!;
        Assert(first.StartsWith("[28.10.2026, 16:00:00] ERROR · 7-Zip · Update · Code 0x00000005:"), "Error has stable date/time, source, phase and code");
        Assert(store.Error(error) is null && File.ReadAllLines(Path.Combine(output, "Basic", "errors.log")).Length == 1, "Identical error bundled inside same operation");
        store.Error(error with { OperationId = "operation-2" }); store.Error(error with { Source = "Other app" }); store.Error(error with { Code = "0x00000006" });
        Assert(File.ReadAllLines(Path.Combine(output, "Basic", "errors.log")).Length == 4, "Other operations, applications and codes retain distinct errors");
        store.Error(error with { Message = "\u001b[31mMultiline\r\nerror\t\0end\u001b[0m" });
        Assert(File.ReadAllLines(Path.Combine(output, "Basic", "errors.log")).Last().EndsWith("Multiline error end"), "Control sequences and embedded newlines cannot corrupt entries");
        var events = new List<string>(); var errors = new List<AppLogError>();
        var scope = new ProgramOperationLog("7-Zip", "Update", false, events.Add, errors.Add);
        scope.Output("Updating files: 42% (200/475)\n * deadbeef1234 some change\nIf an error occurs, retry later.\nStop-Process -Name explorer\nWARNING: Fixture warning\nSuccess\n");
        Assert(errors.Count == 0 && events.Count == 1 && events[0].Contains("WARNING"), "Only explicit warnings survive routine console noise; advisory error words are not actual errors");
        scope.Output("ERROR actual problem 0x80070005\nERROR actual problem 0x80070005\nSystem.IO.IOException: real exception");
        Assert(errors.Count == 2 && errors[0].Code == "0x80070005", "Actual source severities preserved and real embedded error code extracted");
        scope.ProcessEnded(0); Assert(errors.Count == 2, "Later successful exit does not remove earlier errors");
        foreach (int code in new[] { 0, 3010, 1602, 1223, unchecked((int)0x8A15010C), unchecked((int)0x8A150109) })
        { int before = errors.Count; scope.ProcessEnded(code); Assert(errors.Count == before, "Success/restart/cancel is not an error: " + code); }
        var unknown = new ProgramOperationLog("App", "Install", true, events.Add, errors.Add); unknown.Output("Unclassified diagnostic detail"); unknown.ProcessEnded(1603);
        Assert(errors.Last().Code == "0x00000643" && errors.Last().Message.Contains("Unclassified diagnostic detail"), "Unknown failed process retains bounded useful context and real exit code");
        int errorCount = errors.Count; scope.Output("ERROR: Cancelled by user."); Assert(errors.Count == errorCount, "Explicit user cancellation is informational");

        var replayDirectory = Path.Combine(output, "UserLogReplay"); var replayStore = new AppLogStore(replayDirectory, clock: () => clock);
        var replayEvents = new List<string>(); var replayErrors = new List<AppLogError>();
        var replay = new ProgramOperationLog("7-Zip / Scoop", "Update", false, text => { replayEvents.Add(text); replayStore.Event(text); }, record => { replayErrors.Add(record); replayStore.Error(record); });
        string? fixture = Environment.GetEnvironmentVariable("POWERLEITSTAND_PRIVATE_LOG_FIXTURE");
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            var input = File.ReadAllLines(fixture);
            foreach (string raw in input)
            {
                string text = Regex.Replace(raw, @"^\[\d{2}\.\d{2}\.\d{4}, \d{2}:\d{2}:\d{2}\]\s*", "");
                if (text.StartsWith("7-Zip: ")) text = text[7..];
                replay.Output(text);
                if (text.Contains("Installation/Update bestätigt.")) replay.Event("7-Zip: Installation/Update bestätigt.");
            }
            replay.ProcessEnded(0);
            Assert(input.Length == 1039, "Original provided user log used as private regression fixture");
            Assert(replayErrors.Count == 1 && replayErrors[0].Message.Contains("isn't installed correctly"), "Real error in provided log kept once despite repeated full console block");
            Assert(replayEvents.Count <= 6 && replayEvents.Any(e => e.Contains("bestätigt")), "User console flood reduced to useful phases and final verified outcome");
            Assert(File.ReadAllLines(Path.Combine(replayDirectory, "errors.log")).Length == 1 && !File.ReadAllText(Path.Combine(replayDirectory, "errors.log")).Contains("Updating files"), "Error file contains only actual error from user log");
            File.WriteAllText(Path.Combine(output, "Replay-Ergebnis.json"), System.Text.Json.JsonSerializer.Serialize(new { OriginalLines = input.Length, Events = replayEvents, Errors = replayErrors }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        var rotationDirectory = Path.Combine(output, "Rotation"); Directory.CreateDirectory(rotationDirectory);
        File.WriteAllText(Path.Combine(rotationDirectory, "events-personal.zip"), "must remain untouched");
        var rotating = new AppLogStore(rotationDirectory, 256, 3, () => clock);
        for (int n = 0; n < 14; n++) { clock = clock.AddSeconds(1); rotating.Event("Event " + n + " " + new string('x', 155)); rotating.Error(new("App", "Test", "Failure " + n + " " + new string('e', 155))); }
        foreach (string stem in new[] { "events", "errors" })
        {
            var archives = Directory.GetFiles(rotationDirectory, stem + "-*.zip").Where(p => !p.EndsWith("personal.zip")).ToArray();
            Assert(archives.Length == 3 && new FileInfo(Path.Combine(rotationDirectory, stem + ".log")).Length <= 256, "Independent archive retention and current-file bound: " + stem);
            foreach (var archive in archives)
            {
                using var zip = ZipFile.OpenRead(archive); Assert(zip.Entries.Count == 1 && zip.GetEntry(stem + ".log") is not null, "Archive contains the correct single log file");
                using var reader = new StreamReader(zip.GetEntry(stem + ".log")!.Open(), new UTF8Encoding(false, true));
                Assert(reader.ReadToEnd().Contains("[28.10.2026,"), "Archived UTF8 content readable with date/time");
            }
        }
        Assert(File.ReadAllText(Path.Combine(rotationDirectory, "events-personal.zip")) == "must remain untouched", "Only owned dated archives pruned");
        var unicode = new AppLogStore(Path.Combine(output, "Unicode"), 256, 3, () => clock); unicode.Error(new("CPU", "Test", string.Concat(Enumerable.Repeat("😀漢", 10000))));
        byte[] bytes = File.ReadAllBytes(Path.Combine(output, "Unicode", "errors.log"));
        Assert(bytes.Length <= 256 && new UTF8Encoding(false, true).GetString(bytes).Contains('…'), "Oversized UTF8 errors are bounded without breaking characters");
        var legacyDirectory = Path.Combine(output, "Legacy"); Directory.CreateDirectory(legacyDirectory); string legacy = string.Concat(Enumerable.Repeat("old log\r\n", 100)); File.WriteAllText(Path.Combine(legacyDirectory, "events.log"), legacy);
        new AppLogStore(legacyDirectory, 256, 3, () => clock).Event("New compact event");
        using (var zip = ZipFile.OpenRead(Directory.GetFiles(legacyDirectory, "*.zip").Single()))
        using (var reader = new StreamReader(zip.GetEntry("events.log")!.Open())) Assert(reader.ReadToEnd() == legacy, "Existing oversized log archived intact before replacement");
        var blockedDirectory = Path.Combine(output, "Blocked"); Directory.CreateDirectory(Path.Combine(blockedDirectory, "errors.log"));
        var blocked = new AppLogStore(blockedDirectory); Assert(blocked.Error(error) is not null && blocked.LastWriteFailure is not null && File.Exists(Path.Combine(blockedDirectory, "events.log")), "Error-file failure does not throw or prevent event display/write");
        var parallelDirectory = Path.Combine(output, "Parallel"); var parallel = new AppLogStore(parallelDirectory);
        Parallel.For(0, 40, n => parallel.Error(new("App", "Parallel", "Failure " + n)));
        Assert(File.ReadAllLines(Path.Combine(parallelDirectory, "errors.log")).Length == 40, "Parallel error records remain intact");
        Console.WriteLine($"PASS {checks} compact event/error separation, replay, retention, ZIP integrity and write-failure checks; private files only."); return 0;
    }
    private sealed class Fake : IProgramInstallService, IProgramDiagnosticSource, IProgramStepSource
    {
        internal bool Done, Cancel, Fail;
        public event Action<string>? Diagnostic;
        public event Action<ProgramStepEvent>? StepChanged;
        public Task<ProgramInventory> InspectAsync(CancellationToken token) => Task.FromResult(new ProgramInventory(true,
            new Dictionary<string, string?> { ["7-zip"] = Done ? "2.0" : "1.0" }, Updates: Done ? new Dictionary<string, string>() : new() { ["7-zip"] = "2.0" }, UpdateCheckComplete: true));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token) => Task.FromResult<ProgramPackage?>(new(entry.PackageId!, "2.0", "fixture", DateTime.UtcNow));
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token) => Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started) => InstallAsync(entry, package, started);
        public Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started)
        {
            if (Cancel) throw new System.ComponentModel.Win32Exception(1223);
            if (Fail) throw new IOException("Fixture exception");
            started(); StepChanged?.Invoke(new(ProgramStep.Download, ProgramStepState.Done)); StepChanged?.Invoke(new(ProgramStep.Download, ProgramStepState.Done));
            Diagnostic?.Invoke("Updating files: 41% (195/475)\n * deadbeef1234 fixture\nERROR actual fixture failure\nERROR actual fixture failure"); Done = true; return Task.FromResult(0);
        }
        public void OpenWebsite(ProgramEntry entry) { }
    }
}
