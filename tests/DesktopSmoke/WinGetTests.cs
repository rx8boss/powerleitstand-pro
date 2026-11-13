using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class WinGetTests
{
    internal static int ProbeOnly() => Task.Run(async () =>
    {
        var service=new WinGetService();
        var ready=await service.ProbeAsync(CancellationToken.None);Console.WriteLine("Read-only application backend: WinGet ready = "+ready);
        if(!ready)return 2;
        var package=await service.ResolveAsync("kingston",CancellationToken.None);
        Console.WriteLine("Read-only exact package lookup: "+(package?.Id??"unavailable"));
        return package is null?3:0;
    }).GetAwaiter().GetResult();
    internal sealed class Fake : IWinGetService
    {
        internal bool Ready = true, Package = true;
        internal int Started, Setup, Code;
        internal TaskCompletionSource<int>? Pending;
        public Task<bool> ProbeAsync(CancellationToken token) => Task.FromResult(Ready);
        public Task<WinGetPackage?> ResolveAsync(string id, CancellationToken token) => Task.FromResult(Package ? new WinGetPackage(WinGetService.PackageId(id)!, "Testpaket") : null);
        public Task<int> InstallAsync(string id, Action started) { Started++; started(); return Pending?.Task ?? Task.FromResult(Code); }
        public void OpenSetup() => Setup++;
    }
    internal sealed class Vendor : IManufacturerService
    {
        internal bool Hardware = true;
        internal bool Installed;
        internal int Sites;
        public Task<bool> CheckWinGetHardwareAsync(ManufacturerApp app, CancellationToken token) => Task.FromResult(Hardware);
        public Task<IReadOnlyDictionary<string, ManufacturerState>> InspectAsync() => Task.FromResult<IReadOnlyDictionary<string, ManufacturerState>>(ManufacturerCatalog.Apps.ToDictionary(x => x.Id, x => Installed ? new ManufacturerState(ManufacturerInstallState.Installed, "Fixture.exe") : new ManufacturerState(ManufacturerInstallState.Missing)));
        public Task<ManufacturerDownload?> ResolveAsync(ManufacturerApp app, CancellationToken token) => throw new Exception("No direct download expected");
        public Task<string> DownloadAsync(ManufacturerApp app, ManufacturerDownload download, IProgress<ManufacturerProgress> progress, CancellationToken token) => throw new Exception("No download expected");
        public Task LaunchAsync(ManufacturerApp app, string file, bool installer, Action? onStarted = null) => throw new Exception("No launch expected");
        public void OpenWebsite(ManufacturerApp app) => Sites++;
    }
    internal static int Run(App app, string output)
    {
        int checks = 0; var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Assert(bool condition, string text) { if (!condition) throw new Exception(text); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var timer=Stopwatch.StartNew(); while(!task.IsCompleted && timer.ElapsedMilliseconds<15000){Pump();Thread.Sleep(5);} if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();Pump(); }
        void Save(Window window, string name)
        {
            window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);
        }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));UiSoundService.MuteForTests=true;
        try
        {
            var queried=new List<ProcessStartInfo>(); var installs=0;
            var backend=new WinGetService((start,token)=>{queried.Add(start);return Task.FromResult(new WinGetResult(0,start.ArgumentList.Contains("--version")?"v1.29.380":"Found [Kingston.SSDManager]"));},(start,started)=>{queried.Add(start);installs++;started();return Task.FromResult(0);});
            var probe=backend.ProbeAsync(CancellationToken.None);Wait(probe);Assert(probe.Result,"Probe verifies version");
            var exact=backend.ResolveAsync("kingston",CancellationToken.None);Wait(exact);Assert(exact.Result?.Id=="Kingston.SSDManager","Exact package matches");
            var wrong=backend.ResolveAsync("intel",CancellationToken.None);Wait(wrong);Assert(wrong.Result is null,"Mismatched result rejected");
            try{backend.Command("evil --id Other",true);throw new Exception("Untrusted identifier allowed");}catch(InvalidOperationException){checks++;}
            foreach(var id in new[]{"kingston","intel","msi","sandisk"})
            {
                var info=backend.Command(id,true);var args=info.ArgumentList.ToArray();
                Assert(Path.IsPathFullyQualified(info.FileName)&&Path.GetFileName(info.FileName)=="winget.exe","Fixed absolute WinGet path");
                Assert(args.SequenceEqual(new[]{"install","--id",WinGetService.PackageId(id)!,"--exact","--source","winget","--interactive"}),"No shell, fuzzy match, acceptance or bypass flags");
            }
            Assert(new[]{"nvidia","amd","asus","gigabyte","samsung"}.All(id=>WinGetService.PackageId(id)is null),"Unavailable products use official links");
            var bad=new WinGetService((_,_)=>Task.FromResult(new WinGetResult(0,"not a version")));var badProbe=bad.ProbeAsync(CancellationToken.None);Wait(badProbe);Assert(!badProbe.Result,"Alias without valid version rejected");
            var timed=new WinGetService((_,_)=>Task.FromException<WinGetResult>(new OperationCanceledException()));var timedProbe=timed.ProbeAsync(CancellationToken.None);Wait(timedProbe);Assert(!timedProbe.Result,"Timed out probe reports unavailable");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{Wait(timed.ProbeAsync(cancelled.Token));throw new Exception("Cancellation swallowed");}catch(OperationCanceledException){checks++;}}
            var disk=new ManufacturerDisk("KINGSTON SKC3000S1024G",17);
            var compatible=new ManufacturerService(readHardware:()=>new(Architecture.X64,new Version(10,0,22631),true,"Intel UHD Graphics 770",true,"MSI","PRO Z790-A"),readDisks:_=>Task.FromResult<IReadOnlyList<ManufacturerDisk>>([disk]));
            foreach(var id in new[]{"kingston","msi"}){var check=compatible.CheckWinGetHardwareAsync(ManufacturerCatalog.Apps.Single(x=>x.Id==id),CancellationToken.None);Wait(check);Assert(check.Result,"Existing hardware policy reused "+id);}
            var unsupported=new ManufacturerService(readHardware:()=>new(Architecture.Arm64,new Version(10,0,22631),true,"",true));var reject=unsupported.CheckWinGetHardwareAsync(ManufacturerCatalog.Apps[2],CancellationToken.None);Wait(reject);Assert(!reject.Result,"Unsupported architecture rejected");
            foreach(var theme in new[]{"Standard","Classic","Neon Blau","Neon Pink","Neon Grün","Linux Mint","Dark"})
            {
                var main=new MainWindow(Path.Combine(output,"Fixtures",theme)){ShowActivated=false};
                try
                {
                    var themes=(ComboBox)main.FindName("ThemeBox");themes.SelectedItem=themes.Items.OfType<ComboBoxItem>().Single(x=>x.Tag?.ToString()==theme);
                    main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var fake=new Fake();page.WinGet=fake;Wait(page.RefreshWinGetAsync());
                    var vendor=new Vendor();var view=page.ShowManufacturerSoftware(false);view.Service=vendor;Wait(view.RefreshAsync());
                    foreach(var english in new[]{false,true})
                    {
                        typeof(MainWindow).GetField("_english",flags)!.SetValue(main,english);typeof(MainWindow).GetMethod("ApplyLanguage",flags)!.Invoke(main,null);
                        foreach(var size in new[]{4,1,2,3,0})
                        {
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;page.ShowUpdateOverview();Pump();
                            var button=(Button)page.FindName("WinGetButton");var install=(Button)page.FindName("InstallUpdatesButton");
                            foreach(var action in new[]{button})
                            {
                                var rect=action.TransformToAncestor(page).TransformBounds(new Rect(action.RenderSize));Assert(rect.Right<=page.ActualWidth+1&&rect.Bottom<=page.ActualHeight+1,"Bottom actions inside viewport "+theme+english+size);
                                foreach(var label in UnifiedDialogAppearance.Children<TextBlock>(action)){var r=label.TransformToAncestor(action).TransformBounds(new Rect(label.RenderSize));Assert(r.Right<=action.ActualWidth+1,"Caption fits");}
                            }
                            Assert(button.ToolTip is ToolTip,"Custom explanation replaces plain white tooltip");
                            fake.Ready=false;Wait(page.RefreshWinGetAsync());Assert(!view.PrimaryButtons["kingston"].IsEnabled&&view.PrimaryButtons["amd"].IsEnabled,"Only WinGet installations blocked when missing");
                            var sites=vendor.Sites;Wait(view.ActivateAsync(ManufacturerCatalog.Apps.Single(x=>x.Id=="gigabyte")));Assert(vendor.Sites==sites+1,"Fallback opens official manufacturer website without installation");
                            if(theme=="Standard"&&!english&&size is 4 or 1){((TextBlock)main.FindName("InstalledVersionText")).Text="TESTDARSTELLUNG";Save(main,size==4?"WinGet-Uebersicht-Standard":"WinGet-Uebersicht-Kompakt");}
                            var help=(ToolTip)button.ToolTip;help.PlacementTarget=button;help.IsOpen=true;Pump();
                            var popupPosition=main.PointFromScreen(help.PointToScreen(new Point()));Assert(popupPosition.X>=-1&&popupPosition.X+help.ActualWidth<=main.ActualWidth+1,"Hover remains horizontally inside app");
                            var surface=(Border)help.Content;var heading=((StackPanel)surface.Child).Children.OfType<TextBlock>().First();
                            Assert(heading.Foreground.ToString()==((Brush)page.FindResource("TextBrush")).ToString(),"Hover uses current design text colour");
                            Assert(surface.Background.ToString()==((Brush)page.FindResource("StandardReferenceStatusBrush")).ToString(),"Hover uses current design background");
                            if(theme=="Standard"&&!english&&size==1)
                            {
                                main.UpdateLayout();help.UpdateLayout();
                                var mainImage=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);mainImage.Render(main);
                                var tipImage=new RenderTargetBitmap((int)Math.Ceiling(help.ActualWidth),(int)Math.Ceiling(help.ActualHeight),96,96,PixelFormats.Pbgra32);tipImage.Render(help);
                                var position=main.PointFromScreen(help.PointToScreen(new Point()));var visual=new DrawingVisual();
                                using(var drawing=visual.RenderOpen()){drawing.DrawImage(mainImage,new Rect(0,0,main.ActualWidth,main.ActualHeight));drawing.DrawImage(tipImage,new Rect(position,new Size(help.ActualWidth,help.ActualHeight)));}
                                var combined=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);combined.Render(visual);
                                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(combined));using var file=File.Create(Path.Combine(output,"WinGet-Kompakt-Hinweis.png"));encoder.Save(file);
                            }
                            help.IsOpen=false;
                            fake.Ready=true;Wait(page.RefreshWinGetAsync());Assert(view.PrimaryButtons["kingston"].IsEnabled,"Ready status restores installation route");
                            page.HostBusy=()=>true;page.RefreshAvailability();Assert(!button.IsEnabled&&!view.PrimaryButtons["kingston"].IsEnabled,"Busy state blocks setup and installs");page.HostBusy=()=>false;page.RefreshAvailability();
                        }
                    }
                    var target=ManufacturerCatalog.Apps.Single(x=>x.Id=="kingston");
                    ManufacturerDownloadWindow Dialog()=>new(target,vendor,ThemePalette.For(theme),false,fake){Owner=main};
                    var dialog=Dialog();dialog.Show();Pump();Assert(((Button)dialog.FindName("StartButton")).IsEnabled&&fake.Started==0,"Opening dialog only checks metadata");
                    dialog.ConfirmForTests=_=>false;Wait(dialog.DownloadAndLaunchAsync());Assert(fake.Started==0,"Declining never starts install");
                    fake.Pending=new();dialog.ConfirmForTests=_=>true;var active=dialog.DownloadAndLaunchAsync();Pump();Assert(fake.Started==1&&dialog.StartedInstaller,"Explicitly confirmed install starts exactly once");
                    Wait(dialog.DownloadAndLaunchAsync());Assert(fake.Started==1,"Double start blocked");dialog.Close();Pump();Assert(dialog.IsVisible&&!active.IsCompleted,"Close waits for interactive installer completion");
                    fake.Pending.SetResult(0);Wait(active);Assert(dialog.Failure is null&&((TextBlock)dialog.FindName("PhaseText")).Text.Contains("erneut"),"Exit zero alone is not installed detection");
                    if(theme=="Standard")Save(dialog,"WinGet-Installation-beendet");dialog.Close();Pump();fake.Pending=null;
                    fake.Code=5;dialog=Dialog();dialog.Show();Pump();dialog.ConfirmForTests=_=>true;Wait(dialog.DownloadAndLaunchAsync());Assert(dialog.Failure is not null,"Nonzero exit is an error");dialog.Close();fake.Code=0;
                    fake.Package=false;dialog=Dialog();dialog.Show();Pump();Assert(dialog.Failure is not null&&!((Button)dialog.FindName("StartButton")).IsEnabled,"Missing exact package blocks install");dialog.Close();fake.Package=true;
                    vendor.Hardware=false;dialog=Dialog();dialog.Show();Pump();Assert(dialog.Failure is not null,"Unconfirmed hardware blocks install");dialog.Close();vendor.Hardware=true;
                    fake.Ready=false;dialog=Dialog();dialog.Show();Pump();Assert(dialog.Failure is not null,"Missing WinGet blocks installation");dialog.Close();
                    fake.Ready=true;vendor.Installed=true;dialog=Dialog();dialog.Show();Pump();dialog.ConfirmForTests=_=>true;Wait(dialog.DownloadAndLaunchAsync());Assert(((TextBlock)dialog.FindName("PhaseText")).Text=="Installation bestätigt","Success requires application inspection");dialog.Close();vendor.Installed=false;
                    Console.WriteLine("PASS WinGet "+theme+": DE/EN, five sizes, availability and interactive process states");
                }
                finally{main.Close();Pump();}
            }
            Assert(installs==0,"Command tests executed no real install");
            Console.WriteLine("PASS "+checks+" WinGet checks; fake installation processes only.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
