using System.IO;
using System.IO.Compression;
using System.Text;
using PowerControlProWpf;

internal static partial class VlcZipTests
{
    internal sealed class NoPortable : IProgramPortableService
    {
        public Task<PortablePackage?> ResolveAsync(ProgramEntry entry, CancellationToken token) => Task.FromResult<PortablePackage?>(null);
        public Task<string> InstallAsync(ProgramEntry entry, PortablePackage package, string parent, IProgress<double?> progress, CancellationToken token) => throw new Exception("Unexpected ZIP installation in unrelated fixture");
    }
    internal static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        async Task Refuses(Func<Task> action, string why)
        { try { await action(); } catch (Exception e) when (e is IOException or OperationCanceledException) { checks++; return; } throw new Exception("Expected refusal: " + why); }
        async Task<Fixture> New(string name, Dictionary<string, string>? extra = null, string latest = "3.0.24")
        { var f = new Fixture(Path.Combine(output, name), extra, latest); await f.Initialize(); return f; }
        string index = Fixture.Index("3.0.24", 1234);
        Assert(VlcPortableUpdates.ParseIndex(index) == ("3.0.24", 1234L), "official directory row parsed");
        Assert(VlcPortableUpdates.ParseIndex(index + index) is null, "ambiguous source rows rejected");
        Assert(VlcPortableUpdates.ParseIndex(index, "3.0.21") is null, "versioned source identity exact");
        Assert(VlcPortableUpdates.ParseIndex(index.Replace("1234", "9999999999999")) is null, "archive size bounded");
        Assert(VlcPortableUpdates.Package(("3.0.24", 1234), new string('a', 64) + " *vlc-3.0.24-win64.zip\n")?.Sha256 == new string('A', 64), "published asterisk checksum format accepted");
        Assert(VlcPortableUpdates.Package(("3.0.24", 1234), new string('a', 64) + "  vlc-3.0.21-win64.zip") is null, "checksum filename bound to selected version");
        Assert(VlcPortableUpdates.ReadBinary(Path.Combine(output, "missing.exe")) is null, "missing binary unconfirmed");

        var identity = await New("identity");
        Assert(VlcPortableUpdates.Identify(identity.Exe, identity.Binary)?.Version == "3.0.21", "explicit marked copy recognized");
        string marker = Path.Combine(identity.Root, VlcPortableUpdates.ReceiptName); File.SetAttributes(marker, FileAttributes.Normal); File.Delete(marker);
        Assert(VlcPortableUpdates.Identify(identity.Exe, identity.Binary) is null, "arbitrary EXE scan cannot authorize ZIP updates");
        Assert(VlcPortableUpdates.Identify(identity.Exe, identity.Binary, false) is not null, "unmarked candidate can be offered for explicit association");
        File.WriteAllText(marker, "{\"Schema\":1e99,\"Variant\":\"VLC ZIP\",\"MainFile\":\"vlc.exe\"}");
        Assert(VlcPortableUpdates.Marker(identity.Root) is null, "invalid receipt fails closed without inspection crash"); File.Delete(marker);
        VlcPortableUpdates.ConfirmZip(identity.Exe, identity.Binary);
        File.WriteAllText(Path.Combine(identity.Root, "uninstall.exe"), "native installer evidence");
        Assert(VlcPortableUpdates.Identify(identity.Exe, identity.Binary) is null, "installer footprint rejects ZIP identity"); File.Delete(Path.Combine(identity.Root, "uninstall.exe"));
        Assert(!VlcPortableUpdates.SafePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "Windows excluded");
        Assert(!VlcPortableUpdates.SafePath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), "desktop itself excluded");
        Assert(!VlcPortableUpdates.ProofEligible(identity.Proof with { Variant = null }), "generic Portable label is insufficient");
        Assert(!VlcPortableUpdates.ProofEligible(identity.Proof with { WinGetCompatible = true }), "registered route is not converted");
        Assert(!VlcPortableUpdates.CanAdopt(new("3.0.21", "Registry", identity.Exe), identity.Exe), "same registered installation cannot be adopted");
        Assert(!VlcPortableUpdates.CanAdopt(new("3.0.21", "Registry"), identity.Exe), "unknown registered location cannot be mixed");
        Assert(!VlcPortableUpdates.CanAdopt(identity.Proof with { ManagedCopies = [new("Scoop", "vlc", "3.0.21", identity.Root, "User")] }, identity.Exe), "managed copies cannot be mixed");
        var separate = await New("separate");
        Assert(VlcPortableUpdates.CanAdopt(new("3.0.21", "Registry", identity.Exe), separate.Exe), "non-overlapping registered and ZIP copies can be associated independently");
        Assert(VlcPortableUpdates.CanAdopt(new("3.0.21", "Registry", identity.Exe), Path.Combine(separate.Parent, "NotCreated", "vlc.exe")), "new separate ZIP destination does not require an existing executable");

        var success = await New("success"); var offer = await success.Offer(); string users = success.UserHashes();
        Assert(success.Downloads == 0, "offer reads metadata only, never downloads ZIPs");
        var progress = new List<ProgramDownload>(); var steps = new List<ProgramStepEvent>();
        await success.Updater().ApplyAsync(offer, steps.Add, progress.Add, default);
        Assert(success.Binary(success.Exe)?.Version == "3.0.24", "new main version verified");
        Assert(File.ReadAllText(Path.Combine(success.Root, "libvlc.dll")) == "lib-new", "runtime updated with main");
        Assert(File.Exists(Path.Combine(success.Root, "plugins", "new.dll")) && !File.Exists(Path.Combine(success.Root, "obsolete.txt")), "new owned file installed and obsolete owned file removed");
        Assert(users == success.UserHashes(), "settings, media, custom plugins and receipt preserved");
        Assert(!Directory.GetDirectories(success.Parent, ".PowerLeitstand-VLC-*").Any(), "successful stage cleaned");
        Assert(progress.Count >= 6 && progress.Last().Percent == 100 && progress.Zip(progress.Skip(1)).All(p => p.Second.Received >= p.First.Received), "cumulative actual download progress monotonic");
        Assert(steps.SequenceEqual(new[] { new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Active), new(ProgramStep.Download, ProgramStepState.Done), new(ProgramStep.Install, ProgramStepState.Active), new(ProgramStep.Install, ProgramStepState.Done) }), "phases complete only after actual operations");
        string backup = Directory.GetDirectories(success.Backups).Single();
        using (var zip = ZipFile.OpenRead(Path.Combine(backup, "previous-package.zip")))
            Assert(zip.Entries.Count == success.OldFiles.Count && !zip.Entries.Any(e => e.FullName.Contains("custom") || e.FullName == VlcPortableUpdates.ReceiptName), "verified backup contains only original program files");
        Assert(File.ReadAllText(Path.Combine(backup, "backup.json")).Contains("complete"), "completion journal written");

        foreach (string problem in new[] { "modified", "collision", "locked", "cancel", "bad-hash", "changed-source", "changed-target", "wrong-version", "wrong-architecture", "traversal", "config", "receipt-payload", "old-missing", "unmarked", "readonly" })
        {
            Dictionary<string, string>? extra = problem switch { "traversal" => new() { ["../../escape.exe"] = "bad" }, "config" => new() { ["config/user.json"] = "bad" }, "receipt-payload" => new() { [VlcPortableUpdates.ReceiptName] = "bad" }, _ => null };
            var f = await New("refuse-" + problem, extra); var selected = await f.Offer();
            if (problem == "modified") File.WriteAllText(Path.Combine(f.Root, "libvlc.dll"), "user change");
            if (problem == "collision") File.WriteAllText(Path.Combine(f.Root, "plugins", "new.dll"), "custom collision");
            if (problem == "bad-hash") f.BadDownload = true;
            if (problem == "changed-source") f.ChangedSource = true;
            if (problem == "wrong-version") f.PayloadVersion = "9.0.0";
            if (problem == "wrong-architecture") f.PayloadMachine = 0xaa64;
            if (problem == "old-missing") f.MissingOld = true;
            if (problem == "unmarked") File.Delete(Path.Combine(f.Root, VlcPortableUpdates.ReceiptName));
            if (problem == "readonly") File.SetAttributes(Path.Combine(f.Root, "libvlc.dll"), FileAttributes.ReadOnly);
            string before = f.AllHashes(); using var cancel = new CancellationTokenSource();
            f.Checkpoint = phase => { if (problem == "cancel" && phase == "before-commit") cancel.Cancel(); if (problem == "changed-target" && phase == "downloaded-new") File.WriteAllText(f.Exe, "VLC:3.0.22:x64"); };
            using var locked = problem == "locked" ? new FileStream(Path.Combine(f.Root, "libvlc.dll"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
            await Refuses(() => f.Updater().ApplyAsync(selected, _ => { }, _ => { }, cancel.Token), problem); locked?.Dispose();
            Assert(problem == "changed-target" ? f.Binary(f.Exe)?.Version == "3.0.22" : before == f.AllHashes(), "refusal preserves current files: " + problem);
            Assert(!Directory.GetDirectories(f.Parent, ".PowerLeitstand-VLC-*").Any(), "refused stage cleaned: " + problem);
        }
        foreach (string fault in new[] { "old:", "new:", "after-commit" })
        {
            var f = await New("rollback-" + fault.TrimEnd(':')); var selected = await f.Offer(); string before = f.AllHashes(); bool fired = false;
            f.Checkpoint = phase => { if (!fired && phase.StartsWith(fault)) { fired = true; throw new IOException("injected replacement failure"); } };
            await Refuses(() => f.Updater().ApplyAsync(selected, _ => { }, _ => { }, default), fault);
            Assert(fired && before == f.AllHashes(), "complete rollback restores original package: " + fault);
            Assert(File.ReadAllText(Path.Combine(Directory.GetDirectories(f.Backups).Single(), "backup.json")).Contains("rolled-back"), "rollback journal recorded");
        }
        var late = await New("late-cancel"); var lateOffer = await late.Offer(); using var lateCancel = new CancellationTokenSource();
        late.Checkpoint = phase => { if (phase.StartsWith("old:")) lateCancel.Cancel(); };
        await late.Updater().ApplyAsync(lateOffer, _ => { }, _ => { }, lateCancel.Token);
        Assert(lateCancel.IsCancellationRequested && late.Binary(late.Exe)?.Version == "3.0.24", "late cancellation drains commit");
        var reinstall = await New("reinstall", latest: "3.0.21"); var reinstallOffer = await reinstall.Offer(); string same = reinstall.AllHashes();
        await Refuses(() => reinstall.Updater().ApplyAsync(reinstallOffer, _ => { }, _ => { }, default), "same version requires explicit reinstallation");
        await reinstall.Updater().ApplyAsync(reinstallOffer, _ => { }, _ => { }, default, reinstall: true);
        Assert(same == reinstall.AllHashes(), "explicit reinstallation preserves user files");
        var conflict = await New("rollback-conflict"); var conflictOffer = await conflict.Offer(); bool injected = false;
        conflict.Checkpoint = phase => { if (!injected && phase.StartsWith("new:")) { injected = true; File.WriteAllText(Path.Combine(conflict.Root, phase[4..].Replace('/', Path.DirectorySeparatorChar)), "concurrent edit"); throw new IOException("conflict"); } };
        await Refuses(() => conflict.Updater().ApplyAsync(conflictOffer, _ => { }, _ => { }, default), "concurrent rollback conflict");
        Assert(Directory.GetDirectories(conflict.Parent, ".PowerLeitstand-VLC-*").Length == 1 && Directory.GetFiles(conflict.Backups, "previous-package.zip", SearchOption.AllDirectories).Length == 1, "rollback conflict retains recovery files");

        var fresh = await New("new-install"); var entry = ProgramCatalog.Entries.Single(e => e.Id == "vlc"); var portable = new ProgramPortable(fresh.Updater());
        var package = await portable.ResolveAsync(entry, default) ?? throw new Exception("new ZIP offer missing"); string parent = Path.Combine(fresh.Parent, "SelectedDrive"); Directory.CreateDirectory(parent);
        string exe = await portable.InstallAsync(entry, package, parent, new Progress<double?>(), default);
        Assert(exe == Path.Combine(ProgramPortable.Destination(entry, package, parent), "vlc.exe") && VlcPortableUpdates.Identify(exe, fresh.Binary)?.Version == "3.0.24", "new ZIP route uses selected parent and explicit variant receipt");
        await Refuses(() => portable.InstallAsync(entry, package, parent, new Progress<double?>(), default), "existing destination cannot be overwritten");

        var route = await New("service"); var router = new ProgramPortableUpdates(vlc: route.Updater());
        var service = new ProgramInstallService(query: (_, _) => Task.FromResult(new WinGetResult(0, "{\"Sources\":[]}")), install: (_, _) => throw new Exception("ZIP route must not start WinGet"),
            portableFile: Path.Combine(output, "no-portable.json"), locationsFile: Path.Combine(output, "no-association.json"), portableUpdates: router,
            localReader: (_, _) => Task.FromResult(new Dictionary<string, ProgramInstallation> { ["vlc"] = route.Proof }));
        var inventory = await service.InspectAsync(default); Assert(inventory.HasUpdate("vlc") && inventory.PortableUpdates?.ContainsKey("vlc") == true, "normal inspection exposes confirmed separate ZIP update");
        var resolved = await service.ResolveAsync(entry, default) ?? throw new Exception("service ZIP offer missing"); int started = 0;
        Assert(await service.UpgradeAsync(entry, resolved, () => started++) == 0 && started == 1, "normal installer service executes ZIP adapter once");
        var after = await service.InspectAsync(default); Assert(after.Installed["vlc"] == "3.0.24" && !after.HasUpdate("vlc"), "fresh inspection clears completed update");
        Console.WriteLine($"PASS {checks} VLC ZIP sources, identity, new installation, whole-package replacement, preservation, rollback, cancellation and service checks; fixture archives only"); return 0;
    }

    private sealed class Fixture
    {
        internal string Parent { get; }
        internal string Root => Path.Combine(Parent, "VLC-Copy");
        internal string Exe => Path.Combine(Root, "vlc.exe");
        internal string Backups => Path.Combine(Parent, "Backups");
        internal readonly Dictionary<string, string> OldFiles = new() { ["vlc.exe"] = "VLC:3.0.21:x64", ["libvlc.dll"] = "lib-old", ["libvlccore.dll"] = "core-old", ["plugins/core.dll"] = "plugin-old", ["obsolete.txt"] = "owned old file" };
        private readonly Dictionary<string, string> _newFiles;
        private readonly string _latest;
        private string OldZip => Path.Combine(Parent, "old.zip");
        private string NewZip => _latest == "3.0.21" ? OldZip : Path.Combine(Parent, "new.zip");
        internal Action<string>? Checkpoint;
        internal bool BadDownload, ChangedSource, MissingOld;
        internal string? PayloadVersion;
        internal ushort? PayloadMachine;
        internal int Downloads;
        internal Fixture(string parent, Dictionary<string, string>? extra, string latest)
        {
            Parent = parent; _latest = latest;
            _newFiles = new() { ["vlc.exe"] = "VLC:" + latest + ":x64", ["libvlc.dll"] = "lib-new", ["libvlccore.dll"] = "core-new", ["plugins/core.dll"] = "plugin-new", ["plugins/new.dll"] = "new official module" };
            if (extra is not null) foreach (var p in extra) _newFiles[p.Key] = p.Value;
        }
        internal async Task Initialize()
        {
            Directory.CreateDirectory(Parent); Zip(OldZip, "3.0.21", OldFiles); if (NewZip != OldZip) Zip(NewZip, _latest, _newFiles);
            string initial = Path.Combine(Parent, "initial"); await ProgramPortable.ExtractAsync(OldZip, initial, default);
            Directory.Move(Path.Combine(initial, "vlc-3.0.21"), Root); Directory.Delete(initial); VlcPortableUpdates.ConfirmZip(Exe, Binary);
            foreach (var p in new Dictionary<string, string> { ["config/user.json"] = "user settings", ["vlcrc"] = "custom config", ["plugins/custom.dll"] = "custom plugin", ["media/movie.mp4"] = "media" })
            { string path = Path.Combine(Root, p.Key.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, p.Value); }
        }
        private static void Zip(string file, string version, Dictionary<string, string> files)
        { using var zip = ZipFile.Open(file, ZipArchiveMode.Create); foreach (var p in files) { using var writer = new StreamWriter(zip.CreateEntry("vlc-" + version + "/" + p.Key).Open()); writer.Write(p.Value); } }
        internal VlcZipBinary? Binary(string path)
        {
            string text = File.ReadAllText(path); if (!text.StartsWith("VLC:")) return null; var parts = text.Split(':'); bool payload = path.Contains("new-package", StringComparison.Ordinal);
            return new(payload ? PayloadVersion ?? parts[1] : parts[1], payload ? PayloadMachine ?? (ushort)0x8664 : (ushort)0x8664);
        }
        internal ProgramInstallation Proof => new(Binary(Exe)!.Version, "Portable", Exe, false, VlcPortableUpdates.Variant);
        internal VlcPortableUpdates Updater() => new(Read, Download, Backups, Binary, phase => Checkpoint?.Invoke(phase));
        internal async Task<PortableUpdate> Offer() => await Updater().ResolveAsync(Proof, default) ?? throw new Exception("fixture offer missing");
        internal static string Index(string version, long size) => "<a href=\"vlc-" + version + "-win64.zip\">VLC ZIP</a> 21-Sep-2026 17:05 " + size + "\n";
        internal Task<byte[]> Read(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var match = System.Text.RegularExpressions.Regex.Match(uri.AbsolutePath, @"/(\d+\.\d+\.\d+)/win64/");
            string version = match.Success ? match.Groups[1].Value : _latest; string file = version == "3.0.21" ? OldZip : NewZip;
            string text = uri.AbsolutePath.EndsWith(".sha256") ? (ChangedSource && version == _latest ? new string('a', 64) : ProgramPortableUpdates.Hash(file)) + " *vlc-" + version + "-win64.zip\n"
                : MissingOld && version == "3.0.21" && !uri.AbsolutePath.Contains("/last/") ? "no archive" : Index(version, new FileInfo(file).Length);
            return Task.FromResult(Encoding.UTF8.GetBytes(text));
        }
        internal Task Download(PortableUpdate request, string target, Action<ProgramDownload> progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Downloads++; if (BadDownload) File.WriteAllText(target, "corrupt"); else File.Copy(request.Version == "3.0.21" ? OldZip : NewZip, target); long size = new FileInfo(target).Length; progress(new(0, size)); progress(new(size / 2, size)); progress(new(size, size)); return Task.CompletedTask; }
        internal string AllHashes() => string.Join('\n', Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).Select(p => Path.GetRelativePath(Root, p) + "=" + ProgramPortableUpdates.Hash(p)));
        internal string UserHashes() => string.Join('\n', Directory.GetFiles(Root, "*", SearchOption.AllDirectories).Where(p => !OldFiles.ContainsKey(Path.GetRelativePath(Root, p).Replace('\\', '/')) && !_newFiles.ContainsKey(Path.GetRelativePath(Root, p).Replace('\\', '/'))).Order(StringComparer.OrdinalIgnoreCase).Select(p => Path.GetRelativePath(Root, p) + "=" + ProgramPortableUpdates.Hash(p)));
    }
}
