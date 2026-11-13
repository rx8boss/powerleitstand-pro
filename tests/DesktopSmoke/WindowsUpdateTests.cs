using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class WindowsUpdateTests
{
    internal sealed class FakeService : IWindowsUpdateService
    {
        internal int Searches, Installs;
        internal WindowsUpdateRequest? LastRequest;
        internal Func<bool?,CancellationToken,Task<WindowsUpdateSearch>>? OnSearch;
        internal Func<WindowsUpdateRequest,Action<WindowsUpdateEvent>,CancellationToken,Task>? OnRun;
        internal bool PartialFailure;
        internal static WindowsUpdateItem Item(int n,bool driver=false,bool interactive=false) => new("00000000-0000-0000-0000-"+n.ToString("D12"),1,
            driver?"Netzwerkadapter · TESTDATEN":n==1?"Kumulatives Windows-Update · TESTDATEN":"Microsoft Defender · TESTDATEN",driver,"Test","Simulierter Eintrag",27.4*1024*1024,false,false,interactive,"Fixture EULA / Test-Lizenzbedingungen");
        public Task<WindowsUpdateSearch> SearchAsync(bool? drivers,CancellationToken token)
        {
            Searches++;
            return OnSearch?.Invoke(drivers,token) ?? Task.FromResult(new WindowsUpdateSearch(drivers switch
            {
                true => [Item(3, true)],
                false => [Item(1), Item(2)],
                null => [Item(1), Item(2), Item(3, true)]
            }, false));
        }
        public Task RunElevatedAsync(WindowsUpdateRequest request,Action<WindowsUpdateEvent> progress,CancellationToken token)
        {
            WindowsUpdatePolicy.Validate(request);LastRequest=request;Installs++;
            if(OnRun is not null)return OnRun(request,progress,token);
            progress(new("download",Percent:25));progress(new("install",Percent:75));
            foreach(var item in request.Selection)progress(new("item",PartialFailure&&item.Id.EndsWith("2")?"0x80004005":"0x00000000",Key:item.Id+":"+item.Revision,Code:PartialFailure&&item.Id.EndsWith("2")?4:2));
            progress(new("done",Code:PartialFailure?3:2,RebootRequired:true));return Task.CompletedTask;
        }
    }
    internal static int Run(App app,string output,bool cachedProbe)
    {
        UiSoundService.MuteForTests=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks=0;var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        void Assert(bool ok,string message){if(!ok)throw new Exception(message);checks++;}
        void Reject(Action action,string message){try{action();throw new Exception(message);}catch(InvalidOperationException){checks++;}}
        void Pump()=>app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        void Wait(Task task,int limit=15000){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.ElapsedMilliseconds<limit){Pump();Thread.Sleep(5);}if(!task.IsCompleted)throw new TimeoutException("Fixture timed out");task.GetAwaiter().GetResult();Pump();}
        CheckBox[] Boxes(WindowsUpdatesPage page)=>UnifiedDialogAppearance.Children<CheckBox>(page).Where(x=>x.Tag is string).ToArray();
        try
        {
            var item=FakeService.Item(1);var selected=new WindowsUpdateSelection(item.Id,item.Revision,item.EulaHash);
            WindowsUpdatePolicy.Validate(new("install",[selected]));
            foreach(var operation in new[]{"restore","restore-list","restart","unknown"})Reject(()=>WindowsUpdatePolicy.Validate(new(operation,[selected])),"Unsupported helper operation accepted");
            Reject(()=>WindowsUpdatePolicy.Validate(new("install",[])),"Empty install accepted");Reject(()=>WindowsUpdatePolicy.Validate(new("install",[selected,selected])),"Duplicate install accepted");
            Reject(()=>WindowsUpdatePolicy.Validate(new("install",[selected with{Id="invalid"}])),"Invalid ID accepted");
            Reject(()=>WindowsUpdatePolicy.VerifySelection([selected],[item with{Revision=2}]),"Revision changed");Reject(()=>WindowsUpdatePolicy.VerifySelection([selected],[item with{Eula="Changed EULA"}]),"EULA changed");Reject(()=>WindowsUpdatePolicy.VerifySelection([selected],[item with{RequiresUserInput=true}]),"Interactive item accepted");
            using(var json=JsonDocument.Parse(JsonSerializer.Serialize(new WindowsUpdateRequest("install",[selected]))))Assert(json.RootElement.EnumerateObject().Count()==2,"Helper contract contains only operation and selection");
            foreach(var name in new[]{"WindowsRestorePoints","RestorePointFailure","UnprotectedInstallWindow"})Assert(typeof(MainWindow).Assembly.GetType("PowerControlProWpf."+name) is null,"Removed component absent from built assembly: "+name);
            foreach(var theme in new[]{"Standard","Classic","Neon Blau","Neon Pink","Neon Grün","Linux Mint","Dark"})
            {
                var main=new MainWindow(Path.Combine(output,"Fixtures",theme)){ShowActivated=false};
                try
                {
                    var themes=(ComboBox)main.FindName("ThemeBox");themes.SelectedItem=themes.Items.OfType<ComboBoxItem>().Single(x=>x.Tag?.ToString()==theme);main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var service=new FakeService();page.Service=service;
                    main.ShowRepairPage();
                    var repairGroup = ((Panel)main.FindName("RepairToolGroups")).Children.OfType<Button>()
                        .Single(tile => Equals(tile.Tag, RepairTool.All.Single(tool => tool.Id == "dism").Group));
                    repairGroup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    main.ShowWindowsUpdatesPage();Pump();
                    foreach(bool english in new[]{false,true})
                    {
                        typeof(MainWindow).GetField("_english",flags)!.SetValue(main,english);typeof(MainWindow).GetMethod("ApplyLanguage",flags)!.Invoke(main,null);
                        var subtitle=(TextBlock)page.FindName("DriversSubtitle");var forum=subtitle.Inlines.OfType<Hyperlink>().Single();Uri? opened=null;page.OpenExternalLink=uri=>opened=uri;forum.RaiseEvent(new RequestNavigateEventArgs(WindowsUpdatesPage.NvidiaForumUrl,"Forum & Hotfix"));
                        Assert(AutomationProperties.GetName(forum)=="Forum & Hotfix"&&forum.NavigateUri==WindowsUpdatesPage.NvidiaForumUrl&&opened==WindowsUpdatesPage.NvidiaForumUrl,"Forum and hotfix link is active and opens the NVIDIA forum");
                        Assert(AutomationProperties.GetName(subtitle).Contains(english?"check compatibility":"Kompatibilität prüfen"),"Forum explanation follows selected language");
                        page.ConfirmForTests=_=>true;var searchCount=service.Searches;Wait(page.SearchAsync(false));Wait(page.SearchAsync(true));Assert(service.Searches==searchCount+2,"Windows and driver search use only the existing Windows Update service");Assert(typeof(MainWindow).Assembly.GetType("PowerControlProWpf.GpuHotfixService") is null,"Automatic NVIDIA/AMD hotfix service removed");Assert(service.Installs==0||service.LastRequest?.Operation=="install","Opening and searches do not launch other helper operations");
                        Assert(Boxes(page).Length==3&&page.SelectedKeys.Count==0,"Both categories available without preselection");
                        foreach(var box in Boxes(page))box.IsChecked=true;
                        var prior=service.Installs;page.ConfirmForTests=text=>{Assert(text.Contains("Fixture EULA"),"EULA remains in confirmation");return false;};Wait(page.InstallAsync());Assert(service.Installs==prior&&page.SelectedKeys.Count==3,"Declining install preserves selection");
                        page.ConfirmForTests=_=>true;service.PartialFailure=true;Wait(page.InstallAsync());Assert(service.LastRequest?.Operation=="install"&&service.LastRequest.Selection.Length==3&&service.Installs==prior+1,"Single install helper handles software and driver selection without prerequisites");
                        Assert(page.SoftwareItems.Count==1&&page.SoftwareItems[0].Id.EndsWith("2"),"Installed entries removed, failed entry retained");Assert(((TextBlock)page.FindName("OperationText")).Text.Contains(english?"restart":"Neustart",StringComparison.OrdinalIgnoreCase),"Reboot requirement displayed without rebooting");service.PartialFailure=false;
                        Wait(page.SearchAsync(false));var pending=new TaskCompletionSource<WindowsUpdateSearch>();service.OnSearch=(_,_)=>pending.Task;var active=page.SearchAsync(false);Pump();
                        try
                        {
                            Assert(main.IsBusy&&page.IsWorking&&!((Button)page.FindName("SearchUpdatesButton")).IsEnabled,"Search holds global busy lock");
                            var repairTiles = ((Panel)main.FindName("RepairToolGroups")).Children.OfType<Button>().ToArray();
                            Assert(!((Button)main.FindName("AdminRightsBadge")).IsEnabled && repairTiles.Length > 0
                                && repairTiles.All(tile => !tile.IsEnabled) && !((Button)main.FindName("ShutdownButton")).IsEnabled,
                                "Admin, repair and power locked during work");
                            page.CancelOperation();Pump();Assert(!active.IsCompleted&&main.IsBusy,"Cancel request alone does not release lock");
                        }
                        finally { pending.SetResult(new([],false));Wait(active); }
                        Assert(!main.IsBusy&&!page.IsWorking,"Actual completion releases lock");service.OnSearch=null;
                        Wait(page.SearchAsync(false));Boxes(page)[0].IsChecked=true;var finished=new TaskCompletionSource();service.OnRun=async(_,_,token)=>{await finished.Task;token.ThrowIfCancellationRequested();};active=page.InstallAsync();Pump();page.CancelOperation();Assert(main.IsBusy&&!active.IsCompleted,"Installer cancellation waits for process end");finished.SetResult();Wait(active);Assert(!main.IsBusy&&!page.IsWorking,"Installer lock released on actual end");service.OnRun=null;
                        service.OnSearch=(_,_)=>Task.FromException<WindowsUpdateSearch>(new InvalidOperationException("Fixture service unavailable"));Wait(page.SearchAsync(false));Assert(((TextBlock)page.FindName("SearchSubtitle")).Text.Contains("service unavailable")&&!main.IsBusy,"Search error recoverable");
                        service.OnSearch=(_,_)=>Task.FromResult(new WindowsUpdateSearch([FakeService.Item(1)],true));Wait(page.SearchAsync(false));Assert(!Boxes(page)[0].IsEnabled,"Partial search not installable");service.OnSearch=(_,_)=>Task.FromResult(new WindowsUpdateSearch([FakeService.Item(1,interactive:true)],false));Wait(page.SearchAsync(false));Assert(!Boxes(page)[0].IsEnabled,"Interactive update remains excluded");service.OnSearch=null;
                    }
                    Console.WriteLine("PASS update flow "+theme+": DE/EN, EULA, selected installs, retained errors, process locks and cancellation");
                }
                finally{main.Close();Pump();}
            }
            if(cachedProbe){using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(25));var probe=Task.Run(()=>WindowsUpdateAgent.Search(null,cancel.Token,online:false));try{Wait(probe,90000);Console.WriteLine("PASS native read-only combined cached WUA query; count="+probe.Result.Items.Length);}catch(Exception e){Console.WriteLine("LIMITATION native cached WUA query: "+e.Message);}}
            Console.WriteLine("PASS "+checks+" update-flow checks; fixtures only, no real OS changes.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
