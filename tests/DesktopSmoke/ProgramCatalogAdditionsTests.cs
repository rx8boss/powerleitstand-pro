using System.Diagnostics;
using System.IO;
using PowerControlProWpf;

internal static class ProgramCatalogAdditionsTests
{
    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output); int checks = 0, installs = 0;
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        var filee = ProgramCatalog.Entries.Single(e => e.Id == "filee");
        var adw = ProgramCatalog.Entries.Single(e => e.Id == "adwcleaner");
        Assert(ProgramCatalog.Entries.Length == 148 && ProgramCatalog.Entries.Select(e => e.Id).Distinct().Count() == 148, "148 distinct catalog entries");
        Assert(filee.PackageId == "KnifeLemon.Filee" && adw.PackageId == "Malwarebytes.AdwCleaner", "official WinGet identities");
        Assert(filee.Categories.Contains("office") && filee.Categories.Contains("tools") && adw.Categories.SequenceEqual(["tools"]), "appropriate catalog categories");
        foreach (var entry in new[] { filee, adw })
        {
            Assert(entry.DescriptionDe.Length > 40 && entry.DescriptionEn.Length > 40 && entry.UseDe?.Length > 40 && entry.UseEn?.Length > 40, "bilingual explanation and purpose: " + entry.Id);
            Assert(ProgramLocalInventory.Matches(entry, entry.Name, entry.Publisher) && !ProgramLocalInventory.Matches(entry, entry.Name + " Helper", entry.Publisher), "publisher registration matches without helpers: " + entry.Id);
        }
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "adwcleaner.exe");
        string? actualVersion = null;
        if (File.Exists(desktop))
        {
            var info = FileVersionInfo.GetVersionInfo(desktop);
            Assert(ProgramLocalInventory.MatchesPortable(adw.Id, info.ProductName, info.CompanyName), "actual desktop AdwCleaner product/publisher metadata");
            var local = new Dictionary<string,ProgramInstallation>();
            ProgramLocalInventory.ReadPortable([adw], local, default, [Path.GetDirectoryName(desktop)!]);
            Assert(local.TryGetValue(adw.Id, out var found) && found.Source == "Portable" && found.Path == desktop && found.Version is not null, "bounded desktop portable detection returns exact file and version");
            actualVersion = local[adw.Id].Version;
        }
        var proof = new ProgramInstallation(actualVersion ?? "8.8.1.639", "Portable", desktop, false);
        var service = new ProgramInstallService(query: (start, _) => Task.FromResult(new WinGetResult(0, "Found [" + (start.ArgumentList.Contains("KnifeLemon.Filee") ? filee.PackageId : adw.PackageId) + "]\nVersion: " + (start.ArgumentList.Contains("KnifeLemon.Filee") ? "2.3.0" : "8.8.1.639") + "\n")),
            install: (_, _) => { installs++; throw new Exception("Real installer forbidden"); }, localReader: (_, _) => Task.FromResult(new Dictionary<string,ProgramInstallation> { [adw.Id] = proof }),
            portableUpdates: new ProgramPortableUpdates(read: (_, _) => Task.FromResult(System.Text.Encoding.UTF8.GetBytes("[]"))));
        await service.InspectAsync(default);
        var adwPackage = await service.ResolveAsync(adw, default);
        Assert(adwPackage?.Version == "8.8.1.639" && !adwPackage.CanInstall && adwPackage.PortableUpdate is null, "manually downloaded AdwCleaner gets online version information without an unverified package conversion");
        var fileePackage = await service.ResolveAsync(filee, default);
        Assert(fileePackage?.Id == "KnifeLemon.Filee" && fileePackage.Version == "2.3.0" && fileePackage.CanInstall, "Filee uses normal verified package resolution");
        Assert(installs == 0, "catalog and version checks do not execute either application");
        Console.WriteLine($"PASS {checks} Filee/AdwCleaner catalog, bilingual metadata, local portable identity and version-source checks; no installer or scan executed"); return 0;
    }
}
