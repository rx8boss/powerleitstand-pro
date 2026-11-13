using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class WindowsEsuTests
{
    internal static int AccountLive()
    {
        var account = WindowsAccountInfo.ReadAsync().GetAwaiter().GetResult();
        Console.WriteLine("Windows sign-in account source: " + account.Kind);
        Console.WriteLine("Microsoft account name available: " + (account.Name is not null));
        return account.Kind == WindowsAccountKind.Unknown ? 1 : 0;
    }

    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var dispatcherErrors = new List<Exception>();
        app.DispatcherUnhandledException += (_, error) => dispatcherErrors.Add(error.Exception);
        var checks = 0;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Pump(Window window) { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        void Save(Window window, string name)
        {
            Pump(window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        try
        {
            var eligible = new WindowsEsuInfo(19045, "Professional", false);
            Assert(eligible.ShowOn(new DateOnly(2027, 10, 12)), "ESU remains shown on the last covered day");
            Assert(!eligible.ShowOn(new DateOnly(2027, 10, 13)), "ESU disappears after the end date");
            Assert(!new WindowsEsuInfo(26100, "Professional", true).ShowOn(new DateOnly(2026, 11, 12)), "Windows 11 has no ESU card");
            Assert(!new WindowsEsuInfo(19045, "Enterprise", true).ShowOn(new DateOnly(2026, 11, 12)), "Commercial edition is not treated as consumer ESU");
            Assert(WindowsAccountInfo.Parse(@"MicrosoftAccount|MicrosoftAccount\Beispielkonto").Name == "Beispielkonto", "Microsoft account name is read from the current account source");
            Assert(WindowsAccountInfo.Parse("Microsoft Account|alias|Beispielkonto").Name == "Beispielkonto", "Windows display name is preferred over its local alias");
            Assert(WindowsAccountInfo.Parse("Local|Example") == new WindowsAccountInfo(WindowsAccountKind.Local), "Local Windows sign-in stays distinct from ESU enrollment");
            Assert(WindowsAccountInfo.Parse("Active Directory|Example") == new WindowsAccountInfo(WindowsAccountKind.Work), "Work account is not treated as a personal Microsoft account");
            Assert(WindowsAccountInfo.Parse("unavailable").Kind == WindowsAccountKind.Unknown, "Unavailable account source is not reported as Microsoft account");
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau" })
            {
                var settings = new AppSettings();
                var main = new MainWindow(Path.Combine(output, "Fixture-" + theme), settings) { ShowActivated = false };
                try
                {
                    var themeBox = (ComboBox)main.FindName("ThemeBox");
                    themeBox.SelectedItem = themeBox.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == theme);
                    main.Show(); main.ShowWindowsUpdatesPage(); Pump(main);
                    var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
                    page.EsuInfo = eligible;
                    page.EsuNow = () => new DateTime(2027, 10, 12, 14, 0, 0);
                    page.RefreshEsuVisibility(); Pump(main);
                    var card = (FrameworkElement)page.FindName("EsuSurface");
                    Assert(card.IsVisible, "ESU card shown for eligible Windows 10");
                    Assert(((TextBlock)page.FindName("EsuEndDate")).Text.Contains("2027"), "End date visible");
                    foreach (var (size, label) in new[] { (1, "Kompakt"), (4, "Gross") })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump(main);
                        card.BringIntoView(); Pump(main);
                        var button = (Button)page.FindName("EsuDetailsButton");
                        Assert(button.IsEnabled && button.ActualWidth > 0, "Details reachable in " + label);
                        if (theme == "Standard") Save(main, "ESU-Seite4-" + label);
                    }
                    {
                        var uri = "";
                        page.OpenExternalLink = link => uri = link.AbsoluteUri;
                        var account = new WindowsAccountInfo(WindowsAccountKind.Microsoft, "Beispielkonto");
                        var dialog = new WindowsEsuWindow(main, page, ThemePalette.For(theme), false)
                        { AccountLookup = () => Task.FromResult(account) };
                        dialog.Show(); Pump(dialog);
                        Assert(dialog.ActualWidth <= main.ActualWidth && dialog.ActualHeight <= main.ActualHeight, "Dialog fits owner");
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(dialog.Content).Any(text => text.Text.Contains("Beispielkonto")), "Detected account name appears in ESU dialog");
                        dialog.CheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        dialog.CheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(dialog);
                        dialog.OpenSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(uri == "ms-settings:windowsupdate", "Registration opens Windows Update settings");
                        dialog.OpenAccountButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(uri == "ms-settings:yourinfo", "Account check opens Windows account settings");
                        Save(dialog, "ESU-Dialog-" + theme.Replace(' ', '-'));
                        account = new(WindowsAccountKind.Local);
                        dialog.CheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(dialog);
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(dialog.Content).Any(text => text.Text == "Lokales Konto")
                            && UnifiedDialogAppearance.Children<TextBlock>(dialog.Content).Any(text => text.Text == "Registrierungsstatus in Windows Update anzeigen"),
                            "Refreshing to a local sign-in keeps the enrollment action available");
                        dialog.Close();
                        if (theme == "Standard")
                        {
                            ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump(main);
                            var compactDialog = new WindowsEsuWindow(main, page, ThemePalette.For(theme), false)
                            { AccountLookup = () => Task.FromResult(new WindowsAccountInfo(WindowsAccountKind.Local)) };
                            compactDialog.Show(); Pump(compactDialog);
                            Assert(compactDialog.ActualWidth <= main.ActualWidth && compactDialog.ActualHeight <= main.ActualHeight, "Compact dialog fits owner");
                            Assert(!compactDialog.SaveAndHideButton.IsEnabled, "ESU confirmation requires the checkbox");
                            compactDialog.ConfirmedCheckBox.IsChecked = true;
                            compactDialog.CheckButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Pump(compactDialog);
                            Assert(compactDialog.SaveAndHideButton.IsEnabled, "Checked confirmation survives refresh");
                            Save(compactDialog, "ESU-Dialog-Kompakt");
                            compactDialog.SaveAndHideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Pump(main);
                            Assert(!compactDialog.IsVisible && settings.EsuRegistrationConfirmed && card.Visibility == Visibility.Collapsed,
                                "Manual confirmation closes the dialog and hides the card");
                            var settingsFile = Path.Combine(output, "ESU-Settings-Test.json");
                            settings.Save(settingsFile);
                            var restoredSettings = AppSettings.Load(settingsFile);
                            Assert(restoredSettings.EsuRegistrationConfirmed, "Confirmation survives reading settings from disk");
                            var restoredMain = new MainWindow(Path.Combine(output, "Fixture-Restart"), restoredSettings, loadUsersForTests: false)
                            { ShowActivated = false };
                            try
                            {
                                restoredMain.Show(); restoredMain.ShowWindowsUpdatesPage(); Pump(restoredMain);
                                var restoredPage = (WindowsUpdatesPage)restoredMain.FindName("WindowsUpdatesPage");
                                restoredPage.EsuInfo = eligible;
                                restoredPage.EsuNow = page.EsuNow;
                                restoredPage.RefreshEsuVisibility();
                                var restoredCard = (FrameworkElement)restoredPage.FindName("EsuSurface");
                                var restore = (Button)restoredMain.FindName("RestoreEsuCardButton");
                                Assert(restoredCard.Visibility == Visibility.Collapsed && restore.Visibility == Visibility.Visible,
                                    "A new main window keeps the card hidden and offers restoring it");
                                restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Assert(!restoredSettings.EsuRegistrationConfirmed && restoredCard.Visibility == Visibility.Visible
                                    && restore.Visibility == Visibility.Collapsed, "Settings restores the card and clears confirmation");
                            }
                            finally { restoredMain.Close(); }
                            ((Button)main.FindName("RestoreEsuCardButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var englishDialog = new WindowsEsuWindow(main, page, ThemePalette.For(theme), true)
                            { AccountLookup = () => Task.FromResult(new WindowsAccountInfo(WindowsAccountKind.Work)) };
                            englishDialog.Show(); Pump(englishDialog);
                            Assert(englishDialog.ActualWidth <= main.ActualWidth && englishDialog.ActualHeight <= main.ActualHeight, "English dialog fits compact owner");
                            Save(englishDialog, "ESU-Dialog-English"); englishDialog.Close();
                        }
                    }
                    var simulatedNow = new DateTime(2027, 10, 12, 23, 59, 59, 900);
                    page.EsuNow = () => simulatedNow;
                    typeof(WindowsUpdatesPage).GetMethod("ScheduleEsuClock", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
                    simulatedNow = new DateTime(2027, 10, 13, 0, 0, 0, 300);
                    Thread.Sleep(350); Pump(main);
                    Assert(card.Visibility == Visibility.Collapsed, "ESU card hides automatically after midnight");
                }
                finally { main.Close(); }
            }
            Assert(dispatcherErrors.Count == 0, "No unhandled WPF error after repeated ESU checks and links");
            Console.WriteLine("PASS " + checks + " ESU checks; no registration or update was started.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
