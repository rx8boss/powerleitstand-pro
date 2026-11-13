using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class OverlayDialogTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0;
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Save(Window w, string name)
        {
            var image = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(w);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        void Inside(FrameworkElement element, Window owner)
        {
            var bounds = element.TransformToAncestor(owner).TransformBounds(new Rect(element.RenderSize));
            Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= owner.ActualWidth + 1 && bounds.Bottom <= owner.ActualHeight + 1, "overlay outside app: " + element.Name);
        }
        try
        {
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            {
                var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
                try
                {
                    typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english);
                    typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                    var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().First(i => i.Tag?.ToString() == theme);
                    main.Show(); Pump();
                    // The options overlay is reused when selecting a different design.
                    // A previous neon glow must not leak into a light/standard design.
                    var neon = themes.Items.OfType<ComboBoxItem>().Single(i => i.Tag?.ToString() == "Neon Blau");
                    var selected = themes.SelectedItem;
                    themes.SelectedItem = neon;
                    typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(main, new object[] { true, false }); Pump();
                    Assert(((Border)main.FindName("SettingsPanel")).Effect is System.Windows.Media.Effects.DropShadowEffect, "neon options glow");
                    themes.SelectedItem = selected; Pump();
                    Assert(ThemePalette.For(theme).IsNeon || ((Border)main.FindName("SettingsPanel")).Effect is null, "previous neon glow removed on theme change");
                    typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(main, new object[] { false, false }); Pump();
                    foreach (var size in new[] { 1, 4 })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump();
                        typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(main, new object[] { true, false }); Pump();
                        var panel = (Border)main.FindName("SettingsPanel"); var done = (Button)main.FindName("DoneButton");
                        Inside(panel, main); Inside(done, main); Assert(done.IsVisible, "Done remains visible");
                        if (theme == "Standard" && !english && size == 1) Save(main, "Optionen-Kompakt");
                        done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(panel.Visibility == Visibility.Collapsed, "Done closes options");
                        foreach (var repair in new[] { false, true })
                        {
                            if (repair) main.ShowRepairPage(); else main.ShowUsersPage();
                            typeof(MainWindow).GetMethod("SetExpandedLogOpen", flags)!.Invoke(main, new object[] { true, false }); Pump();
                            var log = (Border)main.FindName("ExpandedLogPanel"); var close = (Button)main.FindName("ExpandedLogCloseButton");
                            Inside(log, main); Inside(close, main); Assert(close.Style == main.FindResource("UnifiedDialogClose"), "shared log close style");
                            var entries = (ListBox)main.FindName("ExpandedLogList"); Assert(ScrollViewer.GetVerticalScrollBarVisibility(entries) == ScrollBarVisibility.Hidden, "log scrollbar remains hidden");
                            if (theme == "Standard" && !english && size == 1) Save(main, repair ? "Reparaturprotokoll-Kompakt" : "Ereignisprotokoll-Kompakt");
                            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            typeof(MainWindow).GetMethod("SetExpandedLogOpen", flags)!.Invoke(main, new object[] { false, false }); Pump();
                            Assert(log.Visibility == Visibility.Collapsed, "log closes safely");
                        }
                    }
                }
                finally { main.Close(); Pump(); }
            }
            Console.WriteLine($"PASS OVERLAY DIALOGS: {checks} assertions, seven themes, DE/EN, compact/large, options and both log views. No system actions.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}
