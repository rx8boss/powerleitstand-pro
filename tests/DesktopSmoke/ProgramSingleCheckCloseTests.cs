using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramSingleCheckCloseTests
{
    internal static int Ui(App app,string output)
    {
        int count=0;void Assert(bool ok,string why){if(!ok)throw new Exception(why);count++;}
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));UiSoundService.MuteForTests=true;
        var main=new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Pump(){app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);main.UpdateLayout();}
        void Until(Func<bool> condition){var watch=Stopwatch.StartNew();while(!condition()&&watch.Elapsed.TotalSeconds<30){Pump();Thread.Sleep(4);}Assert(condition(),"Single-check/close UI did not reach the expected state");}
        void Wait(Task task){Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();Pump();}
        void Save(string name){Pump();var bmp=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(main);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);}
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;var firefox=ProgramCatalog.Entries.Single(e=>e.Id=="firefox");var discord=ProgramCatalog.Entries.Single(e=>e.Id=="discord");
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;
            var selected=firefox;string? version="2.0";bool present=true;int reads=0,queries=0,closed=0;
            var service=new ProgramInstallService((start,_)=>
            {
                if(start.ArgumentList[0]=="search")return Task.FromResult(new WinGetResult(0,"Fixture Paint  Fixture.Paint  2.0"));
                queries++;Assert(start.ArgumentList.Contains("--exact")&&start.ArgumentList.Contains(selected.PackageId!)&&start.ArgumentList[0] is "list" or "show","Every card queries only its exact selected identity");
                return Task.FromResult(new WinGetResult(0,start.ArgumentList[0]=="show"?"Found ["+selected.PackageId+"]\nVersion: 2.0":"No updates"));
            },(_,_)=>throw new Exception("Information checks must not install"),localReader:(entries,_)=>
            {reads++;Assert(entries.Length==1&&entries[0].Id==selected.Id,"Every information check is confined to one entry");return Task.FromResult(present?new Dictionary<string,ProgramInstallation>{{selected.Id,new(version,"Registry")}}:new());},
                closeApplications:(_,_,_)=>{closed++;throw new Exception("Information check must not close applications");});
            view.Service=service;var search=service.SearchAsync("fixture",default);Wait(search);var online=search.Result.Single();
            ((Dictionary<string,ProgramEntry>)typeof(ProgramCatalogView).GetField("_sessionEntries",flags)!.GetValue(view)!)[online.Id]=online;
            void Seed(ProgramEntry entry,bool installed,string? localVersion)
            {
                var values=new Dictionary<string,string?>{{"7-zip","3.0"}};var proofs=new Dictionary<string,ProgramInstallation>();
                if(installed){values[entry.Id]=localVersion;proofs[entry.Id]=new(localVersion,"Registry");}
                typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,new ProgramInventory(true,values,Evidence:proofs,UpdateCheckComplete:true));
            }
            void Check(ProgramEntry entry,bool english,int size,bool installed,string? localVersion)
            {
                selected=entry;version=localVersion;present=installed;page.SetLanguage(english);((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;Seed(entry,installed,localVersion);Pump();
                view.ShowInfo(entry,view,false);var info=view.Info!;Wait(info.Loading);int before=reads,beforeQueries=queries;
                var action=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
                Assert(action.IsVisible&&((TextBlock)action.Content).Text==(english?"Check again":"Erneut prüfen"),"Current, unknown and uninstalled programs expose the targeted check");
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>reads==before+1&&action.IsEnabled);
                Assert(reads==before+1&&queries-beforeQueries==2&&closed==0,"Only one local entry/list/show is checked; no install or application close");
                Assert(view.Inventory.Installed["7-zip"]=="3.0","Other saved installations remain unchanged");
                Assert(Math.Abs(info.Frame.ActualWidth-(size==1?420:580))<1,"Information-card width remains approved");
                foreach(var button in UnifiedDialogAppearance.Children<Button>(info.Frame).Where(b=>b.IsVisible)){var p=button.TranslatePoint(new Point(),info.Frame);Assert(p.X>=0&&p.X+button.ActualWidth<=info.Frame.ActualWidth+1,"Visible actions fit the frame");}
                if(!english&&entry==firefox&&((ComboBoxItem)((ComboBox)main.FindName("ThemeBox")).SelectedItem).Tag?.ToString()=="Standard")Save("Einzelpruefung-"+size);info.Close();
            }
            var themes=(ComboBox)main.FindName("ThemeBox");
            foreach(var theme in themes.Items.OfType<ComboBoxItem>().Where(t=>t.Tag is string).ToArray())
            {themes.SelectedItem=theme;foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})Check(firefox,english,size,true,"2.0");}
            themes.SelectedItem=themes.Items.OfType<ComboBoxItem>().First(t=>t.Tag?.ToString()=="Standard");
            Check(discord,false,1,true,null);Check(firefox,true,1,false,null);Check(online,false,1,false,null);

            page.SetLanguage(false);((ComboBox)main.FindName("SizeBox")).SelectedIndex=1;
            var versions=new Dictionary<string,string>{{discord.Id,"1.0"},{firefox.Id,"1.0"}};var ready=new HashSet<string>();var order=new List<string>();var errors=new List<AppLogError>();
            int installs=0,closes=0;bool reject=false,external=false;TaskCompletionSource<bool>? gate=null;
            var closing=new ProgramInstallService((start,_)=>
            {
                string id=start.ArgumentList[start.ArgumentList.IndexOf("--id")+1];var entry=new[]{discord,firefox}.Single(e=>e.PackageId==id);
                Assert(start.ArgumentList[0] is "list" or "show","Update must not fall back to global export");
                return Task.FromResult(new WinGetResult(0,start.ArgumentList[0]=="show"?"Found ["+id+"]\nVersion: 2.0":versions[entry.Id]=="1.0"?entry.Name+" "+id+" 1.0 2.0 winget":"No updates"));
            },(start,started)=>
            {string id=start.ArgumentList[start.ArgumentList.IndexOf("--id")+1];var entry=new[]{discord,firefox}.Single(e=>e.PackageId==id);Assert(ready.Contains(entry.Id),"No installer starts before its own application close completes");started();installs++;order.Add("install:"+entry.Id);versions[entry.Id]="2.0";return Task.FromResult(0);},
                localReader:(entries,_)=>Task.FromResult(entries.ToDictionary(e=>e.Id,e=>new ProgramInstallation(versions[e.Id],"Registry"))),
                closeApplications:async(entry,proof,token)=>
                {closes++;order.Add("close:"+entry.Id);Assert(proof?.Version==versions[entry.Id],"Close targets the freshly checked installation");if(reject)throw new IOException("Das Programm ist noch geöffnet oder konnte nicht geordnet beendet werden. Bitte Arbeit speichern und das Programm schließen; danach erneut versuchen.");if(gate is not null)await gate.Task.WaitAsync(token);ready.Add(entry.Id);if(external)versions[entry.Id]="2.0";return true;});
            view.Service=closing;view.ConfirmForTests=_=>true;view.WriteErrorLog=errors.Add;
            void SeedUpdates(){ready.Clear();order.Clear();versions[discord.Id]=versions[firefox.Id]="1.0";typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,
                new ProgramInventory(false,new Dictionary<string,string?>{{discord.Id,"1.0"},{firefox.Id,"1.0"}},Evidence:new Dictionary<string,ProgramInstallation>{{discord.Id,new("1.0","Registry")},{firefox.Id,new("1.0","Registry")}},IsStale:true,PendingUpdates:new Dictionary<string,string>{{discord.Id,"2.0"},{firefox.Id,"2.0"}}));}
            SeedUpdates();gate=new(TaskCreationOptions.RunContinuationsAsynchronously);var work=view.InstallEntriesAsync([discord],updatesOnly:true);Until(()=>closes==1);
            Assert(installs==0&&view.IsWorking,"Installer waits while the selected app is still closing");Save("Programm-wird-beendet");gate.SetResult(true);Wait(work);gate=null;
            Assert(installs==1&&versions[discord.Id]=="2.0"&&versions[firefox.Id]=="1.0"&&order.SequenceEqual(new[]{"close:discord","install:discord"}),"Close/recheck/install sequence affects only the selected app");
            SeedUpdates();reject=true;Wait(view.InstallEntriesAsync([discord],updatesOnly:true));reject=false;
            Assert(installs==1&&errors.Any(e=>e.Message.Contains("noch geöffnet"))&&versions[discord.Id]=="1.0","A refused close blocks the installer and reports the reason");
            SeedUpdates();external=true;Wait(view.InstallEntriesAsync([discord],updatesOnly:true));external=false;
            Assert(installs==1&&versions[discord.Id]=="2.0"&&view.UpdateTarget(discord.Id) is null,"An external update completed during closure skips the now-unneeded installer");
            SeedUpdates();gate=new(TaskCreationOptions.RunContinuationsAsynchronously);int beforeClose=closes;var stopped=view.InstallEntriesAsync([discord],updatesOnly:true);Until(()=>closes==beforeClose+1);view.RequestStop();Wait(stopped);gate=null;
            Assert(installs==1&&!view.IsWorking&&!view.SelectionOpen,"Cancellation while closing releases selection without starting an installer");
            SeedUpdates();Wait(view.InstallEntriesAsync([discord,firefox],updatesOnly:true));
            Assert(installs==3&&order.SequenceEqual(new[]{"close:discord","install:discord","close:firefox","install:firefox"}),"A queue closes each application immediately before its own update");
            string teamSpeakPath=Path.Combine(output,"ts3client_win64.exe");File.WriteAllText(teamSpeakPath,"Controlled fixture, never execute");bool teamClosed=false;
            view.Service=new ProgramInstallService((_,_)=>throw new Exception("TeamSpeak must not query generic packages"),(_,_)=>throw new Exception("No real installer"),
                closeApplications:(entry,proof,_)=>{Assert(entry.Id=="teamspeak"&&proof?.Path==teamSpeakPath&&proof.Version=="3.6.1","TeamSpeak closes only the chosen edition's actual copy");teamClosed=true;return Task.FromResult(true);});
            var tsInstaller=new TeamSpeakFixture(()=>teamClosed,teamSpeakPath);view.TeamSpeakInstaller=tsInstaller;
            view.ConfirmForTests=_=>throw new Exception("A single TeamSpeak update must not ask for selection confirmation");
            Wait(view.InstallTeamSpeakAsync(new(3,"3.6.2",TeamSpeakInstallKind.Update,new([new(3,"3.6.1",teamSpeakPath,"Registry")]),new(3,"3.6.1",teamSpeakPath,"Registry"))));
            Assert(teamClosed&&tsInstaller.Prepared==2&&tsInstaller.Applied==1,"TeamSpeak retains its own variant flow and revalidates after the controlled close");
            Console.WriteLine("PASS "+count+" all-program single-check and graceful-close update flow checks; seven themes, DE/EN, compact/large; controlled callbacks only");return 0;
        }
        finally{main.Close();Pump();}
    }
    private sealed class TeamSpeakFixture(Func<bool> closed,string path):ITeamSpeakInstaller
    {
        internal int Prepared,Applied;
        public Task<TeamSpeakInstallPlan> PrepareAsync(TeamSpeakInstallRequest request,CancellationToken token){Prepared++;return Task.FromResult(new TeamSpeakInstallPlan(request,TeamSpeakInstaller.Url(3,"3.6.2"),null,null));}
        public Task<TeamSpeakInstallResult> ApplyAsync(TeamSpeakInstallPlan plan,Action<ProgramStepEvent> step,Action<ProgramDownload> download,CancellationToken token)
        {if(!closed())throw new Exception("TeamSpeak installer started before close completed");Applied++;step(new(ProgramStep.Check,ProgramStepState.Done));step(new(ProgramStep.Install,ProgramStepState.Done));return Task.FromResult(new TeamSpeakInstallResult(0,true,path));}
    }
}
