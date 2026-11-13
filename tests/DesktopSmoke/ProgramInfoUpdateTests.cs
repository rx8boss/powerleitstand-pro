using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramInfoUpdateTests
{
    internal static int Run(App app, string output, bool logicOnly)
    {
        int count = 0; var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); count++; }
        void Fails(Action action, string reason) { try { action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { count++; return; } throw new Exception(reason); }
        var file = Path.Combine(output, "Files", "Chosen.exe"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopSmoke.exe"), file, true);
        var resource = FileVersionInfo.GetVersionInfo(file);
        var fixture = new ProgramEntry("fixture-program", resource.ProductName!, ["tools"], null, resource.CompanyName!, "Test", "Test", "https://example.invalid", "", null, null, "1");
        var storeFile = Path.Combine(output, "Saved", "Locations.json"); var files = new ProgramFileAssociations(storeFile);
        var proof = files.Save(fixture, file);
        Assert(proof.Path == file && proof.Source == "SelectedFile" && !proof.WinGetCompatible && proof.Version is not null, "Validated file and version without claiming an installation channel");
        var evidence = new Dictionary<string, ProgramInstallation>(); new ProgramFileAssociations(storeFile).Merge([fixture], evidence, CancellationToken.None);
        Assert(evidence.GetValueOrDefault(fixture.Id)?.Path == file, "Association survives a fresh store instance");
        evidence[fixture.Id] = new("9.9.9", "Windows-App", WinGetCompatible:false);
        files.Merge([fixture], evidence, CancellationToken.None);
        Assert(evidence[fixture.Id].Version == "9.9.9" && evidence[fixture.Id].Source == "Windows-App" && evidence[fixture.Id].Path == file, "Store package version preserved while path is filled");
        evidence[fixture.Id] = new(null, "Registry"); files.Merge([fixture], evidence, CancellationToken.None);
        Assert(evidence[fixture.Id].Version == proof.Version && evidence[fixture.Id].Source == "Registry", "Registered app missing its version gets a matching file version");
        Fails(() => files.Save(fixture with { Publisher = "Wrong Publisher" }, file), "Wrong publisher accepted");
        Fails(() => files.Save(fixture with { Name = "Different Product" }, file), "Wrong product accepted");
        var setup = Path.Combine(output, "Files", "setup.exe"); File.Copy(file, setup, true);
        Fails(() => files.Save(fixture, setup), "Setup accepted as main program");
        var invalid = Path.Combine(output, "Files", "invalid.exe"); File.WriteAllText(invalid, "not a Windows executable");
        Fails(() => files.Save(fixture, invalid), "Non-PE accepted");
        var library = Path.Combine(output, "Files", "library.exe"); File.Copy(typeof(ProgramInventory).Assembly.Location, library, true);
        Fails(() => files.Save(fixture, library), "Renamed DLL accepted");
        var before = File.ReadAllText(storeFile); Fails(() => files.Save(fixture, Path.Combine(output, "missing.exe")), "Missing file accepted");
        Assert(File.ReadAllText(storeFile) == before && !Directory.GetFiles(Path.GetDirectoryName(storeFile)!, "*.tmp").Any(), "Failure preserves stored associations and leaves no staging file");
        File.Move(file, file + ".moved", true); evidence.Clear(); files.Merge([fixture], evidence, CancellationToken.None);
        Assert(evidence.Count == 0, "Deleted/moved file is not treated as installed"); File.Move(file + ".moved", file);
        File.Copy(invalid, file, true); evidence.Clear(); files.Merge([fixture], evidence, CancellationToken.None);
        Assert(evidence.Count == 0, "Changed file is revalidated, never blindly trusted");
        var firefox = ProgramCatalog.Entries.Single(e => e.Id == "firefox");
        ProgramEntry[]? inspectedEntries = null; int targetedInstalls = 0;
        var targeted = new ProgramInstallService((start, _) => Task.FromResult(new WinGetResult(0, "Found [Mozilla.Firefox]\nVersion: 142.0\nHomepage: https://www.mozilla.org/")),
            (_, _) => { targetedInstalls++; return Task.FromResult(0); },
            localReader: (entries, _) => { inspectedEntries = entries; return Task.FromResult(new Dictionary<string, ProgramInstallation> { [firefox.Id] = new("141.0", "Registry", @"C:\\Program Files\\Mozilla Firefox\\firefox.exe") }); });
        var targetedResult = targeted.InspectEntryAsync(firefox, CancellationToken.None).GetAwaiter().GetResult();
        Assert(inspectedEntries is { Length: 1 } && inspectedEntries[0].Id == firefox.Id, "Single-program check scans only its selected catalog entry");
        Assert(targetedResult.Installation?.Version == "141.0" && targetedResult.Package?.Version == "142.0" && targetedResult.CheckedUtc != default,
            "Single-program check returns fresh local evidence, online package version and its own timestamp");
        Assert(targetedInstalls == 0, "Single-program check never invokes an installer");
        if (logicOnly) { Console.WriteLine("PASS " + count + " selected-file validation/persistence checks"); return 0; }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        var main = new MainWindow(Path.Combine(output, "UiFixture"), new AppSettings { Theme="Standard", SizeIndex=1 }) { ShowActivated=false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Wait(Task task) { var timer=Stopwatch.StartNew(); while(!task.IsCompleted && timer.Elapsed.TotalSeconds<15) { Pump(); Thread.Sleep(3); } if(!task.IsCompleted) throw new TimeoutException("UI fixture timed out"); task.GetAwaiter().GetResult(); Pump(); }
        void Save(string name) { Pump(); var bmp=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32); bmp.Render(main); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var stream=File.Create(Path.Combine(output,name+".png")); encoder.Save(stream); }
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view=page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons=false; var service=new InfoService(); view.Service=service;
            var entry=ProgramCatalog.Entries.Single(x=>x.Id=="powertoys"); var themes=(ComboBox)main.FindName("ThemeBox");
            foreach(var theme in themes.Items.OfType<ComboBoxItem>().Where(x=>x.Tag is string).ToArray())
            {
                themes.SelectedItem=theme;
                foreach(bool english in new[]{false,true}) foreach(int size in new[]{1,4})
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size; page.SetLanguage(english); view.SetCategory("tools"); Pump();
                    foreach(string state in new[]{"update","notice","unknown","saved","unverified"})
                    {
                        var installed=new Dictionary<string,string?>{[entry.Id]=state=="unknown"?null:"1.0"};
                        var ev=new Dictionary<string,ProgramInstallation>{[entry.Id]=new(installed[entry.Id],"Registry")};
                        var updates=state=="update"?new Dictionary<string,string>{{entry.Id,"2.0"}}:null;
                        typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,new ProgramInventory(true,installed,Updates:updates,UpdateCheckComplete:true,Evidence:ev,
                            IsStale:state=="saved",FromCache:state=="saved",UnverifiedInstalledIds:state=="unverified"?[entry.Id]:null));
                        var info=ProgramInfoCard.Open(view,entry,view.Tiles.First(x=>x.Tag?.ToString()==entry.Id),false)!; Wait(info.Loading);
                        var texts=UnifiedDialogAppearance.Children<TextBlock>(info.Frame).ToArray();
                        var installedText=texts.Single(t=>t.Name=="ProgramInfoInstalledVersion"); var available=texts.Single(t=>t.Name=="ProgramInfoAvailableVersion");
                        var action=UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name == "ProgramInfoUpdateAction");
                        var color=((SolidColorBrush)installedText.Foreground).Color;
                        Assert(state=="unknown"?installedText.Text==(english?"Could not be determined":"Konnte nicht ermittelt werden"):color.R>color.G*1.5,"Installed version red only when a newer numeric version is confirmed; unknown version explicit");
                        if(state!="unknown") { var next=((SolidColorBrush)available.Foreground).Color; Assert(next.G>next.R,"Available new version is green"); }
                        Assert(action.Visibility==(state is "update" or "notice" or "saved" or "unverified"?Visibility.Visible:Visibility.Collapsed),"Executable update, unconfirmed newer version or saved/unverified state gets the appropriate action");
                        if(state=="update") Assert(((TextBlock)action.Content).Text==(english?"Install update":"Update installieren"),"Update action is explicit in both languages");
                        if(state is "notice" or "saved" or "unverified") Assert(((TextBlock)action.Content).Text==(english?"Check again":"Erneut prüfen"),"Newer versions without an executable route, saved and unverified installations remain checkable");
                        Assert(UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoStatusReinstall").Visibility==Visibility.Visible,"Installed program retains independent reinstall action even with an update");
                        Assert(UnifiedDialogAppearance.Children<Button>(info.Frame).Single(b=>b.Name=="ProgramInfoUninstallAction").Visibility==Visibility.Visible,"Installed program has independent uninstall action");
                        Assert(UnifiedDialogAppearance.Children<Button>(info.Frame).Any(b=>b.Name=="ProgramInfoChooseFile" && b.Width==34),"Unlocated installation has compact file-picker button");
                        Assert(Math.Abs(info.Frame.ActualWidth-(size==1?420:580))<1 && info.Frame.ActualHeight<=540,"Approved compact/large dialog dimensions preserved");
                        foreach(var button in UnifiedDialogAppearance.Children<Button>(info.Frame).Where(b=>b.Visibility==Visibility.Visible))
                        { var p=button.TranslatePoint(new Point(),info.Frame); Assert(p.X>=0 && p.X+button.ActualWidth<=info.Frame.ActualWidth+1,"Visible action fits inside dialog"); }
                        if(action.IsVisible && action.Content is TextBlock caption){caption.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));Assert(caption.DesiredSize.Width+action.Padding.Left+action.Padding.Right<=action.ActualWidth+1,$"Check/install-update caption fits its button: {theme.Tag}, {english}, {size}, {state}, required {caption.DesiredSize.Width+action.Padding.Left+action.Padding.Right}, actual {action.ActualWidth}");}
                        if(theme.Tag?.ToString()=="Standard" && !english) Save("Info-"+state+"-"+size);
                        if(state=="update" && !english && size==1 && theme.Tag?.ToString()=="Standard")
                        { bool confirmation=false; view.ConfirmForTests=_=>{confirmation=true;return false;}; action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(!confirmation && !info.IsOpen && !view.IsWorking && service.Installs==0 && service.Updates==0,"Single update bypasses confirmation but a disappeared update still prevents execution"); view.ConfirmForTests=null; }
                        info.Close();
                    }
                }
            }
            view.SetCategory("tools"); themes.SelectedItem=themes.Items.OfType<ComboBoxItem>().First(x=>x.Tag?.ToString()=="Standard");
            ((ComboBox)main.FindName("SizeBox")).SelectedIndex=1; page.SetLanguage(false); Pump();
            var savedChecked=DateTime.UtcNow.AddHours(-2); int singleReads=0, singleQueries=0, fullReads=0, focusedInstalls=0; string localVersion="1.0"; bool failQuery=false;
            var focusedCacheFile=Path.Combine(output,"FocusedCheck.json");
            var focusedService=new ProgramInstallService((start, _) => { singleQueries++; if(failQuery) throw new IOException("Fixture online check failure"); return Task.FromResult(new WinGetResult(0,
                start.ArgumentList[0]=="list" ? ProgramVersions.IsNewer(localVersion,"2.0") ? "PowerToys Microsoft.PowerToys "+localVersion+" 2.0 winget" : "No updates" : "Found [Microsoft.PowerToys]\nVersion: 2.0\nHomepage: https://learn.microsoft.com/")); },
                (_,started)=>{focusedInstalls++;started();localVersion="2.0";return Task.FromResult(0);},
                cacheFile:focusedCacheFile, localReader:(entries, _) =>
                { if(entries.Length==1){singleReads++;Assert(entries[0].Id==entry.Id,"Info-card recheck reads only its selected program");}else fullReads++; return Task.FromResult(new Dictionary<string,ProgramInstallation>{{entry.Id,new(localVersion,"Registry",@"C:\\Program Files\\PowerToys\\PowerToys.exe")}}); });
            view.Service=focusedService;
            typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,new ProgramInventory(false,new Dictionary<string,string?>{{entry.Id,"1.0"}},
                Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,new("1.0","Registry",@"C:\\Program Files\\PowerToys\\PowerToys.exe")}},
                CheckedUtc:savedChecked,FromCache:true,IsStale:true));
            view.ShowInfo(entry,view.Tiles.First(x=>x.Tag?.ToString()==entry.Id),false);var focused=view.Info!;Wait(focused.Loading);
            var focusedUpdate=UnifiedDialogAppearance.Children<Button>(focused.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
            focusedUpdate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var checkClock=Stopwatch.StartNew();
            while(checkClock.Elapsed.TotalSeconds<15 && !UnifiedDialogAppearance.Children<TextBlock>(focused.Frame).Any(t=>t.Text.Contains("Einzelprüfung abgeschlossen"))) { Pump(); Thread.Sleep(4); }
            Assert(singleReads==1 && singleQueries==3,"Saved update action performs one local read and selected-package list/show queries");
            Assert(((TextBlock)focusedUpdate.Content).Text=="Update installieren" && focusedUpdate.IsEnabled,"Targeted confirmation exposes the update action without repeatedly checking the same saved state");
            Assert(view.Inventory.IsStale && view.Inventory.CheckedUtc==savedChecked && !view.Inventory.HasUpdate(entry.Id),"One program's result does not freshen the global snapshot or authorize updates");
            Assert(UnifiedDialogAppearance.Children<TextBlock>(focused.Frame).Any(t=>t.Name=="ProgramInfoInstalledStatus" && t.Text.Contains("einzeln geprüft")),"Info card shows the targeted local check result");
            bool updateConfirmation=false; view.ConfirmForTests=_=>{updateConfirmation=true;return false;}; focusedUpdate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var actionClock=Stopwatch.StartNew();while(actionClock.Elapsed.TotalSeconds<20 && (view.IsWorking || !view.CanChooseProgramFile)){Pump();Thread.Sleep(4);}
            Assert(fullReads==0 && !updateConfirmation && !focused.IsOpen && focusedInstalls==1 && localVersion=="2.0","Explicit single update runs only the selected controlled installer without another confirmation");
            view.CloseInfo();view.ConfirmForTests=null;localVersion="1.0";
            typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,new ProgramInventory(true,new Dictionary<string,string?>{{entry.Id,"1.0"}},UpdateCheckComplete:true,
                Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,new("1.0","Registry",@"C:\\Program Files\\PowerToys\\PowerToys.exe")}},CheckedUtc:DateTime.UtcNow));
            view.ShowInfo(entry,view,false);var freshNotice=view.Info!;Wait(freshNotice.Loading);
            var freshCheck=UnifiedDialogAppearance.Children<Button>(freshNotice.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");
            Assert(freshCheck.IsVisible && ((TextBlock)freshCheck.Content).Text=="Erneut prüfen","Fresh inventory with a newer online version but no confirmed update route offers a real check action");
            freshCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var freshClock=Stopwatch.StartNew();while(freshClock.Elapsed.TotalSeconds<15 && !UnifiedDialogAppearance.Children<TextBlock>(freshNotice.Frame).Any(t=>t.Text.Contains("Einzelprüfung abgeschlossen"))){Pump();Thread.Sleep(4);}
            Assert(((TextBlock)freshCheck.Content).Text=="Update installieren" && freshCheck.IsEnabled && view.Inventory.IsStale && !view.Inventory.HasUpdate(entry.Id),"Rechecking a fresh notice exposes an independently confirmed selected update without freshening unrelated inventory");
            freshNotice.Close();
            var cachedProof=new ProgramInstallation("1.0","Registry",@"C:\\Program Files\\PowerToys\\PowerToys.exe");
            var cache=new ProgramInspectionCache(focusedCacheFile);
            var originalSnapshot=new ProgramInventory(true,new Dictionary<string,string?>{{entry.Id,"1.0"}},Updates:new Dictionary<string,string>{{entry.Id,"2.0"}},UpdateCheckComplete:true,
                Evidence:new Dictionary<string,ProgramInstallation>{{entry.Id,cachedProof}},CheckedUtc:savedChecked);
            Assert(cache.Save(originalSnapshot,ProgramCatalog.Entries),"Seed a real display-only saved update notice");
            typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,ProgramInspectionCache.Display(cache.Read(ProgramCatalog.Entries,DateTime.UtcNow)!));localVersion="2.0";
            view.ShowInfo(entry,view,false);var current=view.Info!;Wait(current.Loading);
            var currentCheck=UnifiedDialogAppearance.Children<Button>(current.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");currentCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var currentClock=Stopwatch.StartNew();while(currentClock.Elapsed.TotalSeconds<15 && !UnifiedDialogAppearance.Children<TextBlock>(current.Frame).Any(t=>t.Text.Contains("aktuell · einzeln geprüft"))){Pump();Thread.Sleep(4);}
            Assert(view.Inventory.Installed[entry.Id]=="2.0" && !view.Inventory.HasUpdateNotice(entry.Id) && view.Inventory.IsStale,"An externally installed update clears its old notice without freshening unrelated applications");
            var savedCurrent=cache.Read(ProgramCatalog.Entries,DateTime.UtcNow)!;
            Assert(savedCurrent.Rows.Single(r=>r.Id==entry.Id).Version=="2.0" && savedCurrent.Rows.Single(r=>r.Id==entry.Id).Target is null && savedCurrent.CheckedUtc==savedChecked,"Corrected version and cleared notice survive restart with original full-check timestamp");
            Assert(((TextBlock)currentCheck.Content).Text=="Erneut prüfen" && currentCheck.IsEnabled,"Current application remains explicitly recheckable");current.Close();
            view.ShowInfo(entry,view,false);var failed=view.Info!;Wait(failed.Loading);failQuery=true;localVersion="3.0";
            var retry=UnifiedDialogAppearance.Children<Button>(failed.Frame).Single(b=>b.Name=="ProgramInfoUpdateAction");retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var errorClock=Stopwatch.StartNew();while(errorClock.Elapsed.TotalSeconds<15 && !UnifiedDialogAppearance.Children<TextBlock>(failed.Frame).Any(t=>t.Text=="Einzelprüfung fehlgeschlagen")){Pump();Thread.Sleep(4);}
            Assert(view.Inventory.Installed[entry.Id]=="2.0" && retry.IsEnabled && ((TextBlock)retry.Content).Text=="Erneut prüfen","Failed online check retains saved data and offers retry");
            failQuery=false;retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var retryClock=Stopwatch.StartNew();while(retryClock.Elapsed.TotalSeconds<15 && view.Inventory.Installed[entry.Id]!="3.0"){Pump();Thread.Sleep(4);}
            Assert(view.Inventory.Installed[entry.Id]=="3.0" && focusedInstalls==1,"Retry succeeds without starting another installer or downgrading an application");failed.Close();view.Service=service;
            File.Copy(Path.Combine(AppContext.BaseDirectory,"DesktopSmoke.exe"),file,true);
            var isolated = new ProgramCatalogView(page,new ProgramAssets(Path.Combine(output,"AssociationUi","App Icons")));
            isolated.Assets.AllowOnlineIcons=false; isolated.Service=service;
            ((Dictionary<string,ProgramEntry>)typeof(ProgramCatalogView).GetField("_sessionEntries",flags)!.GetValue(isolated)!)[fixture.Id]=fixture;
            Wait(isolated.RememberProgramFileAsync(fixture,file));
            Assert(isolated.Inventory.Evidence?.GetValueOrDefault(fixture.Id) is { Source:"SelectedFile",WinGetCompatible:false } && !isolated.SelectionOpen,"UI association pipeline records selected-file evidence and releases its lock");
            Assert(File.Exists(isolated.Assets.LocationsFile) && isolated.Inventory.Installed.GetValueOrDefault(fixture.Id)==proof.Version,"UI association persists outside the program and refreshes the version");
            typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(isolated,new ProgramInventory(true,new Dictionary<string,string?>{{fixture.Id,"9.9.9"}},Updates:new Dictionary<string,string>{{fixture.Id,"10.0"}},Evidence:new Dictionary<string,ProgramInstallation>{{fixture.Id,new("9.9.9","Windows-App",WinGetCompatible:false)}}));
            Wait(isolated.RememberProgramFileAsync(fixture,file));
            Assert(isolated.Inventory.Installed[fixture.Id]=="9.9.9" && isolated.Inventory.Evidence![fixture.Id].Source=="Windows-App" && isolated.Inventory.Updates?.ContainsKey(fixture.Id)!=true,"UI preserves Store version and invalidates obsolete update routing");
            isolated.HostBusy=()=>true; bool blocked=false;
            try { Wait(isolated.RememberProgramFileAsync(fixture,file)); } catch(InvalidOperationException) { blocked=true; }
            Assert(blocked,"File associations blocked while another operation is active");
            Assert(service.Installs==0 && service.Updates==0,"No real or fake installer executed during information checks");
            Console.WriteLine("PASS "+count+" file-association and program-info checks; seven themes, DE/EN, compact/large; controlled installer only, no real installer executed"); return 0;
        }
        finally { main.Close(); Pump(); }
    }
    sealed class InfoService : IProgramInstallService
    {
        internal int Installs,Updates;
        public Task<ProgramInventory> InspectAsync(CancellationToken token)=>Task.FromResult(new ProgramInventory(true,new Dictionary<string,string?>()));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry,CancellationToken token)=>Task.FromResult<ProgramPackage?>(new(entry.PackageId!,"2.0","Fixture",DateTime.UtcNow));
        public Task<int> InstallAsync(ProgramEntry entry,ProgramPackage package,Action started) { Installs++;return Task.FromResult(0); }
        public Task<int> UpgradeAsync(ProgramEntry entry,ProgramPackage package,Action started) { Updates++;return Task.FromResult(0); }
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query,CancellationToken token)=>Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public void OpenWebsite(ProgramEntry entry) { }
    }
}
