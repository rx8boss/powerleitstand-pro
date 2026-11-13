using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramLightshotTests
{
    internal static int Run(App app, string output)
    {
        int cases = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); cases++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        var entry = ProgramCatalog.Entries.Single(e => e.Id == "lightshot");
        string assetsPath = Path.Combine(output, "Icons"); Directory.CreateDirectory(assetsPath);
        var iconBytes = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(output)!, "Quellen", "lightshot.ico"));
        var main = new MainWindow(Path.Combine(output, "Settings")) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            typeof(WindowsUpdatesPage).GetField("_programAssets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, new ProgramAssets(assetsPath, (_, _) => Task.FromResult(iconBytes), readLocalIcons: false));
            var view = page.ShowPrograms(false); var fake = new ProgramCatalogTests.Fake(); view.Service = fake;
            fake.ResolveHandler = (e, _) => Task.FromResult<ProgramPackage?>(new(e.PackageId!, "5.5.0.7", "Fixture from verified official package metadata", DateTime.UtcNow, VersionSource: "WinGet"));
            Wait(view.Assets.EnsureIconAsync(entry));
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(t => t.Tag?.ToString() is "Standard" or "Neon Blau").ToArray())
            foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                themes.SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                Wait(view.RefreshAsync()); view.SetCategory("tools"); Pump();
                int index = view.VisibleEntries.ToList().FindIndex(e => e.Id == entry.Id); Assert(index >= 0, "Lightshot in Tools");
                var tile = view.Tiles[index]; tile.BringIntoView(); Pump();
                var icon = UnifiedDialogAppearance.Children<System.Windows.Controls.Image>(tile).FirstOrDefault(i => i.Source is not null);
                Assert(icon is not null, "Lightshot icon loaded from simulated Google response without disk cache");
                var iconBounds = icon!.TransformToAncestor(tile).TransformBounds(new Rect(icon.RenderSize));
                Assert(iconBounds.Top >= 0 && iconBounds.Left >= 0 && iconBounds.Bottom <= tile.ActualHeight + 1 && iconBounds.Right <= tile.ActualWidth + 1, "Lightshot icon is not clipped in compact/large");
                view.ShowInfo(entry, tile, false); Wait(view.Info!.Loading);
                var info = string.Join("\n", UnifiedDialogAppearance.Children<TextBlock>(view.Info.Frame).Select(t => t.Text));
                Assert(info.Contains(english ? "Capture a selected screen area" : "Erstellt Screenshots") && info.Contains("5.5.0.7"), "bilingual Lightshot description/version");
                Assert(view.Info.Frame.Width == (view.Compact ? 340 : 390), "approved info size unchanged");
                main.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, "Lightshot-" + theme.Tag + "-" + (english ? "EN" : "DE") + "-" + size + ".png"))) encoder.Save(file);
                view.CloseInfo();
            }
            Assert(UiText.Translate("Die gewählte WinGet-Version ist nicht mehr verfügbar. Bitte erneut prüfen.", true).StartsWith("WinGet target version"), "new WinGet error uses English");
            Assert(UiText.Translate("Die gewählte WinGet-Version ist nicht mehr verfügbar. Bitte erneut prüfen.", false).StartsWith("Die gewählte"), "new WinGet error remains German");
            Console.WriteLine("PASS " + cases + " Lightshot compact/large DE/EN icon and info checks."); return 0;
        }
        finally { main.Close(); Pump(); }
    }
}

