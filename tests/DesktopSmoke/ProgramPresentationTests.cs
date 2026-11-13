using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramPresentationTests
{
    internal static int Run(App app, string output)
    {
        Directory.CreateDirectory(output); int checks = 0;
        void Assert(bool ok,string reason) { if(!ok) throw new Exception(reason);checks++; }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        UiSoundService.MuteForTests=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var main=new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Pump(){app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);main.UpdateLayout();}
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;view.Service=new ProgramCatalogTests.Fake();Pump();
            var evidence=new Dictionary<string,ProgramInstallation>{{"vlc",new("3.0.23","Registry")}};
            var inventory=new ProgramInventory(true,new Dictionary<string,string?>{{"vlc","3.0.23"}},Updates:new Dictionary<string,string>{{"vlc","3.0.24"}},Evidence:evidence,UpdateCheckComplete:true);
            typeof(ProgramCatalogView).GetProperty("Inventory",flags)!.SetValue(view,inventory);view.Selected.Add("firefox");
            var categories=(Dictionary<string,System.Windows.Controls.Button>)typeof(ProgramCatalogView).GetField("_categories",flags)!.GetValue(view)!;
            var themes=(ComboBox)main.FindName("ThemeBox");
            foreach(var theme in themes.Items.OfType<ComboBoxItem>().Where(t=>t.Tag is string).ToArray())
            foreach(bool english in new[]{false,true})foreach(int size in new[]{1,4})
            {
                themes.SelectedItem=theme;((ComboBox)main.FindName("SizeBox")).SelectedIndex=size;page.SetLanguage(english);Pump();
                File.AppendAllText(Path.Combine(output,"Phasen.txt"),$"{theme.Tag}; {english}; {size}\n");
                var buttonContents=new[]{view.InstallButton,view.UpdateButton,view.OnlineButton,view.InstalledButton}.Select(b=>b.Content).ToArray();view.RefreshAvailability();Pump();
                Assert(buttonContents.SequenceEqual(new[]{view.InstallButton,view.UpdateButton,view.OnlineButton,view.InstalledButton}.Select(b=>b.Content)),"availability refresh reuses icon controls and captions");
                view.MoreCategoriesButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Pump();
                Assert(view.AdditionalCategoriesFrame.IsVisible,"more menu still opens");
                foreach(var(id,button) in categories)
                {
                    var icon=UnifiedDialogAppearance.Children<Viewbox>(button).Single(v=>v.Name=="ProgramActionIcon");
                    Assert((string)icon.Tag==id && !icon.IsHitTestVisible,"each category has its own noninteractive vector");
                    Assert(AutomationProperties.GetName(button)==ProgramCatalog.Category(id,english),"category accessible name remains translated caption");
                    Bounds(button,icon,id);
                    foreach(var text in UnifiedDialogAppearance.Children<TextBlock>(button))Bounds(button,text,id+" caption");
                }
                view.MoreCategoriesButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));Pump();
                foreach(var button in new[]{view.InstallButton,view.UpdateButton,view.OnlineButton,view.MoreCategoriesButton})
                {
                    Assert(!AutomationProperties.GetName(button).Contains("StackPanel") && AutomationProperties.GetName(button).Length>0,"action accessible name is human-readable localized text");
                    foreach(var element in UnifiedDialogAppearance.Children<Viewbox>(button).Cast<FrameworkElement>().Concat(UnifiedDialogAppearance.Children<TextBlock>(button)))Bounds(button,element,"action");
                    var icon=UnifiedDialogAppearance.Children<Viewbox>(button).Single(v=>v.Name=="ProgramActionIcon");
                    var paths=UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(icon).ToArray();
                    Assert(paths.Length==2 && paths.All(p=>p.Effect is null && !p.HasAnimatedProperties),"two-tone icons add no animation clocks or effects");
                    Assert(((SolidColorBrush)paths[0].Stroke).Color==((SolidColorBrush)button.Foreground).Color,"outline follows actual theme button foreground");
                    Assert(((SolidColorBrush)paths[1].Stroke).Color==((SolidColorBrush)main.FindResource("AccentBrush")).Color,"accent follows theme resource");
                    bool enabled=button.IsEnabled;button.IsEnabled=false;Pump();Assert(!button.IsEnabled,"disabled state remains controlled by original button");button.IsEnabled=enabled;
                }
                var tick=UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(view.InstalledButton).Last();
                Assert(((SolidColorBrush)tick.Stroke).Color==((SolidColorBrush)main.FindResource("ReadyBrush")).Color,"installed check follows each theme readiness color");
                if(!english && theme.Tag?.ToString() is "Standard" or "Classic" or "Neon Pink")Save("Symbole-"+theme.Tag!.ToString()!.Replace(' ','-')+"-"+size+".png");
                var entry=ProgramCatalog.Entries.Single(e=>e.Id=="vlc");
                foreach(string result in new[]{"success","failure","stopped"})
                {
                    using var panel=new ProgramProgressPanel(view,[entry],reinstall:true);panel.Current(entry,english?"Checking":"Prüfen",new(entry.Id,"3.0.24","",DateTime.UtcNow));Pump();
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text==(english?"Reinstall program":"Programm neu installieren")),"reinstall heading differs from new installation");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text.Contains(english?"Reinstallation in progress":"Neuinstallation läuft")),"running operation is explicitly a reinstallation");
                    foreach(var stage in Enum.GetValues<ProgramStep>())panel.Step(entry.Id,new(stage,ProgramStepState.Done));
                    panel.State(entry.Id,result=="success"?"Bestätigt":"Nicht bestätigt · siehe Protokoll");panel.Finish(result=="stopped");Pump();
                    string title=result=="success"?(english?"Reinstallation completed":"Neuinstallation abgeschlossen"):result=="stopped"?(english?"Reinstallation stopped":"Neuinstallation gestoppt"):(english?"Reinstallation finished":"Neuinstallation beendet");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(panel.Frame).Any(t=>t.Text==title),"only verified success claims completed reinstallation");
                    Assert(panel.Progress.Visibility==(result=="success"?Visibility.Visible:Visibility.Collapsed),"failure/stop does not show success progress");
                    if(!english && theme.Tag?.ToString()=="Standard" && result=="success")Save("Neuinstallation-Abgeschlossen-"+size+".png");
                }
                using(var fresh=new ProgramProgressPanel(view,[entry]))Assert(UnifiedDialogAppearance.Children<TextBlock>(fresh.Frame).Any(t=>t.Text==(english?"Install programs":"Programme installieren")),"new installation keeps its own heading");
            }
            Console.WriteLine($"PASS {checks} vector-icon palette/bounds/accessibility/lifecycle and reinstall heading/result checks; seven designs, DE/EN, compact/large; no installation");return 0;
        }
        finally{main.Close();}
        void Bounds(FrameworkElement parent,FrameworkElement child,string what)
        {
            var r=child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
            Assert(r.Left>=-1 && r.Top>=-1 && r.Right<=parent.ActualWidth+1 && r.Bottom<=parent.ActualHeight+1,"content fits "+what+" "+r+" / "+parent.RenderSize);
        }
        void Save(string name)
        {
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth),(int)Math.Ceiling(main.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(main);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,name));png.Save(file);
        }
    }
}
