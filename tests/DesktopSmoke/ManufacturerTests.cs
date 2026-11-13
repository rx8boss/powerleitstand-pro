using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ManufacturerTests
{
    private sealed class FakeService : IManufacturerService
    {
        internal int Opened, Sites, Downloaded;
        internal bool DelayOpen;
        internal TaskCompletionSource? OpenCompletion;
        internal bool ResolveMissing;
        internal bool LaunchFails;
        internal bool UnknownAsus;
        internal TaskCompletionSource<string>? DownloadCompletion;
        internal CancellationToken DownloadToken;
        public Task<IReadOnlyDictionary<string, ManufacturerState>> InspectAsync() => Task.FromResult<IReadOnlyDictionary<string, ManufacturerState>>(
            ManufacturerCatalog.Apps.ToDictionary(x => x.Id, x => x.Id == "nvidia" ? new ManufacturerState(ManufacturerInstallState.Installed, "Fixture-NVIDIA.exe") :
                x.Id == "asus" ? UnknownAsus ? new(ManufacturerInstallState.Unknown) : new(ManufacturerInstallState.Installed, "appx:"+ManufacturerPackages.Family("asus")+"!App") : new(ManufacturerInstallState.Missing)));
        public Task<ManufacturerDownload?> ResolveAsync(ManufacturerApp app, CancellationToken token)
        {
            var url = app.Id switch
            {
                "amd" => "https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.8.1-minimalsetup-260818_web.exe",
                "intel" => ManufacturerDownloadPolicy.IntelInstaller,
                "kingston" => "https://media.kingston.com/support/downloads/KSM_setup_1.5.6.5.exe",
                "samsung" => "https://download.semiconductor.samsung.com/resources/software-resources/Samsung_Magician_Installer_Official_9.0.1.950.exe",
                "sandisk" => ManufacturerDownloadPolicy.SandiskInstaller,
                "asus" => "https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/ArmouryCrateInstallTool.zip",
                "msi" => "https://download.msi.com/uti_exe/desktop/MSI-Center.zip",
                "gigabyte" => "https://download.gigabyte.com/FileList/Utility/GCC_1.2.3.4.zip",
                _ => "https://us.download.nvidia.com/nvapp/client/1.0/NVIDIA_app_v1.0.exe"
            };
            return Task.FromResult(ResolveMissing ? null : new ManufacturerDownload(new(url), app.Id == "intel" ? ManufacturerDownloadPolicy.IntelFileName : Path.GetFileName(new Uri(url).AbsolutePath), "Fixture " + app.Name));
        }
        public Task<string> DownloadAsync(ManufacturerApp app, ManufacturerDownload download, IProgress<ManufacturerProgress> progress, CancellationToken token)
        {
            Downloaded++; DownloadToken = token; progress.Report(new("download", 62 * 1048576L, 100 * 1048576L));
            return DownloadCompletion?.Task ?? Task.FromResult("Fixture-Installer.exe");
        }
        public Task LaunchAsync(ManufacturerApp app, string file, bool installer, Action? onStarted = null)
        {
            if(LaunchFails) throw new IOException("Fixture: launch refused");
            Opened++; onStarted?.Invoke(); return DelayOpen ? (OpenCompletion = new()).Task : Task.CompletedTask;
        }
        public void OpenWebsite(ManufacturerApp app) { Sites++; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic; int checks = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task)
        {
            var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.ElapsedMilliseconds < 15000) { Pump(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new TimeoutException("Fixture did not finish."); task.GetAwaiter().GetResult(); Pump();
        }
        void Save(Window window, string name)
        {
            var version = window.FindName("InstalledVersionText") as TextBlock;
            var original = version?.Text;
            if (version is not null) version.Text = "TESTDARSTELLUNG";
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output,name+".png")); png.Save(file);
            if (version is not null) version.Text = original;
        }
        try
        {
            var nv = ManufacturerCatalog.Apps[0];
            const string source = "https://us.download.nvidia.com/nvapp/client/1.0/NVIDIA_app_v1.0.exe";
            Assert(ManufacturerService.ParseNvidiaDownload("<a href='"+source+"'>Windows</a>")?.AbsoluteUri == source,"Official source resolved");
            Assert(ManufacturerService.ParseNvidiaDownload("<a href='http://us.download.nvidia.com/nvapp/client/1.0/NVIDIA_app_v1.0.exe'>x</a>") is null,"HTTP rejected");
            Assert(ManufacturerService.ParseNvidiaDownload("<a href='https://evil.test/nvapp/client/1.0/NVIDIA_app_v1.0.exe'>x</a>") is null,"Other host rejected");
            Assert(ManufacturerService.ParseNvidiaDownload("<a href='"+source+"'>x</a><a href='"+source.Replace("v1.0","v2.0")+"'>x</a>") is null,"Ambiguous versions rejected");
            Assert(!ManufacturerService.IsAllowedDownload(new(source.Replace("v1.0", "v1.0_arm64"))),"ARM executable not confused with x64");
            Assert(ManufacturerService.NvidiaModelSupported("NVIDIA GeForce RTX 4080","Intel Core i7-12700K",16UL<<30,new(560,94)),"Supported concrete model");
            Assert(!ManufacturerService.NvidiaModelSupported("NVIDIA GeForce GTX 970","Intel Core i7-12700K",16UL<<30,new(560,94)),"Brand alone not sufficient");
            Assert(!ManufacturerService.NvidiaModelSupported("NVIDIA GeForce RTX 4080","Intel Core i7-12700K",16UL<<30,null),"Missing driver blocks direct download");
            var unsigned = Path.Combine(output,"unsigned.exe"); File.WriteAllText(unsigned,"Fixture, not an executable");
            Assert(!ManufacturerSignature.Verify(unsigned,nv.Publisher),"Unsigned file rejected");
            Assert(ManufacturerSignature.Verify(Environment.ProcessPath!,"Microsoft Corporation"),"Real signed dotnet host and signer extraction verified without execution");
            Assert(!ManufacturerSignature.Verify(Environment.ProcessPath!,nv.Publisher),"Valid signature with wrong manufacturer rejected");
            var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1,2,3,4]) });
            using var client = new HttpClient(handler);
            var root = Path.Combine(output,"DownloadFixture"); var backend = new ManufacturerService(client,root);
            try { Wait(backend.DownloadAsync(nv,new(new(source),"ignored.exe","Fixture"),new Progress<ManufacturerProgress>(),CancellationToken.None)); throw new Exception("Expected signature failure"); }
            catch (IOException) { Assert(handler.Calls == 1 && !Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Any(),"Invalid downloaded payload cleaned, not launched"); }
            var redirectHandler = new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://evil.test/payload.exe"); return r; });
            using var redirectClient = new HttpClient(redirectHandler);
            try { Wait(new ManufacturerService(redirectClient,root).DownloadAsync(nv,new(new(source),"ignored.exe","Fixture"),new Progress<ManufacturerProgress>(),CancellationToken.None)); throw new Exception("Expected redirect rejection"); }
            catch (IOException) { Assert(redirectHandler.Calls == 1,"External redirect blocked before request"); }
            ManufacturerAdapterTests.Run(Assert, Wait, output);
            ManufacturerSsdTests.Run(Assert, Wait, output);
            ManufacturerBoardTests.Run(Assert, Wait, output);
            foreach (var theme in new[]{"Standard","Classic","Neon Blau","Neon Pink","Neon Grün","Linux Mint","Dark"})
            {
                var main = new MainWindow(Path.Combine(output,"Fixtures",theme)) { ShowActivated=false };
                try
                {
                    void Call(string method, params object[] args) => typeof(MainWindow).GetMethod(method,flags)!.Invoke(main,args);
                    var themes=(ComboBox)main.FindName("ThemeBox");themes.SelectedItem=themes.Items.OfType<ComboBoxItem>().Single(x=>x.Tag?.ToString()==theme);
                    main.Show();main.ShowWindowsUpdatesPage();Pump();
                    var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowManufacturerSoftware(false);var service=new FakeService();view.Service=service;Wait(view.RefreshAsync());Pump();
                    foreach(var english in new[]{false,true})
                    {
                        typeof(MainWindow).GetField("_english",flags)!.SetValue(main,english);Call("ApplyLanguage");
                        foreach(var size in new[]{4,1,2,3,0})
                        {
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;Pump();
                            var scroll=UnifiedDialogAppearance.Children<ScrollViewer>(view).Single(); scroll.ScrollToTop();Pump();
                            Assert(view.CardSurfaces.Count==ManufacturerCatalog.Apps.Length+1,"Header and every manufacturer card including the two additions");
                            Assert(scroll.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden,"Hidden scrolling");
                            for(int i=1;i<=4;i++)
                            {
                                var b=view.CardSurfaces[i].TransformToAncestor(scroll).TransformBounds(new Rect(view.CardSurfaces[i].RenderSize));
                                Assert(b.Bottom<=scroll.ViewportHeight+1,"First four rows fully visible "+theme+size);
                            }
                            Assert(view.PrimaryButtons["nvidia"].IsEnabled,"Detected app can open");
                            Assert(view.PrimaryButtons["asus"].IsEnabled,"Verified registered Store app can open");
                            Assert(view.PrimaryButtons["amd"].IsEnabled && view.PrimaryButtons["intel"].IsEnabled,"AMD/Intel offer device and source check");
                            Assert(new[]{"kingston","samsung","sandisk"}.All(id=>view.PrimaryButtons[id].IsEnabled),"SSD cards offer explicit model and source check");
                            Assert(view.PrimaryButtons["gigabyte"].IsEnabled,"GIGABYTE offers exact device/source check");
                            foreach(var button in UnifiedDialogAppearance.Children<Button>(view))
                            foreach(var text in UnifiedDialogAppearance.Children<TextBlock>(button))
                            {
                                var b=text.TransformToAncestor(button).TransformBounds(new Rect(text.RenderSize));
                                Assert(b.Right<=button.ActualWidth+1 && b.Bottom<=button.ActualHeight+1,"Manufacturer caption fits "+english+size);
                            }
                            if(theme=="Standard"&&!english&&size is 4 or 1) Save(main,size==4?"Hersteller-Standard":"Hersteller-Kompakt");
                            scroll.ScrollToEnd();Pump();Assert(scroll.VerticalOffset>0,"Other manufacturers reachable");
                            if(theme=="Standard"&&!english&&size==1)Save(main,"Hersteller-Kompakt-Weitere");
                            if(theme=="Standard"&&!english&&size is 1 or 4)
                            {
                                scroll.ScrollToVerticalOffset(scroll.VerticalOffset+view.CardSurfaces[5].TranslatePoint(new Point(),scroll).Y);Pump();
                                Save(main,size==1?"Hersteller-SSD-Kompakt":"Hersteller-SSD-Standard");
                            }
                        }
                    }
                    service.DelayOpen=true;var active=view.ActivateAsync(nv);Pump();
                    Assert(main.IsBusy&&!((Button)main.FindName("ShutdownButton")).IsEnabled,"Launch verification locks competing actions");
                    awaitFixture();
                    void awaitFixture(){service.OpenCompletion!.SetResult();Wait(active);}
                    Assert(!main.IsBusy&&service.Opened==1,"Open releases host after actual launch handoff");
                    var site=UnifiedDialogAppearance.Children<Button>(view).First(x=>x.Tag?.ToString()=="nvidia"&&!ReferenceEquals(x,view.PrimaryButtons["nvidia"]));
                    site.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(service.Sites==1,"Website delegates exactly once");
                    var dialog=new ManufacturerDownloadWindow(nv,service,ThemePalette.For(theme),false){Owner=main};
                    dialog.Show();Pump();
                    if(theme=="Standard")Save(dialog,"Hersteller-Download-Bestaetigung");
                    Assert(!dialog.StartedInstaller&&service.Downloaded==0,"Opening dialog does not download or install");
                    dialog.ConfirmForTests=_=>false;Wait(dialog.DownloadAndLaunchAsync());Assert(service.Downloaded==0,"Declining confirmation does not download");
                    dialog.ConfirmForTests=_=>true;service.DelayOpen=false;Wait(dialog.DownloadAndLaunchAsync());
                    Assert(dialog.StartedInstaller&&service.Downloaded==1,"Confirmed download hands original installer over");dialog.Close();Pump();
                    service.ResolveMissing=true;dialog=new(nv,service,ThemePalette.For(theme),true){Owner=main};dialog.Show();Pump();
                    Assert(service.Downloaded==1&&!dialog.StartedInstaller,"Missing verified source never starts download");dialog.Close();Pump();service.ResolveMissing=false;
                    service.DownloadCompletion=new();dialog=new(nv,service,ThemePalette.For(theme),false){Owner=main};dialog.Show();Pump();dialog.ConfirmForTests=_=>true;
                    active=dialog.DownloadAndLaunchAsync();Pump();
                    if(theme=="Standard")
                    {
                        var bar=(AnimatedProgressBar)dialog.FindName("DownloadBar");bar.IsAnimationSuspended=true;bar.IsAnimationSuspended=false;Pump();
                        Save(dialog,"Hersteller-Download-Fortschritt");
                    }
                    dialog.Close();Pump();
                    Assert(service.DownloadToken.IsCancellationRequested&&!active.IsCompleted&&dialog.IsVisible,"Close requests cancellation but waits for transfer end");
                    service.DownloadCompletion.SetCanceled();Wait(active);dialog.Close();Pump();
                    service.DownloadCompletion=null;service.LaunchFails=true;
                    dialog=new(nv,service,ThemePalette.For(theme),false){Owner=main};dialog.Show();Pump();dialog.ConfirmForTests=_=>true;Wait(dialog.DownloadAndLaunchAsync());
                    Assert(!dialog.StartedInstaller&&dialog.Failure is not null,"Failed launch never claims installer started");dialog.Close();Pump();service.LaunchFails=false;
                    foreach(var vendor in ManufacturerCatalog.Apps.Where(x=>x.Id != "nvidia" && ManufacturerDownloadPolicy.HasAdapter(x.Id)))
                    foreach(var english in new[]{false,true})
                    {
                        dialog=new(vendor,service,ThemePalette.For(theme),english){Owner=main};dialog.Show();Pump();
                        Assert(((Button)dialog.FindName("StartButton")).IsEnabled,"Vendor metadata enables explicit confirmation");
                        Assert(dialog.Failure is null&&!dialog.StartedInstaller,"Vendor dialog does not launch on opening");
                        var fileText=(TextBlock)dialog.FindName("FileText");
                        Assert(fileText.TextTrimming==TextTrimming.CharacterEllipsis,"Long vendor filename stays inside dialog");
                        string? confirmation=null;var before=service.Downloaded;
                        dialog.ConfirmForTests=text=>{confirmation=text;return false;};Wait(dialog.DownloadAndLaunchAsync());
                        Assert(confirmation!.Contains(vendor.Title(english))&&service.Downloaded==before,"Vendor-specific confirmation can decline without download");
                        if(theme=="Standard"&&!english)Save(dialog,"Hersteller-Download-"+vendor.Id);
                        dialog.Close();Pump();
                    }
                    foreach (var vendor in ManufacturerCatalog.Apps.Where(x => x.Id is "asus-displaywidget" or "turtle-beach"))
                    {
                        var opened = service.Sites; var downloaded = service.Downloaded;
                        Wait(view.ActivateAsync(vendor));
                        Assert(service.Sites == opened + 1 && service.Downloaded == downloaded && !main.IsBusy, "New website-only manufacturer app opens official page without a fabricated installer");
                    }
                    service.UnknownAsus=true;Wait(view.RefreshAsync());Assert(!view.PrimaryButtons["asus"].IsEnabled,"Unknown packaged state is not treated as missing");
                    service.UnknownAsus=false;Wait(view.RefreshAsync());
                    var prior=service.Opened;Wait(view.ActivateAsync(ManufacturerCatalog.Apps.Single(x=>x.Id=="asus")));Assert(service.Opened==prior+1&&!main.IsBusy,"Registered app opening releases global lock");
                    page.ShowUpdateOverview();Assert(!view.IsVisible&&((FrameworkElement)page.FindName("OverviewRoot")).IsVisible,"Back returns to existing overview");
                    Console.WriteLine("PASS manufacturers "+theme+": DE/EN, five sizes, state, launch, links and download cancellation");
                }
                finally{main.Close();Pump();}
            }
            Console.WriteLine($"PASS {checks} manufacturer checks; native windows with fixture states, local fake HTTP and signature checks; no real installer executed.");return 0;
        }
        catch(Exception exception){Console.Error.WriteLine(exception);return 1;}
    }
}
