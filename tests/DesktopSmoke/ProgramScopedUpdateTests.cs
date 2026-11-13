using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramScopedUpdateTests
{
    internal static int WowUpLive(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        UiSoundService.MuteForTests = true;
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "wowup-cf");
        var main = new MainWindow(Path.Combine(output, "WowUpFixture"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Until(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(75)) { Pump(); Thread.Sleep(20); }
            if (!done()) throw new TimeoutException("WoWUp-CF dialog did not finish its check.");
        }
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump();
            var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            view.Service = new ProgramInstallService(install: (_, _) => throw new InvalidOperationException("Live verification is read-only."));
            var local = ProgramLocalInventory.Read([entry], CancellationToken.None)[entry.Id];
            var snapshot = new ProgramInventory(false, new Dictionary<string, string?> { [entry.Id] = local.Version },
                Evidence: new Dictionary<string, ProgramInstallation> { [entry.Id] = local }, IsStale: true, CheckedUtc: DateTime.UtcNow.AddHours(-1));
            typeof(ProgramCatalogView).GetProperty("Inventory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, snapshot);
            view.ShowInfo(entry, view, false); var info = view.Info!;
            Until(() => info.Loading.IsCompleted);
            var button = UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b => b.Name == "ProgramInfoUpdateAction");
            if (((TextBlock)button.Content).Text != "Erneut prüfen") throw new InvalidOperationException("Expected saved-state recheck.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => button.IsEnabled);
            if (((TextBlock)button.Content).Text != "Update installieren")
                throw new InvalidOperationException(string.Join(" | ", UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Where(t => t.IsVisible).Select(t => t.Text)));
            Pump();
            var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(main);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, "WoWUp-CF-Update.png")); png.Save(file);
            Console.WriteLine($"PASS WoWUp-CF {local.Version}: real one-app check enabled Install update. No installer executed.");
            return 0;
        }
        finally { if (main.IsLoaded) main.Close(); Pump(); }
    }

    internal static int Ui(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        int checks = 0, installs = 0, localReads = 0, lists = 0, confirmations = 0, changeTargetAtRead = 0; string available = "2.0"; bool failedSource = false;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var discord = ProgramCatalog.Entries.Single(e => e.PackageId == "Discord.Discord");
        var firefox = ProgramCatalog.Entries.Single(e => e.Id == "firefox");
        var versions = new Dictionary<string,string> { [discord.Id] = "1.0", [firefox.Id] = "1.0" };
        TaskCompletionSource? installerGate = null;
        string? newerAfterInstall = null;
        ProgramCatalogView? openedView = null;
        var allowed = new HashSet<string> { discord.Id };
        var oldTime = DateTime.UtcNow.AddHours(-2);
        var main = new MainWindow(Path.Combine(output, "Fixture"), new AppSettings { Theme="Standard", SizeIndex=1 }) { ShowActivated=false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Until(Func<bool> condition) { var timer=Stopwatch.StartNew(); while(!condition() && timer.Elapsed.TotalSeconds<25){Pump();Thread.Sleep(4);} Assert(condition(),"Selected-update UI did not reach its expected state"); }
        void Wait(Task task) { Until(()=>task.IsCompleted);task.GetAwaiter().GetResult();Pump(); }
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);openedView=view;view.Assets.AllowOnlineIcons=false;
            var logs=new List<string>();view.WriteLog=logs.Add;
            var service=new ProgramInstallService((start, _) =>
            {
                Assert(start.ArgumentList[0] is "list" or "show","Selected UI action must never export or scan all programs");
                int key=start.ArgumentList.IndexOf("--id");Assert(key>=0 && start.ArgumentList.Contains("--exact"),"Read-only package identity is exact");
                var entry=new[]{discord,firefox}.Single(e=>e.PackageId==start.ArgumentList[key+1]);Assert(allowed.Contains(entry.Id),"No unrelated program may be queried");
                if(failedSource) return Task.FromResult(new WinGetResult(unchecked((int)0x8A15000F),"Fixture source unavailable"));
                if(start.ArgumentList[0]=="show") return Task.FromResult(new WinGetResult(0,"Found ["+entry.PackageId+"]\nVersion: "+available));
                lists++;return Task.FromResult(new WinGetResult(0,ProgramVersions.IsNewer(versions[entry.Id],available)?entry.Name+" "+entry.PackageId+" "+versions[entry.Id]+" "+available+" winget":"No updates"));
            },async(start,started)=>
            {
                Assert(start.ArgumentList[0]=="upgrade" && start.ArgumentList.Contains("--silent") && start.ArgumentList.Contains("--version")
                    && !start.ArgumentList.Contains("--all") && !start.ArgumentList.Contains("--force"),"Only an exact automatic update can be executed");
                var id=start.ArgumentList[start.ArgumentList.IndexOf("--id")+1];var entry=new[]{discord,firefox}.Single(e=>e.PackageId==id);
                Assert(allowed.Contains(entry.Id),"Only a selected installer may be called");started();installs++;
                if(installerGate is not null) await installerGate.Task;
                versions[entry.Id]=start.ArgumentList[start.ArgumentList.IndexOf("--version")+1];
                if(newerAfterInstall is { } newer){available=newer;newerAfterInstall=null;}return 0;
            },cacheFile:Path.Combine(output,"DisplayCache.json"),localReader:(entries,_)=>
            {
                localReads++;if(localReads==changeTargetAtRead)available="3.0";Assert(entries.Length>0 && entries.All(e=>allowed.Contains(e.Id)),"Local inventory is bounded to the update selection");
                return Task.FromResult(entries.ToDictionary(e=>e.Id,e=>new ProgramInstallation(versions[e.Id],"Registry")));
            });
            view.Service=service;view.ConfirmForTests=_=>{confirmations++;return true;};
            void Seed()
            {
                typeof(ProgramCatalogView).GetProperty("Inventory",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,
                    new ProgramInventory(false,new Dictionary<string,string?>{{discord.Id,"1.0"},{firefox.Id,"1.0"}},
                        Evidence:new Dictionary<string,ProgramInstallation>{{discord.Id,new("1.0","Registry")},{firefox.Id,new("1.0","Registry")}},
                        CheckedUtc:oldTime,IsStale:true,FromCache:true,PendingUpdates:new Dictionary<string,string>{{discord.Id,"2.0"},{firefox.Id,"2.0"}}));
            }
            Seed();view.ShowInfo(discord,view,false);var info=view.Info!;Wait(info.Loading);
            var action=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>action.IsEnabled && ((TextBlock)action.Content).Text=="Update installieren");
            Assert(localReads==1 && lists==1,"Check-again queries only Discord, not the entire catalog");
            installerGate=new(TaskCreationOptions.RunContinuationsAsynchronously);action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Until(()=>installs==1);
            int selectionCount=view.Selected.Count;view.Select(firefox.Id,true);
            Assert(view.IsWorking && view.Selected.Count==selectionCount,"Selection remains locked until the controlled installer has actually exited");
            installerGate.SetResult();Until(()=>!view.IsWorking);installerGate=null;
            Assert(versions[discord.Id]=="2.0" && view.Inventory.Installed[discord.Id]=="2.0","Actual shared information-button flow reaches a controlled update and result verification");
            Assert(confirmations==0,"A verified individual information-button update never asks for selection confirmation");
            Assert(view.Inventory.IsStale && view.Inventory.CheckedUtc==oldTime && view.Inventory.Installed[firefox.Id]=="1.0","Unrelated programs retain their original saved state");
            Assert(!view.Inventory.HasUpdateNotice(discord.Id) && view.Inventory.HasUpdateNotice(firefox.Id),"Completed update clears only its own old notice");
            Assert(logs.Any(t=>t.Contains("Installation/Update bestätigt")),"Update completes with verified result instead of returning to another full scan");
            int beforeBackground=localReads;Wait(view.CheckInBackgroundAsync(DateTime.UtcNow.AddHours(1)));
            Assert(localReads==beforeBackground && !view.BackgroundChecking,"An open update result blocks unrelated periodic background scans");
            versions[discord.Id]="1.0";Seed();allowed.Add(firefox.Id);view.ConfirmForTests=_=>{confirmations++;return false;};Wait(view.InstallEntriesAsync([discord,firefox],updatesOnly:true));allowed.Remove(firefox.Id);
            Assert(confirmations==1 && installs==1 && !view.IsWorking && !view.SelectionOpen && view.OperationPanel is null,"Batch cancellation still requires confirmation and releases selection without an installer");
            Seed();changeTargetAtRead=localReads+2;view.ConfirmForTests=_=>throw new Exception("An individual update must not ask for confirmation");Wait(view.InstallEntriesAsync([discord],updatesOnly:true));changeTargetAtRead=0;
            Assert(installs==1 && logs.Any(t=>t.Contains("Updateversion nicht mehr bestätigt")),"A target changed between preparation and execution blocks the direct individual update");
            available="2.0";versions[discord.Id]="2.0";Seed();view.ConfirmForTests=_=>true;Wait(view.InstallEntriesAsync([discord],updatesOnly:true));
            Assert(installs==1 && view.Inventory.Installed[discord.Id]=="2.0" && !view.Inventory.HasUpdateNotice(discord.Id),"An externally updated program is recognized and its old candidate is cleared");
            versions[discord.Id]="1.0";Seed();failedSource=true;Wait(view.InstallEntriesAsync([discord],updatesOnly:true));
            Assert(installs==1 && !view.IsWorking && !view.SelectionOpen,"Source failure cannot launch an installer or leave selection locked");failedSource=false;
            Seed();int readsBeforeList=localReads;var choose=view.ChooseUpdatesAsync();var root=(Grid)main.FindName("Root");
            Until(()=>UnifiedDialogAppearance.Children<Grid>(root).Any(g=>g.Name=="ProgramUpdatesOverlay"));
            Assert(localReads==readsBeforeList,"Opening the common update picker does not start a full or selected scan before a choice is made");
            var picker=UnifiedDialogAppearance.Children<Grid>(root).Single(g=>g.Name=="ProgramUpdatesOverlay");
            UnifiedDialogAppearance.Children<CheckBox>(picker).Single(c=>c.Tag?.ToString()==discord.Id).IsChecked=true;
            UnifiedDialogAppearance.Children<Button>(picker).Single(b=>b.Name=="ProgramUpdatesStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Wait(choose);
            Assert(installs==2 && versions[discord.Id]=="2.0" && versions[firefox.Id]=="1.0","Common update picker executes only the selected cached candidate");
            versions[discord.Id]="1.0";
            allowed.Add(firefox.Id);versions[firefox.Id]="2.0";Seed();int beforeFiltered=confirmations;view.ConfirmForTests=_=>{confirmations++;return false;};Wait(view.InstallEntriesAsync([discord,firefox],updatesOnly:true));
            Assert(confirmations==beforeFiltered+1&&installs==2&&versions[discord.Id]=="1.0","An originally multiple selection still requires confirmation when only one update remains");
            versions[firefox.Id]="1.0";Seed();view.ConfirmForTests=_=>{confirmations++;return true;};int beforeBatch=confirmations;Wait(view.InstallEntriesAsync([discord,firefox],updatesOnly:true));
            Assert(confirmations==beforeBatch+1,"A batch update asks for exactly one selection confirmation");
            Assert(installs==4 && versions.Values.All(v=>v=="2.0") && view.Inventory.Installed[discord.Id]=="2.0" && view.Inventory.Installed[firefox.Id]=="2.0","Batch update validates, executes and verifies every chosen program in sequence");
            versions[discord.Id]=versions[firefox.Id]="1.0";Seed();installerGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopped=view.InstallEntriesAsync([discord,firefox],updatesOnly:true);Until(()=>installs==5);view.RequestStop();Pump();
            Assert(view.IsWorking && !stopped.IsCompleted,"Stopping the queue waits for the running installer");installerGate.SetResult();Wait(stopped);installerGate=null;
            Assert(installs==5 && versions[discord.Id]=="2.0" && versions[firefox.Id]=="1.0" && !view.SelectionOpen,"Queue stop completes the current update without starting the next program");
            versions[discord.Id]="1.0";Seed();newerAfterInstall="3.0";Wait(view.InstallEntriesAsync([discord],updatesOnly:true));
            Assert(installs==6 && versions[discord.Id]=="2.0" && view.Inventory.PendingUpdates?.GetValueOrDefault(discord.Id)=="3.0","A newer offer discovered during result verification is retained rather than erased by the completed older target");
            foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})
            {
                page.SetLanguage(english);((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;available="2.0";versions[discord.Id]="1.0";Seed();
                view.ConfirmForTests=null;int prior=installs;Wait(view.InstallEntriesAsync([discord],updatesOnly:true));
                Assert(installs==prior+1&&!view.SelectionOpen&&!UnifiedDialogAppearance.Children<Grid>(root).Any(g=>Panel.GetZIndex(g)==6000),"The real single-update route has no confirmation overlay in compact/large DE/EN");
            }
            var managedProof=new ProgramInstallation("1.0","Scoop",WinGetCompatible:false,
                ManagedCopies:[new("Scoop","7zip","2.0",output,"User",null,output)]);
            typeof(ProgramCatalogView).GetProperty("Inventory",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,
                new ProgramInventory(true,new Dictionary<string,string?>{{"7-zip","1.0"}},Evidence:new Dictionary<string,ProgramInstallation>{{"7-zip",managedProof}}));
            view.VersionChecked("7-zip","2.0");Assert(view.UpdateTarget("7-zip") is null,"Current manager package must not become an update merely because its executable has another version number");
            Console.WriteLine("PASS "+checks+" selected-update UI checks; controlled installers only, no real program changed");return 0;
        }
        finally
        {
            if(openedView?.IsWorking==true) openedView.RequestStop();installerGate?.TrySetResult();
            var cleanup=Stopwatch.StartNew();while(openedView?.IsWorking==true && cleanup.Elapsed.TotalSeconds<10){Pump();Thread.Sleep(4);}
            if(main.IsLoaded) main.Close();Pump();
        }
    }

    internal static int Logic(string output)
    {
        int checks = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        var firefox = ProgramCatalog.Entries.Single(e => e.Id == "firefox");
        var discord = ProgramCatalog.Entries.Single(e => e.PackageId == "Discord.Discord");
        var chrome = ProgramCatalog.Entries.Single(e => e.Id == "google-chrome");
        var requested = new List<string>(); int localReads = 0, installs = 0; bool noOffer = false, fail = false, otherChannel = false; string localVersion = "1.0";
        ProgramEntry[] expected = [discord];
        var cacheFile = Path.Combine(output, "Scoped-cache.json");
        var service = new ProgramInstallService((start, _) =>
        {
            Assert(start.ArgumentList[0] == "list" && start.ArgumentList.Contains("--id") && start.ArgumentList.Contains("--exact")
                && start.ArgumentList.Contains("--upgrade-available") && !start.ArgumentList.Contains("--include-pinned")
                && !start.ArgumentList.Contains("--include-unknown"), "Selection check uses only filtered read-only list queries");
            var id = start.ArgumentList[start.ArgumentList.IndexOf("--id") + 1]; requested.Add(id);
            Assert(expected.Any(e => e.PackageId == id || e.Id == "google-chrome" && id == "Google.Chrome.EXE"), "Query is confined to selected package identities");
            if (fail) return Task.FromResult(new WinGetResult(unchecked((int)0x8A15000F), "Source unavailable"));
            if (noOffer || id == "Google.Chrome") return Task.FromResult(new WinGetResult(unchecked((int)0x8A150014), "No matching applications"));
            return Task.FromResult(new WinGetResult(0, "Name Id Version Available Source\nApp " + id + " 1.0 2.0 winget\nUnselected Mozilla.Firefox 1.0 9.0 winget"));
        }, (_, _) => { installs++; throw new Exception("Information check cannot execute an installer"); }, cacheFile: cacheFile,
            localReader: (entries, _) =>
            {
                localReads++; Assert(entries.Select(e => e.Id).SequenceEqual(expected.Select(e => e.Id)), "Only selected local applications are inspected");
                return Task.FromResult(entries.ToDictionary(e => e.Id, e => new ProgramInstallation(localVersion, otherChannel ? "Windows-App" : "Registry", WinGetCompatible: !otherChannel)));
            });
        var result = service.InspectUpdatesAsync(expected, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.HasUpdate(discord.Id) && !result.Installed.ContainsKey(firefox.Id), "Discord update is executable without importing unrelated rows");
        Assert(result.Complete && result.UpdateCheckComplete && localReads == 1, "Selected inventory is complete for its own scope");
        Assert(!File.Exists(cacheFile) && !File.Exists(cacheFile + ".partial"), "Selected check cannot overwrite the complete display snapshot");
        expected = [discord, chrome]; requested.Clear();
        result = service.InspectUpdatesAsync(expected, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.HasUpdate(discord.Id) && result.HasUpdate(chrome.Id) && result.Installed.Count == 2, "Batch checks only chosen applications and supports Chrome EXE identity");
        var command = service.UpgradeCommand(chrome, new("Google.Chrome.EXE", "2.0", "", DateTime.UtcNow));
        Assert(command.ArgumentList.Contains("Google.Chrome.EXE") && command.ArgumentList.Contains("--version") && !command.ArgumentList.Contains("--all"), "Confirmed alias is used for exact version-bound execution");
        expected = [discord]; noOffer = true;
        result = service.InspectUpdatesAsync(expected, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.HasUpdate(discord.Id) && result.Installed[discord.Id] == "1.0", "Absent, current or pinned rows cannot fabricate an executable offer");
        noOffer = false; localVersion = "1.5";
        result = service.InspectUpdatesAsync(expected, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.HasUpdate(discord.Id) && result.Installed[discord.Id] == "1.5", "Different local and package versions cannot be silently rebound to an update");
        localVersion = "1.0"; otherChannel = true; requested.Clear();
        result = service.InspectUpdatesAsync(expected, CancellationToken.None).GetAwaiter().GetResult();
        Assert(requested.Count == 0 && !result.HasUpdate(discord.Id), "Store or other non-WinGet copies cannot be converted into a WinGet update");otherChannel = false;
        noOffer = false; fail = true;
        result = service.InspectUpdatesAsync(expected, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.HasUpdate(discord.Id) && !result.UpdateCheckComplete && result.Error is not null, "Source failure remains visible and cannot authorize updates");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { service.InspectUpdatesAsync(expected, cancelled.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks++; }
        try { service.InspectUpdatesAsync([discord with { PackageId = "Other.Package" }], CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Altered package accepted"); }
        catch (InvalidOperationException) { checks++; }
        var catalog = ProgramCatalog.Entries;
        var versionProbe = new ProgramInstallService((start, _) =>
        {
            string id = start.ArgumentList[start.ArgumentList.IndexOf("--id") + 1];
            bool pinned = start.ArgumentList.Contains("--version");
            if (pinned) Assert(start.ArgumentList[^1] == "2.0", "Exact lookup requests the checked upgrade target");
            return Task.FromResult(new WinGetResult(0, $"Found [{id}]\nVersion: {(pinned ? "2.0" : "3.0")}"));
        });
        foreach (var entry in catalog)
        {
            var scope = new ProgramInventory(true, new Dictionary<string, string?> { [entry.Id] = "1.0" },
                Updates: new Dictionary<string, string> { [entry.Id] = "2.0" },
                Evidence: new Dictionary<string, ProgramInstallation> { [entry.Id] = new("1.0", "Registry") });
            var offer = versionProbe.ResolveCheckedUpdateAsync(entry, scope, CancellationToken.None).GetAwaiter().GetResult();
            if (entry.PackageId is not null && entry.Id != "extra-electronicarts-eadesktop")
            {
                Assert(offer?.Id == entry.PackageId && ProgramInstallService.CheckedUpdateMatches(entry, offer, scope), "Exact update handoff for " + entry.Id);
                Assert(!ProgramInstallService.CheckedUpdateMatches(entry, offer, scope with { IsStale = true }), "Saved offer cannot authorize " + entry.Id);
            }
            else Assert(offer is null, "No fabricated WinGet route for " + entry.Id);
        }
        Assert(installs == 0, "Read-only selected checks never launch installers");
        Console.WriteLine("PASS " + checks + " selected-update backend checks; no installer executed"); return 0;
    }
}
