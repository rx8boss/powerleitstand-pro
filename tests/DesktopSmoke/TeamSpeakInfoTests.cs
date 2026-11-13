using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class TeamSpeakInfoTests
{
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    internal static int Live(string output)
    {
        var service=new TeamSpeakInfoService(); using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var local=Task.Run(()=>service.ReadLocalAsync(limit.Token)).GetAwaiter().GetResult();
        var online=Task.Run(()=>service.ReadAvailableAsync(limit.Token)).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output,"TeamSpeak-Live.json"),JsonSerializer.Serialize(new{Local=local,Online=online},new JsonSerializerOptions{WriteIndented=true}));
        if(!online.ContainsKey(3)||!online.ContainsKey(6)||online.ContainsKey(5)) throw new Exception("Official page did not confirm separate TS3/TS6 versions");
        Console.WriteLine("PASS read-only TeamSpeak local scan and official Windows-client metadata: "+JsonSerializer.Serialize(new{Local=local,Online=online}));return 0;
    }
    internal static int Run(App app,string output,bool logicOnly)
    {
        int count=0;void Assert(bool ok,string message){if(!ok)throw new Exception(message);count++;}
        Assert(TeamSpeakInfoService.Normalize("5, 0, 0, 0")=="5.0.0.0","Comma-separated executable version normalized");
        foreach(int major in new[]{3,5,6}) Assert(TeamSpeakInfoService.Edition(major+".0.0")==major,"Independent main edition recognized");
        foreach(string version in new[]{"4.0.0","6.0.0-betaX","unknown","6.0<script>"}) Assert(TeamSpeakInfoService.Edition(version)==0,"Unverified version does not determine an edition");
        Assert(TeamSpeakInfoService.Newer("6.0.0-beta4","6.0.0-beta5"),"Newer beta within TS6 recognized");
        Assert(!TeamSpeakInfoService.Newer("6.0.0-beta5","6.0.0-beta4"),"Older beta not an update");
        Assert(!TeamSpeakInfoService.Newer("3.6.2","6.0.0-beta5")&&!TeamSpeakInfoService.Newer("5.0.0","3.6.2"),"No cross-edition updates");
        Assert(!TeamSpeakInfoService.Newer("6.0.0","6.1.0-beta1"),"Stable installation does not move automatically to beta");
        Assert(TeamSpeakInfoService.Newer("6.0.0-beta5","6.0.0"),"Stable release can succeed its same-edition beta");
        Assert(!TeamSpeakInfoService.Newer(null,"6.0.0-beta5"),"Unknown local version not marked outdated");
        Assert(TeamSpeakInfoService.Client("TeamSpeak3 Client","TeamSpeak Systems GmbH")&&TeamSpeakInfoService.Client("TeamSpeak Client","TeamSpeak Systems GmbH"),"Explicit client identities accepted");
        Assert(!TeamSpeakInfoService.Client("TeamSpeak 3 Server","TeamSpeak Systems GmbH")&&!TeamSpeakInfoService.Client("TeamSpeak SDK","TeamSpeak Systems GmbH")&&!TeamSpeakInfoService.Client("TeamSpeak","Other Vendor"),"Servers, SDK and wrong publisher rejected");
        string page="<a href=\"https://files.teamspeak-services.com/releases/client/3.6.2/TeamSpeak3-Client-win64-3.6.2.exe\">Windows</a> <a href=\"https://files.teamspeak-services.com/pre_releases/client/6.0.0-beta5/teamspeak-client.msi\">Windows</a>";
        var releases=TeamSpeakInfoService.ParseAvailable(page);Assert(releases.Count==2&&releases[3]=="3.6.2"&&releases[6]=="6.0.0-beta5"&&!releases.ContainsKey(5),"Exact Windows-client links determine distinct versions");
        string noise="https://files.teamspeak-services.com/releases/server/3.99.0/TeamSpeak3-Client-win64-3.99.0.exe https://files.teamspeak-services.com.evil.invalid/releases/client/3.99.0/TeamSpeak3-Client-win64-3.99.0.exe https://files.teamspeak-services.com/releases/client/6.9.0/teamspeak-client.tar.xz";
        Assert(TeamSpeakInfoService.ParseAvailable(noise).Count==0,"Server, spoofed host and non-Windows links rejected");
        Assert(!TeamSpeakInfoService.ParseAvailable(page+page.Replace("3.6.2","3.6.3")).ContainsKey(3),"Ambiguous same-edition releases do not choose an arbitrary version");
        var copies=new List<TeamSpeakCopy>{new(3,"3.6.2",null,"Registry"),new(5,"5.0.0",null,"Registry")};
        TeamSpeakInfoService.AddFile(copies,new(5,"5.0.0.0",@"C:\Example\TeamSpeak.exe","File"));
        Assert(copies.Count==2&&copies.Single(x=>x.Edition==5).Path is not null&&copies.Single(x=>x.Edition==3).Path is null,"File evidence fills only its matching edition");
        TeamSpeakInfoService.AddFile(copies,new(3,null,@"C:\Example\ts3client_win64.exe","File"));Assert(copies.Count==3,"Unknown file version does not attach arbitrarily to a registered copy");
        Assert(TeamSpeakInfoService.ReadExecutable(Path.Combine(output,"missing.exe")) is null,"Missing executable not detected");
        if(logicOnly){Console.WriteLine("PASS "+count+" TeamSpeak edition/parser comparisons");return 0;}
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));UiSoundService.MuteForTests=true;
        var main=new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Pump(){app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);main.UpdateLayout();}
        void Wait(Task task){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.Elapsed.TotalSeconds<15){Pump();Thread.Sleep(3);}if(!task.IsCompleted)throw new TimeoutException("TeamSpeak fixture timed out");task.GetAwaiter().GetResult();Pump();}
        void Save(string name){Pump();var bmp=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(main);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);}
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();Pump();var updates=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=updates.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;view.Service=new NoGenericCalls();view.SetCategory("streaming");Pump();
            var entry=ProgramCatalog.Entries.Single(x=>x.Id=="teamspeak");
            typeof(ProgramCatalogView).GetProperty("Inventory",Flags)!.SetValue(view,new ProgramInventory(true,new Dictionary<string,string?>()));
            string p3=Path.Combine(output,"TS3","ts3client_win64.exe"),p6=Path.Combine(output,"TS6","TeamSpeak.exe");foreach(string file in new[]{p3,p6}){Directory.CreateDirectory(Path.GetDirectoryName(file)!);File.WriteAllText(file,"Fixture, never execute");}
            TeamSpeakSnapshot snapshot=new([new(3,"3.6.2",p3,"Registry"),new(6,"6.0.0-beta4",p6,"Registry")]);
            ProgramInfoCard Open(Fake service){view.TeamSpeakService=service;view.CloseInfo();view.ShowInfo(entry,view.Tiles.First(x=>x.Tag?.ToString()==entry.Id),false);return view.Info!;}
            CheckBox Check(ProgramInfoCard info,int edition)=>UnifiedDialogAppearance.Children<CheckBox>(info.Frame).Single(x=>x.Name=="TeamSpeakEdition"+edition);
            TextBlock Value(ProgramInfoCard info,string kind,int edition)=>UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Single(x=>x.Name=="TeamSpeak"+kind+edition);
            foreach(var theme in ((ComboBox)main.FindName("ThemeBox")).Items.OfType<ComboBoxItem>().Where(x=>x.Tag is string).ToArray())
            {
                ((ComboBox)main.FindName("ThemeBox")).SelectedItem=theme;
                foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;updates.SetLanguage(english);Pump();var fake=new Fake(snapshot,releases);var info=Open(fake);Wait(info.Loading);
                    Assert(info.TeamSpeak!.SelectedEditions.Order().SequenceEqual(new[]{3,6}),"All found editions automatically selected");
                    Assert(UnifiedDialogAppearance.Children<Border>(info.Frame).Count(x=>x.Name.StartsWith("TeamSpeakResult"))==2,"Simultaneous editions have separate result cards");
                    Assert(Value(info,"Installed",3).Text=="3.6.2"&&Value(info,"Installed",6).Text=="6.0.0-beta4","Separate local versions preserved");
                    Assert(Value(info,"Available",3).Text=="3.6.2"&&Value(info,"Available",6).Text=="6.0.0-beta5","Separate online versions preserved");
                    var red=((SolidColorBrush)Value(info,"Installed",6).Foreground).Color;var green=((SolidColorBrush)Value(info,"Available",6).Foreground).Color;
                    Assert(red.R>red.G&&green.G>green.R,"Outdated version red, new version green");
                    var scroll=UnifiedDialogAppearance.Children<ScrollViewer>(info.Frame).First();
                    if(scroll.ScrollableHeight>=1) typeof(ProgramReinstallUiTests).GetMethod("Save",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[main,Path.Combine(output,"TeamSpeak-Layout-Diagnose.png")]);
                    Assert(scroll.ScrollableHeight<1&&scroll.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden,$"Two editions fit in compact and large without scrollbars: {theme.Tag}, EN={english}, size={size}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}");
                    Assert(info.Frame.ActualWidth==(size==1?420:580)&&info.Frame.ActualHeight<=540,"Approved dialog dimensions preserved");
                    foreach(var button in UnifiedDialogAppearance.Children<Button>(info.Frame)){var p=button.TranslatePoint(new Point(),info.Frame);Assert(p.X>=0&&p.X+button.ActualWidth<=info.Frame.ActualWidth+1,"Buttons bounded inside frame");}
                    Assert(fake.OnlineCalls==1&&fake.LocalCalls==1,"Initial editions share one metadata request and one local scan");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Any(x=>x.Text==(english?"What is TeamSpeak?":"Was ist TeamSpeak?")),"Description heading translated");
                    var updateAction=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(x=>x.Name=="TeamSpeakUpdate6");
                    Assert(updateAction.IsEnabled&&updateAction.Content is TextBlock updateCaption&&updateCaption.Text=="Update","Explicit visible update button for selected edition");
                    Assert(UnifiedDialogAppearance.Children<Button>(info.Frame).Single(x=>x.Name=="TeamSpeakAction6").Content.ToString()!.Contains(english?"Reinstall":"Neu installieren"),"Selected installed edition keeps independent reinstallation action");
                    Assert(UnifiedDialogAppearance.Children<Button>(info.Frame).Single(x=>x.Name=="TeamSpeakUninstall6").Style==(Style)main.FindResource("UnifiedDialogDanger"),"Installed edition has red uninstallation action");
                    var details3=UnifiedDialogAppearance.Children<StackPanel>(info.Frame).Single(x=>x.Name=="TeamSpeakDetails3");
                    Assert(details3.Visibility==Visibility.Collapsed,"Other selected edition starts collapsed");
                    UnifiedDialogAppearance.Children<Button>(info.Frame).Single(x=>x.Name=="TeamSpeakExpand3").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                    Assert(UnifiedDialogAppearance.Children<StackPanel>(info.Frame).Single(x=>x.Name=="TeamSpeakDetails3").Visibility==Visibility.Visible&&info.TeamSpeak.SelectedEditions.Count==2,"Expanding one edition preserves both checkboxes");
                    Assert(UnifiedDialogAppearance.Children<StackPanel>(info.Frame).Single(x=>x.Name=="TeamSpeakDetails6").Visibility==Visibility.Collapsed,"Only active details expanded");
                    UnifiedDialogAppearance.Children<Button>(info.Frame).Single(x=>x.Name=="TeamSpeakExpand6").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
                    Wait(info.TeamSpeak.RefreshAsync());Assert(fake.OnlineCalls==2,"Manual check requests fresh online data");
                    if(theme.Tag?.ToString()=="Standard"&&!english)Save("TeamSpeak-Mehrfach-"+size);
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size==1?4:1;Pump();
                    Assert(info.IsOpen&&info.TeamSpeak.SelectedEditions.Count==2,"Resizing open dialog retains both checked editions");
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;Pump();
                    var resizedScroll=UnifiedDialogAppearance.Children<ScrollViewer>(info.Frame).First();
                    if(resizedScroll.ScrollableHeight>=1) Save("Resize-Failure-"+theme.Tag+"-"+english+"-"+size);
                    Assert(resizedScroll.ScrollableHeight<1,"Returning to initial size preserves readable two-edition layout: "+theme.Tag+", English="+english+", size="+size+", scroll="+resizedScroll.ScrollableHeight+", frame="+info.Frame.ActualWidth+"x"+info.Frame.ActualHeight);
                    Assert(!UnifiedDialogAppearance.Children<CheckBox>(info.Frame).Any(x=>x.Name=="TeamSpeakEdition5"),"Obsolete TS5 is not an installable edition");
                    Check(info,3).IsChecked=false;Assert(!UnifiedDialogAppearance.Children<Border>(info.Frame).Any(x=>x.Name=="TeamSpeakResult3"),"Unchecking removes only its own result");
                    Check(info,6).IsChecked=false;Assert(info.TeamSpeak.SelectedEditions.Count==0,"Empty selection allowed without execution");info.Close();
                }
            }
            updates.SetLanguage(false);((ComboBox)main.FindName("SizeBox")).SelectedIndex=1;Pump();
            var legacy=Open(new Fake(new([new(5,"5.0.0",p6,"Registry")]),releases));Wait(legacy.Loading);
            Assert(legacy.TeamSpeak!.SelectedEditions.SequenceEqual(new[]{6})&&Value(legacy,"Installed",6).Text=="Nicht installiert"&&Value(legacy,"Available",6).Text=="6.0.0-beta5","Legacy TS5 is identified separately, TS6 is selected as an explicit migration target");Save("TeamSpeak-5");legacy.Close();
            int migrationConfirms=0;view.ConfirmForTests=q=>{migrationConfirms++;Assert(q.Contains("Ausgabe-/Kanalwechsel")&&q.Contains("Betaversion"),"Migration confirmation names edition switch and beta");return false;};
            var migration=Open(new Fake(new([new(5,"5.0.0",p6,"Registry")]),releases));Wait(migration.Loading);
            UnifiedDialogAppearance.Children<Button>(migration.Frame).Single(x=>x.Name=="TeamSpeakAction6").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Assert(migrationConfirms==1&&migration.IsOpen&&!view.IsWorking,"Cancelled switch keeps info open and executes nothing");view.ConfirmForTests=null;migration.Close();
            var unknown=Open(new Fake(new([new(6,null,p6,"Registry")]),releases));Wait(unknown.Loading);Assert(!UnifiedDialogAppearance.Children<Button>(unknown.Frame).Single(x=>x.Name=="TeamSpeakAction6").IsEnabled&&Value(unknown,"Installed",6).Text=="Nicht ermittelbar","Unknown installed version has no executable action");unknown.Close();
            var store=Open(new Fake(new([new(6,"6.0.0-beta4",p6,"Windows-App")]),releases));Wait(store.Loading);Assert(!UnifiedDialogAppearance.Children<Button>(store.Frame).Single(x=>x.Name=="TeamSpeakAction6").IsEnabled,"Store copy cannot be replaced by desktop installer");store.Close();
            var multiple=Open(new Fake(new([new(6,"6.0.0-beta4",p6,"Registry"),new(6,"6.0.0-beta4",p6+"-other","Registry")]),releases));Wait(multiple.Loading);Assert(UnifiedDialogAppearance.Children<Button>(multiple.Frame).Where(x=>x.Name=="TeamSpeakAction6").All(x=>!x.IsEnabled),"Multiple copies do not execute an arbitrary target");multiple.Close();
            var held=new Fake(snapshot,releases){Held=new(TaskCreationOptions.RunContinuationsAsynchronously)};var pending=Open(held);Pump();
            Assert(held.OnlineCalls==1&&!pending.Loading.IsCompleted,"Delayed shared request stays asynchronous");
            Check(pending,3).IsChecked=false;Check(pending,3).IsChecked=true;Assert(held.OnlineCalls==1&&held.LocalCalls==1,"Rapid reselect does not start duplicate in-flight work");
            Check(pending,3).IsChecked=false;held.Held.SetResult(releases);Wait(pending.Loading);
            Assert(!UnifiedDialogAppearance.Children<Border>(pending.Frame).Any(x=>x.Name=="TeamSpeakResult3"),"Late response does not restore an unchecked card");pending.Close();
            var closing=new Fake(snapshot,releases){Held=new(TaskCreationOptions.RunContinuationsAsynchronously)};var closed=Open(closing);Pump();closed.Close();closing.Held.SetResult(releases);Wait(closed.Loading);Assert(!closed.IsOpen,"Closing during a request prevents reopening");
            var offline=Open(new Fake(snapshot,releases){Offline=true});Wait(offline.Loading);Assert(Value(offline,"Available",6).Text=="Nicht bestätigt"&&Value(offline,"Status",6).Text.Contains("Onlineversion"),"Offline preserves local version without claiming current status");offline.Close();
            Assert(((NoGenericCalls)view.Service).Calls==0,"TeamSpeak check never invokes generic WinGet resolve or any installer");
            int installedCalls=0,confirmed=0;var registered=new ProgramInstallation("3.6.2","Registry",p3);
            var reinstall=new ProgramInstallService(query:(_,_)=>Task.FromResult(new WinGetResult(0,"")),install:(_,_)=>{installedCalls++;return Task.FromResult(0);});
            ((Dictionary<string,string>)typeof(ProgramInstallService).GetField("_installedPackageIds",Flags)!.GetValue(reinstall)!)[entry.Id]=entry.PackageId!;
            typeof(ProgramInstallService).GetField("_recentExport",Flags)!.SetValue(reinstall,new Dictionary<string,string?>{{entry.PackageId!,"3.6.2"}});
            typeof(ProgramInstallService).GetField("_lastEvidence",Flags)!.SetValue(reinstall,new Dictionary<string,ProgramInstallation>{{entry.Id,registered}});
            typeof(ProgramCatalogView).GetProperty("Inventory",Flags)!.SetValue(view,new ProgramInventory(true,new Dictionary<string,string?>{{entry.Id,"3.6.2"}},Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,registered}}));
            view.Service=reinstall;view.ConfirmForTests=_=>{confirmed++;return false;};var retained=Open(new Fake(new([new(3,"3.6.2",p3,"Registry")]),releases));Wait(retained.Loading);
            var statusAction=UnifiedDialogAppearance.Children<Button>(retained.Frame).Single(x=>x.Name=="TeamSpeakAction3");Assert(statusAction.ToolTip?.ToString()?.Contains("Neu installieren")==true,"Verified existing TS3 reinstall action remains available");
            statusAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();Assert(confirmed==1&&installedCalls==0&&!view.IsWorking,"TS3 action uses existing confirmation; cancellation executes no installer");view.ConfirmForTests=null;retained.Close();
            var actionBackend=new FakeInstaller(p6);view.TeamSpeakInstaller=actionBackend;view.ConfirmForTests=_=>true;
            var operation=view.InstallTeamSpeakAsync(new(6,"6.0.0-beta5",TeamSpeakInstallKind.Install,new([]),null));Pump();
            Assert(view.IsWorking&&actionBackend.Applies==1,"Confirmed action uses in-app progress and locks selection");
            var duplicate=view.InstallTeamSpeakAsync(new(6,"6.0.0-beta5",TeamSpeakInstallKind.Install,new([]),null));Wait(duplicate);Assert(actionBackend.Applies==1,"Second click cannot launch another installer");
            view.RequestStop();Pump();Assert(view.IsWorking&&!operation.IsCompleted,"Stop does not unlock while active installer is unfinished");
            actionBackend.Finished.SetResult(true);Wait(operation);Assert(!view.IsWorking&&view.Inventory.Installed.GetValueOrDefault(entry.Id)=="6.0.0-beta5","Verified completion updates inventory and unlocks selection");view.ConfirmForTests=null;
            Console.WriteLine("PASS "+count+" TeamSpeak checks: editions, multiple selection, late replies, cancellation, offline; seven themes, DE/EN, compact/large; no installer executed");return 0;
        }
        finally{((WindowsUpdatesPage)main.FindName("WindowsUpdatesPage")).ProgramView?.CloseInfo();main.Close();Pump();}
    }
    sealed class Fake(TeamSpeakSnapshot snapshot,IReadOnlyDictionary<int,string> online):ITeamSpeakInfoService
    {
        internal int LocalCalls,OnlineCalls;internal bool Offline;internal TaskCompletionSource<IReadOnlyDictionary<int,string>>? Held;
        public Task<TeamSpeakSnapshot> ReadLocalAsync(CancellationToken token){LocalCalls++;return Task.FromResult(snapshot);}
        public Task<IReadOnlyDictionary<int,string>> ReadAvailableAsync(CancellationToken token){OnlineCalls++;return Offline?Task.FromException<IReadOnlyDictionary<int,string>>(new HttpRequestException("Fixture offline")):Held?.Task??Task.FromResult(online);}
    }
    sealed class NoGenericCalls:IProgramInstallService
    {
        internal int Calls;Task<T> No<T>(){Calls++;throw new Exception("TeamSpeak must not call a generic installer or query");}
        public Task<ProgramInventory> InspectAsync(CancellationToken token)=>No<ProgramInventory>();
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry,CancellationToken token)=>No<ProgramPackage?>();
        public Task<int> InstallAsync(ProgramEntry entry,ProgramPackage package,Action started)=>No<int>();
        public Task<int> UpgradeAsync(ProgramEntry entry,ProgramPackage package,Action started)=>No<int>();
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query,CancellationToken token)=>No<IReadOnlyList<ProgramEntry>>();
        public void OpenWebsite(ProgramEntry entry){Calls++;throw new Exception("No browser action during tests");}
    }
    sealed class FakeInstaller(string path):ITeamSpeakInstaller
    {
        internal int Applies;internal TaskCompletionSource<bool> Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TeamSpeakInstallPlan> PrepareAsync(TeamSpeakInstallRequest r,CancellationToken token)=>Task.FromResult(new TeamSpeakInstallPlan(r,TeamSpeakInstaller.Url(r.Edition,r.Version),null,null));
        public async Task<TeamSpeakInstallResult> ApplyAsync(TeamSpeakInstallPlan p,Action<ProgramStepEvent> step,Action<ProgramDownload> download,CancellationToken token)
        {Applies++;step(new(ProgramStep.Check,ProgramStepState.Done));step(new(ProgramStep.Download,ProgramStepState.Active));download(new(10,10));step(new(ProgramStep.Download,ProgramStepState.Done));step(new(ProgramStep.Install,ProgramStepState.Active));await Finished.Task;step(new(ProgramStep.Install,ProgramStepState.Done));step(new(ProgramStep.Finish,ProgramStepState.Active));return new(0,true,path);}
    }
}

