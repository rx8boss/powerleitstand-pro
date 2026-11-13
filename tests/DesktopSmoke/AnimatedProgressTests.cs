using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class AnimatedProgressTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Advance(int milliseconds)
        {
            var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(5); } Pump();
        }
        RenderTargetBitmap Capture(FrameworkElement element)
        {
            element.UpdateLayout(); var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            if (element is Window) image.Render(element);
            else { var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize)); image.Render(visual); }
            return image;
        }
        void Save(FrameworkElement element, string name)
        {
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(Capture(element))); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        byte[] Pixels(BitmapSource image) { var bytes = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(bytes, image.PixelWidth * 4, 0); return bytes; }
        try
        {
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            {
                var main = new MainWindow(Path.Combine(output, "Fixtures", theme)) { ShowActivated = false };
                try
                {
                    void Field(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(main, value);
                    void Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, values);
                    var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == theme);
                    main.Show(); main.ShowRepairPage(); Pump();
                    var bar = (AnimatedProgressBar)main.FindName("RepairProgress");
                    foreach (var english in new[] { false, true })
                    {
                        Field("_english", english); Call("ApplyLanguage");
                        foreach (var size in new[] { 4, 1, 2, 3, 0 })
                        {
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump();
                            Field("_repairRunning", true); Field("_repairHasMeasuredProgress", false); Field("_repairPercent", 0d); bar.Value = 0; Call("UpdateRepairProgressAnimation"); Pump();
                            Call("SetTimerState", false); ((Button)main.FindName("RepairCancelButton")).IsEnabled = true;
                            Assert(bar.IsIndeterminate && bar.PercentageText.Length == 0, "No fabricated repair percentage before first measurement");
                            Assert(bar.HasMotion == SystemParameters.ClientAreaAnimation, "Waiting animation obeys system preference");
                            Field("_repairHasMeasuredProgress", true); Call("UpdateRepairProgressAnimation"); Pump();
                            Assert(!bar.IsIndeterminate && bar.PercentageText == "0 %", "Measured zero is displayed as a real value");
                            Field("_repairPercent", 62d); bar.Value = 62; Call("UpdateRepairProgressAnimation");
                            bar.IsAnimationSuspended = true; bar.IsAnimationSuspended = false; Pump();
                            Assert(bar.Value == 62 && bar.PercentageText == "62 %", "Presentation does not change the engine value");
                            Assert(bar.HasMotion == SystemParameters.ClientAreaAnimation, "Known progress has moving reflection");
                            var bounds = bar.TransformToAncestor(main).TransformBounds(new Rect(bar.RenderSize));
                            Assert(bar.ActualHeight >= 30 && bounds.Top >= 0 && bounds.Bottom < main.ActualHeight, "Readable repair bar stays inside window " + size);
                            var cancel = (Button)main.FindName("RepairCancelButton"); var cancelBounds = cancel.TransformToAncestor(main).TransformBounds(new Rect(cancel.RenderSize));
                            Assert(cancelBounds.Bottom < bounds.Top, "Repair actions do not overlap enlarged progress bar");
                            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(bar);
                            var provider = (IRangeValueProvider?)peer?.GetPattern(PatternInterface.RangeValue);
                            Assert(provider?.Value == 62, "Accessibility exposes measured progress");
                            if (theme == "Standard" && !english && size == 4 && SystemParameters.ClientAreaAnimation)
                            {
                                Advance(80); var first = Pixels(Capture(bar)); Advance(160); var second = Pixels(Capture(bar));
                                Assert(first.Where((value, index) => value != second[index]).Count() > 100, "Real rendered animation changes pixels at unchanged measured percentage");
                                Assert(bar.Value == 62, "Decorative movement never invents progress");
                                Call("BeginWindowDragPresentation"); Assert(!bar.HasMotion && bar.IsAnimationSuspended, "Window drag pauses repair animation");
                                Call("EndWindowDragPresentation"); Pump(); Assert(bar.HasMotion && !bar.IsAnimationSuspended, "Window drag end resumes repair animation");
                            }
                            ((TextBlock)main.FindName("RepairProgressLabel")).Text = english ? "DISM is running… · simulated progress" : "DISM wird ausgeführt… · Testdarstellung";
                            if (theme == "Standard" && !english && size is 4 or 1) Save(main, size == 4 ? "Reparatur-Standard" : "Reparatur-Kompakt");
                            if (theme == "Classic" && !english && size == 1) Save(main, "Reparatur-Classic");
                            main.ShowWindowsUpdatesPage(); Pump(); Assert(!bar.HasMotion, "Hidden repair page stops its animation");
                            main.ShowRepairPage(); Pump(); Assert(bar.HasMotion == SystemParameters.ClientAreaAnimation, "Returning to repair resumes active animation");
                            Field("_repairRunning", false); Call("UpdateRepairProgressAnimation"); Pump();
                            Call("SetTimerState", false); ((Button)main.FindName("RepairCancelButton")).IsEnabled = false;
                            Assert(!bar.HasMotion && bar.Value == 62 && bar.PercentageText == "62 %", "Cancellation or error retains last measurement and stops movement");
                            bar.Value = 100; Call("UpdateRepairProgressAnimation"); Pump(); Assert(bar.PercentageText == "100 %" && !bar.HasMotion, "Completed progress is stable");
                        }
                        var dialog = new UpdateDownloadWindow(new AvailableUpdate(new Version(1, 1, 6), "https://example.invalid/no-download.exe", new string('a', 64)), ThemePalette.For(theme), english, () => false) { Owner = main, ShowActivated = false };
                        // Disconnect production I/O before rendering the genuine dialog for controlled UI tests.
                        dialog.ContentRendered -= (EventHandler)typeof(UpdateDownloadWindow).GetMethod("RunDownload", flags)!.CreateDelegate(typeof(EventHandler), dialog);
                        try
                        {
                            dialog.Show(); Pump();
                            var download = (AnimatedProgressBar)dialog.FindName("DownloadBar");
                            void Report(UpdateProgress progress) { typeof(UpdateDownloadWindow).GetMethod("ApplyProgress", flags)!.Invoke(dialog, new object[] { progress }); Pump(); }
                            Report(new(UpdatePhase.Downloading, 62_000_000, 100_000_000));
                            download.IsAnimationSuspended = true; download.IsAnimationSuspended = false;
                            Assert(download.Value == 62 && download.PercentageText == "62 %", "Download percentage is inside the shared component");
                            Assert(dialog.FindName("PercentText") is null, "No duplicate percentage outside the bar");
                            var rect = download.TransformToAncestor(dialog).TransformBounds(new Rect(download.RenderSize));
                            var cancel = (Button)dialog.FindName("CancelButton"); var cancelRect = cancel.TransformToAncestor(dialog).TransformBounds(new Rect(cancel.RenderSize));
                            Assert(rect.Bottom < cancelRect.Top && cancelRect.Bottom <= dialog.ActualHeight, "Download bar and cancel button fit dialog");
                            if (theme == "Standard" && !english) Save(dialog, "App-Download");
                            Report(new(UpdatePhase.Downloading, 5_000_000, null)); Assert(download.IsIndeterminate && download.PercentageText == "", "Unknown download length hides percentage");
                            Report(new(UpdatePhase.Verifying, 100_000_000, 100_000_000)); Assert(download.IsIndeterminate && download.PercentageText == "", "Verification does not show fake percentage");
                            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                            Assert(((TextBlock)dialog.FindName("PhaseText")).Text == (english ? "Cancelling download …" : "Download wird abgebrochen …"), "Cancellation remains localized");
                            Assert(!cancel.IsEnabled && download.IsRunning, "Cancellation waits for I/O completion instead of claiming immediate completion");
                        }
                        finally { ((AnimatedProgressBar)dialog.FindName("DownloadBar")).IsRunning = false; typeof(UpdateDownloadWindow).GetField("_finished", flags)!.SetValue(dialog, true); dialog.Close(); Pump(); }
                    }
                    Console.WriteLine("PASS shared progress " + theme + ": DE/EN, five sizes, unknown/known/completed/cancelled, visibility and download dialog");
                }
                finally { typeof(MainWindow).GetField("_repairRunning", flags)!.SetValue(main, false); main.Close(); Pump(); }
            }
            var sample = new AnimatedProgressBar { Height = 34, Margin = new Thickness(0, 4, 0, 8), IsRunning = true, Value = 12, Background = new SolidColorBrush(Color.FromRgb(6, 28, 48)), Foreground = new SolidColorBrush(Color.FromRgb(19, 201, 255)), BorderBrush = new SolidColorBrush(Color.FromRgb(19, 201, 255)) };
            sample.ApplyPalette(ThemePalette.For("Standard"));
            var content = new StackPanel { Margin = new Thickness(16) };
            content.Children.Add(new TextBlock { Text = "Gemeinsamer Fortschrittsbalken · Animationsbeispiel", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
            content.Children.Add(sample);
            content.Children.Add(new TextBlock { Text = "Testdaten – keine Reparatur und kein Download gestartet.", Foreground = Brushes.LightBlue, FontSize = 11 });
            var sampleWindow = new Window { Width = 740, Height = 132, Content = content, Background = new SolidColorBrush(Color.FromRgb(0, 31, 61)), WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                sampleWindow.Show(); Pump();
                var gif = new GifBitmapEncoder();
                for (int frame = 0; frame < 40; frame++)
                {
                    sample.Value = 12 + frame * 2; Advance(80);
                    gif.Frames.Add(BitmapFrame.Create(Capture(sampleWindow)));
                }
                var animationPath = Path.Combine(output, "Fortschrittsbalken-Animation.gif");
                using (var file = File.Create(animationPath)) gif.Save(file);
                var bytes = File.ReadAllBytes(animationPath);
                int start = 13 + ((bytes[10] & 128) != 0 ? 3 * (1 << ((bytes[10] & 7) + 1)) : 0), position = start, frames = 0;
                while (position < bytes.Length && bytes[position] != 59)
                {
                    if (bytes[position] == 33)
                    {
                        if (bytes[position + 1] == 249) { bytes[position + 4] = 8; bytes[position + 5] = 0; frames++; }
                        position += 2; while (bytes[position] != 0) position += 1 + bytes[position]; position++;
                    }
                    else if (bytes[position] == 44)
                    {
                        byte packed = bytes[position + 9]; position += 10;
                        if ((packed & 128) != 0) position += 3 * (1 << ((packed & 7) + 1));
                        position++; while (bytes[position] != 0) position += 1 + bytes[position]; position++;
                    }
                    else throw new Exception("Invalid GIF frame block");
                }
                Assert(frames == 40, "Animation has 40 timed frames");
                if (!System.Text.Encoding.ASCII.GetString(bytes).Contains("NETSCAPE2.0"))
                {
                    var loop = new byte[] { 33, 255, 11 }.Concat(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0")).Concat(new byte[] { 3, 1, 0, 0, 0 });
                    bytes = bytes.Take(start).Concat(loop).Concat(bytes.Skip(start)).ToArray();
                }
                File.WriteAllBytes(animationPath, bytes);
            }
            finally { sampleWindow.Close(); Pump(); }
            Assert(!sample.HasMotion, "Unloading shared component releases its animation");
            Console.WriteLine($"PASS {checks} progress checks; simulated measurements in actual WPF windows; production download handler disconnected; no network, installer or repair process.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
