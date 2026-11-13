using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PowerControlProWpf;

internal static class ProgramDetectionCompletionTests
{
    internal static async Task<int> Run(string output, bool live)
    {
        int checks = 0;
        void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        if (live)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var watch = Stopwatch.StartNew();
            var managed = ProgramManagedInventory.Read(ProgramCatalog.Entries, limit.Token);
            var merged = new Dictionary<string, ProgramInstallation>(); ProgramManagedInventory.Merge(merged, managed);
            var store = await ProgramStoreApplications.ReadAsync(limit.Token);
            Assert(store.Complete, "Live Store enumeration completed: " + store.Error);
            File.WriteAllText(Path.Combine(output, "Quellen-Abgleich-Privat.json"), JsonSerializer.Serialize(new
            { Checked = DateTimeOffset.Now, Managed = managed, Merged = merged, Store = store }, new JsonSerializerOptions { WriteIndented = true }));
            var timingFile = Path.Combine(output, "Listen-Lesezeiten-Privat.txt");
            var listed = ProgramRemovalService.ReadWithLocations(limit.Token, new ProgramPathResolver((name, elapsed) =>
                File.AppendAllText(timingFile, $"{elapsed.TotalMilliseconds:F0} ms\t{name}\n")));
            foreach (string id in new[] { "7-zip", "git" })
            {
                if (!managed.Matches.TryGetValue(id, out var copies)) { Console.WriteLine("SKIP live " + id + ": no current managed registration"); continue; }
                Assert(copies.All(c => c.Location?.Executable is not null), "Live " + id + " confirmed executable in package folder");
                Assert(copies.All(c => c.Location?.FileVersion is not null), "Live " + id + " confirmed file product version");
                Assert(listed.Any(p => p.CatalogId == id && p.Source is "Scoop" or "Chocolatey" && p.Version is not null), "Live " + id + " included in installed-program list");
                if (copies.Count == 1) Assert(merged[id].Version == copies[0].Location!.FileVersion && !merged[id].WinGetCompatible, "Live managed product version kept in its own source");
            }
            Assert(listed.Count(p => p.Source == "Windows-App") == store.Applications.Count, "All enumerated Store families represented in installed list");
            Assert(listed.Where(p => p.Source == "Windows-App" && p.CatalogId is null).All(p => p.Protected), "Unknown Store families cannot inherit another uninstaller");
            File.WriteAllText(Path.Combine(output, "Lokaler-Abgleich-Privat.json"), JsonSerializer.Serialize(new
            { Checked = DateTimeOffset.Now, Milliseconds = watch.Elapsed.TotalMilliseconds, Managed = managed, ManagedMerged = merged,
                Store = store, ListedStore = listed.Where(p => p.Source == "Windows-App"), ListedManaged = listed.Where(p => p.Source is "Scoop" or "Chocolatey"),
                InstallersExecuted = false, RegistryOrPackageFilesWritten = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks} live read-only checks; {store.Applications.Count} Store families, {managed.Matches.Count} managed catalog matches; no installer or uninstaller executed.");
            return 0;
        }
        var resource = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"));
        var entry = new ProgramEntry("fixture-managed", resource.ProductName!, ["tools"], "Fixture.Exact", resource.CompanyName!, "Fixture", "Fixture",
            "https://example.invalid", "", null, null, "1");
        string root = Path.Combine(output, "Scoop"), choco = Path.Combine(output, "Chocolatey");
        Directory.CreateDirectory(Path.Combine(root, "shims")); Directory.CreateDirectory(Path.Combine(root, "apps", "scoop"));
        File.WriteAllText(Path.Combine(root, "shims", "scoop.ps1"), "throw 'must never run'");
        string package = Path.Combine(root, "apps", entry.Id, "current"); Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(new { version = "99.8.7.20261028", homepage = entry.Website }));
        File.WriteAllText(Path.Combine(package, "install.json"), "{}");
        string executable = Path.Combine(package, "Fixture.exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), executable, true);
        var scoopRoot = new ProgramManagerRoot("Scoop", root, "User");
        var snapshot = ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot]);
        var copy = snapshot.Matches[entry.Id].Single();
        Assert(copy.Location?.Executable == executable, "Main executable confirmed from its actual resource identity");
        Assert(copy.Location?.FileVersion == ProgramPathResolver.NumericVersion(resource.ProductVersion), "Actual product version read from file");
        Assert(copy.Location?.FileVersion != copy.PackageVersion, "Wrapper version never substituted for executable version");
        var installed = new Dictionary<string, ProgramInstallation>(); ProgramManagedInventory.Merge(installed, snapshot);
        Assert(installed[entry.Id].Version == copy.Location!.FileVersion && installed[entry.Id].Path == executable, "Single managed copy supplies version and exact file");
        Assert(installed[entry.Id].Source == "Scoop" && !installed[entry.Id].WinGetCompatible, "Confirmed executable does not enable cross-manager WinGet route");
        var registry = new ProgramInstallation("3.2.1", "Registry", Path.Combine(output, "DifferentRegisteredCopy"));
        installed[entry.Id] = registry; ProgramManagedInventory.Merge(installed, snapshot);
        Assert(installed[entry.Id].Version == registry.Version && installed[entry.Id].Path == registry.Path && installed[entry.Id].Source == "Registry", "Independent Registry installation preserved");
        var rows = ProgramManagedInventory.Rows([entry], snapshot);
        Assert(rows.Count == 1 && rows[0].Version == copy.Location.FileVersion && rows[0].PackageVersion == copy.PackageVersion, "List preserves product and package version separately");
        Assert(rows[0].Protected && rows[0].Command is null && rows[0].Source == "Scoop", "Managed registration is not a portable-file delete action");
        bool removeBlocked = false;
        try { await ProgramRemovalService.RemoveAsync(rows[0]); } catch (IOException) { removeBlocked = true; }
        Assert(removeBlocked, "Unconfirmed manager uninstall cannot execute");
        Directory.CreateDirectory(Path.Combine(package, "bin")); File.Copy(executable, Path.Combine(package, "bin", "Other.exe"), true);
        var ambiguous = ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot]);
        Assert(ambiguous.Matches[entry.Id].Single().Location is { Executable: null, Directory: null, Alternatives.Count: 2 }, "Two equal main-file candidates remain ambiguous");
        installed.Clear(); ProgramManagedInventory.Merge(installed, ambiguous);
        Assert(installed[entry.Id].Version is null && installed[entry.Id].Path is null, "Ambiguous files do not fabricate one version/path");
        File.Delete(Path.Combine(package, "bin", "Other.exe"));
        File.Move(executable, Path.Combine(package, "setup.exe"));
        var setup = ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot]);
        Assert(setup.Matches[entry.Id].Single().Location is null, "Installer identity cannot masquerade as installed program");
        File.Move(Path.Combine(package, "setup.exe"), executable);
        File.WriteAllText(executable, "not an executable");
        Assert(ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot]).Matches[entry.Id].Single().Location is null, "Invalid PE rejected despite valid package metadata");
        File.Copy(typeof(ProgramInventory).Assembly.Location, executable, true);
        Assert(ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot]).Matches[entry.Id].Single().Location is null, "Renamed DLL rejected");
        File.Delete(executable);
        var missing = ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot]);
        Assert(missing.Matches[entry.Id].Single().Location is null, "Missing program remains registration-only");
        installed.Clear(); ProgramManagedInventory.Merge(installed, missing);
        Assert(installed[entry.Id].Version is null && installed[entry.Id].Path is null, "No wrapper version or package folder invented as program evidence");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), executable, true);
        Directory.CreateDirectory(Path.Combine(choco, "bin")); File.WriteAllText(Path.Combine(choco, "bin", "choco.exe"), "must never execute");
        string library = Path.Combine(choco, "lib", entry.Id), tools = Path.Combine(library, "tools"); Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(library, entry.Id + ".nupkg"), "fixture");
        File.WriteAllText(Path.Combine(library, entry.Id + ".nuspec"), $"<package><metadata><id>{entry.Id}</id><version>101.2.3.20261028</version><projectUrl>{entry.Website}</projectUrl></metadata></package>");
        File.Copy(executable, Path.Combine(tools, "Fixture.exe"), true);
        var both = ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot, new("Chocolatey", choco, "Machine")]);
        Assert(both.Matches[entry.Id].Count == 2 && both.Matches[entry.Id].All(c => c.Location?.Executable is not null), "Scoop and nested Chocolatey portable files each confirmed");
        installed.Clear(); ProgramManagedInventory.Merge(installed, both);
        Assert(installed[entry.Id].Version is null && installed[entry.Id].Path is null && installed[entry.Id].ManagedCopies!.Count == 2, "Multiple manager copies never select arbitrary first version");
        Assert(ProgramManagedInventory.Rows([entry], both).Select(p => p.Id).Distinct().Count() == 2, "Both source copies remain in installed-program list");
        var filesBefore = Directory.GetFiles(output, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        ProgramManagedInventory.Read([entry], CancellationToken.None, [scoopRoot, new("Chocolatey", choco, "Machine")]);
        Assert(filesBefore.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)) && filesBefore.Count == Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length, "Inventory does not change package files");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool stopped = false;
        try { new ProgramPathResolver().ResolveManaged(entry, copy, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "Package file scan observes cancellation");
        string Json(params object[] values) => JsonSerializer.Serialize(values);
        object Store(string family, string version, bool nonRemovable = false) => new { PackageFamilyName = family, Name = "Store sample", Publisher = "Vendor", Version = version, InstallLocation = output, NonRemovable = nonRemovable };
        var parsed = ProgramStoreApplications.Parse(Json(Store("Vendor.App_abc123", "1.2.0.0"), Store("vendor.app_ABC123", "2.0.0.0"), Store("Vendor.Other_xyz321", "1.0.0.0", true),
            new { PackageFamilyName = "Vendor.Framework_abc123", Name = "Framework", Version = "1.0.0.0", IsFramework = true },
            new { PackageFamilyName = "Vendor.Resource_abc123", Name = "Resource", Version = "1.0.0.0", IsResourcePackage = true },
            new { PackageFamilyName = "Vendor.Optional_abc123", Name = "Optional", Version = "1.0.0.0", IsOptional = true },
            Store("invalid family", "1.0.0.0"), Store("Vendor.BadVersion_abc123", "missing"), new { PackageFamilyName = 123, Name = "bad", Version = true }));
        Assert(parsed.Count == 2, "Store main apps retained; malformed/resource/framework/optional entries excluded");
        Assert(parsed.Single(p => p.Family.Equals("Vendor.App_abc123", StringComparison.OrdinalIgnoreCase)).Version == "2.0.0.0", "Duplicate family chooses current numeric version case-insensitively");
        Assert(parsed.All(p => p.Location == output) && parsed.Single(p => p.Family == "Vendor.Other_xyz321").Protected, "Store location and system protection retained");
        var unmapped = ProgramStoreApplications.Rows(parsed);
        Assert(unmapped.Count == 2 && unmapped.All(p => p.CatalogId is null && p.Protected), "Unmapped Store apps visible without invented catalog identity or uninstaller");
        var known = ProgramStoreApplications.Rows(ProgramStoreApplications.Parse(Json(Store(ProgramVersions.StoreFamilies["chatgpt"], "1.2.3.0")))).Single();
        Assert(known.CatalogId == "chatgpt" && known.Name == "ChatGPT" && !known.Protected, "Exact known family preserves existing catalog uninstall route");
        var unknownPath = ProgramStoreApplications.Parse(Json(new { PackageFamilyName = "Vendor.Path_abc123", Name = "Path", Version = "1.0.0.0", InstallLocation = @"\\server\share" })).Single();
        Assert(unknownPath.Location is null, "Network path is not a program location");
        int calls = 0;
        Task<IReadOnlyList<ProgramStoreApplication>> Success(CancellationToken _) { calls++; return Task.FromResult(parsed); }
        var primary = await ProgramStoreApplications.ReadAsync(CancellationToken.None, Success, _ => throw new Exception("Fallback must not run"));
        Assert(primary.Complete && primary.Applications.Count == 2 && calls == 1, "Primary Store read succeeds without PowerShell");
        var fallback = await ProgramStoreApplications.ReadAsync(CancellationToken.None, _ => Task.FromException<IReadOnlyList<ProgramStoreApplication>>(new IOException("first")), Success);
        Assert(fallback.Complete && fallback.Provider == "Windows PowerShell" && calls == 2, "Independent Store fallback supplies complete result");
        var failed = await ProgramStoreApplications.ReadAsync(CancellationToken.None, _ => Task.FromException<IReadOnlyList<ProgramStoreApplication>>(new IOException("first")),
            _ => Task.FromException<IReadOnlyList<ProgramStoreApplication>>(new IOException("second")));
        Assert(!failed.Complete && failed.Error!.Contains("first") && failed.Error.Contains("second"), "Store failure is not successful absence");
        var empty = await ProgramStoreApplications.ReadAsync(CancellationToken.None, _ => Task.FromResult<IReadOnlyList<ProgramStoreApplication>>([]));
        Assert(empty.Complete && empty.Applications.Count == 0, "Empty successful Store result explicit");
        stopped = false;
        try { await ProgramStoreApplications.ReadAsync(canceled.Token, Success); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "Canceled Store read never returns completed state");
        Assert(ProgramVersions.ParseStoreInstallations(Json(Store("Vendor.App_abc123", "2.0.0.0"))).Count == 0, "General Store listing does not guess catalog mappings");
        Console.WriteLine($"PASS {checks} managed product/path/copy and full Store-list checks; fixtures only, no installer/uninstaller executed."); return 0;
    }
}
