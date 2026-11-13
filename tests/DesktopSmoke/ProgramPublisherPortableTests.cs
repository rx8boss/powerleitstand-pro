using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramPublisherPortableTests
{
    private static string Manifest(string version, string hash) => $"""
        PackageIdentifier: Malwarebytes.AdwCleaner
        PackageVersion: {version}
        InstallerType: portable
        Installers:
        - Architecture: x86
          InstallerUrl: {ProgramPortableUpdates.AdwCleanerDownload}
          InstallerSha256: {hash}
        """;
    internal static async Task<int> Logic(string output)
    {
        int count=0, installs=0;void Assert(bool ok,string why){if(!ok)throw new Exception(why);count++;}
        var tukui=ProgramCatalog.Entries.Single(e=>e.Id=="tukui-client");
        Assert(tukui.PackageId=="TukuiCommunity.TukuiClient" && tukui.PackageEvidence?.EndsWith("/TukuiCommunity/TukuiClient")==true,"Tukui uses the verified Microsoft catalog identity");
        foreach(var name in new[]{"Tukui", "TukuiClient", "Tukui Client 1.1.2"}) foreach(var publisher in new[]{"Tukui", "Tukui.org", "Tukui Community"})
            Assert(ProgramLocalInventory.Matches(tukui,name,publisher),"Known Tukui registration name and publisher match");
        Assert(!ProgramLocalInventory.Matches(tukui,"Tukui Client Helper","Tukui Community") && !ProgramLocalInventory.Matches(tukui,"Tukui Client","Not Tukui Malware"),"Helpers and substring publisher impersonation are rejected");
        var index=JsonSerializer.Serialize(new[]{new{type="dir",name="8.8.1.639"},new{type="dir",name="8.10.0.1"},new{type="file",name="99.0.0.0"},new{type="dir",name="9.0.0-beta"},new{type="dir",name="../9.0"}});
        Assert(ProgramPortableUpdates.LatestAdwCleanerVersion(index)=="8.10.0.1","Stable numeric directory ordering ignores files, prereleases and paths");
        Assert(ProgramPortableUpdates.LatestAdwCleanerVersion("{}") is null && ProgramPortableUpdates.LatestAdwCleanerVersion("[]") is null,"Missing version arrays produce no invented release");
        Assert(ProgramPortableUpdates.LatestAdwCleanerVersion(new string(' ',128*1024+1)) is null,"Version metadata is bounded");
        var hash=new string('A',64);var manifest=Manifest("8.8.1.639",hash);var path=Path.Combine(output,"adwcleaner.exe");
        var update=ProgramPortableUpdates.ParseAdwCleaner(manifest,path,"8.8.0.1",new string('B',64),"8.8.1.639");
        Assert(update is {Id:"adwcleaner",ZipMember:null} && update.Url==ProgramPortableUpdates.AdwCleanerDownload && update.Sha256==hash,"AdwCleaner is an exact checksum-bound publisher EXE, not an installer conversion");
        foreach(var bad in new[]{manifest.Replace("Malwarebytes.AdwCleaner","Other.AdwCleaner"),manifest.Replace("PackageVersion: 8.8.1.639","PackageVersion: 8.8.0.1"),
            manifest.Replace("InstallerType: portable","InstallerType: msi"),manifest.Replace("Architecture: x86","Architecture: x64"),
            manifest.Replace("adwcleaner.malwarebytes.com","evil.example"),manifest.Replace("channel=release","channel=beta"),manifest.Replace(hash,"invalid"),manifest+"\nInstallerSha256: "+new string('C',64)})
            Assert(ProgramPortableUpdates.ParseAdwCleaner(bad,path,"8.8.0.1",new string('B',64),"8.8.1.639") is null,"Wrong identity, version, distribution, architecture, URL or hash cannot authorize replacement");
        Assert(ProgramPortableUpdates.Eligible("adwcleaner",new("8.8.0.1","Portable",path,false)) && !ProgramPortableUpdates.Eligible("adwcleaner",new("8.8.0.1","Registry",path))
            && ProgramPortableUpdates.Eligible("tukui-client",new("1.1.2","Portable",path,false)),"Manual AdwCleaner and standalone Tukui can be checked; registered copies retain their own routes");
        string source="Registry";int queries=0;
        var service=new ProgramInstallService((start,_)=>{queries++;Assert(start.ArgumentList.Contains(tukui.PackageId!) && start.ArgumentList.Contains("--exact"),"Tukui metadata uses exact MSI package identity");
            return Task.FromResult(new WinGetResult(0,start.ArgumentList[0]=="list"?"Tukui Client TukuiCommunity.TukuiClient 1.1.2 1.1.3 winget":"Found [TukuiCommunity.TukuiClient]\nVersion: 1.1.3\nWindows MSI reference"));},
            (_,_)=>{installs++;throw new Exception("No real installer permitted");},localReader:(entries,_)=>Task.FromResult(new Dictionary<string,ProgramInstallation>{{tukui.Id,new("1.1.2",source,path,source=="Registry")}}));
        var inventory=await service.InspectUpdatesAsync([tukui],default);var package=await service.ResolveAsync(tukui,default);
        Assert(inventory.HasUpdate(tukui.Id) && package is {CanInstall:true,Version:"1.1.3"},"Registered MSI can use the existing selected update flow");
        var command=service.UpgradeCommand(tukui,package!);Assert(command.ArgumentList[0]=="upgrade" && command.ArgumentList.Contains("1.1.3") && !command.ArgumentList.Contains("--all"),"MSI upgrade remains exact-version bound");
        source="Portable";int before=queries;inventory=await service.InspectUpdatesAsync([tukui],default);package=await service.ResolveAsync(tukui,default);
        Assert(!inventory.HasUpdate(tukui.Id) && package is {CanInstall:false,PortableUpdate:null,Version:null} && queries==before,"Unprobed standalone Tukui gets neither an MSI query nor an executable offer");
        Assert(installs==0,"Source discovery never installs or launches either product");
        Assert(UiText.Translate("Die Malwarebytes-Signatur wurde nicht bestätigt.",true)=="Malwarebytes signature was not verified.","New signature failure is translated");
        Console.WriteLine("PASS "+count+" Tukui/AdwCleaner source and variant checks; no installer executed");return 0;
    }

    internal static int Ui(App app,string output)
    {
        int count=0;var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        void Assert(bool ok,string why){if(!ok)throw new Exception(why);count++;}
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));UiSoundService.MuteForTests=true;
        var main=new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Pump(){app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);main.UpdateLayout();}
        void Wait(Task task){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.Elapsed.TotalSeconds<15){Pump();Thread.Sleep(3);}if(!task.IsCompleted)throw new TimeoutException("Publisher UI fixture timed out");task.GetAwaiter().GetResult();Pump();}
        void Save(string name){Pump();var bmp=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(main);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);}
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;view.Service=new InfoService();
            var entry=ProgramCatalog.Entries.Single(e=>e.Id=="tukui-client");var themes=(ComboBox)main.FindName("ThemeBox");
            foreach(var theme in themes.Items.OfType<ComboBoxItem>().Where(x=>x.Tag is string).ToArray())
            {
                themes.SelectedItem=theme;
                foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})foreach(string state in new[]{"standalone","msi","cached-msi"})
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;page.SetLanguage(english);view.SetCategory("gaming");Pump();
                    bool standalone=state=="standalone",cached=state=="cached-msi";
                    var proof=new ProgramInstallation("1.1.2",standalone?"Portable":"Registry",@"I:\Tools\Tukui Client.exe",!standalone&&!cached);
                    typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,new ProgramInventory(true,new Dictionary<string,string?>{{entry.Id,proof.Version}},
                        Updates:standalone?null:new Dictionary<string,string>{{entry.Id,"1.1.3"}},UpdateCheckComplete:true,Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,proof}},IsStale:cached,FromCache:cached));
                    var info=ProgramInfoCard.Open(view,entry,view.Tiles.First(x=>x.Tag?.ToString()==entry.Id),false)!;Wait(info.Loading);
                    var texts=UnifiedDialogAppearance.Children<TextBlock>(info.Frame).ToArray();
                    var installed=texts.Single(t=>t.Name=="ProgramInfoInstalledVersion");var available=texts.Single(t=>t.Name=="ProgramInfoAvailableVersion");
                    Assert(installed.Text=="1.1.2","Local version remains visible for MSI and standalone");
                    Assert(available.Text==(standalone?english?"Unavailable":"Nicht ermittelbar":"1.1.3"),"MSI reference never becomes a standalone version; cached MSI is still MSI");
                    if(standalone)Assert(texts.Any(t=>t.Text.Contains(english?"checks its EXE version and developer signature":"prüft EXE-Version und Entwicklersignatur")),"Standalone variant explains its explicit signed package check in both languages");
                    var update=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
                    Assert(update.Visibility==Visibility.Visible&&(!standalone||((TextBlock)update.Content).Text==(english?"Check again":"Erneut prüfen")),"Standalone receives a targeted check, never an MSI update action");
                    Assert(Math.Abs(info.Frame.ActualWidth-(size==1?420:580))<1&&info.Frame.ActualHeight<=540,"Approved information-card dimensions are unchanged");
                    foreach(var button in UnifiedDialogAppearance.Children<Button>(info.Frame).Where(b=>b.IsVisible))
                    {var point=button.TranslatePoint(new Point(),info.Frame);Assert(point.X>=0&&point.X+button.ActualWidth<=info.Frame.ActualWidth+1,"Visible action remains inside the card");}
                    if(theme.Tag?.ToString()=="Standard"&&!english)Save("Tukui-"+state+"-"+size);info.Close();
                }
            }
            Console.WriteLine("PASS "+count+" Tukui variant UI checks; seven themes, DE/EN, compact/large; no installer executed");return 0;
        }
        finally{main.Close();Pump();}
    }
    private sealed class InfoService:IProgramInstallService
    {
        public Task<ProgramInventory> InspectAsync(CancellationToken token)=>Task.FromResult(new ProgramInventory(true,new Dictionary<string,string?>()));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry,CancellationToken token)=>Task.FromResult<ProgramPackage?>(new(entry.PackageId!,"1.1.3","Verified MSI reference fixture",DateTime.UtcNow));
        public Task<int> InstallAsync(ProgramEntry entry,ProgramPackage package,Action started)=>throw new Exception("No installer permitted");
        public Task<int> UpgradeAsync(ProgramEntry entry,ProgramPackage package,Action started)=>throw new Exception("No installer permitted");
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query,CancellationToken token)=>Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public void OpenWebsite(ProgramEntry entry){}
    }
    internal static async Task<int> Files(string output,string adwFile,string tukuiZip)
    {
        int count=0,downloads=0;void Assert(bool ok,string why){if(!ok)throw new Exception(why);count++;}
        string version=ProgramPortableUpdates.FileVersion("adwcleaner",adwFile)??throw new Exception("AdwCleaner identity missing");
        string root=Path.Combine(output,"PrivateCopy");Directory.CreateDirectory(root);string copy=Path.Combine(root,"adwcleaner.exe");File.Copy(adwFile,copy,true);
        string originalHash=ProgramPortableUpdates.Hash(copy);Assert(ManufacturerSignature.Verify(copy,"Malwarebytes Inc"),"Private copy retains the real Malwarebytes signature");
        var captured=File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!,"Herstellerquellen","Malwarebytes.AdwCleaner.installer.yaml"));
        Assert(ProgramPortableUpdates.ManifestArtifact(captured,"Malwarebytes.AdwCleaner",version,ProgramPortableUpdates.AdwCleanerDownload)?.Sha==originalHash,"Current user file matches the captured published artifact checksum");
        string settings=Path.Combine(root,"settings.json");File.WriteAllText(settings,"keep private fixture settings");string backups=Path.Combine(output,"Backups");
        string broken=Path.Combine(output,"BadSignature.exe");var bytes=File.ReadAllBytes(copy);bytes[0x40]^=1;File.WriteAllBytes(broken,bytes);
        Assert(!ManufacturerSignature.Verify(broken,"Malwarebytes Inc"),"Tampered signed fixture is rejected by Windows signature verification");
        bool badPayload=false;var steps=new List<ProgramStepEvent>();var sizes=new List<ProgramDownload>();
        var updater=new ProgramPortableUpdates((uri,_)=>Task.FromResult(Encoding.UTF8.GetBytes(uri.AbsoluteUri==ProgramPortableUpdates.AdwCleanerIndex
                ? JsonSerializer.Serialize(new[]{new{type="dir",name=version}})
                : badPayload?captured.Replace(originalHash,ProgramPortableUpdates.Hash(broken),StringComparison.OrdinalIgnoreCase):captured)),
            (_,target,progress,_)=>{downloads++;File.Copy(badPayload?broken:adwFile,target);progress(new(new FileInfo(target).Length,new FileInfo(target).Length));return Task.CompletedTask;},backups);
        var proof=new ProgramInstallation(version,"Portable",copy,false);var offer=await updater.ResolveAsync("adwcleaner",proof,default);
        Assert(offer?.Sha256==originalHash && downloads==0,"Metadata check does not download the executable");
        await updater.ApplyAsync(offer!,steps.Add,sizes.Add,default,reinstall:true);
        Assert(downloads==1 && ProgramPortableUpdates.Hash(copy)==originalHash && File.ReadAllText(settings)=="keep private fixture settings","Same-version publisher replacement preserves exact bytes and adjacent settings without execution");
        Assert(Directory.GetFiles(backups,"previous.exe",SearchOption.AllDirectories).Length==1 && !Directory.GetDirectories(backups,"Download-*").Any(),"Previous executable is backed up and staging cleaned");
        Assert(steps.Any(e=>e.Step==ProgramStep.Download&&e.State==ProgramStepState.Done) && steps.Any(e=>e.Step==ProgramStep.Install&&e.State==ProgramStepState.Done) && sizes.Last().Percent==100,"Measured download and confirmed replacement use existing progress stages");
        using(var cancel=new CancellationTokenSource())
        {
            bool stopped=false;try{await updater.ApplyAsync(offer!,e=>{if(e is {Step:ProgramStep.Download,State:ProgramStepState.Done})cancel.Cancel();},_=>{},cancel.Token,reinstall:true);}catch(OperationCanceledException){stopped=true;}
            Assert(stopped && ProgramPortableUpdates.Hash(copy)==originalHash && Directory.GetFiles(backups,"previous.exe",SearchOption.AllDirectories).Length==1,"Cancellation before replacement preserves the original and creates no second committed backup");
        }
        badPayload=true;var poisoned=await updater.ResolveAsync("adwcleaner",proof,default,fresh:true);bool rejected=false;
        try{await updater.ApplyAsync(poisoned!,_=>{},_=>{},default,reinstall:true);}catch(IOException){rejected=true;}
        Assert(rejected && ProgramPortableUpdates.Hash(copy)==originalHash && Directory.GetFiles(backups,"previous.exe",SearchOption.AllDirectories).Length==1,"Even a matching injected checksum cannot bypass the payload publisher signature");
        badPayload=false;await updater.ResolveAsync("adwcleaner",proof,default,fresh:true);int started=0;
        var adw=ProgramCatalog.Entries.Single(e=>e.Id=="adwcleaner");
        var service=new ProgramInstallService((_,_)=>throw new Exception("Confirmed standalone recipe must not invoke WinGet"),
            (_,_)=>throw new Exception("Standalone replacement must not launch an installer"),
            localReader:(_,_)=>Task.FromResult(new Dictionary<string,ProgramInstallation>{{adw.Id,proof}}),portableUpdates:updater);
        var inventory=await service.InspectUpdatesAsync([adw],default);var package=await service.ResolveAsync(adw,default);
        Assert(!inventory.HasUpdate(adw.Id)&&package?.PortableUpdate is not null,"Current portable AdwCleaner resolves its publisher recipe without a false update or WinGet conversion");
        var options=await service.ResolveReinstallOptionsAsync(adw,inventory,default);
        Assert(options.Count==1&&options[0].ReinstallProof is {SameVersion:true,Installed.Route:ProgramReinstallRoute.Portable},"Shared reinstallation offers the verified same-version standalone route");
        Assert(await service.ReinstallAsync(adw,options[0],inventory,()=>started++)==0&&started==1&&ProgramPortableUpdates.Hash(copy)==originalHash,
            "Shared service executes signed private-copy replacement without launching the scanner or a package installer");
        using(var zip=ZipFile.OpenRead(tukuiZip))
        {
            var member=zip.GetEntry("Tukui Client.exe")??throw new Exception("Official standalone member missing");Assert(zip.Entries.Count==1 && member.Length<32*1024*1024,"Captured Tukui standalone archive is the known single-file variant");
            string tukuiRoot=Path.Combine(output,"TukuiStandalone");Directory.CreateDirectory(tukuiRoot);string target=Path.Combine(tukuiRoot,"Tukui Client.exe");member.ExtractToFile(target,true);
            var entry=ProgramCatalog.Entries.Single(e=>e.Id=="tukui-client");var found=new Dictionary<string,ProgramInstallation>();ProgramLocalInventory.ReadPortable([entry],found,default,[tukuiRoot]);
            Assert(found.TryGetValue(entry.Id,out var local) && local.Source=="Portable" && !local.WinGetCompatible && local.Version=="1.1.2.0" && local.Path==target,"Real standalone filename, product and version are recognized without converting to MSI");
        }
        Assert(ProgramPortableUpdates.Hash(adwFile)==originalHash,"Original desktop program was read only and remains unchanged");
        Console.WriteLine("PASS "+count+" real signed private-copy/source/replacement checks; no scanner, client or installer launched");return 0;
    }
}
