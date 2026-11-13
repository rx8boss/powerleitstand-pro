using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramRowsTests
{
    internal static int Run(App app,string output)
    {
        Directory.CreateDirectory(output);int checks=0;void Assert(bool ok,string reason){if(!ok)throw new Exception(reason);checks++;}
        UiSoundService.MuteForTests=true;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var main=new MainWindow(Path.Combine(output,"Fixture"),new AppSettings{Theme="Standard",SizeIndex=1}){ShowActivated=false};
        void Pump(){app.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);main.UpdateLayout();}
        try
        {
            main.Show();main.ShowWindowsUpdatesPage();var page=(WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");var view=page.ShowPrograms(false);view.Assets.AllowOnlineIcons=false;
            var fake=new ProgramCatalogTests.Fake();fake.Installed["firefox"]="1.0";fake.Updates["firefox"]="2.0";fake.Installed["google-chrome"]="1.0";view.Service=fake;
            var scan=view.RefreshAsync();while(!scan.IsCompleted)Pump();scan.GetAwaiter().GetResult();Pump();
            var scroll=(ScrollViewer)typeof(ProgramCatalogView).GetField("_scroll",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            var themes=(ComboBox)main.FindName("ThemeBox");var sizes=(ComboBox)main.FindName("SizeBox");var measurements=new List<object>();
            foreach(var theme in themes.Items.OfType<ComboBoxItem>().Where(i=>i.Tag is string).ToArray())
            foreach(bool english in new[]{false,true})foreach(int size in new[]{0,1,3,4})
            {
                File.AppendAllText(Path.Combine(output,"Phasen.txt"),$"{theme.Tag}; en={english}; size={size}\n");
                themes.SelectedItem=theme;sizes.SelectedIndex=size;page.SetLanguage(english);view.SetQuery("");view.SetCategory("all");scroll.ScrollToTop();Pump();
                var visible=FullyVisible();int columns=visible.Select(t=>Math.Round(t.TranslatePoint(new Point(),scroll).X,1)).Distinct().Count();
                int rows=visible.Select(t=>Math.Round(t.TranslatePoint(new Point(),scroll).Y,1)).Distinct().Count();
                // UI presets: 0 Automatic, 1 Compact, 3 Extended, 4 Large.
                // Automatic/Extended may use the compact catalog when available space is smaller.
                int expectedColumns=size==1?3:size==4?6:view.ActualWidth>=1000 && view.ActualHeight>=470?6:3;
                int expectedRows=expectedColumns==6?4:3;
                Assert(columns==expectedColumns && rows==expectedRows,"actual fully visible grid matches size "+size+": "+columns+"x"+rows);
                Assert(visible.Length==expectedColumns*expectedRows,"all expected tiles completely visible");
                Assert(scroll.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden && scroll.ScrollableHeight>0,"hidden scrollbar still provides access to remaining programs");
                Assert(view.Tiles.All(t=>t.ActualHeight>=55),"existing minimum tile height retained");
                foreach(var tile in visible)
                {
                    foreach(var check in UnifiedDialogAppearance.Children<CheckBox>(tile).Where(c=>c.IsVisible))Inside(tile,check,"checkbox");
                    foreach(var image in UnifiedDialogAppearance.Children<System.Windows.Controls.Image>(tile).Where(i=>i.IsVisible))Inside(tile,image,"program icon");
                    foreach(var label in UnifiedDialogAppearance.Children<TextBlock>(tile).Where(t=>t.IsVisible && t.TextTrimming!=TextTrimming.None))Inside(tile,label,"name/description");
                }
                var installed=view.Tiles.Single(t=>t.Tag?.ToString()=="google-chrome");var update=view.Tiles.Single(t=>t.Tag?.ToString()=="firefox");
                Assert(UnifiedDialogAppearance.Children<CheckBox>(installed).All(c=>!c.IsVisible) && UnifiedDialogAppearance.Children<CheckBox>(update).Any(c=>c.IsVisible),"installed/update selection visibility retained at shorter tile height");
                Assert(UnifiedDialogAppearance.Children<TextBlock>(installed).Any(t=>t.Text.Contains(english?"INSTALLED":"INSTALLIERT")) && UnifiedDialogAppearance.Children<TextBlock>(update).Any(t=>t.Text.Contains("UPDATE")),"both status ribbons remain present");
                Assert(view.InstallButton.IsVisible && view.InstalledButton.IsVisible && view.UpdateButton.IsVisible,"footer actions remain visible");
                foreach(var button in new[]{view.InstallButton,view.UpdateButton,view.InstalledButton})Inside(view,button,"footer action");
                measurements.Add(new{Theme=theme.Tag,English=english,Size=size,Columns=columns,Rows=rows,Visible=visible.Length,TileHeight=visible[0].ActualHeight,ScrollHeight=scroll.ActualHeight});
                if(!english && size is 1 or 4)Save(theme.Tag!.ToString()!.Replace(' ','-')+"-"+size+".png");
                scroll.ScrollToEnd();Pump();Inside(scroll,view.Tiles.Last(),"last catalog tile after scrolling");scroll.ScrollToTop();Pump();Assert(scroll.VerticalOffset==0,"scrolling returns to top");
                view.SetCategory("browser");Pump();Assert(view.VisibleEntries.Count>0 && FullyVisible().Length==Math.Min(expectedColumns*expectedRows,view.VisibleEntries.Count),"smaller category retains complete available rows without filler tiles");
                view.SetQuery("Firefox");Pump();Assert(view.VisibleEntries.Count>0 && FullyVisible().Length==Math.Min(expectedColumns*expectedRows,view.VisibleEntries.Count),"local search refits visible results without clipping");
                view.SetQuery("");view.SetCategory("all");Pump();
            }
            File.WriteAllText(Path.Combine(output,"Layout-Messungen.json"),System.Text.Json.JsonSerializer.Serialize(measurements,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS {checks} actual program row/column, content, ribbons, footer, scrolling, category and search layout checks; seven designs, DE/EN, four sizes; no installation");return 0;

            Border[] FullyVisible()=>view.Tiles.Where(t=>{var p=t.TranslatePoint(new Point(),scroll);return p.X>=-1 && p.Y>=-1 && p.X+t.ActualWidth<=scroll.ActualWidth+1 && p.Y+t.ActualHeight<=scroll.ActualHeight+1;}).ToArray();
            void Inside(FrameworkElement host,FrameworkElement child,string label)
            {var rect=child.TransformToAncestor(host).TransformBounds(new Rect(child.RenderSize));Assert(rect.Left>=-1 && rect.Top>=-1 && rect.Right<=host.ActualWidth+1 && rect.Bottom<=host.ActualHeight+1,label+" contained: "+rect+" / "+host.RenderSize);}
            void Save(string name)
            {var bitmap=new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth),(int)Math.Ceiling(main.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(main);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,name));png.Save(stream);}
        }
        finally{main.Close();}
    }
}
