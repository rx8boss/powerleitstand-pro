using System.Diagnostics;
using System.IO;
using System.Reflection;
using PowerControlProWpf;

internal static class ProgramVlcReinstallTests
{
    internal static int Local(string output)
    {
        Directory.CreateDirectory(output);var copies=ProgramVlcReinstallation.Read();
        File.WriteAllText(Path.Combine(output,"VLC-Kopien.json"),System.Text.Json.JsonSerializer.Serialize(copies,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        foreach(var copy in copies)Console.WriteLine(copy.Label+" · "+copy.Version);
        Console.WriteLine("PASS local read-only VLC registration and binary metadata probe; no installation, network request or VLC execution");return 0;
    }
    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output); int checks = 0; void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        string root32 = Path.Combine(output, "Program32", "VLC"), root64 = Path.Combine(output, "Program64", "VLC");
        foreach (string root in new[] { root32, root64 }) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "vlc.exe"), "Test fixture only; never executed."); }
        var x86 = new VlcRegisteredCopy(Path.Combine(root32, "vlc.exe"), "3.0.24", "x86", "nullsoft", "machine", "VLC media player");
        var x64 = new VlcRegisteredCopy(Path.Combine(root64, "vlc.exe"), "3.0.24", "x64", "wix", "machine", "{585C109D-9729-4B98-A2B8-EACD8885D115}");
        var proof = new ProgramInstallation("3.0.24", "Registry", x86.Executable);
        Assert(ProgramVlcReinstallation.Select(proof, [x64, x86]) == x86, "exact chosen root wins over another architecture/installer at same version");
        Assert(ProgramVlcReinstallation.Select(proof with { Path = root32 }, [x64, x86]) == x86, "directory and executable resolve to the same selected copy");
        Assert(ProgramVlcReinstallation.Select(proof, [x86, x86 with { InstallerType = "wix" }]) is null, "two installer identities at selected root are ambiguous");
        Assert(ProgramVlcReinstallation.Select(proof with { Version = "3.0.23" }, [x86]) is null, "changed selected copy version is rejected");
        Assert(ProgramVlcReinstallation.Version("3.0.24.0") == "3.0.24" && ProgramVlcReinstallation.Version("3.0.24.1") == "3.0.24.1", "only insignificant trailing zero normalized");
        Assert(ProgramInstallService.VlcPublisherVersion("<a href=\"vlc-3.0.24-win64.exe\">EXE</a><a href=\"vlc-3.0.24-win64.msi\">MSI</a>", "win64") == "3.0.24", "official installer listing supplies version without a ZIP download");
        Assert(ProgramInstallService.VlcPublisherVersion("<a href=\"vlc-3.0.24-win64.zip\">ZIP</a>", "win64") is null, "ZIP listing is not the normal installer version source");
        Assert(ProgramInstallService.VlcPublisherVersion("<a href=\"vlc-3.0.24-win32.exe\">EXE</a>", "win64") is null, "wrong architecture cannot supply version information");
        Assert(ProgramInstallService.VlcPublisherVersion("<a href=\"vlc-3.0.24-win64.exe\">EXE</a><a href=\"vlc-3.0.25-win64.msi\">MSI</a>", "win64") is null, "inconsistent manufacturer versions remain unavailable");
        string yaml = Manifest("3.0.24", [x86, x64]);
        foreach (var copy in new[] { x86, x64 }) Assert(ProgramVlcReinstallation.ParseManifest("3.0.24", copy, yaml)?.Length == 64, "matching installer block confirmed");
        foreach (string bad in new[] { yaml.Replace("PackageIdentifier: VideoLAN.VLC", "PackageIdentifier: Other.VLC"), yaml.Replace("PackageVersion: 3.0.24", "PackageVersion: 3.0.23"),
            yaml.Replace("Scope: machine", "Scope: user"), yaml.Replace("InstallerType: nullsoft", "InstallerType: zip"), yaml.Replace("download.videolan.org", "untrusted.example"),
            yaml.Replace("UpgradeBehavior: install", "UpgradeBehavior: uninstallPrevious"), yaml.Replace(new string('A',64), "bad"), yaml + "\nPackageVersion: 3.0.24\n",
            yaml.Replace("- Architecture: x86", "- Architecture:"), yaml.Replace("  InstallerType: nullsoft", "  InstallerType: nullsoft\n  InstallerType: nullsoft") })
            Assert(ProgramVlcReinstallation.ParseManifest("3.0.24", x86, bad) is null, "wrong or unsupported manifest cannot authorize selected installer");
        Assert(ProgramVlcReinstallation.ParseManifest("3.0.24", x86, Manifest("3.0.24", [x86,x86])) is null, "duplicate matching installers are ambiguous");
        Assert(ProgramVlcReinstallation.ParseManifest("3.0.24", x64, yaml.Replace("APPLICATIONFOLDER=\"<INSTALLPATH>\"", "BAD=\"<INSTALLPATH>\"")) is null, "MSI location switch is verified");
        var entry = ProgramCatalog.Entries.Single(e=>e.Id=="vlc");
        IReadOnlyList<VlcRegisteredCopy> copies = [x86, x64]; bool changedManifest = false, missingManifest = false; int installs = 0; ProcessStartInfo? executed = null;
        string offeredVersion = "3.0.24";
        var service = new ProgramInstallService(query:(start,token)=> { token.ThrowIfCancellationRequested(); return Task.FromResult(new WinGetResult(0,"Found VLC [VideoLAN.VLC]\nVersion: " + offeredVersion + "\n")); },
            install:(start,started)=> { installs++; executed=start; started(); return Task.FromResult(0); }, vlcCopies:()=>copies,
            vlcManifest:(p,c,t)=> { t.ThrowIfCancellationRequested(); return Task.FromResult(missingManifest ? null : ProgramVlcReinstallation.ParseManifest(p.Version!,c,changedManifest ? yaml+"\n# changed" : yaml)); });
        void Set(string name,object value)=>typeof(ProgramInstallService).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,value);
        ((Dictionary<string,string>)typeof(ProgramInstallService).GetField("_installedPackageIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!)["vlc"]="VideoLAN.VLC";
        Set("_lastEvidence",new Dictionary<string,ProgramInstallation>{{"vlc",proof}});Set("_recentExport",new Dictionary<string,string?>{{"VideoLAN.VLC","3.0.24.0"}});
        var inventory=new ProgramInventory(true,new Dictionary<string,string?>{{"vlc","3.0.24"}},Evidence:new Dictionary<string,ProgramInstallation>{{"vlc",proof}});
        var options=await service.ResolveReinstallOptionsAsync(entry,inventory,default);var selected=options.Single();
        Assert(selected.ReinstallProof is { SameVersion:true, VlcManifestHash:not null } && selected.ReinstallProof.Installed.VlcCopy==x86,"offer binds selected x86 EXE and manifest, not preferred x64 MSI");
        await service.ReinstallAsync(entry,selected,inventory,()=>{});
        string Arg(string flag)=>executed!.ArgumentList[executed.ArgumentList.IndexOf(flag)+1];
        Assert(installs==1 && Arg("--architecture")=="x86" && Arg("--installer-type")=="nullsoft" && Arg("--scope")=="machine" && Arg("--location")==root32,"command pins architecture, kind, scope and exact existing destination");
        Assert(executed!.CreateNoWindow && !executed.UseShellExecute && !executed.ArgumentList.Contains("--ignore-security-hash"),"hidden existing runner and hash checks retained");
        Assert(service.ReinstallResultVerified(entry,selected,inventory with { Evidence=new Dictionary<string,ProgramInstallation>{{"vlc",proof with {Path=x64.Executable}}} }),"final verification finds original copy even when preferred inventory row changes");
        copies=[x64];Assert(!service.ReinstallResultVerified(entry,selected,inventory),"another same-version copy cannot confirm missing selected target");
        bool blocked=false;try{await service.ReinstallAsync(entry,selected,inventory,()=>{});}catch(InvalidOperationException){blocked=true;}
        Assert(blocked && installs==1,"removed selected copy blocks installer launch");
        copies=[x86 with {InstallerType="wix"},x64];Assert(!service.ReinstallResultVerified(entry,selected,inventory),"installer kind migration is not accepted");
        copies=[x86 with {Version="3.0.23"},x64];Assert(!service.ReinstallResultVerified(entry,selected,inventory),"older selected version remains unconfirmed");
        copies=[x86,x64];Assert(!service.ReinstallResultVerified(entry,selected,inventory with {IsStale=true}) && !service.ReinstallResultVerified(entry,selected,inventory with {IsPartial=true}),"old/partial inventory cannot prove final completion");
        changedManifest=true;blocked=false;try{await service.ReinstallAsync(entry,selected,inventory,()=>{});}catch(IOException){blocked=true;}
        Assert(blocked && installs==1,"manifest changed after confirmation blocks start without fallback");changedManifest=false;missingManifest=true;
        blocked=false;try{await service.ResolveReinstallOptionsAsync(entry,inventory,default);}catch(IOException){blocked=true;}
        Assert(blocked && installs==1,"missing matching source explains inability without launching a different installer");
        Assert(service.ReinstallFailureReason(entry,selected,inventory,false).Contains(root32) && service.ReinstallFailureReason(entry,selected,inventory,true).Contains("selected VLC copy"),"failed verification identifies exact copy in both languages");
        missingManifest = false;
        ((Dictionary<string,string>)typeof(ProgramInstallService).GetField("_installedPackageIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!).Clear();
        ((HashSet<string>)typeof(ProgramInstallService).GetField("_ambiguousReinstallIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(service)!).Add("vlc");
        Assert(service.ReinstallCapability(entry, inventory).VlcCopy == x86, "website-installed registered copy works without WinGet export correlation; exact path resolves multiple architectures");
        offeredVersion = "3.0.25"; yaml = Manifest(offeredVersion, [x86, x64]);
        var upgrade = await service.ResolveAsync(entry, default) ?? throw new Exception("VLC update metadata missing");
        Assert(upgrade.CanInstall && upgrade.ReinstallProof?.Installed.VlcCopy == x86 && upgrade.Version == offeredVersion, "update offer binds official manifest and selected x86 copy");
        await service.UpgradeAsync(entry, upgrade, () => { });
        Assert(installs == 2 && Arg("--architecture") == "x86" && Arg("--installer-type") == "nullsoft" && Arg("--location") == root32 && Arg("--version") == offeredVersion, "update keeps exact selected architecture, installer, destination and new version");
        copies = [x86 with { Version = offeredVersion }, x64];
        Assert(service.ReinstallResultVerified(entry, upgrade, inventory with { Evidence = new Dictionary<string,ProgramInstallation>{{"vlc", proof with { Path = x64.Executable }}} }), "update completion verifies selected copy even if inventory prefers another architecture");
        copies = [x86, x64]; changedManifest = true; blocked = false;
        try { await service.UpgradeAsync(entry, upgrade, () => { }); } catch (IOException) { blocked = true; }
        Assert(blocked && installs == 2, "changed official update manifest cannot launch any installer");
        changedManifest = false; missingManifest = true;
        Assert((await service.ResolveAsync(entry, default))?.CanInstall == false && installs == 2, "unverified official installer keeps version information but disables automatic update");
        if(File.Exists(Path.Combine(output,"Official-VLC.installer.yaml")))
            foreach(var copy in new[]{x86,x64}) Assert(ProgramVlcReinstallation.ParseManifest("3.0.24",copy,File.ReadAllText(Path.Combine(output,"Official-VLC.installer.yaml"))) is not null,"captured official complete VLC manifest supports pinned variant");
        Console.WriteLine($"PASS {checks} VLC EXE/MSI/architecture/copy/version/manifest/command/final-verification checks; no installer executed");return 0;
    }
    private static string Manifest(string version,IEnumerable<VlcRegisteredCopy> copies)=>"PackageIdentifier: VideoLAN.VLC\nPackageVersion: "+version+"\nInstallers:\n"+string.Join("",copies.Select(c=>
        "- Architecture: "+c.Architecture+"\n  InstallerType: "+c.InstallerType+"\n  Scope: "+c.Scope+"\n  InstallerUrl: https://download.videolan.org/pub/videolan/vlc/"+version+"/"+(c.Architecture=="x86"?"win32":"win64")+"/vlc-"+version+"-"+(c.Architecture=="x86"?"win32":"win64")+(c.InstallerType=="wix"?".msi":".exe")+"\n  InstallerSha256: "+new string('A',64)+"\n  UpgradeBehavior: install\n"+(c.InstallerType=="wix"?"  InstallerSwitches:\n    InstallLocation: APPLICATIONFOLDER=\"<INSTALLPATH>\"\n":"")))+"ManifestType: installer\n";
}
