using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PowerControlProWpf;

internal static class TeamSpeakActionTests
{
    internal static async Task<int> PrepareLive(string output)
    {
        using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(60));var info=new TeamSpeakInfoService();
        var snapshot=await info.ReadLocalAsync(cancellation.Token);var online=await info.ReadAvailableAsync(cancellation.Token);
        string version=online[6];var kind=TeamSpeakInstallPolicy.Decide(6,version,snapshot,out string? reason)??throw new IOException("Live preparation blocked: "+reason);
        var copy=snapshot.Copies.SingleOrDefault(x=>x.Edition==6);
        var plan=await new TeamSpeakInstaller(info).PrepareAsync(new(6,version,kind,snapshot,copy),cancellation.Token);
        File.WriteAllText(Path.Combine(output,"TeamSpeak-Vorbereitung.json"),JsonSerializer.Serialize(plan,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS read-only TeamSpeak 6 preparation: kind="+kind+", official target="+version+"; no installer downloaded or executed");return 0;
    }
    internal static int Artifact(string output, string file)
    {
        var request = new TeamSpeakInstallRequest(6, "6.0.0-beta5", TeamSpeakInstallKind.Install, new([]), null);
        var plan = new TeamSpeakInstallPlan(request, TeamSpeakInstaller.Url(6, request.Version), null, null);
        var result = TeamSpeakInstaller.Validate(file, plan);
        File.WriteAllText(Path.Combine(output, "TeamSpeak-Artifact.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine("PASS signed official TeamSpeak MSI read-only: " + JsonSerializer.Serialize(result)); return 0;
    }
    internal static int Run(string output)
    {
        int checks=0; void Assert(bool ok, string label) {if(!ok)throw new Exception(label);checks++;}
        var a = new TeamSpeakSnapshot([]); string path=Path.Combine(output,"ts3client_win64.exe");File.WriteAllText(path,"Fixture - never execute");
        var copy = new TeamSpeakCopy(3,"3.6.1",path,"Registry",Scope:"user");
        var old = new TeamSpeakSnapshot([copy]);
        Assert(TeamSpeakInstallPolicy.Equal("3.6.2.0","3.6.2"),"Trailing resource zero not a different version");
        Assert(!TeamSpeakInstallPolicy.Equal("6.0.0-beta4","6.0.0-beta5"),"Different betas not equal");
        Assert(!TeamSpeakInstallPolicy.Equal("6.0.0","6.0.0-beta5"),"Stable and beta not equal");
        Assert(TeamSpeakInstallPolicy.Equal("3.999999999999999999999999999.0.0","3.999999999999999999999999999.0"),"Large numeric metadata does not overflow");
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.2",a,out _)==TeamSpeakInstallKind.Install,"Missing TS3 can be installed");
        Assert(TeamSpeakInstallPolicy.Decide(6,"6.0.0-beta5",a,out _)==TeamSpeakInstallKind.Install,"Missing TS6 can be installed");
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.2",old,out _)==TeamSpeakInstallKind.Update,"Matching older TS3 can update");
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.1",old,out _)==TeamSpeakInstallKind.Reinstall,"Same installed TS3 can reinstall");
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.0",old,out _)==null,"Downgrade blocked");
        Assert(TeamSpeakInstallPolicy.Decide(5,"5.0.0",old,out _)==null,"No obsolete TS5 download");
        Assert(TeamSpeakInstallPolicy.Decide(6,"3.6.2",a,out _)==null,"Wrong major installer blocked");
        Assert(TeamSpeakInstallPolicy.Decide(3,null,old,out _)==null,"Missing online version blocks installation");
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.2",old with {Complete=false},out _)==null,"Incomplete scan blocks ambiguous target");
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.2",new([copy,copy with {Path=path+"2"}]),out _)==null,"Multiple same-edition copies blocked");
        foreach(string source in new[]{"Portable","Windows-App","Scoop","Chocolatey","SelectedFile"})
            Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.2",new([copy with {Source=source}]),out _)==null,"Other distribution stays separate: "+source);
        Assert(TeamSpeakInstallPolicy.Decide(3,"3.6.2",new([copy with {Version=null}]),out _)==null,"Unknown installed version blocked");
        var legacy=copy with {Edition=5,Version="5.0.0"};
        Assert(TeamSpeakInstallPolicy.Decide(6,"6.0.0-beta5",new([legacy]),out _)==TeamSpeakInstallKind.Switch,"Legacy 5 requires explicit edition switch");
        Assert(TeamSpeakInstallPolicy.Decide(6,"6.0.0-beta5",new([legacy with {Scope="machine"}]),out _)==null,"Machine installation is not silently changed to per-user MSI");
        string url=TeamSpeakInstaller.Url(3,"3.6.2"),sha=new('A',64);
        string manifest=$"PackageIdentifier: TeamSpeakSystems.TeamSpeakClient\nPackageVersion: 3.6.2\nInstallerType: nullsoft\nInstallers:\n- Architecture: x64\n  InstallerUrl: {url}\n  InstallerSha256: {sha}\n";
        Assert(TeamSpeakInstaller.ManifestHash(manifest,"3.6.2",url)==sha,"Exact official x64 manifest hash accepted");
        Assert(TeamSpeakInstaller.ManifestHash(manifest.Replace("3.6.2","3.6.3"),"3.6.2",url)==null,"Wrong manifest version rejected");
        Assert(TeamSpeakInstaller.ManifestHash(manifest+manifest.Replace(sha,new string('B',64)),"3.6.2",url)==null,"Conflicting hashes rejected");
        Assert(TeamSpeakInstaller.ManifestHash(manifest.Replace("TeamSpeakSystems.TeamSpeakClient","Other.Program"),"3.6.2",url)==null,"Wrong package rejected");
        var request=new TeamSpeakInstallRequest(3,"3.6.2",TeamSpeakInstallKind.Update,old,copy);
        var plan=new TeamSpeakInstallPlan(request,url,sha,null);
        var start=TeamSpeakInstaller.StartInfo(plan,Path.GetFullPath(Path.Combine(output,"installer.exe")));
        Assert(start.FileName.EndsWith("installer.exe")&&start.Arguments.Contains("/S /CURRENTUSER")&&start.Arguments.EndsWith("/D="+Path.GetDirectoryName(path)),"Silent TS3 command preserves exact destination");
        var r6=new TeamSpeakInstallRequest(6,"6.0.0-beta5",TeamSpeakInstallKind.Reinstall,a,null);
        var start6=TeamSpeakInstaller.StartInfo(new(r6,TeamSpeakInstaller.Url(6,r6.Version),null,null),Path.GetFullPath(Path.Combine(output,"installer.msi")));
        Assert(Path.GetFileName(start6.FileName)=="msiexec.exe"&&start6.Arguments.StartsWith("/famus ")&&start6.Arguments.EndsWith("/qn /norestart"),"MSI reinstall is automatic and has no CMD shell");
        int launches=0; var info=new Fake(a,new Dictionary<int,string>{{3,"3.6.2"}});
        var backend=new TeamSpeakInstaller(info,download:(p,file,progress,token)=>{File.WriteAllText(file,"Fixture");progress(new(7,7));return Task.CompletedTask;},
            validate:(_,_)=>null,run:_=>{launches++;info.Snapshot=new([copy with {Version="3.6.2"}]);return Task.FromResult(0);},
            manifest:(_,_)=>Task.FromResult(manifest),root:Path.Combine(output,"Stage"));
        var installRequest=request with {Kind=TeamSpeakInstallKind.Install,Snapshot=a,Copy=null};
        var prepared=backend.PrepareAsync(installRequest,CancellationToken.None).GetAwaiter().GetResult();
        var steps=new List<ProgramStepEvent>();var result=backend.ApplyAsync(prepared,steps.Add,_=>{},CancellationToken.None).GetAwaiter().GetResult();
        Assert(launches==1&&result.Verified&&result.Path==path,"Injected runner only: verified successful install result");
        Assert(steps.Contains(new(ProgramStep.Download,ProgramStepState.Done))&&steps.Contains(new(ProgramStep.Install,ProgramStepState.Done))&&steps.Contains(new(ProgramStep.Finish,ProgramStepState.Active)),"Real stage transitions retained");
        Assert(!Directory.EnumerateDirectories(Path.Combine(output,"Stage")).Any(),"Staging directory removed after completion");
        info.Snapshot=a; info.Online=new Dictionary<int,string>{{3,"3.6.3"}};
        bool failed=false;try{backend.PrepareAsync(installRequest,CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){failed=true;}
        Assert(failed&&launches==1,"Changed publisher version prevents another installer");
        info.Online=new Dictionary<int,string>{{3,"3.6.2"}};
        prepared=backend.PrepareAsync(installRequest,CancellationToken.None).GetAwaiter().GetResult(); info.Snapshot=old;
        failed=false;try{backend.ApplyAsync(prepared,_=>{},_=>{},CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){failed=true;}
        Assert(failed&&launches==1,"Changed local target before install prevents runner");
        info.Snapshot=a;
        backend=new TeamSpeakInstaller(info,download:(_,file,_,_)=>{File.WriteAllText(file,"Bad signature fixture");return Task.CompletedTask;},validate:(_,_)=>throw new IOException("Signature not trusted"),run:_=>{launches++;return Task.FromResult(0);},manifest:(_,_)=>Task.FromResult(manifest),root:Path.Combine(output,"BadSignature"));
        prepared=backend.PrepareAsync(installRequest,CancellationToken.None).GetAwaiter().GetResult();
        failed=false;try{backend.ApplyAsync(prepared,_=>{},_=>{},CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){failed=true;}
        Assert(failed&&launches==1,"Untrusted signature never reaches runner");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        failed=false;try{backend.ApplyAsync(prepared,_=>{},_=>{},cancelled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){failed=true;}
        Assert(failed&&launches==1,"Cancelled preparation never reaches runner");
        string receiptFile=Path.Combine(output,"Receipt.json");
        var c6=new TeamSpeakCopy(6,"6.0.0.5163",path,"Registry","{C6F94575-927B-4BD1-BE22-BA6FE3552FC7}","user");
        TeamSpeakInstallReceipts.SaveAt(c6,"6.0.0-beta5",receiptFile);
        Assert(TeamSpeakInstallReceipts.ApplyAt(c6,receiptFile,_=>c6).Version=="6.0.0-beta5","Receipt preserves exact beta after verified install");
        Assert(TeamSpeakInstallReceipts.ApplyAt(c6 with {Path=null},receiptFile,_=>c6).Path==path,"Receipt restores only matching MSI product path");
        Assert(TeamSpeakInstallReceipts.ApplyAt(c6 with {ProductCode=Guid.NewGuid().ToString("B")},receiptFile,_=>c6).Version==c6.Version,"Another MSI product cannot inherit beta receipt");
        Assert(TeamSpeakInstallReceipts.ApplyAt(c6 with {Source="Windows-App"},receiptFile,_=>c6).Version==c6.Version,"Store installation cannot inherit desktop receipt");
        Assert(TeamSpeakInstallReceipts.ApplyAt(c6,receiptFile,_=>copy).Version==c6.Version,"Wrong executable edition invalidates receipt");
        File.AppendAllText(path,"changed");Assert(TeamSpeakInstallReceipts.ApplyAt(c6,receiptFile,_=>c6).Version==c6.Version,"Changed executable invalidates saved beta receipt");
        File.WriteAllText(receiptFile,"{\"Path\":null,\"Hash\":null,\"ProductCode\":null,\"Version\":\"6.0.0-beta5\"}");
        Assert(TeamSpeakInstallReceipts.ApplyAt(c6,receiptFile,_=>c6)==c6,"Malformed receipt is ignored without crashing");
        Console.WriteLine("PASS "+checks+" TeamSpeak installer policy/flow checks; injected runner only, no foreign installer executed");return 0;
    }
    sealed class Fake(TeamSpeakSnapshot snapshot,IReadOnlyDictionary<int,string> online):ITeamSpeakInfoService
    {
        internal TeamSpeakSnapshot Snapshot=snapshot;internal IReadOnlyDictionary<int,string> Online=online;
        public Task<TeamSpeakSnapshot> ReadLocalAsync(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(Snapshot);}
        public Task<IReadOnlyDictionary<int,string>> ReadAvailableAsync(CancellationToken token){token.ThrowIfCancellationRequested();return Task.FromResult(Online);}
    }
}
