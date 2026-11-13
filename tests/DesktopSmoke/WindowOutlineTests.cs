using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class WindowOutlineTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    internal static int Run(App app, string output)
    {
        Directory.CreateDirectory(output); UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks = 0; var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        app.DispatcherUnhandledException += capture;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Pump()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        var main = new MainWindow(Path.Combine(output, "Isoliert"), new AppSettings { Theme = "Standard", Language = "de", SizeIndex = 1 }) { ShowActivated = false };
        main.PreviewMouseDown += (_, e) => e.Handled = true; main.PreviewKeyDown += (_, e) => e.Handled = true;
        try
        {
            main.Show(); Pump();
            var outline = (Border)main.Template.FindName("OuterWindowOutline", main);
            var root = (FrameworkElement)main.FindName("Root");
            void Bounds()
            {
                main.UpdateLayout(); var origin = outline.TranslatePoint(new Point(), main);
                Assert(Math.Abs(origin.X - 1) < .01 && Math.Abs(origin.Y - 1) < .01, "outline sits safely inside native clipping");
                Assert(Math.Abs(outline.ActualWidth - main.ActualWidth + 2) < .01 && Math.Abs(outline.ActualHeight - main.ActualHeight + 2) < .01, "outline follows full current viewport with a one-pixel inset");
                Assert(outline.BorderThickness == new Thickness(1.2) && !outline.IsHitTestVisible && !outline.Focusable && outline.Effect is null, "thin frame adds neither input interception nor an animated effect");
            }
            void HeaderInput()
            {
                foreach (var name in new[] { "PageNext", "SettingsHeaderButton", "HeaderMinimizeButton", "HeaderCloseButton" })
                {
                    var button = (Button)main.FindName(name);
                    var middle = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), main);
                    var hit = main.InputHitTest(middle) as DependencyObject;
                    while (hit is Visual && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                    Assert(ReferenceEquals(hit, button), "existing header button still receives input: " + name);
                    var screen = main.PointToScreen(middle);
                    var packed = unchecked(((int)screen.Y << 16) | ((int)screen.X & 0xffff));
                    Assert((int)SendMessage(new WindowInteropHelper(main).Handle, 0x0084, IntPtr.Zero, new IntPtr(packed)) == 1, "native header center remains client input: " + name);
                }
            }
            foreach (var english in new[] { false, true })
            {
                typeof(MainWindow).GetField("_english", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, english);
                typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
                {
                    var themes = (ComboBox)main.FindName("ThemeBox");
                    themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(item => (item.Tag?.ToString() ?? item.Content?.ToString()) == theme);
                    foreach (var size in new[] { 1, 4 })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump(); Bounds();
                        Assert(ReferenceEquals(outline.BorderBrush, main.FindResource("AccentBrush")), "theme switch updates outline accent: " + theme);
                        Assert(main.ActualWidth == (size == 1 ? 860 : 1200) && main.ActualHeight == (size == 1 ? 620 : 760), "existing window dimensions preserved");
                        Assert(Math.Abs(root.ActualWidth - main.ActualWidth) < .01 && Math.Abs(root.ActualHeight - main.ActualHeight) < .01 && root.TranslatePoint(new Point(), main) == new Point(), "outline does not inset or resize application content");
                        Assert(AdornerLayer.GetAdornerLayer(root) is not null, "existing adorners still have their layer");
                        HeaderInput();
                        var image = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(main);
                        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                        var accent = ((SolidColorBrush)outline.BorderBrush).Color;
                        foreach (var (x, y) in new[] { (image.PixelWidth / 2, 1), (1, image.PixelHeight / 2), (image.PixelWidth - 2, image.PixelHeight / 2), (image.PixelWidth / 2, image.PixelHeight - 2) })
                        {
                            int at = (y * image.PixelWidth + x) * 4;
                            Assert(Math.Abs(pixels[at] - accent.B) + Math.Abs(pixels[at + 1] - accent.G) + Math.Abs(pixels[at + 2] - accent.R) < 36, "actual rendered accent line on all four sides: " + theme);
                        }
                    }
                }
            }
            main.WindowState = WindowState.Maximized; Pump(); Bounds();
            Assert(outline.CornerRadius == new CornerRadius(0), "maximized frame follows square native contour");
            main.WindowState = WindowState.Normal; Pump();
            Assert(outline.CornerRadius == new CornerRadius(11), "restored inset frame follows rounded native contour");
            main.WindowState = WindowState.Minimized; Pump(); main.WindowState = WindowState.Normal; Pump(); Bounds(); HeaderInput();
            ((Button)main.FindName("SettingsHeaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Bounds();
            Assert(((FrameworkElement)main.FindName("SettingsPanel")).IsVisible, "settings overlay still opens below viewport outline");
            ((Button)main.FindName("SettingsHeaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert(!((FrameworkElement)main.FindName("SettingsPanel")).IsVisible, "settings overlay still closes");
            // Exercise the automatic small-screen scroll wrapper without changing display settings.
            typeof(MainWindow).GetMethod("ConfigureCompactScrolling", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [true]);
            main.MinWidth = 400; main.MinHeight = 400; main.Width = 760; main.Height = 560; Pump(); Bounds();
            var scroll = (ScrollViewer)main.Content;
            scroll.ScrollToHorizontalOffset(80); scroll.ScrollToVerticalOffset(60); Pump(); Bounds();
            Assert(scroll.HorizontalOffset > 0 && root.ActualWidth >= 860, "content can scroll independently of outline");
            typeof(MainWindow).GetMethod("ConfigureCompactScrolling", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [false]);
            ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump(); Bounds();
            Assert(ReferenceEquals(main.Content, root), "original content reattaches after compact scrolling");
        }
        finally { main.Close(); Pump(); app.DispatcherUnhandledException -= capture; }
        Assert(errors.Count == 0, "no hidden dispatcher exception: " + errors.FirstOrDefault());
        Console.WriteLine($"PASS {checks} outline rendering, seven-theme DE/EN, compact/large, native input, overlay and viewport-scroll checks; isolated app, no power or installation actions"); return 0;
    }
}
