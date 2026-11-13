using System.IO;
using System.Security.Cryptography;
using PowerControlProWpf;

internal static class ProgramExpansionTests
{
    internal static int LiveGpu(string output) => Task.Run(async () =>
    {
        string source = Path.Combine(Path.GetDirectoryName(output)!, "Quellen", "GPU-Z.2.71.0.exe");
        string fixture = Path.Combine(output, "GPU-Z.exe"); File.Copy(source, fixture, true);
        var reader = new ProgramPortableUpdates(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var recipe = await reader.ResolveAsync("gpu-z", new("2.71.0", "Portable", fixture, false), timeout.Token) ?? throw new Exception("Live publisher recipe not verified");
        var progress = new List<ProgramDownload>(); string download = Path.Combine(output, "manufacturer-download");
        await reader.DownloadAsync(recipe, download, progress.Add, timeout.Token);
        if (!string.Equals(ProgramPortableUpdates.Hash(download), recipe.Sha256, StringComparison.OrdinalIgnoreCase) || progress[^1].Percent != 100
            || !ProgramPortableUpdates.SameVersion(ProgramPortableUpdates.FileVersion("gpu-z", download) ?? "", recipe.Version)) throw new Exception("Live GPU-Z download verification failed");
        Console.WriteLine("PASS real in-app GPU-Z manufacturer server selection, download, checksum, product version and measured progress. No EXE executed or installed."); return 0;
    }).GetAwaiter().GetResult();
    internal static int Run(string output)
    {
        int count = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); count++; }
        string root = Path.GetDirectoryName(output)!, sources = Path.Combine(root, "Quellen");
        var steps = new List<ProgramStepEvent>(); var data = new List<ProgramDownload>();
        var tracker = new ProgramApiSteps(steps.Add, data.Add);
        tracker.Report(new(ProgramApiPhase.Queued, 0, 0)); Assert(steps.Count == 0, "queued is not a completed stage");
        tracker.Report(new(ProgramApiPhase.Downloading, 50, 100)); tracker.Report(new(ProgramApiPhase.Downloading, 60, 100));
        Assert(steps.Count == 1 && steps[0] == new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Active) && data[^1].Percent == 60, "structured byte counters update without repeated active milestones");
        tracker.Report(new(ProgramApiPhase.Installing, 100, 100));
        Assert(steps[1] == new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Done) && steps[2] == new ProgramStepEvent(ProgramStep.Install, ProgramStepState.Active), "API installing confirms artifact acceptance");
        tracker.Report(new(ProgramApiPhase.Downloading, 10, 100)); Assert(data.Count == 2, "late download cannot replace installing display");
        tracker.Report(new(ProgramApiPhase.Finished, 100, 100)); Assert(steps.Count == 3, "finished phase alone is not success");
        tracker.Complete(false); Assert(!steps.Contains(new(ProgramStep.Install, ProgramStepState.Done)), "failed result has no green installation tick");
        tracker.Report(new(ProgramApiPhase.Downloading, 99, 100)); Assert(data.Count == 2, "callbacks after result ignored");
        steps.Clear(); tracker = new(steps.Add, data.Add); tracker.Report(new(ProgramApiPhase.Installing, 0, 0)); tracker.Complete(true);
        Assert(steps[0] == new ProgramStepEvent(ProgramStep.Download, ProgramStepState.Unknown) && steps[^1] == new ProgramStepEvent(ProgramStep.Install, ProgramStepState.Done), "unobserved download is unknown rather than a guessed cache hit");
        steps.Clear(); data.Clear(); tracker = new(steps.Add, data.Add); tracker.Report(new(ProgramApiPhase.Downloading, 120, 0));
        Assert(data.Single().Percent is null, "API absent total stays indeterminate"); tracker.Complete(true);
        int api = 0, cli = 0;
        int code = Task.Run(() => ProgramWinGetApi.PreferAsync(() => Task.FromResult<Func<Task<int>>>(() => { api++; return Task.FromResult(0); }), () => { cli++; return Task.FromResult(0); }, _ => { })).GetAwaiter().GetResult();
        Assert(api == 1 && cli == 0 && code == 0, "API executes once without CLI");
        code = Task.Run(() => ProgramWinGetApi.PreferAsync(() => Task.FromException<Func<Task<int>>>(new ProgramApiUnavailableException("fixture activation failure")), () => { cli++; return Task.FromResult(7); }, _ => { })).GetAwaiter().GetResult();
        Assert(api == 1 && cli == 1 && code == 7, "unavailable before start uses CLI once");
        bool failed = false;
        try { Task.Run(() => ProgramWinGetApi.PreferAsync(() => Task.FromResult<Func<Task<int>>>(() => { api++; throw new ProgramApiUnavailableException("fixture after start"); }), () => { cli++; return Task.FromResult(0); }, _ => { })).GetAwaiter().GetResult(); } catch (ProgramApiUnavailableException) { failed = true; }
        Assert(failed && api == 2 && cli == 1, "no duplicate installer after an API call has started");
        failed = false;
        try { Task.Run(() => ProgramWinGetApi.PreferAsync(() => Task.FromException<Func<Task<int>>>(new IOException("version changed")), () => { cli++; return Task.FromResult(0); }, _ => { })).GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
        Assert(failed && cli == 1, "changed identity/version is not a fallback reason");
        Assert(ProgramWinGetApi.ResultCode(false, false, 0) != 0 && ProgramWinGetApi.ResultCode(false, true, null) != 0, "failed API status with missing/zero HRESULT cannot appear as success");
        Assert(ProgramWinGetApi.ResultCode(true, true, null) == 3010 && ProgramWinGetApi.ResultCode(true, false, null) == 0, "API result preserves restart requirement");
        string group = Path.Combine(output, "Group"); Directory.CreateDirectory(group);
        string oldA = Path.Combine(group, "a.exe"), oldB = Path.Combine(group, "b.exe"), newA = Path.Combine(group, "new-a.exe"), newB = Path.Combine(group, "new-b.exe");
        File.WriteAllText(oldA, "old A"); File.WriteAllText(oldB, "old B"); File.WriteAllText(newA, "new A"); File.WriteAllText(newB, "new B"); File.WriteAllText(Path.Combine(group, "settings.ini"), "keep");
        PortableUpdate Recipe(string path) => new("autoruns", path, "1.0", "2.0", "https://download.sysinternals.com/files/Autoruns.zip", new string('a', 64), null, "a.exe", ProgramPortableUpdates.Hash(path));
        string? Version(string path) => File.ReadAllText(path).StartsWith("old") ? "1.0" : "2.0";
        var a = Recipe(oldA); var b = Recipe(oldB); var files = new[] { (a, newA), (b, newB) };
        try { ProgramPortableUpdates.ReplaceGroup(files, Path.Combine(group, "Backups"), Version, n => { if (n == 1) throw new IOException("second component failed"); }); } catch (IOException) { failed = true; }
        Assert(File.ReadAllText(oldA) == "old A" && File.ReadAllText(oldB) == "old B", "second component failure rolls back previous component");
        Assert(File.ReadAllText(Path.Combine(group, "settings.ini")) == "keep" && !Directory.GetFiles(group, ".PowerLeitstand-*").Any(), "group rollback leaves configuration and no side files");
        ProgramPortableUpdates.ReplaceGroup(files, Path.Combine(group, "Backups"), Version);
        Assert(File.ReadAllText(oldA) == "new A" && File.ReadAllText(oldB) == "new B", "both components updated as one verified group");
        var lightshot = ProgramCatalog.Entries.Single(x => x.Id == "lightshot");
        Assert(lightshot.PackageId == "Skillbrains.Lightshot" && lightshot.Categories.Contains("tools"), "Lightshot exact official package in Tools");
        Assert(lightshot.PreviewUrl is null && lightshot.Description(false).Contains("Screenshots") && lightshot.Description(true).Contains("Capture"), "bilingual descriptions and no preview screenshots");
        Assert(ProgramLocalInventory.Matches(lightshot, "Lightshot 5.5.0.7", "Skillbrains") && !ProgramLocalInventory.Matches(lightshot, "Lightshot Toolbar", "Other Publisher"), "Lightshot exact registry identity");
        var icon = ProgramAssets.Decode(File.ReadAllBytes(Path.Combine(sources, "lightshot.ico")), 32);
        Assert(icon.PixelWidth > 0 && icon.PixelHeight > 0, "official Lightshot icon decodes");
        var sourceReader = new ProgramPortableUpdates((uri, _) => Task.FromResult(File.ReadAllBytes(Path.Combine(sources,
            uri.Host == "learn.microsoft.com" ? "autoruns.html" : uri.AbsolutePath.Contains("Autoruns/") ? "autoruns.yaml" : uri.Host == "www.cpuid.com" ? "cpuz.html" : uri.Host == "download.cpuid.com" ? "cpuz-checksum.txt" : uri.Host == "www.techpowerup.com" ? "gpuz.html" : "gpuz.yaml"))));
        string autoruns = Path.Combine(output, "Autoruns"); Directory.CreateDirectory(autoruns);
        foreach (string file in Directory.GetFiles(Path.Combine(sources, "Autoruns-Dateien"), "*.exe")) File.Copy(file, Path.Combine(autoruns, Path.GetFileName(file)), true);
        string autorunsExe = Path.Combine(autoruns, "Autoruns64.exe");
        var autorunsRecipe = Task.Run(() => sourceReader.ResolveAsync("autoruns", new("14.3", "Portable", autorunsExe, false), CancellationToken.None)).GetAwaiter().GetResult();
        Assert(autorunsRecipe is { Parts.Count: 6, Version: "14.30" }, "official Microsoft signed archive maps all six existing Autoruns components");
        Assert(ProgramVersions.IsNewer("14.11", autorunsRecipe!.Version) && ProgramPortableUpdates.FileVersion("autoruns", autorunsExe) == "14.30", "Sysinternals minor 30 is newer than minor 11");
        var autorunsFresh = Task.Run(() => sourceReader.ResolveAsync("autoruns", new("14.3", "Portable", autorunsExe, false), CancellationToken.None, fresh: true)).GetAwaiter().GetResult();
        Assert(ProgramPortableUpdates.Equivalent(autorunsRecipe, autorunsFresh!), "fresh multi-file recipe uses structural equality");
        string cpu = Path.Combine(output, "CPUZ"); Directory.CreateDirectory(cpu);
        foreach (string file in Directory.GetFiles(Path.Combine(sources, "CPUZ-Dateien"), "*.exe")) File.Copy(file, Path.Combine(cpu, Path.GetFileName(file)), true);
        string cpuExe = Path.Combine(cpu, "cpuz_x64.exe");
        var cpuRecipe = Task.Run(() => sourceReader.ResolveAsync("cpu-z", new("3.01", "Portable", cpuExe, false), CancellationToken.None)).GetAwaiter().GetResult();
        Assert(cpuRecipe is { Parts.Count: 2, Version: "3.01" } && ProgramPortableUpdates.FileVersion("cpu-z", cpuExe) == "3.01", "CPU-Z decimal version and x86/x64 pair read correctly");
        string zipHash = ProgramPortableUpdates.Hash(Path.Combine(sources, "cpuz.zip"));
        Assert(ProgramPortableUpdates.CpuArtifactMatches(File.ReadAllBytes(Path.Combine(sources, "cpuz.zip")), zipHash, cpuRecipe!.Parts!), "original CPU-Z distribution verified by archive hash and actual inner file hashes");
        Assert(!ProgramPortableUpdates.CpuArtifactMatches(File.ReadAllBytes(Path.Combine(sources, "cpuz.zip")), zipHash, [cpuRecipe.Parts![0] with { OriginalHash = new string('0', 64) }]), "themed or modified CPU-Z file cannot change variant silently");
        var actualChecksum = ProgramPortableUpdates.CpuChecksum(File.ReadAllText(Path.Combine(sources, "cpuz-checksum.txt")), "3.01");
        Assert(string.Equals(actualChecksum, zipHash, StringComparison.OrdinalIgnoreCase), "publisher CPU-Z archive checksum matches real download");
        string gpuExe = Path.Combine(output, "GPU-Z.exe"); File.Copy(Path.Combine(sources, "GPU-Z.2.71.0.exe"), gpuExe, true);
        var gpuRecipe = Task.Run(() => sourceReader.ResolveAsync("gpu-z", new("2.71.0", "Portable", gpuExe, false), CancellationToken.None)).GetAwaiter().GetResult();
        Assert(gpuRecipe?.Version == "2.71.0" && ProgramPortableUpdates.FileVersion("gpu-z", gpuExe) == "2.71.0", "GPU-Z actual standard file matches manufacturer and manifest");
        Assert(ProgramPortableUpdates.GpuFileId(File.ReadAllText(Path.Combine(sources, "gpuz.html")), "2.71.0", gpuRecipe!.Sha256) == "3257", "GPU-Z official current server selection selects standard rather than ROG");
        Assert(ProgramPortableUpdates.GpuFileId(File.ReadAllText(Path.Combine(sources, "gpuz.html")), "2.71.0", new string('0', 64)) is null, "changed publisher checksum stops server selection");
        Console.WriteLine("PASS " + count + " expanded profiles / structured API / group rollback / Lightshot checks. No installer or publisher EXE executed.");
        return 0;
    }
}
