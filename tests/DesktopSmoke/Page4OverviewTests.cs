using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PowerControlProWpf;

internal static class Page4OverviewTests
{
    internal static int Run(App app,string output, bool registryOnly = false)
    {
        UiSoundService.MuteForTests=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks=0;var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        void Assert(bool value,string message){if(!value)throw new Exception(message);checks++;}
        void Pump()=>app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        void Wait(Task task){var timer=Stopwatch.StartNew();while(!task.IsCompleted&&timer.ElapsedMilliseconds<15000){Pump();Thread.Sleep(5);}if(!task.IsCompleted)throw new TimeoutException();task.GetAwaiter().GetResult();Pump();}
        Rect Bounds(FrameworkElement item,Visual host)=>item.TransformToAncestor(host).TransformBounds(new Rect(item.RenderSize));
        void Save(Window main,string name){main.UpdateLayout();var bmp=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(main);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));using var file=File.Create(Path.Combine(output,name+".png"));png.Save(file);}
        try
        {
            if (registryOnly)
            {
            string startupFixture = @"Software\PowerLeitstand PRO\Tests\Startup-" + Guid.NewGuid().ToString("N");
            var startupDirectory = Directory.CreateDirectory(Path.Combine(output, Guid.NewGuid().ToString("N"))).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(startupDirectory, "Startup")).FullName;
            var savedFolder = Path.Combine(startupDirectory, "Backup");
            var startup = new StartupApps(startupFixture + @"\Run", startupFixture + @"\Saved", startupFixture + @"\Approval",
                folder, savedFolder, startupFixture + @"\FolderApproval");
            try
            {
                const string command = "%LOCALAPPDATA%\\Example\\App.exe --tray";
                using (var run = Registry.CurrentUser.CreateSubKey(startupFixture + @"\Run"))
                    run.SetValue("Startup fixture", command, RegistryValueKind.ExpandString);
                var enabled = startup.ReadUser().Single();
                Assert(enabled is { Enabled: true, CanToggle: true }, "Current user startup command can be switched");
                startup.Set(enabled, false);
                var disabled = startup.ReadUser().Single();
                Assert(disabled is { Enabled: false, CanToggle: true, Kind: RegistryValueKind.ExpandString } && disabled.Command == command,
                    "Disabling preserves the exact expandable command and type");
                startup.Set(disabled, true);
                Assert(startup.ReadUser().Single() == enabled, "Enabling restores the original command");
                var enabledMarker = new byte[12]; enabledMarker[0] = 2;
                using (var approved = Registry.CurrentUser.CreateSubKey(startupFixture + @"\Approval"))
                    approved.SetValue("Startup fixture", enabledMarker, RegistryValueKind.Binary);
                var windowsEnabled = startup.ReadUser().Single();
                Assert(windowsEnabled is { Enabled: true, CanToggle: true }, "Observed Windows enabled marker allows a reversible toggle");
                startup.Set(windowsEnabled, false);
                Assert(startup.ReadUser().Single() is { Enabled: false, CanToggle: true }, "Windows-managed entry stays in the list after disabling");
                startup.Set(startup.ReadUser().Single(), true);
                using (var approved = Registry.CurrentUser.OpenSubKey(startupFixture + @"\Approval"))
                    Assert(((byte[])approved!.GetValue("Startup fixture")!).SequenceEqual(enabledMarker), "The Windows marker is unchanged after both toggles");
                var disabledMarker = new byte[12]; disabledMarker[0] = 3;
                using (var approved = Registry.CurrentUser.CreateSubKey(startupFixture + @"\Approval"))
                    approved.SetValue("Startup fixture", disabledMarker, RegistryValueKind.Binary);
                var windowsDisabled = startup.ReadUser().Single();
                Assert(windowsDisabled is { Enabled: false, CanToggle: true }, "Previously disabled Windows entries can be enabled");
                bool refused = false;
                try { startup.Set(windowsEnabled, false); } catch (IOException) { refused = true; }
                Assert(refused, "New Windows approval marker blocks a stale toggle");
                startup.Set(windowsDisabled, true);
                Assert(startup.ReadUser().Single() == windowsEnabled, "Enabling restores the effective state and leaves the command intact");
                using (var approved = Registry.CurrentUser.CreateSubKey(startupFixture + @"\Approval"))
                    approved.SetValue("Startup fixture", new byte[] { 7 }, RegistryValueKind.Binary);
                Assert(startup.ReadUser().Single() is { Enabled: null, CanToggle: false, Block: StartupBlock.UnknownWindowsState }, "Unknown states stay read-only");
                Assert(startup.ReadFolder().Count == 0, "No shortcut initially");
                var shortcut = Path.Combine(folder, "New program.lnk");
                File.WriteAllText(shortcut, "Isolated shortcut move fixture; never launch");
                var folderEnabled = startup.Read().Apps.Single(item => item.Name == "New program.lnk");
                Assert(folderEnabled is { Enabled: true, CanToggle: true }, "A new shortcut is detected without a fixed program list");
                startup.Set(folderEnabled, false);
                var folderDisabled = startup.ReadFolder().Single();
                Assert(folderDisabled is { Enabled: false, CanToggle: true } && !File.Exists(shortcut) && File.Exists(folderDisabled.StartupFile),
                    "Disabling a shortcut keeps its recoverable copy visible");
                startup.Set(folderDisabled, true);
                Assert(File.ReadAllText(shortcut) == "Isolated shortcut move fixture; never launch" && startup.ReadFolder().Single() == folderEnabled,
                    "Enabling restores exactly the original shortcut");
                using (var approved = Registry.CurrentUser.CreateSubKey(startupFixture + @"\FolderApproval"))
                    approved.SetValue(folderEnabled.Name, disabledMarker, RegistryValueKind.Binary);
                startup.Set(startup.ReadFolder().Single(), true);
                Assert(startup.ReadFolder().Single() is { Enabled: true, CanToggle: true } && File.Exists(shortcut),
                    "A Windows-disabled shortcut can be enabled in its original location");
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                Assert(StartupApps.ReadIcon(enabled with { Command = '"' + explorer + '"' }) is { IsFrozen: true }, "Local executable icon can cross the worker/UI boundary");
                Assert(StartupApps.ReadIcon(enabled with { Command = "\"\\\\server\\share\\app.exe\"" }) is null, "Remote icon source is ignored");
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(startupFixture, false); }
            Console.WriteLine("PASS " + checks + " startup registry checks; isolated test key only."); return 0;
            }
            foreach(var theme in new[]{"Standard","Classic","Neon Blau","Neon Pink","Neon Grün","Linux Mint","Dark"})
            {
                var main=new MainWindow(Path.Combine(output,"Fixtures",theme)){ShowActivated=false};
                try
                {
                    var themes=(ComboBox)main.FindName("ThemeBox");themes.SelectedItem=themes.Items.OfType<ComboBoxItem>().Single(x=>x.Tag?.ToString()==theme);main.Show();main.ShowWindowsUpdatesPage();Pump();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var service=new WindowsUpdateTests.FakeService();page.Service=service;page.ConfirmForTests=_=>true;
                    Assert(((TextBlock)main.FindName("PageIndicator")).Text=="4 / 4"&&!((Button)main.FindName("PageNext")).IsEnabled,"Fourth page remains reachable and clamped");
                    main.ShowRepairPage();Assert(!page.IsVisible,"Repair shortcut hides fourth page");main.ShowUsersPage();Assert(!page.IsVisible,"User shortcut hides fourth page");main.ShowWindowsUpdatesPage();Pump();Wait(page.SearchAsync(false));
                    var lastCheck = typeof(WindowsUpdatesPage).GetField("_lastCheck", flags)!;
                    var checkTime = new DateTime(2026, 11, 6, 9, 41, 0); lastCheck.SetValue(page, checkTime);
                    Wait(page.SearchAsync(true)); Assert(Equals(lastCheck.GetValue(page), checkTime), "Driver checks do not overwrite Windows Update time");
                    foreach(bool english in new[]{false,true})
                    {
                        typeof(MainWindow).GetField("_english",flags)!.SetValue(main,english);typeof(MainWindow).GetMethod("ApplyLanguage",flags)!.Invoke(main,null);
                        foreach(int size in new[]{1,2,3,4,0})
                        {
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;page.ShowUpdateOverview();Pump();var scroll=(ScrollViewer)page.FindName("PageScroll");scroll.ScrollToHome();Pump();
                            var header=(FrameworkElement)page.FindName("HeaderSurface");var stripes=(FrameworkElement)page.FindName("HeaderStripes");var stripeBounds=Bounds(stripes,header);Assert(stripeBounds.Left>=-1&&stripeBounds.Right<=header.ActualWidth+1,"Stripes remain inside header");
                            foreach(string id in new[]{"ManufacturersButton","ProgramsButton","PrivacyButton","SearchUpdatesButton","CheckDriversButton","OpenStartupButton","InstallUpdatesButton","WinGetButton"})
                            {
                                var button=(Button)page.FindName(id);Assert(button.IsVisible,"Original overview action visible "+id);var rect=Bounds(button,page);
                                Assert(rect.Left>=-1&&rect.Right<=page.ActualWidth+1,"Button horizontally contained "+id);
                                if(id is "InstallUpdatesButton" or "WinGetButton")Assert(rect.Top>=-1&&rect.Bottom<=page.ActualHeight+1,"Footer always visible "+id);
                                if(id is "ManufacturersButton" or "ProgramsButton" or "PrivacyButton")Assert(!Bounds(button,header).IntersectsWith(stripeBounds),"Header button clear of stripes "+id);
                                foreach(var text in UnifiedDialogAppearance.Children<TextBlock>(button)){var r=Bounds(text,button);Assert(r.Left>=-1&&r.Top>=-1&&r.Right<=button.ActualWidth+1&&r.Bottom<=button.ActualHeight+1,$"Caption clipped {theme}/{english}/{size}/{id}/{text.Text}: {r} in {button.ActualWidth}x{button.ActualHeight}");Assert(text.FontSize>=10,"Action caption readable");}
                                foreach(var icon in UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(button)){var r=Bounds(icon,button);Assert(r.Left>=-1&&r.Top>=-1&&r.Right<=button.ActualWidth+1&&r.Bottom<=button.ActualHeight+1,"Action icon fully contained");}
                            }
                            Assert(((FrameworkElement)page.FindName("UpdatesSurface")).IsVisible&&((FrameworkElement)page.FindName("DriversSurface")).IsVisible,"Updates and drivers share the original page");
                            var driverCard=(FrameworkElement)page.FindName("DriversSurface");
                            if (((FrameworkElement)page.FindName("EsuSurface")).IsVisible) { driverCard.BringIntoView(); Pump(); }
                            var driverBounds=Bounds(driverCard,scroll);
                            Assert(driverBounds.Top>=-1&&driverBounds.Bottom<=scroll.ViewportHeight+1,$"Driver card reachable {theme}/{english}/{size}: {driverBounds} in {scroll.ViewportHeight}");
                            scroll.ScrollToHome(); Pump();
                            Assert(scroll.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden,"Global scrolling remains available without visible bar");
                            foreach(var check in UnifiedDialogAppearance.Children<CheckBox>(page)){check.ApplyTemplate();var border=(Border)check.Template.FindName("Box",check);Assert(border.ActualWidth==20&&border.BorderThickness.Left>=1.5,"Framed selection checkbox preserved");}
                            if(theme=="Standard"&&!english&&size is 1 or 4){((TextBlock)main.FindName("InstalledVersionText")).Text="TESTDARSTELLUNG";Save(main,size==1?"Gemeinsame-Seite-Kompakt":"Gemeinsame-Seite-Gross");}
                            if (theme == "Standard" && size is 1 or 4)
                            {
                                Assert(((TextBlock)page.FindName("PageTitle")).Text == "System & Software", "Page title covers all tools");
                                Assert(((TextBlock)page.FindName("UpdatesLastCheck")).Text.Contains(checkTime.ToString("g")), "Windows Update card shows its last check");
                                page.StartupInventory = new StartupApps(userFolder: Path.Combine(output, "StartupFixture"),
                                    savedFolder: Path.Combine(output, "StartupFixtureBackup"));
                                Wait(page.OpenStartupAsync()); Assert(page.HasStartupOverlay, "Autostart opens inside page four");
                                var overlay = UnifiedDialogAppearance.Children<Grid>(page).Single(x => x.Name == "StartupOverlay");
                                var frame = overlay.Children.OfType<Border>().Single();
                                Assert(frame.ActualWidth <= page.ActualWidth && frame.ActualHeight <= page.ActualHeight, "Autostart dialog fits the selected size");
                                Assert(!UnifiedDialogAppearance.Children<Button>(overlay).Any(x => x.Content as string == (english ? "Open Windows" : "Windows öffnen")),
                                    "Individual rows have no generic Windows redirect");
                                if (!english) Save(main, size == 1 ? "Autostart-Kompakt" : "Autostart-Gross");
                                var close = UnifiedDialogAppearance.Children<Button>(overlay).Single(x => x.Content as string == (english ? "Close" : "Schließen"));
                                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert(!page.HasStartupOverlay, "Closing restores the overview");
                            }
                            scroll.ScrollToEnd();Pump();if(scroll.ScrollableHeight>1)Assert(((TextBlock)page.FindName("ScrollHint")).Text.Contains('↑'),"Scroll direction hinted");scroll.ScrollToHome();
                        }
                    }
                    foreach(string obsolete in new[]{"RestoreSurface","CreateRestorePointButton","RestoreBeforeInstallSwitch","RestoreAreaButton","WindowsAreaButton","RecoveryContent"})Assert(page.FindName(obsolete) is null,"Removed control absent "+obsolete);
                    var selected=UnifiedDialogAppearance.Children<CheckBox>(page).First(x=>x.Tag is string);selected.IsChecked=true;Assert(((Button)page.FindName("InstallUpdatesButton")).IsEnabled,"Selection still enables explicit installation");selected.IsChecked=false;
                    var headerCheck=UnifiedDialogAppearance.Children<CheckBox>((StackPanel)page.FindName("SoftwareRows")).First();headerCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert(page.SelectedKeys.Count==2,"Software group selector selects only its rows");
                    var view=page.ShowManufacturerSoftware(false);Pump();Assert(view.IsVisible,"Manufacturer view preserved");page.ShowUpdateOverview();Pump();Assert(!view.IsVisible&&page.IsVisible,"Manufacturer back returns to shared overview");
                    service.OnSearch=(drivers,_)=>Task.FromResult(new WindowsUpdateSearch(drivers==true?[]:Enumerable.Range(1,30).Select(x=>WindowsUpdateTests.FakeService.Item(x)).ToArray(),false));Wait(page.SearchAsync(false));Wait(page.SearchAsync(true));Pump();var longScroll=(ScrollViewer)page.FindName("PageScroll");Assert(longScroll.ScrollableHeight>0,"Long result lists remain scrollable");longScroll.ScrollToEnd();Pump();Assert(longScroll.VerticalOffset>0,"Last result remains reachable");
                    Console.WriteLine("PASS shared overview "+theme+": DE/EN, five sizes, original layout, readable controls and long result lists");
                }
                finally{main.Close();Pump();}
            }
            Console.WriteLine("PASS "+checks+" shared-overview checks; fixture data only.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
