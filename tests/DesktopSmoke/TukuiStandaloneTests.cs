using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class TukuiStandaloneTests
{
    internal static int Ui(App app,string output,string original,string archive)
    {
        int checks=0;void Assert(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));UiSoundService.MuteForTests=true;
        var main=new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Pump(){app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);main.UpdateLayout();}
        void Until(Func<bool> test){var clock=Stopwatch.StartNew();while(!test()&&clock.Elapsed.TotalSeconds<35){Pump();Thread.Sleep(4);}Assert(test(),"Tukui UI did not reach the expected state");}
        void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();Pump();}
        void Save(string name){Pump();var bmp=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(main);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var stream=File.Create(Path.Combine(output,name+".png"));png.Save(stream);}
        string originalHash=ProgramPortableUpdates.Hash(original);var bytes=File.ReadAllBytes(archive);var entry=ProgramCatalog.Entries.Single(e=>e.Id=="tukui-client");
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;
            foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})
            {
                ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;page.SetLanguage(english);view.SetCategory("gaming");Pump();
                string root=Path.Combine(output,"Case-"+english+"-"+size);Directory.CreateDirectory(root);string copy=Path.Combine(root,"Tukui Client.exe");File.Copy(original,copy,true);
                var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int downloads=0,reads=0,queries=0;
                var profile=new ProgramTukuiUpdates(async(progress,token)=>{downloads++;progress(new(0,bytes.Length));await gate.Task.WaitAsync(token);progress(new(bytes.Length,bytes.Length));return bytes;},Path.Combine(root,"Temporary"));
                string source=english&&size==4?"SelectedFile":"Portable";
                var service=new ProgramInstallService((_,_)=>{queries++;throw new Exception("No MSI/WinGet query allowed for standalone Tukui");},
                    (_,_)=>throw new Exception("No client/installer may be launched"),portableUpdates:new ProgramPortableUpdates(backupRoot:Path.Combine(root,"Backups"),tukui:profile),
                    localReader:(entries,_)=>{reads++;Assert(entries.Length==1&&entries[0].Id==entry.Id,"Only Tukui is locally inspected");return Task.FromResult(new Dictionary<string,ProgramInstallation>{{entry.Id,new(ProgramPortableUpdates.FileVersion(entry.Id,copy),source,copy,false)}});});
                view.Service=service;view.ConfirmForTests=_=>throw new Exception("A single verified Tukui update must not ask for another confirmation");
                typeof(ProgramCatalogView).GetProperty("Inventory",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(view,new ProgramInventory(true,
                    new Dictionary<string,string?>{{entry.Id,"1.1.1.0"},{"firefox","1.0"}},Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,new("1.1.1.0",source,copy,false)}}));
                Wait(service.InspectUpdatesAsync([entry],default));view.ShowInfo(entry,view,false);var info=view.Info!;Wait(info.Loading);
                var action=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
                Assert(((TextBlock)action.Content).Text==(english?"Check again":"Erneut prüfen")&&downloads==0,"Opening the information card never downloads the package or offers MSI");
                var reinstall=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoStatusReinstall");
                Assert(((TextBlock)reinstall.Content).Text==(english?"Publisher site":"Herstellerseite"),"Unverified automatic reinstallation is not misleadingly offered");
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>downloads==1);
                Assert(!action.IsEnabled&&!reinstall.IsEnabled,"Version/reinstall actions stay disabled during the targeted package check");
                if(!english)Save("Tukui-Pruefung-"+size);gate.TrySetResult();
                Until(()=>action.IsEnabled&&((TextBlock)action.Content).Text==(english?"Install update":"Update installieren"));
                var texts=UnifiedDialogAppearance.Children<TextBlock>(info.Frame).ToArray();
                Assert(texts.Single(t=>t.Name=="ProgramInfoInstalledVersion").Text=="1.1.1.0"&&texts.Single(t=>t.Name=="ProgramInfoAvailableVersion").Text=="1.1.2.0","Actual private executable versions are displayed in the confirmed direct offer");
                Assert(downloads==1&&queries==0&&reads==2,"Targeted check does not scan other programs, use MSI or repeat downloads");
                Assert(Math.Abs(info.Frame.ActualWidth-(size==1?420:580))<1&&info.Frame.ActualHeight<=540,"Approved compact/large dimensions remain unchanged");
                if(!english)Save("Tukui-Update-bereit-"+size);
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>!info.IsOpen&&!view.IsWorking);
                Assert(ProgramPortableUpdates.FileVersion(entry.Id,copy)=="1.1.2.0"&&downloads==1&&queries==0,"Clicking the normal update action upgrades only the signed private copy using the already checked download");
                Assert(view.Inventory.Installed.GetValueOrDefault("firefox")=="1.0"&&view.UpdateTarget(entry.Id) is null,"Other saved programs remain intact and the completed update candidate disappears");
                Assert(Directory.GetFiles(Path.Combine(root,"Backups"),"previous.exe",SearchOption.AllDirectories).Length==1,"Normal UI update flow creates the original-file backup");
                Assert(!profile.HasPackage(new("1.1.2.0","Portable",copy,false)),"Finished UI operation releases the package cache");
                if(!english)Save("Tukui-Update-beendet-"+size);
            }
            string extra=Path.Combine(output,"NoUpdateAndFailure");Directory.CreateDirectory(extra);string current=Path.Combine(extra,"Tukui Client.exe");
            using(var zip=ZipFile.OpenRead(archive))zip.Entries.Single().ExtractToFile(current,true);
            bool fail=false,waitForCancel=false,entered=false,cancelled=false;int extraReads=0;
            var extraProfile=new ProgramTukuiUpdates(async(progress,token)=>
            {
                extraReads++;if(fail)throw new System.Net.Http.HttpRequestException("Controlled source unavailable");entered=true;
                if(waitForCancel)try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){cancelled=true;throw;}
                progress(new(bytes.Length,bytes.Length));return bytes;
            },Path.Combine(extra,"Temporary"));
            var extraService=new ProgramInstallService((_,_)=>throw new Exception("No MSI query allowed"),(_,_)=>throw new Exception("No installer permitted"),
                portableUpdates:new ProgramPortableUpdates(backupRoot:Path.Combine(extra,"Backups"),tukui:extraProfile),
                localReader:(_,_)=>Task.FromResult(new Dictionary<string,ProgramInstallation>{{entry.Id,new(ProgramPortableUpdates.FileVersion(entry.Id,current),"Portable",current,false)}}));
            view.Service=extraService;view.ConfirmForTests=_=>throw new Exception("A single verified Tukui update must not ask for another confirmation");
            void SeedExtra(string version){typeof(ProgramCatalogView).GetProperty("Inventory",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(view,
                new ProgramInventory(true,new Dictionary<string,string?>{{entry.Id,version}},Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,new(version,"Portable",current,false)}}));}
            Wait(extraService.InspectUpdatesAsync([entry],default));SeedExtra("1.1.2.0");view.ShowInfo(entry,view,false);var currentInfo=view.Info!;Wait(currentInfo.Loading);
            var currentAction=UnifiedDialogAppearance.Children<Button>(currentInfo.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
            currentAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>extraReads==1&&currentAction.IsEnabled);
            Assert(((TextBlock)currentAction.Content).Text=="Check again"&&view.UpdateTarget(entry.Id) is null,"An already current signed version has no false update action");
            fail=true;currentAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>extraReads==2&&currentAction.IsEnabled);
            Assert(UnifiedDialogAppearance.Children<TextBlock>(currentInfo.Frame).Any(t=>t.IsVisible&&t.Text=="Online service unreachable. Check your connection and try again.")
                &&view.UpdateTarget(entry.Id) is null,"Unavailable manufacturer source is shown as a retryable error, not a confirmed no-update result");
            currentInfo.Close();fail=false;waitForCancel=true;entered=false;
            view.ShowInfo(entry,view,false);var cancelledInfo=view.Info!;Wait(cancelledInfo.Loading);
            UnifiedDialogAppearance.Children<Button>(cancelledInfo.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>entered);
            cancelledInfo.Close();Until(()=>cancelled);Assert(!extraProfile.HasPackage(new("1.1.2.0","Portable",current,false)),"Closing the card cancels an in-flight check without retaining an offer");
            waitForCancel=false;File.Copy(original,current,true);SeedExtra("1.1.1.0");view.ShowInfo(entry,view,false);var stopInfo=view.Info!;Wait(stopInfo.Loading);
            var stopAction=UnifiedDialogAppearance.Children<Button>(stopInfo.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");stopAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(()=>stopAction.IsEnabled&&((TextBlock)stopAction.Content).Text=="Install update");stopAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>!stopInfo.IsOpen&&!view.IsWorking);
            Assert(ProgramPortableUpdates.FileVersion(entry.Id,current)=="1.1.2.0"&&!extraProfile.HasPackage(new("1.1.2.0","Portable",current,false)),"A single verified Tukui update finishes without additional confirmation and releases the checked package");
            Assert(ProgramPortableUpdates.Hash(original)==originalHash,"The user's desktop Tukui EXE remains read only throughout UI tests");
            Console.WriteLine("PASS "+checks+" real Tukui private-copy UI action checks; DE/EN, compact/large; no program executed");return 0;
        }
        finally{main.Close();Pump();}
    }
    internal static async Task<int> Files(string output, string original, string archive)
    {
        int count=0,reads=0;void Assert(bool ok,string why){if(!ok)throw new Exception(why);count++;}
        var bytes=File.ReadAllBytes(archive);string hash=ProgramPortableUpdates.Hash(original);
        byte[] ZipPayload(byte[] payload){using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){using var stream=zip.CreateEntry("Tukui Client.exe").Open();stream.Write(payload);}return memory.ToArray();}
        string copy=Path.Combine(output,"Copy","Tukui Client.exe");Directory.CreateDirectory(Path.GetDirectoryName(copy)!);File.Copy(original,copy,true);
        string settings=Path.Combine(Path.GetDirectoryName(copy)!,"settings.json");File.WriteAllText(settings,"preserve");
        var old=ProgramPortableUpdates.FileVersion("tukui-client",copy)!;var proof=new ProgramInstallation(old,"Portable",copy,false);
        Assert(ProgramTukuiUpdates.Signed(copy),"Actual old standalone developer signature is valid");
        Assert(!ManufacturerSignature.Verify(copy,"Open Source Developer",["Some other developer"]),"The generic code-signing organization cannot authorize another author");
        Assert(!ProgramTukuiUpdates.Eligible(proof with{Source="Registry"})&&!ProgramTukuiUpdates.Eligible(proof with{WinGetCompatible=true}),"MSI and compatible registered installations stay separate");
        byte[] selected=bytes;var profile=new ProgramTukuiUpdates((progress,token)=>{reads++;token.ThrowIfCancellationRequested();progress(new(0,selected.Length));progress(new(selected.Length,selected.Length));return Task.FromResult(selected);},Path.Combine(output,"Staging"));
        var updater=new ProgramPortableUpdates(backupRoot:Path.Combine(output,"Backups"),tukui:profile);
        Assert(await updater.ResolveAsync("tukui-client",proof,default) is null&&reads==0,"Passive version checks never download a Tukui archive");
        var measurements=new List<ProgramDownload>();var offer=await updater.ProbeTukuiAsync(proof,measurements.Add,default);
        Assert(offer is not null&&ProgramPortableUpdates.SameVersion(offer.Version,"1.1.2.0")&&ProgramVersions.IsNewer(old,offer.Version),"Target version comes from the actual captured signed standalone EXE, not MSI metadata");
        Assert(offer!.Sha256==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))&&measurements[^1].Percent==100,"Checked archive bytes and measured download are bound to the offer");
        Assert(!Directory.GetDirectories(Path.Combine(output,"Staging"),"Tukui-Pruefung-*").Any(),"Probe staging is removed immediately");
        Assert(await updater.ResolveAsync("tukui-client",proof,default,fresh:true)==offer&&reads==1,"Targeted revalidation reuses the signed checked package without another download");
        var steps=new List<ProgramStepEvent>();await updater.ApplyAsync(offer,steps.Add,measurements.Add,default);
        Assert(ProgramPortableUpdates.FileVersion("tukui-client",copy)=="1.1.2.0"&&File.ReadAllText(settings)=="preserve","Actual signed private copy upgrades in place while adjacent settings remain untouched");
        Assert(Directory.GetFiles(Path.Combine(output,"Backups"),"previous.exe",SearchOption.AllDirectories).Length==1&&ProgramPortableUpdates.Hash(original)==hash,"Previous EXE is backed up and personal desktop original remains unchanged");
        Assert(steps.SequenceEqual(new[]{new ProgramStepEvent(ProgramStep.Download,ProgramStepState.Active),new(ProgramStep.Download,ProgramStepState.Done),new(ProgramStep.Install,ProgramStepState.Active),new(ProgramStep.Install,ProgramStepState.Done)})&&reads==1,"Existing transaction stages run without executing Tukui or redownloading the checked archive");
        Assert(await updater.ResolveAsync("tukui-client",proof,default) is null,"Successful operation releases the checked package");
        File.Copy(original,copy,true);offer=await updater.ProbeTukuiAsync(proof,_=>{},default);
        using(var cancel=new CancellationTokenSource())
        {
            bool stopped=false;try{await updater.ApplyAsync(offer!,e=>{if(e is{Step:ProgramStep.Download,State:ProgramStepState.Done})cancel.Cancel();},_=>{},cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Assert(stopped&&ProgramPortableUpdates.Hash(copy)==hash,"Cancellation before commit preserves original bytes");
        }
        Assert(await updater.ResolveAsync("tukui-client",proof,default) is null,"Cancelled operation releases its package");
        using(var cancel=new CancellationTokenSource())
        {
            bool stopped=false;try{await updater.ProbeTukuiAsync(proof,_=>cancel.Cancel(),cancel.Token);}catch(OperationCanceledException){stopped=true;}
            Assert(stopped&&profile.Cached(proof) is null&&ProgramPortableUpdates.Hash(copy)==hash,"Cancellation during source inspection produces no cached offer and leaves the original intact");
        }
        offer=await updater.ProbeTukuiAsync(proof,_=>{},default);profile.Clear();bool expired=false;
        try{await updater.ApplyAsync(offer!,_=>{},_=>{},default);}catch(IOException){expired=true;}
        Assert(expired&&ProgramPortableUpdates.Hash(copy)==hash,"Expired/unavailable checked package cannot replace a file");
        foreach(string member in new[]{"../Tukui Client.exe","Other.exe"})
        {
            using var memory=new MemoryStream();using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){using var writer=new StreamWriter(zip.CreateEntry(member).Open());writer.Write("wrong");}
            selected=memory.ToArray();bool rejected=false;try{await updater.ProbeTukuiAsync(proof,_=>{},default);}catch(IOException){rejected=true;}
            Assert(rejected&&ProgramPortableUpdates.Hash(copy)==hash,"Unexpected archive member/path cannot authorize a replacement");
        }
        using(var memory=new MemoryStream())
        {
            using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)){using var writer=new StreamWriter(zip.CreateEntry("Tukui Client.exe").Open());writer.Write("not a Windows program");}
            selected=memory.ToArray();bool rejected=false;try{await updater.ProbeTukuiAsync(proof,_=>{},default);}catch(Exception e)when(e is IOException or ArgumentException){rejected=true;}
            Assert(rejected&&ProgramPortableUpdates.Hash(copy)==hash,"Untrusted non-program payload cannot authorize an update");
        }
        using(var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read))
        {
            using var payload=new MemoryStream();using(var input=zip.Entries.Single().Open())input.CopyTo(payload);var tampered=payload.ToArray();tampered[0x40]^=1;selected=ZipPayload(tampered);
            bool rejected=false;try{await updater.ProbeTukuiAsync(proof,_=>{},default);}catch(IOException){rejected=true;}
            Assert(rejected&&profile.Cached(proof) is null&&ProgramPortableUpdates.Hash(copy)==hash,"Matching product metadata with an invalid developer signature is rejected");
            File.WriteAllBytes(copy,payload.ToArray());selected=ZipPayload(File.ReadAllBytes(original));rejected=false;
            try{await updater.ProbeTukuiAsync(proof with{Version="1.1.2.0"},_=>{},default);}catch(IOException){rejected=true;}
            Assert(rejected&&ProgramPortableUpdates.FileVersion("tukui-client",copy)=="1.1.2.0","Even a valid old signed payload cannot downgrade an existing newer copy");
        }
        File.Copy(original,copy,true);selected=bytes;offer=await updater.ProbeTukuiAsync(proof,_=>{},default);
        using(var locked=new FileStream(copy,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            bool rejected=false;try{await updater.ApplyAsync(offer!,_=>{},_=>{},default);}catch(IOException){rejected=true;}
            Assert(rejected&&ProgramPortableUpdates.Hash(copy)==hash,"Locked original cannot be replaced and remains intact");
        }
        offer=await updater.ProbeTukuiAsync(proof,_=>{},default);File.AppendAllText(copy,"changed");
        bool changed=false;try{await updater.ApplyAsync(offer!,_=>{},_=>{},default);}catch(IOException){changed=true;}
        Assert(changed&&ProgramPortableUpdates.Hash(copy)!=hash,"Changed original is never overwritten");
        profile.Clear();Assert(ProgramPortableUpdates.Hash(original)==hash,"Original desktop EXE is read only throughout all checks");
        Console.WriteLine("PASS "+count+" Tukui signed standalone source/cache/real-copy transaction checks; captured source, no client executed");return 0;
    }
}
