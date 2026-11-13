using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerControlProWpf;

internal static class ProgramManagedPackageTests
{
    internal static async Task<int> Run(string output, bool live)
    {
        int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        if (live)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var inventory = ProgramManagedInventory.Read(ProgramCatalog.Entries, limit.Token);
            var packages = new ProgramManagedPackages(); var found = new List<object>();
            foreach (var pair in inventory.Matches)
            {
                var merged = new Dictionary<string, ProgramInstallation>(); ProgramManagedInventory.Merge(merged, new(new Dictionary<string, IReadOnlyList<ProgramManagedCopy>> { [pair.Key] = pair.Value }, []));
                var entry = ProgramCatalog.Entries.Single(e => e.Id == pair.Key);
                var offers = await packages.ExistingAsync(entry, merged[pair.Key], limit.Token, fresh: true);
                found.Add(new { entry.Id, Offers = offers, Error = packages.Problem(entry.Id) });
                File.WriteAllText(Path.Combine(output, "Live-Quellen-Privat.json"), JsonSerializer.Serialize(found, new JsonSerializerOptions { WriteIndented = true }));
                Assert(offers.All(p => p.Manager == pair.Value[0].Manager && p.Target.Id == pair.Value[0].PackageId), "Live source preserved");
                Assert(offers.Count > 0, "Live official source offer for " + entry.Id + ": " + packages.Problem(entry.Id));
            }
            File.WriteAllText(Path.Combine(output, "Live-Quellen-Privat.json"), JsonSerializer.Serialize(found, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks} live source/manifest checks; no manager or installer executed."); return 0;
        }
        var info = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"));
        var entryFixture = new ProgramEntry("fixture-managed", info.ProductName!, ["tools"], "Fixture.Manager", info.CompanyName!, "Fixture", "Fixture", "https://example.invalid", "", null, null, "1");
        string root = Path.Combine(output, "Scoop"), home = Path.Combine(root, "apps", "scoop", "current"), bucket = Path.Combine(root, "buckets", "main");
        Directory.CreateDirectory(Path.Combine(home, "bin")); Directory.CreateDirectory(Path.Combine(home, ".git")); Directory.CreateDirectory(Path.Combine(root, "shims"));
        Directory.CreateDirectory(Path.Combine(bucket, ".git")); Directory.CreateDirectory(Path.Combine(bucket, "bucket"));
        File.WriteAllText(Path.Combine(home, "bin", "scoop.ps1"), "# fixture only - never execute"); File.WriteAllText(Path.Combine(root, "shims", "scoop.ps1"), "# fixture");
        File.WriteAllText(Path.Combine(home, ".git", "config"), "[remote \"origin\"]\nurl = https://github.com/ScoopInstaller/Scoop\n");
        File.WriteAllText(Path.Combine(bucket, ".git", "config"), "[remote \"origin\"]\nurl = https://github.com/ScoopInstaller/Main\n");
        var managerRoot = new ProgramManagerRoot("Scoop", root, "User");
        string current = Path.Combine(root, "apps", entryFixture.Id, "1.0"); Directory.CreateDirectory(current);
        string exe = Path.Combine(current, "Fixture.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), exe, true);
        string Manifest(string version, string homepage = "https://example.invalid", string? hash = null) => JsonSerializer.Serialize(new { version, homepage, license = "MIT", architecture = new Dictionary<string, object> { ["64bit"] = new { url = "https://example.invalid/app.zip", hash = hash ?? new string('a', 64) } } });
        void Config(bool fresh) => File.WriteAllText(Path.Combine(root, "config.json"), JsonSerializer.Serialize(new { last_update = DateTimeOffset.UtcNow.AddHours(fresh ? 0 : -4).ToString("o") }));
        void Marker(bool hold = false) => File.WriteAllText(Path.Combine(current, "install.json"), JsonSerializer.Serialize(new { bucket = "main", architecture = "64bit", hold }));
        Marker(); Config(true); File.WriteAllText(Path.Combine(current, "manifest.json"), Manifest("1.0"));
        string localManifest = Path.Combine(bucket, "bucket", entryFixture.Id + ".json"); File.WriteAllText(localManifest, Manifest("2.0"));
        byte[] remote = Encoding.UTF8.GetBytes(Manifest("2.0")); int reads = 0, starts = 0, runs = 0; ProcessStartInfo? executed = null;
        ProgramManagedCopy Copy() => ProgramManagedInventory.Read([entryFixture], CancellationToken.None, [managerRoot]).Matches[entryFixture.Id].Single();
        Dictionary<string, ProgramInstallation> Proof()
        { var result = new Dictionary<string, ProgramInstallation>(); ProgramManagedInventory.Merge(result, ProgramManagedInventory.Read([entryFixture], CancellationToken.None, [managerRoot])); return result; }
        var adapter = new ProgramManagedPackages(read: (url, token) => { token.ThrowIfCancellationRequested(); reads++; return Task.FromResult(remote); },
            run: (command, started, line) => { runs++; executed = command; started(); line("Updating 'fixture-managed' (1.0 -> 2.0)"); line("Downloading https://example.invalid/app.zip"); line("  1 MB / 2 MB"); line("Extracting app.zip"); return Task.FromResult(new WinGetResult(0, "fixture complete")); },
            roots: () => [managerRoot], elevated: () => false, git: _ => exe);
        var proof = Proof()[entryFixture.Id];
        var target = adapter.Target(entryFixture, Copy());
        Assert(target.OldVersion == "1.0" && target.File == exe && target.Scope == "User", "Bound registered version, file and scope");
        var package = (await adapter.ExistingAsync(entryFixture, proof, default)).Single();
        Assert(package.Version == "2.0" && package.IsUpdate && !package.NeedsPreparation, "Exact official-bucket offer with fresh matching local manifest");
        Assert(package.Target.OldVersion != proof.Version, "Package version and executable product version intentionally different");
        await adapter.ResolveAsync(entryFixture, target, default); Assert(reads == 1, "Recent metadata reused without another download");
        await adapter.ResolveAsync(entryFixture, target, default, fresh: true); Assert(reads == 2, "Explicit fresh read bypasses cache");
        var command = adapter.Command(package, false);
        Assert(command.CreateNoWindow && !command.UseShellExecute && command.ArgumentList.Contains("-NoProfile") && command.ArgumentList.Contains("update") && command.ArgumentList.Contains(entryFixture.Id), "Hidden exact single-package update");
        Assert(!command.ArgumentList.Contains("--force") && !command.ArgumentList.Contains("--skip-hash-check") && !command.ArgumentList.Contains("-ExecutionPolicy"), "No implicit force, checksum or policy bypass");
        Assert(adapter.Command(package, true).ArgumentList.Contains("--force"), "Force only for explicit reinstall");
        var steps = new List<ProgramStepEvent>(); var progress = new List<ProgramDownload>();
        var result = await adapter.ApplyAsync(entryFixture, package, false, () => starts++, steps.Add, progress.Add, _ => { }, default);
        Assert(result.ExitCode == 0 && starts == 1 && runs == 1 && executed is not null, "Selected adapter executed once through controlled runner");
        Assert(progress.Count == 1 && steps.Any(e => e.Step == ProgramStep.Install && e.State == ProgramStepState.Active), "Measured bytes and selected-app stage forwarded");
        Assert(steps.All(e => e.Step != ProgramStep.Download || e.State != ProgramStepState.Done), "Process exit cannot manufacture hash/download completion");
        Config(false); var stale = await adapter.ResolveAsync(entryFixture, target, default, true); Assert(stale!.NeedsPreparation, "Old Scoop configuration requires explicit preparation");
        bool denied = false;
        try { await adapter.ApplyAsync(entryFixture, package, false, () => starts++, steps.Add, progress.Add, _ => { }, default); } catch (IOException) { denied = true; }
        Assert(denied && runs == 1, "Stale sources block installer before launch"); Config(true);
        File.WriteAllText(localManifest, Manifest("1.9"));
        Assert((await adapter.ResolveAsync(entryFixture, target, default, true))!.NeedsPreparation, "Local/remote mismatch requires preparation");
        File.WriteAllText(localManifest, Manifest("2.0").Replace(",", ",\r\n"));
        Assert(!(await adapter.ResolveAsync(entryFixture, target, default, true))!.NeedsPreparation, "CRLF/formatting difference is not a false source mismatch");
        remote = Encoding.UTF8.GetBytes(Manifest("2.0", "https://unrelated.invalid"));
        Assert(await adapter.ResolveAsync(entryFixture, target, default, true) is null, "Wrong publisher homepage rejected");
        remote = Encoding.UTF8.GetBytes(Manifest("2.0", hash: "skip"));
        Assert(await adapter.ResolveAsync(entryFixture, target, default, true) is null, "Missing checksum rejected"); remote = Encoding.UTF8.GetBytes(Manifest("2.0"));
        Marker(true); Assert((await adapter.ExistingAsync(entryFixture, proof, default, true)).Count == 0, "Held package has no executable offer"); Marker();
        var changed = package with { Target = package.Target with { OldVersion = "0.9" } }; denied = false;
        try { await adapter.ApplyAsync(entryFixture, changed, false, () => starts++, steps.Add, progress.Add, _ => { }, default); } catch (IOException) { denied = true; }
        Assert(denied && runs == 1, "Changed installed package version blocks process");
        remote = Encoding.UTF8.GetBytes(Manifest("3.0")); denied = false;
        try { await adapter.ApplyAsync(entryFixture, package, false, () => starts++, steps.Add, progress.Add, _ => { }, default); } catch (IOException) { denied = true; }
        Assert(denied && runs == 1, "Changed remote target never silently advances installer"); remote = Encoding.UTF8.GetBytes(Manifest("2.0"));
        var registry = proof with { Source = "Registry", Path = Path.Combine(output, "OtherCopy", "Fixture.exe") };
        Assert((await adapter.ExistingAsync(entryFixture, registry, default)).Count == 0, "Different registered copy not borrowed for manager update");
        File.WriteAllText(Path.Combine(bucket, ".git", "config"), "[remote \"origin\"]\nurl = https://github.com/Unrelated/Main\n");
        Assert((await adapter.ExistingAsync(entryFixture, proof, default, true)).Count == 0, "Bucket name alone cannot impersonate official source");
        File.WriteAllText(Path.Combine(bucket, ".git", "config"), "[remote \"origin\"]\nurl = https://github.com/ScoopInstaller/Main\n");
        var newTarget = target with { OldVersion = null, OldDirectory = null, File = null };
        var install = adapter.Command(package with { Target = newTarget }, false);
        Assert(install.ArgumentList.Contains("install") && install.ArgumentList.Contains("main/fixture-managed") && install.ArgumentList.Contains("--no-update-scoop"), "New installation keeps named source without implicit engine refresh");
        var machine = package with { Target = target with { Scope = "Machine" } }; denied = false;
        try { await adapter.ApplyAsync(entryFixture, machine, false, () => starts++, steps.Add, progress.Add, _ => { }, default); } catch (IOException) { denied = true; }
        Assert(denied && runs == 1, "Missing administrator rights block machine changes before launch");
        foreach (string invalid in new[] { "all", "scoop", "chocolatey", "id;Write-Host", "../../id", "--force", "id'" }) Assert(!ProgramManagedPackages.Identifier(invalid), "Unsafe identity rejected: " + invalid);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); denied = false;
        try { await adapter.ResolveAsync(entryFixture, target, canceled.Token); } catch (OperationCanceledException) { denied = true; }
        Assert(denied, "Canceled metadata query cannot return cached executable offer");
        var library = Path.Combine(output, "Chocolatey"); Directory.CreateDirectory(library);
        var chocoTarget = new ProgramManagedTarget("Chocolatey", library, Path.Combine(library, "choco.exe"), "7zip.install", "Machine", "https://community.chocolatey.org/api/v2", "native", "1.0", Path.Combine(library, "lib", "7zip.install"), "fixture", null);
        var chocoPackage = new ProgramManagedPackage(chocoTarget, "2.0", chocoTarget.Source, "sha512", null, false, "https://7-zip.org/license.txt", "runtime");
        var chocoCommand = adapter.Command(chocoPackage, false);
        Assert(chocoCommand.ArgumentList.Contains("upgrade") && chocoCommand.ArgumentList.Contains("--skip-if-not-installed") && chocoCommand.ArgumentList.Contains("--version=2.0"), "Chocolatey upgrade cannot become implicit install and pins chosen version");
        Assert(chocoCommand.ArgumentList.Contains("--source=https://community.chocolatey.org/api/v2") && !chocoCommand.ArgumentList.Contains("--force"), "Chocolatey exact source without force");
        string Xml(string id = "7zip.install", string version = "2.0", string website = "https://www.7-zip.org", string? hash = null) => $"<entry xmlns='http://www.w3.org/2005/Atom' xmlns:m='http://schemas.microsoft.com/ado/2007/08/dataservices/metadata' xmlns:d='http://schemas.microsoft.com/ado/2007/08/dataservices'><m:properties><d:Id>{id}</d:Id><d:Version>{version}</d:Version><d:ProjectUrl>{website}</d:ProjectUrl><d:PackageHashAlgorithm>SHA512</d:PackageHashAlgorithm><d:PackageHash>{hash ?? Convert.ToBase64String(new byte[64])}</d:PackageHash><d:LicenseUrl>https://7-zip.org/license.txt</d:LicenseUrl><d:Dependencies>runtime</d:Dependencies></m:properties></entry>";
        var seven = ProgramCatalog.Entries.Single(e => e.Id == "7-zip");
        Assert(ProgramManagedPackages.ParseChoco(Encoding.UTF8.GetBytes(Xml()), seven, "7zip.install") is { Version: "2.0" }, "Chocolatey official feed metadata parsed with exact identity");
        Assert(ProgramManagedPackages.ParseChoco(Encoding.UTF8.GetBytes(Xml(id: "other")), seven, "7zip.install") is null, "Unrelated feed identity rejected");
        Assert(ProgramManagedPackages.ParseChoco(Encoding.UTF8.GetBytes(Xml(website: "https://wrong.invalid")), seven, "7zip.install") is null, "Unrelated feed product rejected");
        Assert(ProgramManagedPackages.ParseChoco(Encoding.UTF8.GetBytes(Xml(hash: "invalid")), seven, "7zip.install") is null, "Unverified archive hash rejected");
        Assert(!ProgramInstallService.ManagedReinstallSupported(chocoPackage) && ProgramInstallService.ManagedReinstallSupported(chocoPackage with { Target = chocoTarget with { Id = "7zip.portable" } }), "Unknown installer repair not advertised as full reinstallation");
        Directory.CreateDirectory(Path.Combine(library, "bin")); Directory.CreateDirectory(Path.Combine(library, "config"));
        string chocoEngine = Path.Combine(library, "bin", "choco.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), chocoEngine, true);
        File.WriteAllText(Path.Combine(library, "config", "chocolatey.config"), "<chocolatey><sources><source id='chocolatey' value='https://community.chocolatey.org/api/v2/' disabled='false'/></sources></chocolatey>");
        string chocoDir = Path.Combine(library, "lib", entryFixture.Id); Directory.CreateDirectory(Path.Combine(chocoDir, "tools"));
        byte[] archive = Encoding.UTF8.GetBytes("fixture nupkg bytes - no executable scripts");
        string archiveHash = Convert.ToBase64String(SHA512.HashData(archive));
        File.WriteAllBytes(Path.Combine(chocoDir, entryFixture.Id + ".nupkg"), archive);
        void ChocoVersion(string version) => File.WriteAllText(Path.Combine(chocoDir, entryFixture.Id + ".nuspec"), $"<package><metadata><id>{entryFixture.Id}</id><version>{version}</version><projectUrl>{entryFixture.Website}</projectUrl></metadata></package>");
        ChocoVersion("1.0"); File.Copy(exe, Path.Combine(chocoDir, "tools", "Fixture.exe"), true);
        var chocoRoot = new ProgramManagerRoot("Chocolatey", library, "Machine");
        var chocoSnapshot = ProgramManagedInventory.Read([entryFixture], default, [chocoRoot]); var chocoCopy = chocoSnapshot.Matches[entryFixture.Id].Single();
        bool pinned = false; int chocoRuns = 0;
        var chocoAdapter = new ProgramManagedPackages(read: (url, _) => Task.FromResult(Encoding.UTF8.GetBytes(Xml(id: entryFixture.Id,
            version: url.AbsoluteUri.Contains("Version=", StringComparison.Ordinal) ? "1.0" : "2.0", website: entryFixture.Website, hash: archiveHash))),
            roots: () => [chocoRoot], elevated: () => true,
            query: (_, _) => Task.FromResult(new WinGetResult(0, pinned ? entryFixture.Id + "|1.0" : "")),
            engine: _ => chocoEngine,
            run: (start, began, line) => { chocoRuns++; began(); line(entryFixture.Id + " v2.0 [Approved]"); line("Installing " + entryFixture.Id + "..."); ChocoVersion("2.0"); return Task.FromResult(new WinGetResult(3010, "fixture reboot required")); });
        var chocoProof = new Dictionary<string, ProgramInstallation>(); ProgramManagedInventory.Merge(chocoProof, chocoSnapshot);
        var chocoOffer = (await chocoAdapter.ExistingAsync(entryFixture, chocoProof[entryFixture.Id], default, true)).Single();
        Assert(chocoOffer.IsUpdate && chocoOffer.Target.Scope == "Machine", "Chocolatey origin archive hash and current feed target verified together");
        pinned = true; Assert((await chocoAdapter.ExistingAsync(entryFixture, chocoProof[entryFixture.Id], default, true)).Count == 0, "Pinned Chocolatey package not offered"); pinned = false;
        File.AppendAllText(Path.Combine(chocoDir, entryFixture.Id + ".nupkg"), "changed");
        Assert((await chocoAdapter.ExistingAsync(entryFixture, chocoProof[entryFixture.Id], default, true)).Count == 0, "Unmatched installed nupkg cannot borrow official source identity");
        File.WriteAllBytes(Path.Combine(chocoDir, entryFixture.Id + ".nupkg"), archive);
        var chocoResult = await chocoAdapter.ApplyAsync(entryFixture, chocoOffer, false, () => { }, _ => { }, _ => { }, _ => { }, default);
        Assert(chocoResult.ExitCode == 3010 && chocoRuns == 1, "Chocolatey reboot-required outcome preserved, one controlled process");
        var changedChoco = ProgramManagedInventory.Read([entryFixture], default, [chocoRoot]);
        Assert(changedChoco.Matches[entryFixture.Id].Single().PackageVersion == "2.0", "Post-operation package registration is re-read independently");
        Config(false); var prepareOffer = (await adapter.ExistingAsync(entryFixture, proof, default, true)).Single();
        ProcessStartInfo? preparationCommand = null;
        var preparer = new ProgramManagedPackages(read: (_, _) => Task.FromResult(remote), roots: () => [managerRoot], git: _ => exe,
            run: (start, began, _) => { preparationCommand = start; began(); Config(true); File.WriteAllText(localManifest, Manifest("2.0")); return Task.FromResult(new WinGetResult(0, "fixture source refresh")); });
        await preparer.PrepareAsync(prepareOffer, () => { }, _ => { });
        Assert(preparationCommand?.ArgumentList[^1] == "update" && !preparationCommand.ArgumentList.Contains(entryFixture.Id) && ProgramManagedPackages.ScoopFresh(root), "Preparation is a distinct engine/source operation, not a silent application upgrade");
        var sourceSteps = new List<ProgramStepEvent>();
        var reporter = new ProgramManagedSteps(entryFixture.Id, sourceSteps.Add, _ => { });
        reporter.Line("Installing 'dependency' (1.0)"); reporter.Line("Checking hash of dependency.zip ... ok.");
        Assert(sourceSteps.Count == 0, "Dependency hash cannot mark chosen app downloaded");
        reporter.Line("Installing '" + entryFixture.Id + "' (2.0)"); reporter.Line("Checking hash of app.zip ... ok."); reporter.Line("Extracting app.zip"); reporter.End(0);
        Assert(sourceSteps.Any(s => s.Step == ProgramStep.Download && s.State == ProgramStepState.Done) && sourceSteps.All(s => s.Step != ProgramStep.Finish), "Actual selected-app hash can mark download done; finish still requires inventory");
        Assert(!ProgramManagedPackages.ResultVerified(package, new(true, new Dictionary<string, string?> { [entryFixture.Id] = "2.0" }), entryFixture.Id), "Version string alone does not verify managed result");
        var selectedCopy = Copy() with { PackageVersion = "2.0" };
        var finalProof = proof with { ManagedCopies = [selectedCopy] };
        var finalInventory = new ProgramInventory(true, new Dictionary<string, string?> { [entryFixture.Id] = finalProof.Version }, Evidence: new Dictionary<string, ProgramInstallation> { [entryFixture.Id] = finalProof });
        Assert(ProgramManagedPackages.ResultVerified(package, finalInventory, entryFixture.Id), "Confirmed package registration plus matching existing main file verifies result");
        Assert(!ProgramManagedPackages.ResultVerified(package, finalInventory with { Evidence = new Dictionary<string, ProgramInstallation> { [entryFixture.Id] = finalProof with { ManagedCopies = [selectedCopy with { Scope = "Machine" }] } } }, entryFixture.Id), "Other scope cannot satisfy result");
        Assert((await adapter.ExistingAsync(entryFixture, proof with { Source = "WinGet" }, default, true)).Count == 0,
            "A separate WinGet installation cannot silently become a Scoop operation");
        string settings = Path.Combine(root, "config.json");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { last_update = DateTimeOffset.UtcNow.ToString("o"), scoop_repo = "https://github.com/other/Scoop" }));
        Assert((await adapter.ExistingAsync(entryFixture, proof, default, true)).Count == 0 && runs == 1,
            "Changed Scoop engine repository blocks offer without starting process");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { last_update = DateTimeOffset.UtcNow.ToString("o"), scoop_branch = "develop" }));
        Assert((await adapter.ExistingAsync(entryFixture, proof, default, true)).Count == 0 && runs == 1,
            "Unconfirmed Scoop branch cannot use the official-source offer");
        File.WriteAllText(settings, JsonSerializer.Serialize(new { last_update = DateTimeOffset.UtcNow.ToString("o") }), new UTF8Encoding(true));
        Assert((await adapter.ExistingAsync(entryFixture, proof, default, true)).Single().NeedsPreparation == false,
            "Windows UTF8-BOM configuration keeps a valid fresh Scoop source usable");
        File.WriteAllText(Path.Combine(library, "config", "chocolatey.config"), "<chocolatey><sources><source id='chocolatey' value='https://community.chocolatey.org/api/v2/'/><source id='other' value='https://other.invalid/api/v2'/></sources></chocolatey>");
        var refreshedChocoProof = new Dictionary<string, ProgramInstallation>(); ProgramManagedInventory.Merge(refreshedChocoProof, changedChoco);
        Assert((await chocoAdapter.ExistingAsync(entryFixture, refreshedChocoProof[entryFixture.Id], default, true)).Count == 0 && chocoRuns == 1,
            "Multiple enabled Chocolatey sources block automatic source selection without a second process");
        string? inheritedPath = Environment.GetEnvironmentVariable("PATH");
        Assert(executed!.Environment["PATH"]!.StartsWith(Path.GetDirectoryName(exe) + ";", StringComparison.OrdinalIgnoreCase), "Package command receives confirmed Git directory first");
        Assert(preparationCommand!.Environment["PATH"]!.StartsWith(Path.GetDirectoryName(exe) + ";", StringComparison.OrdinalIgnoreCase), "Source preparation uses the same Git environment as package commands");
        int prerequisiteRuns = 0;
        var missingGit = new ProgramManagedPackages(roots: () => [managerRoot], git: _ => null,
            run: (_, _, _) => { prerequisiteRuns++; throw new Exception("Must not start without Git"); });
        string? failure = null;
        try { await missingGit.PrepareAsync(prepareOffer with { NeedsPreparation = true }, () => { }, _ => { }); } catch (IOException error) { failure = error.Message; }
        Assert(failure == ProgramScoopGit.Missing && prerequisiteRuns == 0, "Missing Git blocks source refresh before process start with actionable cause");
        var unusableGit = new ProgramManagedPackages(roots: () => [managerRoot], git: _ => exe,
            run: (_, _, _) => Task.FromResult(new WinGetResult(1, "Scoop uses Git to update itself. Run 'scoop install git' and try again.")));
        failure = null;
        try { await unusableGit.PrepareAsync(prepareOffer with { NeedsPreparation = true }, () => { }, _ => { }); } catch (IOException error) { failure = error.Message; }
        Assert(failure == ProgramScoopGit.Unusable, "Git failure after launch retains a specific actionable preparation error");
        Assert(Environment.GetEnvironmentVariable("PATH") == inheritedPath, "Source and package commands never modify parent PATH");
        Console.WriteLine($"PASS {checks} managed adapter/source/command/verification checks; controlled runner only, no actual package command executed."); return 0;
    }
}
