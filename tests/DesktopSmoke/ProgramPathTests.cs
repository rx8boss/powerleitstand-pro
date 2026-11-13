using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using PowerControlProWpf;

internal static class ProgramPathTests
{
    internal static int Run(string output)
    {
        int checks = 0;
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; }
        ProgramEntry Entry(string id) => ProgramCatalog.Entries.Single(e => e.Id == id);
        ProgramPathRegistration Registered(ProgramEntry entry, string? version = "1.2.3") => new(entry.Name, entry.Publisher, version, null, null, null, "fixture", false, RegistryHive.LocalMachine, RegistryView.Registry64);
        string root = Path.Combine(output, "Path fixtures"); Directory.CreateDirectory(root);
        string FilePath(string name, string copy = "A") => Path.Combine(root, copy, name);
        ProgramPathEvidence? Select(string id, ProgramPathFile file, string filename, string? version = "1.2.3", bool anchored = true, Func<string, bool>? signature = null)
        {
            var entry = Entry(id);
            return ProgramPathResolver.Select(Registered(entry, version), entry, [new(FilePath(filename), "Fixture", 0, anchored)], _ => file, trustedEmptyPublisher: signature);
        }
        Assert(Select("extra-electronicarts-eadesktop", new("EA", "Electronic Arts", "13.796.0.6309", "EA app"), "EADesktop.exe", "13.796.0.6309")?.Executable is not null, "EA file product alias accepted");
        Assert(Select("extra-electronicarts-eadesktop", new("EA", "Unrelated", "13.796.0.6309", "EA app"), "EADesktop.exe", "13.796.0.6309") is null, "EA wrong publisher excluded");
        Assert(Select("extra-electronicarts-eadesktop", new("", "", null, ""), "EADesktop.exe", "13.796.0.6309") is null, "Empty compatibility copy excluded");
        Assert(Select("node-js", new("Node.js", "Node.js", "24.22.1", "Node.js"), "node.exe", "24.22.1")?.Executable is not null, "Node file publisher alias accepted");
        Assert(Select("node-js", new("Node.js", "Node.js", "24.21.0", "Node.js"), "node.exe", "24.22.1") is null, "Older Node copy excluded");
        foreach (var alias in new[] {
            ("brave", "brave.exe", "Brave Browser", "Brave Software, Inc."),
            ("opera-gx", "opera.exe", "Opera GX Internet Browser", "Opera Software"),
            ("notepad", "notepad++.exe", "Notepad++", "Don HO don.h@free.fr"),
            ("winrar", "WinRAR.exe", "WinRAR", "Alexander Roshal"),
            ("bcuninstaller", "BCUninstaller.exe", "Bulk Crap Uninstaller", "https://github.com/Klocman/Bulk-Crap-Uninstaller"),
            ("dlss-swapper", "DLSS Swapper.exe", "DLSS Swapper", "DLSS Swapper") })
        {
            Assert(Select(alias.Item1, new(alias.Item3, alias.Item4, "1.2.3", alias.Item3), alias.Item2)?.Executable is not null, "Observed file alias retained: " + alias.Item1);
            Assert(Select(alias.Item1, new(alias.Item3, "Unrelated publisher", "1.2.3", alias.Item3), alias.Item2) is null, "File alias does not waive publisher: " + alias.Item1);
        }
        Assert(Select("opera", new("Opera GX Internet Browser", "Opera Software", "1.2.3", "Opera GX"), "opera.exe") is null, "GX executable never mapped to stable Opera");
        foreach (var special in new[] { ("ubisoft-connect", "UbisoftConnect.exe", "Ubisoft Connect", "Ubisoft"), ("ultraplus-manager", "UltraPlusManager.exe", "UltraPlusManager", "UltraPlusManager") })
        {
            Assert(Select(special.Item1, new(special.Item3, special.Item4, "1.0.0", special.Item3), special.Item2, "1.9.11")?.Executable is not null, "Separate bootstrap file version with exact registered anchor: " + special.Item1);
            Assert(Select(special.Item1, new(special.Item3, special.Item4, "1.0.0", special.Item3), special.Item2, "1.9.11", false) is null, "Bootstrap exception never selects independent copy: " + special.Item1);
        }
        Assert(Select("putty", new("PuTTY suite", "Simon Tatham", "0.85", "SSH client"), "putty.exe", "0.85.0.0")?.Executable is not null, "PuTTY suite alias and trailing registry zeros");
        Assert(Select("steam", new("Steam", "Valve Corporation", "1.0.0.2", "Steam"), "steam.exe", "2.10.91.91")?.Executable is not null, "Steam file and package version differ with registered anchor");
        Assert(Select("steam", new("Steam", "Valve Corporation", "1.0.0.2", "Steam"), "steam.exe", "2.10.91.91", false) is null, "Unanchored Steam copy with different file version excluded");
        var afterburner = new ProgramPathFile("MSIAfterburner", "", "4.6.6.16757", "MSI Afterburner");
        Assert(Select("msi-afterburner", afterburner, "MSIAfterburner.exe", "4.6.6", signature: _ => true)?.Executable is not null, "Afterburner empty resource publisher needs signature and anchor");
        Assert(Select("msi-afterburner", afterburner, "MSIAfterburner.exe", "4.6.6", signature: _ => false) is null, "Untrusted empty publisher excluded");
        Assert(Select("msi-afterburner", afterburner, "MSIAfterburner.exe", "4.6.6", false, _ => true) is null, "Signature alone cannot attach unrelated copy");
        var swarm = new ProgramPathFile("Turtle Beach Swarm II", "", "0.0.0.0", "");
        const string swarmExe = "Turtle Beach Swarm II.exe";
        var swarmPath = Select("turtle-beach-swarm-ii", swarm, swarmExe, null, signature: _ => true);
        Assert(swarmPath is { Executable: not null, FileVersion: null }, "Signed Swarm II is recognized without treating its placeholder as a version");
        Assert(Select("turtle-beach-swarm-ii", swarm, swarmExe, null, signature: _ => false) is null, "Swarm II without publisher metadata needs a trusted signature");
        foreach (string bad in new[] { "uninstall.exe", "unins000.exe", "setup.exe", "EA.Helper.exe", "EADesktopUpdater.exe", "service.exe", "client.dll" })
            Assert(Select("extra-electronicarts-eadesktop", new("EA", "Electronic Arts", "1.2.3", "EA"), bad) is null, "Helper/library excluded: " + bad);
        Assert(Select("node-js", new("Node.js", "Node.js", "1.2.3", "Node.js Helper"), "node.exe") is null, "Helper description excluded");
        foreach (var item in new[] { ("3.6.0", "3.6.2", false), ("3.6.2", "6.0.0", false), ("24.22.1", "24.22.0", false),
            ("0.85.0.0", "0.85", true), ("6.0.0", "6.0.0.5163", true), ("6.0.0", "6.0.1.1", false),
            ("1.2.3", "1.2", false), ("1.2", "1.2.9", true), ("999999999999999999999.2", "999999999999999999999.2", true), ("1.2.3", "", false) })
            Assert(ProgramPathResolver.VersionFits(item.Item1, item.Item2) == item.Item3, "Version relationship: " + item.Item1 + "/" + item.Item2);
        Assert(ProgramPathResolver.NumericVersion("13, 796, 0, 6309") == "13.796.0.6309", "Comma file version normalized");
        Assert(ProgramPathResolver.NumericVersion("Release 0.85") == "0.85", "PuTTY release file version normalized");
        Assert(ProgramPathResolver.NumericVersion("not a version") is null, "Opaque file version remains unknown");
        var node = Entry("node-js"); var reg = Registered(node);
        var file = new ProgramPathFile("Node.js", "Node.js", "1.2.3", "Node.js");
        var ambiguous = ProgramPathResolver.Select(reg, node, [new(FilePath("node.exe"), "A", 3), new(FilePath("node.exe", "B"), "B", 3)], _ => file);
        Assert(ambiguous?.Executable is null && ambiguous?.Alternatives?.Count == 2, "Equal-priority different copies stay ambiguous");
        var exact = ProgramPathResolver.Select(reg, node, [new(FilePath("node.exe"), "Registry", 0, true), new(FilePath("node.exe", "B"), "App Paths", 3)], _ => file);
        Assert(exact?.Executable == FilePath("node.exe"), "Registered copy takes precedence over independent same-product copy");
        var duplicate = ProgramPathResolver.Select(reg, node, [new(FilePath("node.exe"), "Registry", 0), new(FilePath("node.exe"), "MSI", 1)], _ => file);
        Assert(duplicate?.Executable == FilePath("node.exe"), "Same physical path from two hints is not ambiguous");
        var missing = ProgramPathResolver.Select(reg, node, [new(FilePath("node.exe"), "Missing", 0)], _ => null, root);
        Assert(missing?.Directory == root && missing.Executable is null, "Missing file keeps directory without inventing main executable");
        var unknown = new ProgramPathRegistration("Example Tool 1.2.3 (64-bit)", "Example Ltd", "1.2.3", null, null, null, "fixture", false, RegistryHive.CurrentUser, RegistryView.Registry64);
        Assert(ProgramPathResolver.IdentityMatches(unknown, null, FilePath("example.exe"), new("Example Tool", "Example Ltd", "1.2.3", "Example"), true), "Outside-catalog exact product and publisher");
        Assert(!ProgramPathResolver.IdentityMatches(unknown, null, FilePath("example.exe"), new("Other Tool", "Example Ltd", "1.2.3", "Example"), true), "Same publisher different product excluded");
        var uninstallerTool = new ProgramPathRegistration("Bulk Crap Uninstaller", "Klocman software", "1.2.3", null, null, null, "fixture", false, RegistryHive.CurrentUser, RegistryView.Registry64);
        Assert(ProgramPathResolver.IdentityMatches(uninstallerTool, null, FilePath("BCUninstaller.exe"), new("Bulk Crap Uninstaller", "Klocman software", "1.2.3", "Bulk Crap Uninstaller"), true), "Uninstaller application itself is not confused with a setup helper");
        Assert(ProgramPathResolver.ExecutableToken("\"C:\\Program Files\\Example\\tool.exe\",-2", true) == @"C:\Program Files\Example\tool.exe", "Quoted icon index parsed");
        Assert(ProgramPathResolver.ExecutableToken("\"C:\\Program Files\\Example\\uninstall.exe\" /S") == @"C:\Program Files\Example\uninstall.exe", "Only executable token parsed from uninstaller");
        foreach (string bad in new[] { "tool.exe", @"\\server\share\tool.exe", "https://example.invalid/tool.exe", "C:\\tool.exe\r\nrun.exe", "C:\\icon.ico" })
            Assert(ProgramPathResolver.ExecutableToken(bad) is null, "Invalid executable token rejected: " + bad);
        Assert(ProgramPathResolver.ExistingDirectory('"' + root + '"') == root, "Quoted directory normalized");
        Assert(ProgramPathResolver.InDirectory(@"C:\Apps\Example", @"C:\Apps\Example\bin\example.exe"), "Concrete child folder stays within registered installation");
        Assert(!ProgramPathResolver.InDirectory(@"C:\Apps\Example", @"C:\Apps\Example-Other\example.exe"), "Similar sibling folder is a separate installation");
        Assert(!ProgramPathResolver.InDirectory(@"C:\Apps\Example", @"C:\Apps\Example\..\Other\example.exe"), "Parent traversal does not attach another copy");
        if (Directory.Exists(Path.Combine(output, "LocalLink")))
            Assert(ProgramPathResolver.ExistingDirectory(Path.Combine(output, "LocalLink")) == Path.Combine(output, "LocalTarget"), "Existing local directory junction resolves to verified target");
        string invalid = Path.Combine(root, "node.exe"); File.WriteAllText(invalid, "not a PE file");
        var isolated = new ProgramPathResolver().Resolve(reg with { InstallLocation = root }, node, CancellationToken.None);
        Assert(isolated?.Executable != invalid, "Actual malformed PE never selected");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool cancelledRead = false;
        try { new ProgramPathResolver().Resolve(reg, node, cancelled.Token); } catch (OperationCanceledException) { cancelledRead = true; }
        Assert(cancelledRead, "Cancellation stops discovery");
        File.WriteAllText(Path.Combine(output, "Path-Checks.json"), JsonSerializer.Serialize(new { Checks = checks, Passed = true, RealInstallersExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS PATHS: {checks} checks; no real installer or target executed."); return 0;
    }

    internal static int Live(string output)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<object>();
        var resolver = new ProgramPathResolver((name, time) => { timings.Add(new { Name = name, Milliseconds = time.TotalMilliseconds }); File.WriteAllText(Path.Combine(output, "Path-Timings.json"), JsonSerializer.Serialize(timings)); });
        var catalog = ProgramLocalInventory.Read(ProgramCatalog.Entries, limit.Token, pathResolver: resolver);
        Console.WriteLine($"Catalog read: {watch.Elapsed.TotalSeconds:F1}s");
        var installed = ProgramRemovalService.ReadWithLocations(limit.Token, resolver);
        foreach (var store in installed.Where(p => p.Source == "Windows-App" && p.CatalogId is not null))
            if (!catalog.ContainsKey(store.CatalogId!) || catalog[store.CatalogId!].Source == "SelectedFile")
                catalog[store.CatalogId!] = new(store.Version, "Windows-App", store.Location, false);
        var report = new { Checked = DateTimeOffset.Now, Seconds = watch.Elapsed.TotalSeconds,
            CatalogTotal = ProgramCatalog.Entries.Length, CatalogDetected = catalog.Count,
            CatalogMissingPath = catalog.Count(p => string.IsNullOrWhiteSpace(p.Value.Path)),
            CatalogExecutables = catalog.Count(p => File.Exists(p.Value.Path)), CatalogDirectories = catalog.Count(p => Directory.Exists(p.Value.Path)),
            InstalledRows = installed.Count, InstalledMissingPath = installed.Count(p => string.IsNullOrWhiteSpace(p.Location)),
            InstalledWarning = ProgramRemovalService.LastReadWarning, Evidence = catalog, Installed = installed,
            RealInstallersExecuted = false, OnlineVersionsChecked = false };
        File.WriteAllText(Path.Combine(output, "Live-Paths.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"LIVE PATHS: {catalog.Count} catalog detections, {report.CatalogMissingPath} missing paths; {installed.Count} installed rows, {report.InstalledMissingPath} missing paths; {report.Seconds:F1}s.");
        foreach (string id in new[] { "extra-electronicarts-eadesktop", "node-js", "putty", "steam", "msi-afterburner", "curseforge" })
            if (catalog.TryGetValue(id, out var proof) && (string.IsNullOrWhiteSpace(proof.Path) || !File.Exists(proof.Path) && !Directory.Exists(proof.Path)))
                throw new Exception("Known registered product has no usable location: " + id);
        if (catalog.TryGetValue("curseforge", out var curse) && curse.LocationEvidence?.Source == "Registry Overwolf extension" && curse.LocationEvidence.Executable is not null)
            throw new Exception("Overwolf extension must not invent a standalone executable");
        if (curse?.LocationEvidence?.Source == "Registry Overwolf extension" && curse.WinGetCompatible)
            throw new Exception("Overwolf extension must not allow switching to standalone WinGet installer");
        if (catalog.TryGetValue("extra-electronicarts-eadesktop", out var ea) && File.Exists(ea.Path) && System.Diagnostics.FileVersionInfo.GetVersionInfo(ea.Path!).ProductName != "EA")
            throw new Exception("Wrong EA main executable");
        return ProgramRemovalService.LastReadWarning is null ? 0 : 1;
    }

    internal static int Msi(string output)
    {
        var rows = new List<object>();
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (string keyName in uninstall?.GetSubKeyNames() ?? [])
        {
            using var key = uninstall!.OpenSubKey(keyName);
            string name = key?.GetValue("DisplayName") as string ?? "", publisher = key?.GetValue("Publisher") as string ?? "";
            var entry = ProgramCatalog.Entries.FirstOrDefault(e => e.Id is "node-js" or "putty" && ProgramLocalInventory.Matches(e, name, publisher));
            if (entry is null) continue;
            var registration = ProgramPathResolver.Registration(key!, keyName, name, publisher, key?.GetValue("DisplayVersion") as string, RegistryHive.LocalMachine, RegistryView.Registry64);
            var candidates = ProgramMsiPaths.Read(registration, limit.Token);
            string expected = entry.Id == "node-js" ? "node.exe" : "putty.exe";
            if (!candidates.Any(c => Path.GetFileName(c.Path).Equals(expected, StringComparison.OrdinalIgnoreCase) && File.Exists(c.Path)))
                throw new Exception("MSI exact file key-path probe failed: " + entry.Name);
            rows.Add(new { entry.Name, Candidates = candidates });
        }
        File.WriteAllText(Path.Combine(output, "MSI-Live.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS MSI: {rows.Count} actual registered products; exact component file paths, no repair or installer."); return 0;
    }
}
