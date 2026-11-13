using System.Diagnostics;
using System.Reflection;
using PowerControlProWpf;

internal static class ProgramReinstallTests
{
    internal static async Task<int> Run(string output)
    {
        int checks = 0;
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
        ProcessStartInfo? executed = null; bool offerOld = true, mismatch = false;
        var service = new ProgramInstallService(query: (start, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (start.ArgumentList[0] == "search") return Task.FromResult(new WinGetResult(0, "Fixture App  FixturePublisher.FixtureApp  2.0\n"));
            int index = start.ArgumentList.IndexOf("--version"); string version = index >= 0 ? start.ArgumentList[index + 1] : "3.0.22";
            return Task.FromResult(!offerOld && version == "3.0.21" ? new WinGetResult(1, "Unavailable")
                : new WinGetResult(0, "Found Fixture [" + (mismatch ? "Wrong.Identity" : start.ArgumentList[start.ArgumentList.IndexOf("--id") + 1]) + "]\nVersion: " + version + "\n"));
        }, install: (start, started) => { executed = start; started(); return Task.FromResult(0); });
        var evidence = new Dictionary<string, ProgramInstallation> { [entry.Id] = new("3.0.21", "Registry", @"C:\Fixture\vlc.exe") };
        Set(service, "_lastEvidence", evidence);
        Map(service, "_installedPackageIds")[entry.Id] = entry.PackageId!;
        Set(service, "_recentExport", new Dictionary<string, string?> { [entry.PackageId!] = "3.0.21" });
        var inventory = new ProgramInventory(true, new Dictionary<string, string?> { [entry.Id] = "3.0.21" },
            Updates: new Dictionary<string, string> { [entry.Id] = "3.0.22" }, Evidence: evidence);
        Assert(inventory.HasUpdate(entry.Id) && service.CanReinstall(entry, inventory), "Update and reinstallation available independently");
        Assert(service.CanReinstall(entry, inventory with { Complete = false, StoreCheckComplete = false, StoreError = "Unrelated Store error" }), "Unrelated Store failure does not block confirmed WinGet copy");
        var options = await service.ResolveReinstallOptionsAsync(entry, inventory, default);
        Assert(options.Count == 2 && options[0].Version == "3.0.21" && options[0].ReinstallProof!.SameVersion
            && options[1].Version == "3.0.22" && !options[1].ReinstallProof!.SameVersion, "Same and current available versions are explicit independent choices");
        Assert(service.ReinstallStillMatches(entry, options[0], inventory) && service.ReinstallStillMatches(entry, options[1], inventory), "Both choices bind to same confirmed installed copy");
        await service.ReinstallAsync(entry, options[1], inventory, () => { });
        Assert(executed is not null && executed.ArgumentList[0] == "install" && executed.ArgumentList.Contains("--force")
            && executed.ArgumentList.Contains("3.0.22") && !executed.ArgumentList.Contains("--no-upgrade"), "Explicit latest reinstallation uses forced install rather than silently switching to upgrade");
        executed = null;
        var changed = inventory with { Installed = new Dictionary<string, string?> { [entry.Id] = "3.0.23" } };
        Assert(!service.ReinstallStillMatches(entry, options[0], changed), "Changed version invalidates earlier choice");
        bool blocked = false; try { await service.ReinstallAsync(entry, options[0], changed, () => { }); } catch (InvalidOperationException) { blocked = true; }
        Assert(blocked && executed is null, "Changed target cannot launch installer");
        Assert(!service.CanReinstall(entry, inventory with { IsPartial = true }), "Partial scan cannot authorize operation");
        foreach (string source in new[] { "Windows-App", "Scoop", "Chocolatey", "SelectedFile", "Portable" })
        {
            var alternative = inventory with { InstalledSources = new Dictionary<string, string> { [entry.Id] = source },
                Evidence = new Dictionary<string, ProgramInstallation> { [entry.Id] = new("3.0.21", source, @"C:\Fixture\vlc.exe", false) } };
            var route = service.ReinstallCapability(entry, alternative);
            Assert(route.Route == ProgramReinstallRoute.Provider && !route.Automatic, source + " preserves existing source and exposes provider alternative");
            Assert(ProgramInstallService.ReinstallExplanation(route, true).Length > 15 && ProgramInstallService.ReinstallExplanation(route, false).Length > 15, source + " has DE/EN explanation");
        }
        var copies = inventory with { Evidence = new Dictionary<string, ProgramInstallation> { [entry.Id] = evidence[entry.Id] with { ManagedCopies = [new("Scoop", "vlc", "3.0.21", @"C:\Fixture", "User")] } } };
        Assert(!service.CanReinstall(entry, copies), "Multiple source copies never silently migrate to WinGet");
        var ambiguous = (HashSet<string>)typeof(ProgramInstallService).GetField("_ambiguousReinstallIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        ambiguous.Add(entry.Id); Assert(!service.CanReinstall(entry, inventory), "Conflicting installed package identities cannot authorize forced reinstall"); ambiguous.Clear();
        offerOld = false; options = await service.ResolveReinstallOptionsAsync(entry, inventory, default);
        Assert(options.Count == 1 && options[0].Version == "3.0.22" && !options[0].ReinstallProof!.SameVersion, "Unavailable previous version explicitly offers latest only");
        mismatch = true; Assert((await service.ResolveReinstallOptionsAsync(entry, inventory, default)).Count == 0, "Wrong remote identity never becomes reinstall option"); mismatch = false;
        Set(service, "_recentExport", new Dictionary<string, string?> { [entry.PackageId!] = null });
        var unknown = inventory with { Installed = new Dictionary<string, string?> { [entry.Id] = null } };
        Assert(service.CanReinstall(entry, unknown), "Unknown local version does not hide verified package route");
        options = await service.ResolveReinstallOptionsAsync(entry, unknown, default);
        Assert(options.Count == 1 && options[0].Version == "3.0.22" && !options[0].ReinstallProof!.SameVersion, "Unknown installed version cannot pretend same-version reinstall");
        using (var cancelled = new CancellationTokenSource())
        { cancelled.Cancel(); blocked = false; try { await service.ResolveReinstallOptionsAsync(entry, inventory, cancelled.Token); } catch (OperationCanceledException) { blocked = true; } Assert(blocked, "Cancellation stops metadata checks"); }
        foreach (var result in new[] { new WinGetResult(1, "Found [VideoLAN.VLC]\nVersion: 3.0.21"), new(0, "Found [Other.VLC]\nVersion: 3.0.21"), new(0, "Found [VideoLAN.VLC]\nVersion: unknown"), new(0, "Found [VideoLAN.VLC]\nVersion: 3.0.21\nVersion: 3.0.22") })
            Assert(ProgramInstallService.ParseReinstallWinGet(entry.PackageId!, "3.0.21", result) is null, "Malformed/failed exact version check rejected");
        Assert(ProgramInstallService.ParseReinstallWinGet(entry.PackageId!, "3.0.21", new(0, "Found [VideoLAN.VLC]\nVersion: 3.0.22")) is null, "Different requested version rejected");
        var latestProof = options[0];
        Set(service, "_recentExport", new Dictionary<string, string?> { [entry.PackageId!] = "3.0.22" });
        Assert(service.ReinstallResultVerified(entry, latestProof, unknown with { Installed = new Dictionary<string, string?> { [entry.Id] = "3.0.22" } }), "Final exact package version confirms result");
        Set(service, "_recentExport", new Dictionary<string, string?> { [entry.PackageId!] = "3.0.21" });
        Assert(!service.ReinstallResultVerified(entry, latestProof, inventory), "Old installed version cannot confirm latest reinstall");
        var found = (await service.SearchAsync("Fixture", default)).Single();
        Set(service, "_recentInventory", new ProgramInventory(true, new Dictionary<string, string?>(), Updates: new Dictionary<string, string>(), UpdateCheckComplete: true, CheckedUtc: DateTime.UtcNow));
        Set(service, "_recentUpdatesText", "Fixture App  FixturePublisher.FixtureApp  1.0  2.0\n");
        var searchInventory = await service.InspectForSearchAsync(default);
        Assert(searchInventory.FromCache && searchInventory.Installed.GetValueOrDefault(found.Id) == "1.0" && searchInventory.HasUpdate(found.Id), "Cached search inherits exact installed/update row");
        Assert(service.CanReinstall(found, searchInventory), "Cached discovered package gets exact reinstallation identity");
        options = await service.ResolveReinstallOptionsAsync(found, searchInventory, default);
        Assert(options.Count >= 1 && options[0].ReinstallProof!.Installed.PackageVersion == "1.0", "Search retains package version separately from product version");
        Console.WriteLine("PASS " + checks + " reinstallation/source/search/version boundary checks; no real installer executed.");
        return 0;
    }
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static Dictionary<string, string> Map(object target, string field) => (Dictionary<string, string>)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
}
