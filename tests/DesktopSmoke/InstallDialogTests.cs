using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class InstallDialogTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var errors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        int checks = 0;
        void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Save(Window w, string name)
        {
            w.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(w); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        try
        {
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            {
                var suffix = theme.Replace(" ", "-") + (english ? "-EN" : "-DE");
                Console.WriteLine("BEGIN " + suffix);
                var main = new MainWindow(Path.Combine(output, "Fixture-" + suffix)) { ShowActivated = false };
                try
                {
                    typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english);
                    typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                    var themes = (ComboBox)main.FindName("ThemeBox");
                    themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().First(i => (i.Tag?.ToString() ?? i.Content?.ToString()) == theme);
                    Console.WriteLine("SHOW " + suffix);
                    main.Show(); Pump(); main.ShowUsersPage(); Pump();
                    var sizes = (ComboBox)main.FindName("SizeBox");
                    for (var size = 0; size < sizes.Items.Count; size++)
                    {
                        Console.WriteLine("SIZE " + suffix + " / " + size);
                        sizes.SelectedIndex = size; Pump(); main.UpdateLayout();
                        foreach (var name in new[] { "CreateUserButton", "EditUserButton", "RefreshUsersButton" })
                        {
                            var button = (Button)main.FindName(name);
                            var content = (FrameworkElement)button.Content;
                            var rect = content.TransformToAncestor(button).TransformBounds(new Rect(content.RenderSize));
                            Assert(rect.Left >= -0.5 && rect.Right <= button.ActualWidth + .5 && rect.Top >= -.5 && rect.Bottom <= button.ActualHeight + .5, "user button content clipped " + suffix + " / " + size + " / " + name);
                            var bounds = button.TransformToAncestor(main).TransformBounds(new Rect(button.RenderSize));
                            Assert(bounds.Left >= 0 && bounds.Right <= main.ActualWidth && bounds.Bottom <= main.ActualHeight, "user button outside app " + suffix + " / " + size + " / " + name);
                        }
                        if (size is 1 or 4) Save(main, "Benutzer-" + suffix + "-" + size);
                    }
                }
                finally { main.Close(); Pump(); }

                foreach (var older in new[] { false, true })
                {
                    var update = new AvailableUpdate(new Version(1, 1, older ? 2 : 4), "", new string('a', 64));
                    var dialog = new UpdateInstallWindow(ThemePalette.For(theme), english, new Version(1, 1, 3), update) { ShowActivated = false };
                    dialog.Show(); Pump(); dialog.UpdateLayout();
                    Assert(((TextBlock)dialog.FindName("HeaderText")).Text == (english ? "Install version?" : "Version installieren?"), "localized install heading " + suffix);
                    Assert(((TextBlock)dialog.FindName("ConfirmCaption")).Text == (english ? "Install" : "Installieren"), "localized install action " + suffix);
                    Assert(((TextBlock)dialog.FindName("CancelCaption")).Text == (english ? "Cancel" : "Abbrechen"), "localized cancel " + suffix);
                    Assert(((TextBlock)dialog.FindName("OlderVersionText")).IsVisible == older, "downgrade warning " + suffix);
                    foreach (var width in new[] { 580.0, 388.0 })
                    {
                        dialog.Width = width; Pump(); dialog.UpdateLayout();
                        foreach (var name in new[] { "ConfirmButton", "CancelButton", "HeaderCloseButton" })
                        {
                            var element = (FrameworkElement)dialog.FindName(name);
                            var rect = element.TransformToAncestor(dialog).TransformBounds(new Rect(element.RenderSize));
                            Assert(rect.Left >= 0 && rect.Right <= dialog.ActualWidth && rect.Bottom <= dialog.ActualHeight, "dialog action clipped " + suffix + " / " + width + " / " + name);
                        }
                        if (!older) Save(dialog, "Installation-" + suffix + "-" + width);
                    }
                    dialog.Close(); Pump();
                }
                // A modal confirmation must return false for Cancel/X and true only for Install.
                foreach (var action in new[] { "ConfirmButton", "CancelButton", "HeaderCloseButton" })
                {
                    var dialog = new UpdateInstallWindow(ThemePalette.For(theme), english, new Version(1, 1, 3), new AvailableUpdate(new Version(1, 1, 4), "", new string('a', 64)));
                    dialog.Loaded += (_, _) => app.Dispatcher.BeginInvoke(() => ((Button)dialog.FindName(action)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                    Assert(dialog.ShowDialog() == (action == "ConfirmButton"), "modal result " + suffix + " / " + action);
                }
            }
            Assert(errors.Count == 0, "unhandled dialog error: " + string.Join("; ", errors.Select(e => e.Message)));
            Console.WriteLine($"PASS INSTALLATION: {checks} assertions; 7 themes, DE/EN, 5 app sizes, narrow dialogs, downgrade warnings and modal actions.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
