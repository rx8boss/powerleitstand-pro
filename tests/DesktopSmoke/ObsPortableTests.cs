using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PowerControlProWpf;

internal static class ObsPortableTests
{
    internal static async Task<int> VerifyOfficialAsync(string output, string archive)
    {
        Directory.CreateDirectory(output);
        if (new FileInfo(archive).Length != 223737114 || ProgramPortableUpdates.Hash(archive) != "3369A3ECD67B5669A8A08D77357F87CC786D1FBA42FBBD1054D841A69F6271A7")
            throw new Exception("Official pinned OBS 33.0.0 archive not verified");
        string root = Path.Combine(output, "Extracted"); await ProgramPortable.ExtractAsync(archive, root, default);
        string exe = Path.Combine(root, "bin", "64bit", "obs64.exe");
        var binary = ObsPortableUpdates.ReadBinary(exe);
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
        File.WriteAllText(Path.Combine(output, "official-binary.json"), JsonSerializer.Serialize(new { info.ProductName, info.CompanyName, info.ProductVersion, info.FileVersion, binary }));
        if (binary is not { Version: "33.0.0", Machine: 0x8664 }) throw new Exception("Official OBS binary identity not recognized");
        var method = typeof(ObsPortableUpdates).GetMethod("PackageFiles", BindingFlags.Static | BindingFlags.NonPublic)!;
        var files = (System.Collections.IDictionary)method.Invoke(null, [root, "33.0.0", (Func<string, ObsPortableBinary?>)ObsPortableUpdates.ReadBinary, CancellationToken.None])!;
        File.WriteAllText(Path.Combine(root, "portable_mode"), "");
        if (ObsPortableUpdates.Identify(exe) is null) throw new Exception("Official binary with extensionless marker not recognized");
        var store = new ProgramPortableStore(Path.Combine(output, "paths.json")); store.Remember("obs-studio", exe);
        if (store.Read().GetValueOrDefault("obs-studio") is not { Version: "33.0.0", Source: "Portable", WinGetCompatible: false }) throw new Exception("Saved official portable copy not recognized");
        File.Move(Path.Combine(root, "portable_mode"), Path.Combine(root, "portable_mode.txt"));
        if (!store.Read().ContainsKey("obs-studio")) throw new Exception("Official binary with txt marker not recognized");
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "obs-studio");
        var selected = ProgramFileAssociations.Validate(entry, exe);
        if (selected.Source != "Portable" || selected.Version != "33.0.0") throw new Exception("Manual official portable copy lost source identity");
        var association = new ProgramFileAssociations(Path.Combine(output, "association.json")); association.Save(entry, exe);
        var evidence = new Dictionary<string, ProgramInstallation> { [entry.Id] = new("31.0.0", "Registry", Path.Combine(output, "OtherCopy", "obs64.exe")) };
        association.Merge([entry], evidence, default);
        if (evidence[entry.Id].Source != "Portable" || evidence[entry.Id].Version != "33.0.0" || evidence[entry.Id].Path != exe) throw new Exception("Explicit copy mixed with unrelated registry version");
        string config = Path.Combine(root, "config", "basic", "scenes", "fixture.json");
        string plugin = Path.Combine(root, "plugins", "fixture", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!); File.WriteAllText(config, "private fixture scene");
        Directory.CreateDirectory(Path.GetDirectoryName(plugin)!); File.WriteAllText(plugin, "private fixture plugin");
        string userHashes = ProgramPortableUpdates.Hash(config) + ProgramPortableUpdates.Hash(plugin);
        var release = JsonSerializer.SerializeToUtf8Bytes(new { tag_name = "33.0.0", draft = false, prerelease = false,
            assets = new[] { new { name = "OBS-Studio-33.0.0-Windows-x64.zip", size = new FileInfo(archive).Length,
                digest = "sha256:" + ProgramPortableUpdates.Hash(archive), browser_download_url = "https://github.com/obsproject/obs-studio/releases/download/33.0.0/OBS-Studio-33.0.0-Windows-x64.zip" } } });
        var updater = new ObsPortableUpdates((_, _) => Task.FromResult(release), (_, target, progress, token) =>
        { token.ThrowIfCancellationRequested(); File.Copy(archive, target); progress(new(new FileInfo(archive).Length, new FileInfo(archive).Length)); return Task.CompletedTask; }, Path.Combine(output, "Backups"));
        var offer = await updater.ResolveAsync(new("33.0.0", "Portable", exe, false), default) ?? throw new Exception("Real package fixture offer missing");
        await updater.ApplyAsync(offer, _ => { }, _ => { }, default, reinstall: true);
        if (ObsPortableUpdates.Identify(exe)?.Version != "33.0.0") throw new Exception("Real package completion identity lost");
        if (userHashes != ProgramPortableUpdates.Hash(config) + ProgramPortableUpdates.Hash(plugin)) throw new Exception("Real package user files changed");
        if (Directory.GetDirectories(output, ".PowerLeitstand-OBS-*").Length != 0) throw new Exception("Real package stage not cleaned");
        Console.WriteLine($"PASS 11 official OBS archive, real PE metadata, {files.Count} package-file identities, both markers, stored/manual copy and complete same-version replacement on private copy; no OBS execution"); return 0;
    }

    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        async Task Failure(Func<Task> action, string message)
        { try { await action(); } catch (Exception e) when (e is IOException or OperationCanceledException) { checks++; return; } throw new Exception("Expected refusal: " + message); }
        async Task<Fixture> New(string name, Dictionary<string, string>? extra = null, string latest = "33.0.0")
        { var f = new Fixture(Path.Combine(output, name), extra, latest); await f.Initialize(); return f; }

        Assert(ObsPortableUpdates.ReadBinary(Path.Combine(output, "missing.exe")) is null, "missing executable is unconfirmed without crashing inspection");
        Assert(ProgramLocalInventory.MatchesPortable("obs-studio", "OBS Studio", "OBS"), "official OBS resource company recognized");
        Assert(!ProgramLocalInventory.MatchesPortable("obs-studio", "Other software", "OBS"), "company alone never confirms OBS identity");

        var markers = await New("markers");
        Assert(ObsPortableUpdates.Identify(markers.Exe, markers.Binary)?.Version == "32.0.0", "txt marker confirms exact portable layout");
        File.Move(Path.Combine(markers.Root, "portable_mode.txt"), Path.Combine(markers.Root, "portable_mode"));
        Assert(ObsPortableUpdates.Identify(markers.Exe, markers.Binary) is not null, "extensionless official marker also recognized");
        Assert(ObsPortableUpdates.Identify(markers.Exe.ToUpperInvariant(), markers.Binary) is not null, "Windows executable casing does not change identity");
        File.Delete(Path.Combine(markers.Root, "portable_mode"));
        Assert(ObsPortableUpdates.Identify(markers.Exe, markers.Binary) is null, "ordinary OBS exe without marker is not converted");
        Directory.CreateDirectory(Path.Combine(markers.Root, "portable_mode"));
        Assert(ObsPortableUpdates.Identify(markers.Exe, markers.Binary) is null, "directory is not a marker file");
        Assert(ObsPortableUpdates.Identify(markers.Exe, _ => null) is null, "missing binary identity remains unconfirmed");
        Assert(!ObsPortableUpdates.SafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), "desktop itself is never a package root");
        Assert(!ObsPortableUpdates.SafeRoot(Path.GetPathRoot(markers.Root)!), "drive root is never a package root");

        var success = await New("success"); var offer = await success.Offer();
        Assert(offer.Id == "obs-studio" && offer.Version == "33.0.0" && offer.Path == success.Exe, "offer is tied to exact OBS copy and official release");
        var protectedBefore = success.UserHashes();
        var progress = new List<ProgramDownload>(); var steps = new List<ProgramStepEvent>();
        await success.Updater().ApplyAsync(offer, steps.Add, progress.Add, default);
        Assert(success.Binary(success.Exe)?.Version == "33.0.0", "whole-package update ends with verified new main executable");
        Assert(File.ReadAllText(Path.Combine(success.Root, "bin", "64bit", "obs.dll")) == "dll33", "matching runtime DLL updates together");
        Assert(File.Exists(Path.Combine(success.Root, "core", "new.dll")) && !File.Exists(Path.Combine(success.Root, "obsolete.txt")), "owned new module added and owned obsolete file removed");
        Assert(protectedBefore == success.UserHashes(), "settings, scenes, recordings and both custom plugin locations preserved byte-for-byte");
        Assert(!Directory.GetDirectories(success.Parent, ".PowerLeitstand-OBS-*").Any(), "temporary archives and displaced files cleaned after success");
        Assert(progress.Count >= 6 && progress.All(p => p.Total > 0 && p.Received <= p.Total)
            && progress.Zip(progress.Skip(1)).All(p => p.Second.Received >= p.First.Received)
            && progress.Last().Percent == 100, "both real archives have cumulative bounded monotonic download progress");
        Assert(steps.SequenceEqual(new[] { new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Active), new(ProgramStep.Download, ProgramStepState.Done), new(ProgramStep.Install, ProgramStepState.Active), new(ProgramStep.Install, ProgramStepState.Done) }), "download/install phases finish only after actual verification");
        var backup = Directory.GetDirectories(success.Backups).Single();
        using (var zip = ZipFile.OpenRead(Path.Combine(backup, "previous-package.zip")))
        {
            Assert(zip.Entries.Count == success.OldFiles.Count && zip.Entries.Any(e => e.FullName == "obsolete.txt"), "verified backup contains previous owned package");
            Assert(!zip.Entries.Any(e => e.FullName.StartsWith("config/") || e.FullName.StartsWith("plugins/")), "backup does not collect unrelated user settings or plugins");
        }
        Assert(File.ReadAllText(Path.Combine(backup, "backup.json")).Contains("complete"), "backup journal records confirmed completion");

        foreach (string problem in new[] { "modified", "collision", "locked", "cancel", "bad-hash", "changed-source", "changed-target", "wrong-version", "wrong-architecture", "traversal", "config-payload", "old-unsupported" })
        {
            Dictionary<string, string>? extra = problem switch
            {
                "traversal" => new() { ["../escape.exe"] = "bad" },
                "config-payload" => new() { ["config/basic/scenes/user.json"] = "must not overwrite" },
                _ => null
            };
            var f = await New("refuse-" + problem, extra);
            var selected = await f.Offer();
            if (problem == "modified") File.WriteAllText(Path.Combine(f.Root, "bin", "64bit", "obs.dll"), "user-modified runtime");
            if (problem == "collision") { Directory.CreateDirectory(Path.Combine(f.Root, "core")); File.WriteAllText(Path.Combine(f.Root, "core", "new.dll"), "user module"); }
            if (problem == "wrong-version") f.OverrideBinaryVersion = "34.0.0";
            if (problem == "wrong-architecture") f.OverrideMachine = 0xaa64;
            if (problem == "bad-hash") f.BadDownload = true;
            if (problem == "changed-source") f.ChangeSelectedSource = true;
            if (problem == "old-unsupported") f.MissingOldRelease = true;
            var before = f.AllHashes();
            using var cancel = new CancellationTokenSource();
            f.Checkpoint = stage =>
            {
                if (problem == "cancel" && stage == "before-commit") cancel.Cancel();
                if (problem == "changed-target" && stage == "downloaded-new") File.WriteAllText(f.Exe, "OBS:32.0.1:x64");
            };
            using var locked = problem == "locked" ? new FileStream(Path.Combine(f.Root, "bin", "64bit", "obs.dll"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
            await Failure(() => f.Updater().ApplyAsync(selected, _ => { }, _ => { }, cancel.Token), problem);
            locked?.Dispose();
            if (problem == "changed-target") Assert(f.Binary(f.Exe)?.Version == "32.0.1", "changed target is not overwritten or rolled back to a stale snapshot");
            else Assert(before == f.AllHashes(), "refusal preserves entire target: " + problem);
            Assert(!Directory.GetDirectories(f.Parent, ".PowerLeitstand-OBS-*").Any(), "refused operation cleans private staging: " + problem);
        }

        foreach (var fault in new[] { "old:", "new:", "after-commit" })
        {
            var f = await New("rollback-" + fault.TrimEnd(':')); var selected = await f.Offer(); var before = f.AllHashes(); bool triggered = false;
            f.Checkpoint = stage => { if (!triggered && stage.StartsWith(fault)) { triggered = true; throw new IOException("fixture commit fault"); } };
            await Failure(() => f.Updater().ApplyAsync(selected, _ => { }, _ => { }, default), fault);
            Assert(triggered && before == f.AllHashes(), "rollback restores every original program and user file after " + fault);
            Assert(File.ReadAllText(Path.Combine(Directory.GetDirectories(f.Backups).Single(), "backup.json")).Contains("rolled-back"), "rollback recorded " + fault);
            Assert(!Directory.GetDirectories(f.Parent, ".PowerLeitstand-OBS-*").Any(), "rollback stage cleaned " + fault);
        }
        var late = await New("late-cancel"); var lateOffer = await late.Offer(); using var lateCancel = new CancellationTokenSource();
        late.Checkpoint = stage => { if (stage.StartsWith("old:")) lateCancel.Cancel(); };
        await late.Updater().ApplyAsync(lateOffer, _ => { }, _ => { }, lateCancel.Token);
        Assert(lateCancel.IsCancellationRequested && late.Binary(late.Exe)?.Version == "33.0.0", "late cancellation drains atomic commit instead of leaving a mixed package");

        var same = await New("reinstall", latest: "32.0.0"); var sameOffer = await same.Offer(); var sameBefore = same.AllHashes();
        await Failure(() => same.Updater().ApplyAsync(sameOffer, _ => { }, _ => { }, default), "same version requires reinstallation intent");
        await same.Updater().ApplyAsync(sameOffer, _ => { }, _ => { }, default, reinstall: true);
        Assert(sameBefore == same.AllHashes(), "explicit same-version reinstallation preserves all content");

        var incomplete = await New("rollback-conflict"); var incompleteOffer = await incomplete.Offer(); bool once = false;
        incomplete.Checkpoint = stage =>
        {
            if (!once && stage.StartsWith("new:")) { once = true; File.WriteAllText(Path.Combine(incomplete.Root, stage[4..].Replace('/', Path.DirectorySeparatorChar)), "unexpected concurrent edit"); throw new IOException("fixture conflict"); }
        };
        await Failure(() => incomplete.Updater().ApplyAsync(incompleteOffer, _ => { }, _ => { }, default), "rollback conflict");
        Assert(Directory.GetDirectories(incomplete.Parent, ".PowerLeitstand-OBS-*").Length == 1 && Directory.GetFiles(incomplete.Backups, "previous-package.zip", SearchOption.AllDirectories).Length == 1,
            "unresolved rollback preserves both staging and verified backup for manual recovery");

        var route = await New("service-route"); var routeObs = route.Updater();
        var router = new ProgramPortableUpdates(route.Read, route.Download, route.Backups, obs: routeObs);
        var service = new ProgramInstallService(query: (_, _) => Task.FromResult(new WinGetResult(0, "{\"Sources\":[]}")),
            install: (_, _) => throw new Exception("OBS portable must not invoke WinGet installer"), portableUpdates: router,
            localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation> { ["obs-studio"] = route.Proof }));
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "obs-studio");
        var inventory = await service.InspectAsync(default);
        File.WriteAllText(Path.Combine(output, "service-inventory.json"), JsonSerializer.Serialize(inventory));
        Assert(inventory.Evidence?.GetValueOrDefault(entry.Id)?.Source == "Portable" && inventory.PortableUpdates?.ContainsKey(entry.Id) == true && inventory.HasUpdate(entry.Id), "normal inspection exposes confirmed portable OBS update");
        var package = await service.ResolveAsync(entry, default);
        Assert(package?.PortableUpdate?.Id == "obs-studio" && package.VersionSource == "Publisher", "normal resolver retains manufacturer route");
        int started = 0; var serviceEvents = new List<ProgramStepEvent>(); service.StepChanged += serviceEvents.Add;
        Assert(await service.UpgradeAsync(entry, package!, () => started++) == 0 && started == 1, "existing installation service executes OBS package adapter once");
        Assert(serviceEvents.Any(e => e.Step == ProgramStep.Install && e.State == ProgramStepState.Done), "package adapter reports actual completion through existing service events");
        var refreshed = await service.InspectAsync(default);
        Assert(refreshed.Installed[entry.Id] == "33.0.0" && !refreshed.HasUpdate(entry.Id), "fresh inspection clears completed update with verified new version");
        Console.WriteLine($"PASS {checks} OBS portable identity, two-package ownership, progress, preservation, cancellation, rollback and service integration checks; fixture metadata and archives only, no real OBS executed");
        return 0;
    }

    private sealed class Fixture
    {
        internal string Parent { get; }
        internal string Root => Path.Combine(Parent, "OBS-Portable");
        internal string Exe => Path.Combine(Root, "bin", "64bit", "obs64.exe");
        internal string Backups => Path.Combine(Parent, "Backups");
        internal readonly Dictionary<string, string> OldFiles = new() { ["bin/64bit/obs64.exe"] = "OBS:32.0.0:x64", ["bin/64bit/obs.dll"] = "dll32", ["data/notes.txt"] = "notes32", ["obsolete.txt"] = "old owned file" };
        private readonly Dictionary<string, string> _newFiles;
        private readonly string _latest;
        private string OldZip => Path.Combine(Parent, "old.zip");
        private string NewZip => _latest == "32.0.0" ? OldZip : Path.Combine(Parent, "new.zip");
        internal Action<string>? Checkpoint;
        internal bool BadDownload, ChangeSelectedSource, MissingOldRelease;
        internal string? OverrideBinaryVersion;
        internal ushort? OverrideMachine;
        internal Fixture(string parent, Dictionary<string, string>? extra, string latest)
        {
            Parent = parent; _latest = latest;
            _newFiles = new() { ["bin/64bit/obs64.exe"] = "OBS:" + latest + ":x64", ["bin/64bit/obs.dll"] = "dll33", ["data/notes.txt"] = "notes33", ["core/new.dll"] = "new owned module" };
            if (extra is not null) foreach (var item in extra) _newFiles[item.Key] = item.Value;
        }
        internal async Task Initialize()
        {
            Directory.CreateDirectory(Parent); Zip(OldZip, OldFiles); if (NewZip != OldZip) Zip(NewZip, _newFiles);
            await ProgramPortable.ExtractAsync(OldZip, Root, default);
            File.WriteAllText(Path.Combine(Root, "portable_mode.txt"), "");
            foreach (var pair in new Dictionary<string, string> { ["config/basic/scenes/user.json"] = "user scenes", ["plugins/custom/custom.dll"] = "user plugin", ["obs-plugins/64bit/custom.dll"] = "legacy user plugin", ["recordings/private.mp4"] = "user recording" })
            { var path = Path.Combine(Root, pair.Key.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, pair.Value); }
        }
        private static void Zip(string path, Dictionary<string, string> files)
        { using var zip = ZipFile.Open(path, ZipArchiveMode.Create); foreach (var f in files) { using var writer = new StreamWriter(zip.CreateEntry(f.Key).Open()); writer.Write(f.Value); } }
        internal ObsPortableBinary? Binary(string path)
        {
            string text = File.ReadAllText(path); if (!text.StartsWith("OBS:")) return null;
            var parts = text.Split(':'); bool payload = path.Contains("new-package", StringComparison.Ordinal);
            return new(payload ? OverrideBinaryVersion ?? parts[1] : parts[1], payload ? OverrideMachine ?? (ushort)0x8664 : (ushort)0x8664);
        }
        internal ProgramInstallation Proof => new(Binary(Exe)!.Version, "Portable", Exe, false);
        internal ObsPortableUpdates Updater() => new(Read, Download, Backups, Binary, stage => Checkpoint?.Invoke(stage));
        internal async Task<PortableUpdate> Offer() => await Updater().ResolveAsync(Proof, default) ?? throw new Exception("Fixture offer missing");
        internal Task<byte[]> Read(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); bool latest = uri.AbsolutePath.EndsWith("/latest");
            string version = latest ? _latest : uri.Segments.Last();
            if (MissingOldRelease && !latest && version == "32.0.0") return Task.FromResult(Encoding.UTF8.GetBytes("{\"draft\":true,\"prerelease\":false}"));
            string file = version == "32.0.0" ? OldZip : NewZip;
            string hash = ChangeSelectedSource && !latest && version == _latest ? new string('a', 64) : ProgramPortableUpdates.Hash(file);
            var json = JsonSerializer.Serialize(new { tag_name = version, draft = false, prerelease = false, assets = new[] { new { name = "OBS-Studio-" + version + "-Windows-x64.zip", digest = "sha256:" + hash,
                browser_download_url = "https://github.com/obsproject/obs-studio/releases/download/" + version + "/OBS-Studio-" + version + "-Windows-x64.zip", size = new FileInfo(file).Length } } });
            return Task.FromResult(Encoding.UTF8.GetBytes(json));
        }
        internal Task Download(PortableUpdate request, string target, Action<ProgramDownload> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); string source = request.Version == "32.0.0" ? OldZip : NewZip;
            if (BadDownload) File.WriteAllText(target, "corrupt download"); else File.Copy(source, target);
            long size = new FileInfo(target).Length;
            progress(new(0, size)); progress(new(size / 2, size)); progress(new(size, size)); return Task.CompletedTask;
        }
        internal string AllHashes() => string.Join('\n', Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).Select(p => Path.GetRelativePath(Root, p) + "=" + ProgramPortableUpdates.Hash(p)));
        internal string UserHashes() => string.Join('\n', Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Where(p => Path.GetRelativePath(Root, p).Replace('\\', '/') is var relative && !OldFiles.ContainsKey(relative) && !_newFiles.ContainsKey(relative)).Order(StringComparer.OrdinalIgnoreCase).Select(p => Path.GetRelativePath(Root, p) + "=" + ProgramPortableUpdates.Hash(p)));
    }
}
