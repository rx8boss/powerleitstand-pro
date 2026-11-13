using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using PowerControlProWpf;

internal static class ProgramStoreInventoryTests
{
    internal static async Task<int> Run(string output)
    {
        int checks = 0;
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        var entries = new Dictionary<string, ProgramInstallation> { ["chatgpt"] = new("1.2026.190.0", "Windows-App", output, false) };
        int fallbackCalls = 0;
        Task<Dictionary<string, ProgramInstallation>> Fallback(CancellationToken _) { fallbackCalls++; return Task.FromResult(entries); }
        var native = await ProgramStoreInventory.ReadAsync(CancellationToken.None, _ => Task.FromResult(entries), Fallback);
        Assert(native.Complete && native.Entries["chatgpt"].Version == "1.2026.190.0" && fallbackCalls == 0, "Native result is used without starting PowerShell");
        var empty = await ProgramStoreInventory.ReadAsync(CancellationToken.None, _ => Task.FromResult(new Dictionary<string, ProgramInstallation>()), Fallback);
        Assert(empty.Complete && empty.Entries.Count == 0 && fallbackCalls == 0, "Successful empty inventory is distinct from failure");
        var fallback = await ProgramStoreInventory.ReadAsync(CancellationToken.None, _ => Task.FromException<Dictionary<string, ProgramInstallation>>(new COMException("native unavailable")), Fallback);
        Assert(fallback.Complete && fallback.Provider == "Windows PowerShell" && fallbackCalls == 1, "Native failure uses independent fallback");
        var failed = await ProgramStoreInventory.ReadAsync(CancellationToken.None,
            _ => Task.FromException<Dictionary<string, ProgramInstallation>>(new IOException("native failed")),
            _ => Task.FromException<Dictionary<string, ProgramInstallation>>(new IOException("shell failed")));
        Assert(!failed.Complete && failed.Entries.Count == 0 && failed.Error!.Contains("native failed") && failed.Error.Contains("shell failed"), "Both failures produce explicit incomplete state");
        var timeout = await ProgramStoreInventory.ReadAsync(CancellationToken.None,
            _ => Task.FromException<Dictionary<string, ProgramInstallation>>(new OperationCanceledException()), Fallback);
        Assert(timeout.Complete && fallbackCalls == 2, "Provider timeout can fall back without canceling the parent operation");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool stopped = false;
        try { await ProgramStoreInventory.ReadAsync(canceled.Token, _ => Task.FromResult(entries), Fallback); }
        catch (OperationCanceledException) { stopped = true; }
        Assert(stopped && fallbackCalls == 2, "Explicit cancellation never silently retries");
        int phase = 0;
        var service = new ProgramInstallService((_, _) => throw new Exception("No WinGet command in Store unit test"), storeReader: _ => Task.FromResult(++phase switch
        {
            1 => new ProgramStoreSnapshot(entries, true, Provider: "Fixture"),
            2 => failed,
            3 => new ProgramStoreSnapshot(new Dictionary<string, ProgramInstallation>(), true),
            _ => failed
        }));
        var method = typeof(ProgramInstallService).GetMethod("ReadStoreForInspectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task<ProgramStoreSnapshot> Read() => await (Task<ProgramStoreSnapshot>)method.Invoke(service, [CancellationToken.None])!;
        var first = await Read(); Assert(first.Complete && first.Entries.ContainsKey("chatgpt"), "First successful package state remembered");
        var retained = await Read(); Assert(!retained.Complete && retained.PreviousConfirmed && retained.Entries["chatgpt"].Version == "1.2026.190.0", "Later failure retains explicitly marked last confirmed state");
        var removed = await Read(); Assert(removed.Complete && removed.Entries.Count == 0, "Successful removal clears old package state");
        var absent = await Read(); Assert(!absent.Complete && absent.Entries.Count == 0, "A subsequent failure cannot resurrect removed package");
        var parsed = ProgramVersions.ParseStoreInstallations(JsonSerializer.Serialize(new[] { new { PackageFamilyName = "openai.chatgpt-desktop_2P2NQSD0C76G0", Version = "1.2026.190.0", InstallLocation = output } }));
        Assert(parsed["chatgpt"].Source == "Windows-App" && !parsed["chatgpt"].WinGetCompatible && parsed["chatgpt"].Path == output, "Store family identity is case-insensitive and remains its own channel");
        Assert(first.Entries.Count == 1, "Earlier Store snapshots remain immutable after later checks");
        int storePass = 0;
        var storeGate = new TaskCompletionSource<ProgramStoreSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var partialGate = new TaskCompletionSource<ProgramInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeReadyGate = new TaskCompletionSource<ProgramInventory>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queryGate = new TaskCompletionSource<WinGetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var integrated = new ProgramInstallService(async (start, token) =>
        {
            if (start.ArgumentList[0] == "list") return storePass == 1 ? await queryGate.Task.WaitAsync(token) : new(0, "No updates in fixture");
            if (start.ArgumentList[0] != "export") throw new Exception("Unexpected WinGet query in integration fixture");
            string path = start.ArgumentList[start.ArgumentList.IndexOf("--output") + 1];
            await File.WriteAllTextAsync(path, "{\"Sources\":[]}", token); return new(0, "");
        }, (_, _) => throw new Exception("No installer in Store integration test"),
            storeReader: _ => ++storePass == 1 ? storeGate.Task : Task.FromResult(failed),
            localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation>()));
        integrated.InventoryChanged += value =>
        {
            partialGate.TrySetResult(value);
            if (value.StoreCheckComplete && value.Installed.ContainsKey("chatgpt")) storeReadyGate.TrySetResult(value);
        };
        var inspection = integrated.InspectAsync(CancellationToken.None);
        var partial = await partialGate.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert(partial.IsPartial && !partial.StoreCheckComplete && !inspection.IsCompleted, "Initial local snapshot distinguishes pending Store enumeration");
        storeGate.TrySetResult(new(entries, true, Provider: "Fixture"));
        var storeReady = await storeReadyGate.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Assert(storeReady.IsPartial && storeReady.Installed["chatgpt"] == "1.2026.190.0" && !inspection.IsCompleted, "Store results become visible while unrelated WinGet query is still pending");
        queryGate.TrySetResult(new(0, "No updates in fixture"));
        var complete = await inspection;
        Assert(complete.Complete && complete.StoreCheckComplete && complete.Installed["chatgpt"] == "1.2026.190.0", "Full inspection merges late Store version before completion");
        Assert(complete.Evidence!["chatgpt"].Source == "Windows-App" && !complete.Evidence["chatgpt"].WinGetCompatible, "Inspection preserves Store installation channel");
        Assert(!partial.Installed.ContainsKey("chatgpt"), "Earlier partial snapshot was not mutated by Store merge");
        var unhealthy = await integrated.InspectAsync(CancellationToken.None);
        Assert(!unhealthy.Complete && !unhealthy.StoreCheckComplete && unhealthy.StoreError is not null, "Successful WinGet export cannot mask failed Store enumeration");
        Assert(unhealthy.Installed["chatgpt"] == "1.2026.190.0" && unhealthy.Evidence!["chatgpt"].Source == "Windows-App", "Full failed recheck retains previously verified package state");
        var ea = ProgramCatalog.Entries.Single(e => e.Id == "extra-electronicarts-eadesktop");
        var fakeEa = new ProgramInstallation("1.0", "Registry", output);
        var eaInventory = new ProgramInventory(true, new Dictionary<string, string?> { [ea.Id] = "1.0" },
            Updates: new Dictionary<string, string> { [ea.Id] = "2.0" }, Evidence: new Dictionary<string, ProgramInstallation> { [ea.Id] = fakeEa });
        Assert(!eaInventory.HasUpdate(ea.Id), "EA provider preference excludes queued WinGet replacement even if a row is injected");
        var field = typeof(ProgramInstallService).GetField("_lastEvidence", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(integrated, new Dictionary<string, ProgramInstallation> { [ea.Id] = fakeEa });
        bool blocked = false;
        try { integrated.UpgradeCommand(ea, new(ea.PackageId!, "2.0", "Fixture", DateTime.UtcNow)); }
        catch (InvalidOperationException) { blocked = true; }
        Assert(blocked, "EA backend also refuses the unrequested WinGet upgrade path");
        var terminal = ProgramCatalog.Entries.Single(e => e.Id == "windows-terminal");
        const string currentTerminal = "1.24.12741.0", nextTerminal = "1.25.3131.0";
        string terminalRow = $"Windows Terminal   {ProgramInstallService.TerminalStoreId}   {currentTerminal}   {nextTerminal}   msstore";
        Assert(ProgramInstallService.ReadTerminalStoreOffer(terminalRow, currentTerminal) == nextTerminal
            && ProgramInstallService.ReadTerminalStoreOffer(terminalRow, "1.22.0.0") is null
            && ProgramInstallService.ReadTerminalStoreOffer(terminalRow + "\n" + terminalRow, currentTerminal) is null, "Store offer must be unique and match the installed version");
        string manifest = $"""
            PackageIdentifier: Microsoft.WindowsTerminal
            PackageVersion: {nextTerminal}
            PackageFamilyName: Microsoft.WindowsTerminal_8wekyb3d8bbwe
            InstallerType: msix
            - InstallerUrl: ignored
              InstallerUrl: https://github.com/microsoft/terminal/releases/download/v{nextTerminal}/Microsoft.WindowsTerminal_{nextTerminal}_8wekyb3d8bbwe.msixbundle
              InstallerSha256: {new string('a', 64)}
            """;
        Assert(ProgramInstallService.TerminalMsixMatches(manifest, nextTerminal)
            && !ProgramInstallService.TerminalMsixMatches(manifest.Replace("WindowsTerminal_8we", "WindowsTerminalPreview_8we"), nextTerminal)
            && !ProgramInstallService.TerminalMsixMatches(manifest.Replace("github.com/microsoft", "example.org/microsoft"), nextTerminal),
            "The direct package must be the official Microsoft MSIX for the installed family");
        var terminalProof = new ProgramInstallation(currentTerminal, "Windows-App", output, false);
        bool upgraded = false;
        var terminalService = new ProgramInstallService((start, _) => Task.FromResult(new WinGetResult(0,
            start.ArgumentList.Contains("msstore") && !upgraded ? terminalRow : "")),
            (start, started) =>
            {
                Assert(start.ArgumentList.SequenceEqual(new[] { "upgrade", "--id", ProgramInstallService.TerminalStoreId, "--exact", "--source", "msstore", "--disable-interactivity", "--silent" })
                    && !start.UseShellExecute && start.CreateNoWindow, "Exact Store upgrade runs in PowerLeitstand with no external window");
                started(); upgraded = true; return Task.FromResult(0);
            },
            storeReader: _ => Task.FromResult(new ProgramStoreSnapshot(new Dictionary<string, ProgramInstallation>
            { [terminal.Id] = upgraded ? terminalProof with { Version = nextTerminal } : terminalProof }, true)),
            localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation>()));
        var terminalInventory = await terminalService.InspectUpdatesAsync([terminal], CancellationToken.None);
        var terminalPackage = await terminalService.ResolveCheckedUpdateAsync(terminal, terminalInventory, CancellationToken.None);
        Assert(terminalPackage?.VersionSource == "msstore" && ProgramInstallService.CheckedUpdateMatches(terminal, terminalPackage, terminalInventory)
            && !ProgramInstallService.CheckedUpdateMatches(terminal, terminalPackage, terminalInventory with { StoreCheckComplete = false }), "Only a complete matching Store offer becomes installable");
        Assert(await terminalService.UpgradeAsync(terminal, terminalPackage!, () => { }) == 0 && upgraded, "Confirmed offer reaches the Store installer");
        var terminalResult = await terminalService.InspectUpdatesAsync([terminal], CancellationToken.None);
        Assert(terminalResult.Installed[terminal.Id] == nextTerminal && !terminalResult.HasUpdate(terminal.Id), "New Store version is detected after upgrade");
        File.WriteAllText(Path.Combine(output, "Store-Checks.json"), JsonSerializer.Serialize(new { Checks = checks, Passed = true, InstallersExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks} Store-provider/fallback/cache/cancellation checks."); return 0;
    }

    internal static async Task<int> Live(string output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var native = await ProgramStoreInventory.ReadNativeAsync(timeout.Token); double nativeMs = watch.Elapsed.TotalMilliseconds;
        var powershell = await ProgramStoreInventory.ReadPowerShellAsync(timeout.Token);
        if (powershell.TryGetValue("chatgpt", out var shell) && (!native.TryGetValue("chatgpt", out var found) || shell.Version != found.Version))
            throw new Exception("Native and independent PowerShell ChatGPT identities differ");
        var ea = ProgramCatalog.Entries.Single(e => e.Id == "extra-electronicarts-eadesktop");
        var local = ProgramLocalInventory.Read([ea], timeout.Token, includeManaged: false).GetValueOrDefault(ea.Id);
        var start = ProgramPublisherLauncher.CreateEaStart(ea, local);
        if (local is not null && start is null) throw new Exception("Installed EA main executable could not be verified for open action");
        if (start is not null && (start.ArgumentList.Count != 0 || start.Arguments.Length != 0 || !start.UseShellExecute)) throw new Exception("Unexpected EA launcher arguments");
        if (local is not null && ProgramPublisherLauncher.CreateEaStart(ea, local with { Source = "SelectedFile" }) is not null) throw new Exception("Unconfirmed installation channel must not create publisher action");
        if (local is not null && ProgramPublisherLauncher.CreateEaStart(ea, local with { Version = "0.0.1" }) is not null) throw new Exception("Wrong local EA version must not be launched");
        File.WriteAllText(Path.Combine(output, "Live-Store-EA.json"), JsonSerializer.Serialize(new { Checked = DateTimeOffset.Now, NativeMilliseconds = nativeMs,
            Native = native, PowerShell = powershell, Ea = local, EaStartFile = start?.FileName, ExternalAppStarted = false, InstallersExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"LIVE STORE: {native.Count} mapped native packages in {nativeMs:F0}ms; ChatGPT {native.GetValueOrDefault("chatgpt")?.Version}; independent PowerShell agreement; EA command verified without launching.");
        return 0;
    }
}
