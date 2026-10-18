using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

namespace LayoutSnapshot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            System.Windows.Application.Current?.Shutdown(1);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Provide an absolute output directory.");
        Directory.CreateDirectory(args[0]);
        var settingsType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.AppSettings", true)!;
        var defaultSettings = Activator.CreateInstance(settingsType, nonPublic: true)!;
        if ((int)settingsType.GetProperty("SizeIndex")!.GetValue(defaultSettings)! != 2)
            throw new InvalidOperationException("The first-run default is not Standard (900 x 650).");
        var migrateSizeIndex = settingsType.GetMethod("MigrateLegacySizeIndex", BindingFlags.NonPublic | BindingFlags.Static)!;
        var expectedSizeIndices = new[] { 1, 2, 3, 4, 0 };
        for (var legacyIndex = 0; legacyIndex < expectedSizeIndices.Length; legacyIndex++)
            if ((int)migrateSizeIndex.Invoke(null, [legacyIndex])! != expectedSizeIndices[legacyIndex])
                throw new InvalidOperationException($"Saved size {legacyIndex} was not preserved after reordering.");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        VerifyLinkInputAncestors();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var sound in new[] { "warnings/beep.wav", "clicks/soft.wav" })
        {
            var assemblyName = typeof(MainWindow).Assembly.GetName().Name;
            var resource = Application.GetResourceStream(new Uri($"/{assemblyName};component/Assets/sounds/{sound}", UriKind.Relative));
            if (resource is null) throw new InvalidOperationException($"UI sound is missing: {sound}.");
            using (resource.Stream)
            using (var player = new System.Media.SoundPlayer(resource.Stream)) player.Load();
        }
        typeof(MainWindow).Assembly.GetType("PowerControlProWpf.UiSoundService", true)!
            .GetProperty("MuteForTests", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, true);
        var sizes = new[] { (860.0, 620.0), (900.0, 650.0), (1050.0, 670.0), (1200.0, 760.0) };
        for (var sizeIndex = 0; sizeIndex < sizes.Length; sizeIndex++)
        {
            var window = new MainWindow
            {
                Left = -10000,
                Top = -10000,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            typeof(MainWindow).GetProperty("SkipSavedPlacementForTests", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, true);
            ((ComboBox)window.FindName("SizeBox")!).SelectedIndex = sizeIndex + 1;
            ((ComboBox)window.FindName("ThemeBox")!).SelectedItem = ((ComboBox)window.FindName("ThemeBox")!).Items
                .OfType<ComboBoxItem>().First(item => item.Content?.ToString() == "Standard");
            (window.Width, window.Height) = sizes[sizeIndex];
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
            var homeBottomHeight = ((Grid)window.FindName("HomePage")!).RowDefinitions[^1].ActualHeight;

            var standardHome = ((ComboBoxItem)((ComboBox)window.FindName("ThemeBox")!).SelectedItem).Content?.ToString() == "Standard";
            var sizeChoices = (ComboBox)window.FindName("SizeBox")!;
            var autoLabel = ((ComboBoxItem)sizeChoices.Items[0]).Content?.ToString() ?? string.Empty;
            var defaultLabel = ((ComboBoxItem)sizeChoices.Items[2]).Content?.ToString() ?? string.Empty;
            if (!(autoLabel.StartsWith("Automatisch", StringComparison.Ordinal) || autoLabel.StartsWith("Automatic", StringComparison.Ordinal)) ||
                !defaultLabel.StartsWith("Standard", StringComparison.Ordinal))
                throw new InvalidOperationException("Size choices are not ordered with Automatic first and Standard as the default option.");
            if (standardHome) VerifyStandardPageOneLayout(window, sizeIndex);
            var logo = (FrameworkElement)window.FindName("BrandLogo")!;
            var footer = (FrameworkElement)window.FindName("FooterLayout")!;
            if (logo.ActualWidth < 24 || logo.ActualHeight < 24 || footer.ActualHeight < logo.ActualHeight)
                throw new InvalidOperationException($"Logo is clipped at size {sizeIndex}.");

            var notice = (Button)window.FindName("UpdateNoticeButton")!;
            var settingsButton = (Button)window.FindName("SettingsHeaderButton")!;
            var settingsGlyph = (FrameworkElement)window.FindName("SettingsHeaderGlyph")!;
            var settingsImage = (FrameworkElement)window.FindName("SettingsHeaderImage")!;
            var actionsBorder = (Border)window.FindName("HeaderActionsBorder")!;
            var actionsSeparator = (Border)window.FindName("HeaderActionsSeparator")!;
            var minimizeButton = (Button)window.FindName("HeaderMinimizeButton")!;
            var closeButton = (Button)window.FindName("HeaderCloseButton")!;
            window.UpdateLayout();
            if (standardHome && (actionsSeparator.Visibility != Visibility.Visible || actionsBorder.BorderThickness.Left != 0 ||
                                 !ReferenceEquals(minimizeButton.Style, window.Resources["HeaderNoticeButton"]) ||
                                 !ReferenceEquals(closeButton.Style, window.Resources["CloseButtonStyle"])))
                throw new InvalidOperationException($"Reference window controls are missing at size {sizeIndex}.");
            if (standardHome && (settingsGlyph.Visibility != Visibility.Visible || settingsImage.Visibility != Visibility.Collapsed))
                throw new InvalidOperationException($"Reference settings glyph is missing at size {sizeIndex}.");
            if (standardHome)
            {
                var settingsLabel = (FrameworkElement)window.FindName("SettingsHeaderLabel")!;
                var labelRight = settingsLabel.TransformToAncestor(settingsButton).Transform(new Point(settingsLabel.ActualWidth, 0)).X;
                if (labelRight > settingsButton.ActualWidth - 2)
                    throw new InvalidOperationException($"Settings header label is clipped at size {sizeIndex}.");
            }
            var title = (FrameworkElement)window.FindName("TitleProText")!;
            var header = (FrameworkElement)window.FindName("HeaderLayout")!;
            var titleRight = title.TransformToAncestor(header).Transform(new Point(title.ActualWidth, 0)).X;
            var noticeLeft = notice.TransformToAncestor(header).Transform(new Point()).X;
            if (titleRight > noticeLeft)
                throw new InvalidOperationException($"Header overlaps the update bell at size {sizeIndex}.");
            var admin = (Button)window.FindName("AdminRightsBadge")!;
            var pageNavigation = (FrameworkElement)window.FindName("PageNavigation")!;
            var pageIndicator = (FrameworkElement)window.FindName("PageIndicator")!;
            var navigationRight = pageNavigation.TransformToAncestor(header).Transform(new Point(pageNavigation.ActualWidth, 0)).X;
            var indicatorLeft = pageIndicator.TransformToAncestor(header).Transform(new Point()).X;
            if (indicatorLeft <= navigationRight)
                throw new InvalidOperationException($"Page indicator overlaps navigation at size {sizeIndex}.");
            var adminRight = admin.TransformToAncestor(header).Transform(new Point(admin.ActualWidth, 0)).X;
            var titleLeft = ((FrameworkElement)window.FindName("HeaderIcon")!).TransformToAncestor(header).Transform(new Point()).X;
            if (adminRight > titleLeft)
                throw new InvalidOperationException($"Admin badge overlaps the title at size {sizeIndex}.");
            if (sizeIndex == 2)
            {
                typeof(MainWindow).GetField("_english", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
                typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Standard"]);
                var log = (ListBox)window.FindName("LogBox")!;
                log.Items.Clear();
                log.Items.Add("[14.10.2026, 10:00:03] Benutzerübersicht aktualisiert.");
                log.Items.Add("[14.10.2026, 10:00:00] PowerLeitstand PRO WPF gestartet.");
                window.UpdateLayout();
                Capture(window, Path.Combine(args[0], "home-readme.png"));
            }
            VerifyUpdateStates(window);
            VerifyTimerAbortAppearance(window, args[0], sizeIndex);

            if (sizeIndex == 0)
                Capture(window, Path.Combine(args[0], "home-small.png"));
            if (sizeIndex == 1)
                Capture(window, Path.Combine(args[0], "home-standard.png"));
            if (sizeIndex == 2)
                Capture(window, Path.Combine(args[0], "home-1050x670.png"));
            if (sizeIndex == 3)
                Capture(window, Path.Combine(args[0], "home-large.png"));
            if (sizeIndex == 3)
                VerifyAdminBadgeStates(window, args[0]);
            if (sizeIndex == 3)
                CaptureUpdateBellStates(window, args[0]);
            typeof(MainWindow).GetMethod("ToggleSettings", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [null, new RoutedEventArgs()]);
            window.UpdateLayout();
            var options = (FrameworkElement)window.FindName("SettingsPanel")!;
            var content = (FrameworkElement)window.FindName("HomePage")!;
            VerifyOptionsComplete(window, $"size {sizeIndex}");
            if (sizeIndex == 0)
            {
                Capture(window, Path.Combine(args[0], "options-small.png"));
                typeof(MainWindow).GetField("_english", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                window.UpdateLayout();
                Capture(window, Path.Combine(args[0], "options-english-small.png"));
                var englishEditor = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.UserEditorDialog", true)!;
                var englishConstructor = englishEditor.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                CaptureDialog((Window)englishConstructor.Invoke([window, null]), Path.Combine(args[0], "user-create-english.png"));
                CaptureDialog((Window)englishConstructor.Invoke([window, new UserEntry("demo", "Standard user", "Demo account", "Active", true, "Demo")]),
                    Path.Combine(args[0], "user-edit-english.png"));
                var resetType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.PasswordResetDialog", true)!;
                var resetConstructor = resetType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                CaptureDialog((Window)resetConstructor.Invoke([window, "demo", true]), Path.Combine(args[0], "password-reset-english.png"));
                typeof(MainWindow).GetField("_english", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
                typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            }
            typeof(MainWindow).GetMethod("ToggleSettings", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [null, new RoutedEventArgs()]);
            typeof(MainWindow).GetMethod("OpenUpdateNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [null, new RoutedEventArgs()]);
            window.UpdateLayout();
            var settingsShell = (FrameworkElement)window.FindName("SettingsShell")!;
            var noticePanel = (FrameworkElement)window.FindName("SettingsNoticePanel")!;
            var noticePopup = (Popup)window.FindName("SettingsNoticePopup")!;
            if (settingsShell.Visibility != Visibility.Collapsed || options.Visibility != Visibility.Collapsed ||
                !noticePopup.IsOpen || noticePanel.ActualWidth > content.ActualWidth ||
                noticePanel.ActualHeight > content.ActualHeight)
                throw new InvalidOperationException($"Bell flyout is clipped or did not open at size {sizeIndex}.");
            if (noticePanel.ActualWidth > 145 || noticePanel.ActualWidth < 130 || noticePanel.ActualHeight < 230 ||
                ((FrameworkElement)window.FindName("NoticeCurrentRow")!).ActualWidth < noticePanel.ActualWidth - 30)
                throw new InvalidOperationException($"Bell flyout does not match the compact reference proportions at size {sizeIndex}.");
            if (noticePopup.PlacementTarget != window.FindName("UpdateNoticeButton") ||
                noticePopup.Placement != System.Windows.Controls.Primitives.PlacementMode.Bottom ||
                noticePopup.PopupAnimation != PopupAnimation.Slide || !noticePopup.StaysOpen ||
                noticePopup.HorizontalOffset != 0 || noticePopup.VerticalOffset != 25)
                throw new InvalidOperationException("Bell flyout placement, slide animation or outside-click behavior changed.");
            var autoCloseTimer = (DispatcherTimer)typeof(MainWindow).GetField("_noticeAutoCloseTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            if (autoCloseTimer.Interval != TimeSpan.FromSeconds(7) || !autoCloseTimer.IsEnabled)
                throw new InvalidOperationException("Bell flyout does not start its seven-second auto-close timer.");
            typeof(MainWindow).GetMethod("OpenUpdateNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [null, new RoutedEventArgs()]);
            if (noticePopup.IsOpen || autoCloseTimer.IsEnabled)
                throw new InvalidOperationException("Clicking the bell again does not close the open flyout.");
            typeof(MainWindow).GetMethod("OpenUpdateNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [null, new RoutedEventArgs()]);
            if (!noticePopup.IsOpen || !autoCloseTimer.IsEnabled)
                throw new InvalidOperationException("Clicking the bell after closing does not reopen the flyout.");
            if (sizeIndex == 0)
            {
                var outsidePress = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent };
                window.RaiseEvent(outsidePress);
                if (noticePopup.IsOpen || autoCloseTimer.IsEnabled)
                    throw new InvalidOperationException("Clicking elsewhere in the app does not close the bell flyout.");
                typeof(MainWindow).GetMethod("OpenUpdateNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [null, new RoutedEventArgs()]);
                CapturePopupComposite(window, noticePopup, Path.Combine(args[0], "update-notice-small.png"));
                typeof(MainWindow).GetField("_english", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                window.UpdateLayout();
                if (noticePanel.ActualWidth > 145 || noticePanel.ActualHeight < 230)
                    throw new InvalidOperationException("English update notice is clipped at small size.");
                CapturePopupComposite(window, noticePopup, Path.Combine(args[0], "update-notice-english-small.png"));
                typeof(MainWindow).GetField("_english", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
                typeof(MainWindow).GetMethod("ApplyLanguage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("CloseSettingsNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [null, new RoutedEventArgs()]);
                autoCloseTimer.Interval = TimeSpan.FromMilliseconds(100);
                typeof(MainWindow).GetMethod("OpenUpdateNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, [null, new RoutedEventArgs()]);
                var frame = new DispatcherFrame();
                var frameTimeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                frameTimeout.Tick += (_, _) => { frameTimeout.Stop(); frame.Continue = false; };
                frameTimeout.Start();
                Dispatcher.PushFrame(frame);
                autoCloseTimer.Interval = TimeSpan.FromSeconds(7);
                if (noticePopup.IsOpen || autoCloseTimer.IsEnabled)
                    throw new InvalidOperationException("Bell flyout did not close automatically after its timer elapsed.");
            }
            typeof(MainWindow).GetMethod("CloseSettingsNotice", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, [null, new RoutedEventArgs()]);
            typeof(MainWindow).GetMethod("ShowUsersPage", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, null);
            window.UpdateLayout();
            var userBottomHeight = ((Grid)window.FindName("UserPage")!).RowDefinitions[1].ActualHeight;
            if (standardHome)
            {
                VerifyReferenceStripes(window, "UserCardSurface", "UserReferenceStripes", "UserList");
                if (((FrameworkElement)window.FindName("UserCardCorner")!).Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException("The old user-overview triangle is still visible.");
            }
            if (!standardHome && Math.Abs(userBottomHeight - homeBottomHeight) > 1)
                throw new InvalidOperationException($"Status and log shift between pages 1 and 2 at size {sizeIndex}.");
            var sharedStatusLogBar = (FrameworkElement)window.FindName("StandardStatusLogBar")!;
            var userLegacyDetails = (FrameworkElement)window.FindName("UserLegacyDetails")!;
            if (standardHome && (sharedStatusLogBar.Visibility != Visibility.Visible || sharedStatusLogBar.ActualHeight < 65 ||
                                 userLegacyDetails.Visibility != Visibility.Collapsed))
                throw new InvalidOperationException($"Page-two shared status/log footer is missing or clipped at size {sizeIndex}.");
            var userLog = (FrameworkElement)window.FindName("UserLogBox")!;
            if (!standardHome && userLog.ActualHeight < 130)
                throw new InvalidOperationException($"Page-two log is too short at size {sizeIndex}: {userLog.ActualHeight:0}.");
            var users = (ListView)window.FindName("UserList")!;
            users.Items.Clear();
            users.Items.Add(new UserEntry("DefaultAccount", "Lokales Benutzerkonto", "Ein vom System verwaltetes Benutzerkonto", "Deaktiviert", false));
            users.Items.Add(new UserEntry("Gast", "Lokales Benutzerkonto", "Vordefiniertes Konto für Gastzugriff auf den Computer bzw. die Domäne", "Deaktiviert", false));
            users.Items.Add(new UserEntry("rx8boss", "Lokales Benutzerkonto", "", "Deaktiviert", false));
            users.Items.Add(new UserEntry("Administrator", "Administrator", "Eingebautes Administratorkonto für die Verwaltung des Computers bzw. der Domäne", "Aktiv", true));
            users.Items.Add(new UserEntry("WDAGUtilityAccount", "Lokales Benutzerkonto", "Ein vom System verwaltetes und für Windows Defender Application Guard verwendetes Konto", "Deaktiviert", false));
            users.SelectedIndex = 2;
            ((Button)window.FindName("RefreshUsersButton")!).IsEnabled = true;
            window.UpdateLayout();
            var userColumns = ((GridView)users.View).Columns;
            if (userColumns.Sum(column => column.ActualWidth) > users.ActualWidth ||
                userColumns[3].ActualWidth < 90)
                throw new InvalidOperationException($"User overview columns are clipped at size {sizeIndex}.");
            if (sizeIndex == 1)
                Capture(window, Path.Combine(args[0], "users-standard-900x650.png"));
            if (sizeIndex == 3)
                Capture(window, Path.Combine(args[0], "users-large-1200x760.png"));
            if (sizeIndex == 1)
            {
                for (var row = 0; row < 20; row++)
                    users.Items.Add(new UserEntry($"ScrollTest{row:00}", "Lokales Benutzerkonto", "Scrolltest", "Deaktiviert", false));
                window.UpdateLayout();
                var userScrollViewer = VisualChildren<ScrollViewer>(users).FirstOrDefault()
                    ?? throw new InvalidOperationException("The user table has no ScrollViewer.");
                if (ScrollViewer.GetVerticalScrollBarVisibility(users) != ScrollBarVisibility.Hidden || userScrollViewer.ScrollableHeight <= 0)
                    throw new InvalidOperationException("The user table does not retain overflow scrolling with its scrollbar hidden.");
                userScrollViewer.ScrollToVerticalOffset(userScrollViewer.ScrollableHeight);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                window.UpdateLayout();
                if (userScrollViewer.VerticalOffset <= 0)
                    throw new InvalidOperationException($"The user table cannot scroll while its scrollbar is hidden (offset {userScrollViewer.VerticalOffset:0}, extent {userScrollViewer.ExtentHeight:0}, viewport {userScrollViewer.ViewportHeight:0}).");
                Console.WriteLine("PASS user table scrolls with hidden scrollbar");
            }
            if (sizeIndex == 0)
            {
                Capture(window, Path.Combine(args[0], "users-small.png"));
                var refresh = (FrameworkElement)window.FindName("RefreshUsersButton")!;
                var usersPage = (FrameworkElement)window.FindName("UserPage")!;
                var refreshRight = refresh.TransformToAncestor(usersPage).Transform(new Point(refresh.ActualWidth, 0)).X;
                if (refreshRight > usersPage.ActualWidth)
                    throw new InvalidOperationException("User action buttons are clipped at small size.");
                var editorType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.UserEditorDialog", true)!;
                var constructor = editorType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                CaptureDialog((Window)constructor.Invoke([window, null]), Path.Combine(args[0], "user-create.png"));
                CaptureDialog((Window)constructor.Invoke([window, new UserEntry("rx8boss", "Standardbenutzer", "Administrator für Systemwartung", "Aktiv", true, "RX8 BOSS")]),
                    Path.Combine(args[0], "user-edit.png"));
                VerifyUserEditor((Window)constructor.Invoke([window, null]),
                    (Window)constructor.Invoke([window, new UserEntry("demo", "Standardbenutzer", "Testkonto", "Aktiv", true, "Demo")]));
                var resetType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.PasswordResetDialog", true)!;
                var resetConstructor = resetType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
                CaptureDialog((Window)resetConstructor.Invoke([window, "rx8boss", false]), Path.Combine(args[0], "password-reset.png"));
                VerifyPasswordResetValidation((Window)resetConstructor.Invoke([window, "demo", false]));
                VerifyPasswordRemovalChoice((Window)resetConstructor.Invoke([window, "demo", false]),
                    Path.Combine(args[0], "password-remove.png"));
                VerifyPasswordVisibility((Window)constructor.Invoke([window, null]));
                VerifyThemes(window, args[0]);
            }
            typeof(MainWindow).GetMethod("ShowRepairPage", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(window, null);
            window.UpdateLayout();
            var repairBottomHeight = ((Grid)window.FindName("RepairPage")!).RowDefinitions[1].ActualHeight;
            if (standardHome)
            {
                var repairLegacy = (FrameworkElement)window.FindName("RepairLegacyDetails")!;
                var repairFooter = (FrameworkElement)window.FindName("StandardStatusLogBar")!;
                var emptyRepairLog = (FrameworkElement)window.FindName("StandardLogEmptyState")!;
                var repairPreview = (ListBox)window.FindName("StandardLogPreview")!;
                if (repairLegacy.IsVisible || !repairFooter.IsVisible ||
                    emptyRepairLog.IsVisible != (repairPreview.Items.Count == 0))
                    throw new InvalidOperationException($"Repair footer or empty state is incorrect at size {sizeIndex}.");
                foreach (var name in new[] { "StandardRefreshIconButton", "StandardClearIconButton", "StandardOpenIconButton" })
                    EnsureInside((FrameworkElement)window.FindName(name)!, repairFooter, name, sizeIndex);
                var repairCard = (FrameworkElement)window.FindName("RepairCardSurface")!;
                var repairPage = (FrameworkElement)window.FindName("RepairPage")!;
                var cardBottom = repairCard.TranslatePoint(new Point(0, repairCard.ActualHeight), window).Y;
                var footerTop = repairFooter.TranslatePoint(new Point(0, 0), window).Y;
                if (repairCard.ActualHeight < repairPage.ActualHeight - 12 || cardBottom > footerTop || footerTop - cardBottom > 20 ||
                    ((FrameworkElement)window.FindName("RepairCardCorner")!).Visibility != Visibility.Collapsed)
                    throw new InvalidOperationException($"The full-height repair card does not fit at size {sizeIndex}.");
                EnsureInside((FrameworkElement)window.FindName("RepairTimeText")!, repairCard, "Repair runtime", sizeIndex);
                VerifyReferenceStripes(window, "RepairCardSurface", "RepairReferenceStripes", "RepairActionRow");
            }
            if (!standardHome && Math.Abs(repairBottomHeight - homeBottomHeight) > 1)
                throw new InvalidOperationException($"Status and log shift between pages 1 and 3 at size {sizeIndex}.");
            if (sizeIndex == 0)
            {
                Capture(window, Path.Combine(args[0], "repair-small.png"));
                VerifyRepairFooterRouting(window);
                VerifyRepairProgressAnimation(window);
                VerifyRepairActionLocks(window);
                CaptureRepairDialogs(args[0]);
            }
            if (sizeIndex == 1) Capture(window, Path.Combine(args[0], "repair-standard-900x650.png"));
            if (sizeIndex == 3) Capture(window, Path.Combine(args[0], "repair-large-1200x760.png"));
            VerifyExpandedLog(window, args[0], sizeIndex);
            VerifyCrossButtons(window, Path.Combine(args[0], $"x-hover-{sizeIndex}.png"));
            window.Close();
            Console.WriteLine($"PASS layout {sizes[sizeIndex].Item1:0}x{sizes[sizeIndex].Item2:0}: footer, bell, user log");
        }
        var autoWindow = new MainWindow
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        ((ComboBox)autoWindow.FindName("SizeBox")!).SelectedIndex = 0;
        // Allow the real startup placement to run, but never persist a test position.
        autoWindow.SourceInitialized += (_, _) => typeof(MainWindow)
            .GetProperty("SkipSavedPlacementForTests", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(autoWindow, true);
        autoWindow.Show();
        autoWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        autoWindow.UpdateLayout();
        var headerTitle = (FrameworkElement)autoWindow.FindName("TitleText")!;
        var titlePosition = headerTitle.TranslatePoint(new Point(0, 0), autoWindow);
        var titleText = (TextBlock)headerTitle;
        var headerHeight = ((Grid)autoWindow.FindName("Root")!).RowDefinitions[0].ActualHeight;
        if (!headerTitle.IsVisible || string.IsNullOrWhiteSpace(titleText.Text) || titlePosition.Y < 0 ||
            titlePosition.Y + headerTitle.ActualHeight > headerHeight)
            throw new InvalidOperationException("The title is clipped in the compact reference header.");
        var workArea = (Rect)typeof(MainWindow).Assembly.GetType("PowerControlProWpf.WindowPlacementService", true)!
            .GetMethod("GetCursorWorkAreaInDips", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [autoWindow])!;
        var centerX = autoWindow.Left + autoWindow.ActualWidth / 2;
        var centerY = autoWindow.Top + autoWindow.ActualHeight / 2;
        if (Math.Abs(centerX - workArea.Left - workArea.Width / 2.0) > 8 ||
            Math.Abs(centerY - workArea.Top - workArea.Height / 2.0) > 8)
            throw new InvalidOperationException($"Automatic window was not centered: {centerX:0},{centerY:0}.");
        if (autoWindow.ActualWidth > workArea.Width || autoWindow.ActualHeight > workArea.Height)
            throw new InvalidOperationException("Automatic window exceeds the monitor work area.");
        Capture(autoWindow, Path.Combine(args[0], "home-auto.png"));
        Console.WriteLine($"PASS automatic startup: {autoWindow.Width:0}x{autoWindow.Height:0}, centered in work area");
        typeof(MainWindow).GetMethod("ToggleSettings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(autoWindow, [null, new RoutedEventArgs()]);
        autoWindow.UpdateLayout();
        Capture(autoWindow, Path.Combine(args[0], "options-auto.png"));
        typeof(MainWindow).GetMethod("ToggleSettings", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(autoWindow, [null, new RoutedEventArgs()]);
        typeof(MainWindow).GetField("_effectiveSizeIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(autoWindow, 0);
        typeof(MainWindow).GetMethod("ConfigureCompactScrolling", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(autoWindow, [true]);
        autoWindow.MinHeight = 558;
        autoWindow.Width = 860;
        autoWindow.Height = 558;
        typeof(MainWindow).GetMethod("ApplyLayoutScale", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(autoWindow, null);
        autoWindow.UpdateLayout();
        var scrollHost = (ScrollViewer)autoWindow.Content;
        if (scrollHost.ExtentHeight <= scrollHost.ViewportHeight)
            throw new InvalidOperationException("Compact display does not expose clipped content via scrolling.");
        Capture(autoWindow, Path.Combine(args[0], "home-auto-small-scroll.png"));
        Console.WriteLine("PASS compact display: content remains scrollable without scaling");
        typeof(MainWindow).GetMethod("SetSettingsOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(autoWindow, [true, false]);
        autoWindow.UpdateLayout();
        VerifyOptionsComplete(autoWindow, "automatic small viewport 860x558");
        Capture(autoWindow, Path.Combine(args[0], "options-auto-small-scroll.png"));
        autoWindow.Close();
        app.Shutdown();
        return 0;
    }

    private static void Capture(Window window, string file)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(file);
        encoder.Save(output);
    }

    private static void CapturePopupComposite(Window window, Popup popup, string file)
    {
        window.UpdateLayout();
        var windowBitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        windowBitmap.Render(window);
        var content = (FrameworkElement)popup.Child!;
        var popupBitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        popupBitmap.Render(content);
        var button = (FrameworkElement)window.FindName("UpdateNoticeButton")!;
        var origin = button.TranslatePoint(new Point(), window);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(windowBitmap, new Rect(0, 0, window.ActualWidth, window.ActualHeight));
            dc.DrawImage(popupBitmap, new Rect(origin.X + popup.HorizontalOffset,
                origin.Y + button.ActualHeight + popup.VerticalOffset, content.ActualWidth, content.ActualHeight));
        }
        var composite = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        composite.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composite));
        using var output = File.Create(file);
        encoder.Save(output);
    }

    private static void VerifyReferenceStripes(MainWindow window, string cardName, string stripeName, string lowerBoundaryName)
    {
        var card = (Border)window.FindName(cardName)!;
        var stripes = (FrameworkElement)window.FindName(stripeName)!;
        var lowerBoundary = (FrameworkElement)window.FindName(lowerBoundaryName)!;
        var origin = stripes.TranslatePoint(new Point(0, 0), card);
        var contentTop = lowerBoundary.TranslatePoint(new Point(0, 0), card).Y;
        if (!stripes.IsVisible || stripes.IsHitTestVisible || stripes.ActualHeight < 24 ||
            card.Clip is not System.Windows.Media.RectangleGeometry clip ||
            Math.Abs(clip.Rect.Width - card.ActualWidth) > .5 || Math.Abs(clip.Rect.Height - card.ActualHeight) > .5 ||
            origin.X < 0 || origin.Y < 0 || origin.X + stripes.ActualWidth > card.ActualWidth + .5 ||
            origin.Y + stripes.ActualHeight > Math.Min(card.ActualHeight, contentTop) + .5)
            throw new InvalidOperationException($"Reference stripes escape the rounded card or overlap content: {cardName}.");
    }

    private static void VerifyStandardPageOneLayout(MainWindow window, int sizeIndex)
    {
        VerifyReferenceStripes(window, "ImmediateCardSurface", "ImmediateReferenceStripes", "ShutdownButton");
        VerifyReferenceStripes(window, "TimerCardSurface", "TimerReferenceStripes", "TimerShutdown");
        var timerCard = (FrameworkElement)window.FindName("TimerCardSurface")!;
        var immediateCard = (FrameworkElement)window.FindName("ImmediateCardSurface")!;
        var picker = (FrameworkElement)window.FindName("StandardTimePicker")!;
        var compactPicker = (FrameworkElement)window.FindName("CompactStandardTimePicker")!;
        var home = (Grid)window.FindName("HomePage")!;
        if (home.RowDefinitions[0].Height.GridUnitType != GridUnitType.Star ||
            home.RowDefinitions[1].Height.GridUnitType != GridUnitType.Star ||
            Math.Abs(home.RowDefinitions[0].Height.Value - 0.72) > .001 ||
            Math.Abs(home.RowDefinitions[1].Height.Value - 1) > .001 ||
            immediateCard.ActualHeight < 130 || timerCard.ActualHeight < 170)
            throw new InvalidOperationException($"Sofort/Timer adaptive vertical allocation is invalid at size {sizeIndex}: {immediateCard.ActualHeight:0} / {timerCard.ActualHeight:0}.");
        if (picker.Visibility != Visibility.Collapsed || compactPicker.Visibility != Visibility.Visible ||
            ((FrameworkElement)window.FindName("TimerPickerBorder")!).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"Standard timer controls are not using the expected selector at size {sizeIndex}.");
        var stepNames = new[] { "CompactTimerMinusMinute", "CompactTimerPlusMinute", "CompactTimerMinusHour", "CompactTimerPlusHour" };
        foreach (var name in stepNames)
        {
            var button = (Button)window.FindName(name)!;
            if (button.HorizontalContentAlignment != HorizontalAlignment.Center)
                throw new InvalidOperationException($"{name} is not centered at size {sizeIndex}.");
            EnsureInside(button, compactPicker, name, sizeIndex);
        }
        EnsureInside(compactPicker, timerCard, "CompactStandardTimePicker", sizeIndex);
        foreach (var name in new[] { "ShutdownButton", "RestartButton", "LogoutButton", "LockButton" })
            EnsureInside((FrameworkElement)window.FindName(name)!, immediateCard, name, sizeIndex);
        foreach (var name in new[] { "TimerShutdown", "TimerLock", "TimerRestart", "AbortTimerButton" })
            EnsureInside((FrameworkElement)window.FindName(name)!, timerCard, name, sizeIndex);
        var menu = (FrameworkElement)window.FindName("StandardLogMenuButton")!;
        var logActions = (FrameworkElement)window.FindName("StandardLogIconActions")!;
        if (menu.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"The old log menu is still visible at size {sizeIndex}.");
        foreach (var name in new[] { "StandardRefreshIconButton", "StandardClearIconButton", "StandardOpenIconButton" })
            EnsureInside((FrameworkElement)window.FindName(name)!, logActions, name, sizeIndex);

        var logPreview = (ListBox)window.FindName("StandardLogPreview")!;
        var entryType = typeof(MainWindow).GetNestedType("CompactLogEntry", BindingFlags.NonPublic)!;
        var entryConstructor = entryType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .First(constructor => constructor.GetParameters().Length == 2);
        var previewEntries = (System.Collections.IList)logPreview.ItemsSource;
        while (previewEntries.Count < 3)
            previewEntries.Insert(0, entryConstructor.Invoke(["10:42", "Systemprüfung abgeschlossen."]));
        window.UpdateLayout();
        if (logPreview.ActualHeight < 54)
            throw new InvalidOperationException($"Three log rows do not fit at size {sizeIndex}.");
    }

    private static void VerifyRepairFooterRouting(MainWindow window)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(MainWindow);
        var log = (ListBox)window.FindName("StandardLogPreview")!;
        type.GetMethod("AddLogEntry", flags)!.Invoke(window, ["[16.10.2026, 10:42:00] EVENT_CONTEXT_TEST"]);
        var repairType = type.Assembly.GetType("PowerControlProWpf.RepairLogEntry", true)!;
        var entry = Activator.CreateInstance(repairType, ["16.10.2026 10:43:00", "DISM", "INFO", "REPAIR_CONTEXT_TEST"]);
        type.GetMethod("AddRepairLogEntry", flags)!.Invoke(window, [entry]);
        string FirstMessage() => log.Items[0].GetType().GetProperty("Message")!.GetValue(log.Items[0])!.ToString()!;
        if (FirstMessage() != "REPAIR_CONTEXT_TEST")
            throw new InvalidOperationException("The repair footer is displaying the event log.");
        var tooltip = log.Items[0].GetType().GetProperty("Tooltip")!.GetValue(log.Items[0])!.ToString()!;
        if (!tooltip.Contains("DISM") || !tooltip.Contains("INFO") || !tooltip.Contains("16.10.2026 10:43:00"))
            throw new InvalidOperationException("The compact repair log lost its phase, level or full timestamp.");

        type.GetField("_repairRunning", flags)!.SetValue(window, true);
        type.GetMethod("UpdateCompactLogContext", flags)!.Invoke(window, null);
        var refresh = (Button)window.FindName("StandardRefreshIconButton")!;
        var clear = (Button)window.FindName("StandardClearIconButton")!;
        if (refresh.IsEnabled || clear.IsEnabled)
            throw new InvalidOperationException("Repair log changes remain enabled during a repair.");
        var count = log.Items.Count;
        type.GetMethod("RefreshCompactLog", flags)!.Invoke(window, [null, new RoutedEventArgs()]);
        type.GetMethod("ClearCompactLog", flags)!.Invoke(window, [null, new RoutedEventArgs()]);
        if (log.Items.Count != count || FirstMessage() != "REPAIR_CONTEXT_TEST")
            throw new InvalidOperationException("A running repair log was cleared or reloaded.");
        type.GetField("_repairRunning", flags)!.SetValue(window, false);
        type.GetMethod("ShowUsersPage", flags)!.Invoke(window, null);
        if (FirstMessage() != "EVENT_CONTEXT_TEST" || !refresh.IsEnabled || !clear.IsEnabled)
            throw new InvalidOperationException("Page two did not restore the event log and its actions.");
        type.GetMethod("ShowRepairPage", flags)!.Invoke(window, null);
        if (FirstMessage() != "REPAIR_CONTEXT_TEST")
            throw new InvalidOperationException("Returning to page three lost the repair log.");
        Console.WriteLine("PASS repair footer: separate logs, live entries, page switching and running-repair guards");
    }

    private static void VerifyRepairProgressAnimation(MainWindow window)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(MainWindow);
        var progress = (ProgressBar)window.FindName("RepairProgress")!;
        var running = type.GetField("_repairRunning", flags)!;
        var percent = type.GetField("_repairPercent", flags)!;
        var transform = type.GetField("_repairProgressTransform", flags)!;
        var update = type.GetMethod("UpdateRepairProgressAnimation", flags)!;
        try
        {
            running.SetValue(window, true);
            foreach (var value in new[] { 45.0, 0.0 })
            {
                percent.SetValue(window, value);
                progress.Value = value;
                update.Invoke(window, null);
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                update.Invoke(window, null);
                if (progress.IsIndeterminate != (value == 0) ||
                    transform.GetValue(window) is not System.Windows.Media.TranslateTransform movement || !movement.HasAnimatedProperties)
                    throw new InvalidOperationException("Repair progress animation did not start in its expected state.");
                var start = movement.X;
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(140) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                if (Math.Abs(movement.X - start) < .1 || Math.Abs(progress.Value - value) > .001 || (double)percent.GetValue(window)! != value)
                    throw new InvalidOperationException("Repair activity is static or it changed measured progress.");
            }
            type.GetMethod("ShowUsersPage", flags)!.Invoke(window, null);
            window.UpdateLayout();
            update.Invoke(window, null);
            if (transform.GetValue(window) is not null)
                throw new InvalidOperationException("Repair activity animation kept running on a hidden page.");
            type.GetMethod("ShowRepairPage", flags)!.Invoke(window, null);
            window.UpdateLayout();
            update.Invoke(window, null);
            if (transform.GetValue(window) is null)
                throw new InvalidOperationException("Repair activity did not resume when returning to its page.");
        }
        finally
        {
            running.SetValue(window, false);
            percent.SetValue(window, 0.0);
            progress.Value = 0;
            update.Invoke(window, null);
        }
        if (progress.IsIndeterminate || transform.GetValue(window) is not null)
            throw new InvalidOperationException("Repair animation did not stop after the repair ended.");
        Console.WriteLine("PASS repair animation: moving light, measured values preserved, waiting state and visibility lifecycle");
    }

    private static void VerifyTimerAbortAppearance(MainWindow window, string output, int sizeIndex)
    {
        var button = (Button)window.FindName("AbortTimerButton")!;
        var card = (FrameworkElement)window.FindName("TimerCardSurface")!;
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var enabled = button.IsEnabled;
        button.ApplyTemplate();
        var surface = (Border)button.Template.FindName("AbortBorder", button);
        var originalBackground = surface.Background;
        var originalOutline = surface.BorderBrush;
        var red = System.Windows.Media.Color.FromRgb(152, 40, 63);
        try
        {
            if (sizeIndex <= 1 && Math.Abs(button.ActualHeight - 56) > 1)
                throw new InvalidOperationException("Timer abort does not match the approved 56 pixel preview.");
            EnsureInside(button, card, "Timer abort", sizeIndex);
            button.IsEnabled = true;
            Capture(window, Path.Combine(output, $"timer-abort-normal-{(int)window.Width}x{(int)window.Height}.png"));
            button.SetValue(key, true);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render); window.UpdateLayout();
            if (surface.Background is not System.Windows.Media.SolidColorBrush background || background.Color != red ||
                surface.BorderBrush is not System.Windows.Media.SolidColorBrush outline || outline.Color != System.Windows.Media.Color.FromRgb(255, 75, 75))
                throw new InvalidOperationException("Active timer abort hover is not red.");
            Capture(window, Path.Combine(output, $"timer-abort-hover-{(int)window.Width}x{(int)window.Height}.png"));
            button.SetValue(key, false);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (!Equals(surface.Background, originalBackground) || !Equals(surface.BorderBrush, originalOutline))
                throw new InvalidOperationException("Timer abort did not restore its normal appearance.");
            button.IsEnabled = false;
            button.SetValue(key, true);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            if (surface.Background is System.Windows.Media.SolidColorBrush disabled && disabled.Color == red)
                throw new InvalidOperationException($"Disabled timer abort incorrectly shows active hover: enabled={button.IsEnabled}, background={surface.Background}, original={originalBackground}.");
        }
        finally { button.SetValue(key, false); button.IsEnabled = enabled; }
        Console.WriteLine($"PASS timer abort {window.Width}x{window.Height}: height {button.ActualHeight}, red active hover, reset and disabled state");
    }

    private static void VerifyRepairActionLocks(MainWindow window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        var running = type.GetField("_repairRunning", flags)!;
        var cancellation = type.GetField("_repairCancellation", flags)!;
        var update = type.GetMethod("SetTimerState", flags)!;
        var create = (Button)window.FindName("CreateUserButton")!;
        var admin = (Button)window.FindName("AdminRightsBadge")!;
        using var token = new CancellationTokenSource();
        try
        {
            running.SetValue(window, true);
            cancellation.SetValue(window, token);
            foreach (var stopping in new[] { false, true })
            {
                if (stopping) token.Cancel();
                update.Invoke(window, [false]);
                if (create.IsEnabled || admin.IsEnabled)
                    throw new InvalidOperationException("Repair actions unlocked while running or awaiting cancellation completion.");
                type.GetMethod("ShowUsersPage", flags)!.Invoke(window, null);
                if (create.IsEnabled || admin.IsEnabled || !((FrameworkElement)window.FindName("UserPage")!).IsVisible)
                    throw new InvalidOperationException("Page switching either unlocked actions or was blocked.");
                var windowCount = Application.Current.Windows.Count;
                type.GetMethod("CreateUser", flags)!.Invoke(window, [null, new RoutedEventArgs()]);
                type.GetMethod("OpenAdminStatus", flags)!.Invoke(window, [null, new RoutedEventArgs()]);
                type.GetMethod("RequestElevation", flags)!.Invoke(window, [true]);
                if (Application.Current.Windows.Count != windowCount)
                    throw new InvalidOperationException("A guarded repair action opened a dialog.");
                type.GetMethod("ShowRepairPage", flags)!.Invoke(window, null);
            }
        }
        finally
        {
            cancellation.SetValue(window, null);
            running.SetValue(window, false);
            update.Invoke(window, [false]);
        }
        if (!create.IsEnabled || !admin.IsEnabled)
            throw new InvalidOperationException("Actions remain locked after repair completion.");
        Console.WriteLine("PASS repair action locks: create user/admin blocked through cancellation, handler guards, page navigation and unlock after completion");
    }

    private static void VerifyExpandedLog(MainWindow window, string output, int sizeIndex)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(MainWindow);
        var panel = (Border)window.FindName("ExpandedLogPanel")!;
        var list = (ListBox)window.FindName("ExpandedLogList")!;
        var close = (Button)window.FindName("ExpandedLogCloseButton")!;
        var setOpen = type.GetMethod("SetExpandedLogOpen", flags)!;
        void Open(bool value, bool animate = false) => setOpen.Invoke(window, [value, animate]);
        void Settle()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.UpdateLayout();
        }
        var events = (System.Collections.IList)type.GetField("_eventLogEntries", flags)!.GetValue(window)!;
        var repairs = (System.Collections.IList)type.GetField("_repairLogEntries", flags)!.GetValue(window)!;
        var entryType = type.GetNestedType("CompactLogEntry", BindingFlags.NonPublic)!;
        object Entry(string message) => Activator.CreateInstance(entryType, "14:38", message)!;
        var savedEvents = events.Cast<object>().ToArray();
        var savedRepairs = repairs.Cast<object>().ToArray();
        var detailed = type.GetMethod("ToCompactLogEntry", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["[16.10.2026, 14:38:12] Vollständige Testmeldung"] )!;
        if (detailed.GetType().GetProperty("DisplayTime")!.GetValue(detailed)?.ToString() != "14:38:12" ||
            !detailed.GetType().GetProperty("Tooltip")!.GetValue(detailed)!.ToString()!.Contains("16.10.2026"))
            throw new InvalidOperationException("Expanded event logs lose timestamp precision.");
        var originalWidth = window.Width;
        var originalHeight = window.Height;
        try
        {
            events.Clear(); repairs.Clear();
            for (var index = 0; index < 14; index++)
            {
                events.Add(Entry($"Beispieleintrag {index + 1}: Die vollständige Meldung bleibt in der großen Protokollansicht lesbar und wird bei Bedarf automatisch umgebrochen."));
                repairs.Add(Entry($"Reparaturmeldung {index + 1}: Fortschritt und Ausgaben sind vollständig einsehbar."));
            }
            while ((int)type.GetField("_pageIndex", flags)!.GetValue(window)! > 0)
                ((Button)window.FindName("PagePrevious")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Open(true); Settle();
            if (!ReferenceEquals(list.ItemsSource, events) || list.Items.Count != 14)
                throw new InvalidOperationException("Expanded event log lost entries or uses the wrong collection.");
            EnsureInside(panel, (FrameworkElement)window.FindName("Root")!, "Expanded log", sizeIndex);
            EnsureInside(close, panel, "Expanded log close button", sizeIndex);
            var footer = (FrameworkElement)window.FindName("StandardStatusLogBar")!;
            if (panel.TranslatePoint(new Point(0, panel.ActualHeight), window).Y > footer.TranslatePoint(new Point(), window).Y)
                throw new InvalidOperationException("Expanded log hides its compact trigger.");
            var scroll = VisualChildren<ScrollViewer>(list).First();
            if (scroll.ScrollableHeight <= 0) throw new InvalidOperationException("Expanded log cannot scroll to older entries.");
            events.Insert(0, Entry("Neu eingetroffen: Dieser Eintrag erscheint automatisch in der geöffneten Ansicht."));
            Settle();
            if (list.Items.Count != 15) throw new InvalidOperationException("New entries do not appear live.");
            Capture(window, Path.Combine(output, $"expanded-event-{(int)window.Width}x{(int)window.Height}.png"));
            var color = ((System.Windows.Media.SolidColorBrush)close.Foreground).Color;
            var mainClose = (Button)window.FindName("HeaderCloseButton")!;
            if (color.R <= color.G || color.R <= color.B || ((System.Windows.Media.SolidColorBrush)mainClose.Foreground).Color != color)
                throw new InvalidOperationException("Close symbols do not use the same red.");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForAnimation(() => panel.Visibility == Visibility.Collapsed); Settle();
            if (panel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Close X does not collapse expanded log.");
            var smallLog = (ListBox)window.FindName("StandardLogPreview")!;
            smallLog.ScrollIntoView(smallLog.Items[0]); Settle();
            var item = (ListBoxItem)smallLog.ItemContainerGenerator.ContainerFromIndex(0)!;
            void ClickEntry() => smallLog.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = item });
            ClickEntry(); WaitForAnimation(); Settle();
            if (!panel.IsVisible) throw new InvalidOperationException("Clicking a compact entry does not open all logs.");
            ClickEntry(); WaitForAnimation(() => panel.Visibility == Visibility.Collapsed); Settle();
            if (panel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Clicking a compact entry again does not close the view.");
            Open(true, true); Open(false, true); Open(true, true); WaitForAnimation();
            if (!panel.IsVisible) throw new InvalidOperationException("Rapid toggles leave expanded log in the wrong state.");
            var source = PresentationSource.FromVisual(window)!;
            window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            WaitForAnimation(() => !panel.IsVisible);
            if (panel.IsVisible) throw new InvalidOperationException("Escape does not close the log view.");
            Open(true);
            type.GetMethod("ShowRepairPage", flags)!.Invoke(window, null); Settle();
            if (!ReferenceEquals(list.ItemsSource, repairs) || list.Items.Count != 14)
                throw new InvalidOperationException("Page change does not switch expanded log to repair context.");
            Capture(window, Path.Combine(output, $"expanded-repair-{(int)window.Width}x{(int)window.Height}.png"));
            repairs.Clear(); Settle();
            var empty = (TextBlock)panel.FindName("Empty");
            if (!empty.IsVisible || list.Items.Count != 0) throw new InvalidOperationException("Empty expanded log is not represented correctly.");
            type.GetMethod("ShowUsersPage", flags)!.Invoke(window, null); Settle();
            if (!ReferenceEquals(list.ItemsSource, events)) throw new InvalidOperationException("User page expanded log is not the event log.");
            if (window.Width != originalWidth || window.Height != originalHeight)
                throw new InvalidOperationException("Expanded log changed the main window size.");
        }
        finally
        {
            Open(false);
            events.Clear(); foreach (var entry in savedEvents) events.Add(entry);
            repairs.Clear(); foreach (var entry in savedRepairs) repairs.Add(entry);
        }
        Console.WriteLine($"PASS expanded logs {originalWidth}x{originalHeight}: entry clicks, close X, rapid toggles, full list, live updates, scroll, empty state and page context");
    }

    private static void WaitForAnimation(Func<bool>? complete = null)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        var timer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(complete is null ? 210 : 20) };
        timer.Tick += (_, _) =>
        {
            if (complete is not null && !complete() && DateTime.UtcNow < deadline) return;
            timer.Stop(); frame.Continue = false;
        };
        timer.Start(); Dispatcher.PushFrame(frame);
        if (complete?.Invoke() == false) throw new InvalidOperationException("The UI animation did not complete within two seconds.");
    }

    private static void EnsureInside(FrameworkElement element, FrameworkElement container, string name, int sizeIndex)
    {
        var bounds = element.TransformToAncestor(container).TransformBounds(new Rect(new Point(), element.RenderSize));
        if (bounds.Left < -1 || bounds.Top < -1 || bounds.Right > container.ActualWidth + 1 || bounds.Bottom > container.ActualHeight + 1)
            throw new InvalidOperationException($"{name} leaves its card at size {sizeIndex}: {bounds} in {container.ActualWidth:0}x{container.ActualHeight:0}.");
    }

    private static void CaptureDialog(Window dialog, string file)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        dialog.Show();
        dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        dialog.UpdateLayout();
        VerifyCrossButtons(dialog, null);
        Capture(dialog, file);
        dialog.Close();
    }

    private static void VerifyCrossButtons(Window window, string? hoverFile)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var buttons = VisualChildren<Button>(window).Where(button => button.IsVisible &&
            (button.Content is string content && content == "×" ||
             VisualChildren<TextBlock>(button).Any(text => text.Text is "Abbrechen" or "Cancel" or "Später" or "Later" or "Schließen" or "Close"))).ToArray();
        var count = 0;
        foreach (var button in buttons)
        {
            button.ApplyTemplate(); window.UpdateLayout();
            if (button.Template.FindName("RedCancelFrame", button) is Border redFrame)
            {
                VerifyClassicDialogButton(button, redFrame);
                count++;
                continue;
            }
            if (button.Template.FindName("CrossFrame", button) is not Border frame)
                throw new InvalidOperationException($"X button has no unified template in {window.GetType().Name}: {button.Content}; {string.Join(" / ", VisualChildren<System.Windows.Shapes.Path>(button).Select(path => path.Data))}");
            if (frame.BorderThickness.Left == 1.5)
            {
                VerifyClassicDialogButton(button, frame);
                continue;
            }
            static System.Windows.Media.Color Colour(object value) => ((System.Windows.Media.SolidColorBrush)value).Color;
            var red = System.Windows.Media.Color.FromRgb(255, 75, 75);
            if (Colour(frame.Background).A != 0 || frame.Effect is not null ||
                Colour(frame.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)) != red)
                throw new InvalidOperationException("A resting X button is not transparent with a red glyph.");
            try
            {
                button.SetValue(key, true);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render); window.UpdateLayout();
                if (Colour(frame.Background) != System.Windows.Media.Color.FromRgb(233, 39, 73) ||
                    frame.Effect is not System.Windows.Media.Effects.DropShadowEffect { Opacity: > 0, BlurRadius: > 0 } ||
                    Colour(frame.GetValue(System.Windows.Documents.TextElement.ForegroundProperty)) != System.Windows.Media.Colors.White)
                    throw new InvalidOperationException("X hover lacks red background, glow or white glyph colour.");
                foreach (var glyph in VisualChildren<System.Windows.Shapes.Path>(button))
                    if (System.Windows.Data.BindingOperations.IsDataBound(glyph, System.Windows.Shapes.Shape.StrokeProperty) &&
                        Colour(glyph.Stroke) != System.Windows.Media.Colors.White)
                        throw new InvalidOperationException("An X path does not turn white on hover.");
                if (hoverFile is not null && count == 0) Capture(window, hoverFile);
            }
            finally
            {
                button.SetValue(key, false);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render); window.UpdateLayout();
            }
            if (frame.Effect is not null || Colour(frame.Background).A != 0)
                throw new InvalidOperationException("X hover appearance remains after leaving.");
            count++;
        }
        if (hoverFile is not null) Console.WriteLine($"PASS X states: {count} visible buttons, transparent/red normal, red glow/white hover and reset");
    }

    private static void CaptureRepairDialogs(string output)
    {
        var assembly = typeof(MainWindow).Assembly;
        var paletteType = assembly.GetType("PowerControlProWpf.ThemePalette", true)!;
        var palette = paletteType.GetMethod("For", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["Standard"]);
        var summaryType = assembly.GetType("PowerControlProWpf.RepairResultSummary", true)!;
        var summary = summaryType.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["sfc", 0, false, "Keine Integritätsverletzungen gefunden", "", TimeSpan.FromMinutes(6), false]);
        var resultType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.RepairResultWindow", true)!;
        var resultConstructor = resultType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        CaptureDialog((Window)resultConstructor.Invoke([summary, palette, false]), Path.Combine(output, "repair-result.png"));
        var errorSummary = summaryType.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["dism", 87, false, "", "Fehlende Reparaturquelle", TimeSpan.FromSeconds(25), false]);
        CaptureDialog((Window)resultConstructor.Invoke([errorSummary, palette, false]), Path.Combine(output, "repair-result-error.png"));
        foreach (var theme in new[] { "Classic", "Dark", "Neon Pink" })
        {
            var themePalette = paletteType.GetMethod("For", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [theme]);
            CaptureDialog((Window)resultConstructor.Invoke([summary, themePalette, false]),
                Path.Combine(output, $"repair-result-{theme.Replace(' ', '-')}.png"));
        }
        var confirmType = assembly.GetType("PowerControlProWpf.RepairConfirmWindow", true)!;
        var confirmConstructor = confirmType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        CaptureDialog((Window)confirmConstructor.Invoke(["sfc /scannow", palette, false]), Path.Combine(output, "repair-confirm.png"));
        var classicPalette = paletteType.GetMethod("For", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["Classic"]);
        CaptureDialog((Window)confirmConstructor.Invoke(["DISM /Online /Cleanup-Image /RestoreHealth", classicPalette, false]),
            Path.Combine(output, "repair-confirm-Classic.png"));
        var messageType = assembly.GetType("PowerControlProWpf.ThemedMessageBoxWindow", true)!;
        var messageConstructor = messageType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        CaptureDialog((Window)messageConstructor.Invoke(["Ereignisprotokoll unwiderruflich löschen?", "Ereignisprotokoll löschen?",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, palette, false]), Path.Combine(output, "confirm-delete.png"));
        CaptureDialog((Window)messageConstructor.Invoke(["Ereignisprotokoll unwiderruflich löschen?", "Ereignisprotokoll löschen?",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, classicPalette, false]), Path.Combine(output, "confirm-delete-Classic.png"));
        var logType = assembly.GetType("PowerControlProWpf.RepairLogWindow", true)!;
        var logConstructor = logType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var logEntryType = assembly.GetType("PowerControlProWpf.RepairLogEntry", true)!;
        var emptyEntries = Array.CreateInstance(logEntryType, 0);
        CaptureDialog((Window)logConstructor.Invoke([emptyEntries, palette, false, null]), Path.Combine(output, "repair-log.png"));
    }

    private static void VerifyPasswordVisibility(Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        dialog.Show();
        var field = (PasswordBox)dialog.GetType().GetField("_password", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        field.Password = "OnlyForUiTest123!";
        var eye = VisualChildren<Button>(dialog).First(button => (button.ToolTip as string) == "Kennwort anzeigen");
        eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye));
        if (field.Visibility != Visibility.Collapsed ||
            !VisualChildren<TextBox>(dialog).Any(box => box.Visibility == Visibility.Visible && box.Text == "OnlyForUiTest123!"))
            throw new InvalidOperationException("Password reveal button does not display the entered value.");
        eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye));
        if (field.Visibility != Visibility.Visible || field.Password != "OnlyForUiTest123!" ||
            VisualChildren<TextBox>(dialog).Any(box => box.Text == "OnlyForUiTest123!"))
            throw new InvalidOperationException("Password reveal button did not restore masking.");
        dialog.Close();
    }

    private static void VerifyUserEditor(Window create, Window edit)
    {
        foreach (var dialog in new[] { create, edit })
        {
            var root = (Border)dialog.Content;
            var layout = (Grid)root.Child;
            var footer = (Border)layout.Children.Cast<UIElement>().Single(child => Grid.GetRow(child) == 3);
            var actions = (Grid)footer.Child;
            if (!ReferenceEquals(root.Background, ((MainWindow)dialog.Owner).TryFindResource("CardSurfaceBrush")) ||
                root.Padding.Bottom < 10 || layout.RowDefinitions[3].Height.Value < 50 || actions.Margin.Bottom < 4)
                throw new InvalidOperationException("User dialog does not match the card surface or has cramped footer buttons.");
        }
        create.WindowStartupLocation = WindowStartupLocation.Manual;
        create.Left = -10000;
        create.Top = -10000;
        create.ShowActivated = false;
        create.Show();
        create.UpdateLayout();
        var fields = create.GetType();
        var noPassword = (CheckBox)fields.GetField("_withoutPassword", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(create)!;
        var password = (PasswordBox)fields.GetField("_password", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(create)!;
        var repeat = (PasswordBox)fields.GetField("_repeat", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(create)!;
        password.Password = "OnlyForUiTest123!";
        noPassword.IsChecked = true;
        if (password.Password.Length != 0 || repeat.Password.Length != 0 || password.IsEnabled || repeat.IsEnabled)
            throw new InvalidOperationException("Passwordless mode did not clear and disable both password fields.");
        noPassword.IsChecked = false;
        if (!password.IsEnabled || !repeat.IsEnabled)
            throw new InvalidOperationException("Password fields did not return after disabling passwordless mode.");
        create.Close();

        edit.WindowStartupLocation = WindowStartupLocation.Manual;
        edit.Left = -10000;
        edit.Top = -10000;
        edit.ShowActivated = false;
        edit.Show();
        edit.UpdateLayout();
        var name = (TextBox)fields.GetField("_name", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(edit)!;
        if (name.IsReadOnly) throw new InvalidOperationException("The account name is still read-only.");
        var reset = VisualChildren<Button>(edit).First(button =>
            VisualChildren<TextBlock>(button).Any(text => text.Text == "Kennwort zurücksetzen"));
        var delete = VisualChildren<Button>(edit).First(button =>
            VisualChildren<TextBlock>(button).Any(text => text.Text == "Löschen"));
        if (Math.Abs(reset.ActualWidth - delete.ActualWidth) > 2 || reset.ActualWidth < 140)
            throw new InvalidOperationException("Reset and delete buttons do not have equal usable widths.");
        foreach (var button in new[] { reset, delete })
        {
            var content = (FrameworkElement)button.Content;
            if (content.ActualWidth > button.ActualWidth - 6)
                throw new InvalidOperationException("An account action label is clipped.");
        }
        edit.Close();
        Console.WriteLine("PASS user dialogs: rename field, equal action buttons and passwordless switch");
    }

    private static void VerifyPasswordResetValidation(Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        var type = dialog.GetType();
        var first = (PasswordBox)type.GetField("_first", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var second = (PasswordBox)type.GetField("_second", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var error = (TextBlock)type.GetField("_error", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var confirm = type.GetMethod("Confirm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var validated = false;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            confirm.Invoke(dialog, [null, new RoutedEventArgs()]);
            if (dialog.DialogResult == true || error.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Empty password was accepted.");
            first.Password = "LocalUiTest123!";
            var eye = VisualChildren<Button>(dialog).First(button => (button.ToolTip as string) == "Kennwort anzeigen");
            eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye));
            if (first.Visibility != Visibility.Collapsed ||
                !VisualChildren<TextBox>(dialog).Any(box => box.Text == "LocalUiTest123!"))
                throw new InvalidOperationException("Reset dialog did not reveal the password.");
            eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, eye));
            if (first.Visibility != Visibility.Visible || first.Password != "LocalUiTest123!" ||
                VisualChildren<TextBox>(dialog).Any(box => box.Text == "LocalUiTest123!"))
                throw new InvalidOperationException("Reset dialog did not mask the password again.");
            second.Password = "DifferentUiTest123!";
            confirm.Invoke(dialog, [null, new RoutedEventArgs()]);
            if (dialog.DialogResult == true || !error.Text.Contains("stimmen nicht", StringComparison.Ordinal))
                throw new InvalidOperationException("Mismatched passwords were accepted.");
            second.Password = first.Password;
            confirm.Invoke(dialog, [null, new RoutedEventArgs()]);
            validated = true;
        }), DispatcherPriority.Loaded);
        var result = type.GetProperty("Result", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (dialog.ShowDialog() != true || !validated ||
            !IsPasswordChoice(result.GetValue(dialog), "LocalUiTest123!", false))
            throw new InvalidOperationException("Matching password did not produce a result.");
    }

    private static void VerifyPasswordRemovalChoice(Window dialog, string imagePath)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000;
        dialog.Top = -10000;
        dialog.ShowActivated = false;
        var type = dialog.GetType();
        var first = (PasswordBox)type.GetField("_first", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var second = (PasswordBox)type.GetField("_second", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var remove = (CheckBox)type.GetField("_removePassword", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var fields = (StackPanel)type.GetField("_passwordFields", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dialog)!;
        var confirm = type.GetMethod("Confirm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var validated = false;
        dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            first.Password = "OnlyForUiTest123!";
            second.Password = "OnlyForUiTest123!";
            remove.IsChecked = true;
            if (first.Password.Length != 0 || second.Password.Length != 0 || fields.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Password removal did not clear and hide existing input.");
            dialog.UpdateLayout();
            Capture(dialog, imagePath);
            remove.IsChecked = false;
            if (fields.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Password fields did not return after deselecting removal.");
            remove.IsChecked = true;
            confirm.Invoke(dialog, [null, new RoutedEventArgs()]);
            validated = true;
        }), DispatcherPriority.Loaded);
        var result = type.GetProperty("Result", BindingFlags.NonPublic | BindingFlags.Instance)!;
        if (dialog.ShowDialog() != true || !validated ||
            !IsPasswordChoice(result.GetValue(dialog), "", true))
            throw new InvalidOperationException("Password removal did not produce the explicit removal result.");
        Console.WriteLine("PASS password removal choice: clear input, hide fields and distinct result");
    }

    private static bool IsPasswordChoice(object? choice, string password, bool removePassword) =>
        choice is not null &&
        (string?)choice.GetType().GetProperty("Password")?.GetValue(choice) == password &&
        (bool?)choice.GetType().GetProperty("RemovePassword")?.GetValue(choice) == removePassword;

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void VerifyUpdateStates(MainWindow window)
    {
        var enumType = typeof(MainWindow).GetNestedType("UpdateCheckState", BindingFlags.NonPublic)!;
        var setState = typeof(MainWindow).GetMethod("SetUpdateCheckState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var bell = (System.Windows.Shapes.Path)window.FindName("UpdateNoticeIcon")!;
        var bellButton = (Button)window.FindName("UpdateNoticeButton")!;
        var optionsButton = (Button)window.FindName("CheckUpdatesButton")!;
        var optionsLabel = (TextBlock)window.FindName("CheckUpdatesLabel")!;
        if (window.FindName("UpdateNoticeCount") is not null || window.FindName("UpdateHeaderLabel") is not null ||
            window.FindName("FooterUpdateStatus") is not null || window.FindName("SettingsNoticeDetail") is not null ||
            window.FindName("SettingsNoticeInstalled") is not null || window.FindName("SettingsNoticeClose") is not null)
            throw new InvalidOperationException("The bell header or status dialog contains text, badge, or controls removed by the approved design.");
        var bellAutomationName = System.Windows.Automation.AutomationProperties.GetName(bellButton) ?? string.Empty;
        if (bellButton.Content is not System.Windows.Shapes.Path || bellButton.Width > 60 ||
            !(bellAutomationName.Contains("Glocken-Status", StringComparison.OrdinalIgnoreCase) ||
              bellAutomationName.Contains("bell status", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The header bell is not a compact, text-free status button: width={bellButton.Width}, name='{bellAutomationName}'.");
        if (window.FindName("SettingsNoticeAction") is not null)
            throw new InvalidOperationException("The update notice must not contain a check button.");
        var rows = new[]
        {
            (Border)window.FindName("NoticeCurrentRow")!,
            (Border)window.FindName("NoticeConnectionRow")!,
            (Border)window.FindName("NoticeAvailableRow")!
        };
        var colors = new List<System.Windows.Media.Color>();
        var buttonColors = new List<System.Windows.Media.Color>();
        foreach (var (state, expected, activeRow) in new[] { ("Current", "Aktuell", 0), ("Failed", "Prüfung fehlgeschlagen", 1), ("Available", "Update", 2) })
        {
            setState.Invoke(window, [Enum.Parse(enumType, state)]);
            if (rows.Any(row => row.Opacity != 1))
                throw new InvalidOperationException($"The bell status legend changes row visibility in {state}.");
            var connectionLabel = (TextBlock)window.FindName("NoticeConnectionLabel")!;
            if (state == "Failed" && !connectionLabel.Text.Contains(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected failed status label: {connectionLabel.Text}");
            var statusColor = ((System.Windows.Media.SolidColorBrush)bell.Stroke).Color;
            colors.Add(statusColor);
            var bodyKey = state == "Current" ? "ReadyBrush" : state == "Available" ? "DangerBorderBrush" : "WarningBrush";
            var headerKey = state == "Current" ? "HeaderBellCurrentBrush" : state == "Available" ? "HeaderBellAvailableBrush" : "HeaderBellWarningBrush";
            if (((System.Windows.Media.SolidColorBrush)optionsButton.BorderBrush).Color != ((System.Windows.Media.SolidColorBrush)window.Resources[bodyKey]).Color ||
                statusColor != ((System.Windows.Media.SolidColorBrush)window.Resources[headerKey]).Color)
                throw new InvalidOperationException($"Update colours do not match their header/content surfaces: {state}.");
            buttonColors.Add(((System.Windows.Media.SolidColorBrush)optionsButton.Background).Color);
            if (optionsLabel.FontSize < 11 || string.IsNullOrWhiteSpace(optionsLabel.Text))
                throw new InvalidOperationException("Options update button label is too small or missing.");
            var iconBox = (FrameworkElement)window.FindName(activeRow switch
            {
                0 => "NoticeCurrentIconBox",
                1 => "NoticeConnectionIconBox",
                _ => "NoticeAvailableIconBox"
            })!;
            if (iconBox.Width != 42 || iconBox.Height != 42)
                throw new InvalidOperationException($"Bell status icon block has incorrect proportions in {state}: {iconBox.Width}x{iconBox.Height}.");
        }
        if (colors.Distinct().Count() != 3)
            throw new InvalidOperationException("Update bell colors are not distinct.");
        if (buttonColors.Distinct().Count() != 3)
            throw new InvalidOperationException("Options update button backgrounds are not distinct.");
        setState.Invoke(window, [Enum.Parse(enumType, "Checking")]);
        if (!((TextBlock)window.FindName("UpdateStatus")!).Text.Contains("prüf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Update check status is not available in the options status area.");
    }

    private static void CaptureUpdateBellStates(MainWindow window, string output)
    {
        var enumType = typeof(MainWindow).GetNestedType("UpdateCheckState", BindingFlags.NonPublic)!;
        var setState = typeof(MainWindow).GetMethod("SetUpdateCheckState", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var state in new[] { "Current", "Failed", "Available" })
        {
            setState.Invoke(window, [Enum.Parse(enumType, state)]);
            window.UpdateLayout();
            Capture(window, Path.Combine(output, $"update-bell-{state.ToLowerInvariant()}.png"));
        }
        setState.Invoke(window, [Enum.Parse(enumType, "Checking")]);
    }

    private static void VerifyAdminBadgeStates(MainWindow window, string output)
    {
        var appearance = typeof(MainWindow).GetMethod("UpdateAdminBadgeAppearance", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var badge = (Button)window.FindName("AdminRightsBadge")!;
        var label = (TextBlock)window.FindName("AdminRightsStatus")!;
        typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["Standard"]);
        foreach (var (elevated, expected, file) in new[]
        {
            (false, "Ohne Admin-Start", "admin-without.png"),
            (true, "Mit Admin-Start", "admin-with.png")
        })
        {
            appearance.Invoke(window, [elevated, "Standard"]);
            window.UpdateLayout();
            if (label.Text != expected || badge.ActualWidth < 150 || badge.ActualWidth > 180)
                throw new InvalidOperationException($"Admin badge text or dimensions differ in {file}.");
            var color = ((System.Windows.Media.SolidColorBrush)badge.BorderBrush).Color;
            if (elevated ? color.G <= color.R : color.R <= color.G)
                throw new InvalidOperationException($"Admin badge color differs in {file}.");
            Capture(window, Path.Combine(output, file));
        }
        typeof(MainWindow).GetMethod("UpdateCurrentUserLabel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [null]);
        Console.WriteLine("PASS admin badge: both status labels, colors and dimensions");
    }

    private static void VerifyThemes(MainWindow window, string output)
    {
        var applyTheme = typeof(MainWindow).GetMethod("ApplyTheme", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var setOverlay = typeof(MainWindow).GetMethod("SetSettingsOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var switchPage = typeof(MainWindow).GetMethod("SwitchPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var showUsers = typeof(MainWindow).GetMethod("ShowUsersPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var showRepair = typeof(MainWindow).GetMethod("ShowRepairPage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var themeChoices = (ComboBox)window.FindName("ThemeBox")!;
        if (themeChoices.Items.Count != 7 || themeChoices.Items.OfType<ComboBoxItem>().Any(item =>
                (item.Tag?.ToString() ?? item.Content?.ToString()) is "Graphit" or "Retro"))
            throw new InvalidOperationException("Removed themes are still selectable.");
        var paletteType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.ThemePalette", true)!;
        var normalize = paletteType.GetMethod("NormalizeName", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var removed in new[] { "Graphit", "Retro", "unknown" })
            if ((string)normalize.Invoke(null, [removed])! != "Standard")
                throw new InvalidOperationException($"Removed theme {removed} did not fall back to Standard.");
        foreach (var theme in new[] { "Standard", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Classic", "Dark" })
        {
            themeChoices.SelectedItem = themeChoices.Items.OfType<ComboBoxItem>()
                .Single(item => (item.Tag?.ToString() ?? item.Content?.ToString()) == theme);
            applyTheme.Invoke(window, [theme]);
            if (theme == "Classic") VerifyClassicHeaderColors(window, output);
            VerifySharedThemeSizes(window, theme, output);
            var frames = (System.Collections.IList)typeof(MainWindow).GetField("_neonFrames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            if (frames.Count != (theme.StartsWith("Neon", StringComparison.Ordinal) ? 5 : 0))
                throw new InvalidOperationException($"Neon frames were not created/removed correctly for {theme}.");
            if (theme.StartsWith("Neon", StringComparison.Ordinal)) VerifyNeonPulse(window, theme);
            var timerCorner = (System.Windows.Shapes.Path)window.FindName("TimerCardCorner")!;
            var referenceCorner = (System.Windows.Shapes.Path)window.FindName("TimerReferenceCorner")!;
            var activeCorner = referenceCorner;
            var inactiveCorner = timerCorner;
            if (Grid.GetRow(activeCorner) != 1 ||
                Grid.GetColumn(activeCorner) != 0 ||
                activeCorner.Visibility != Visibility.Visible || inactiveCorner.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException($"Timer card corner is missing or inconsistent in {theme}.");
            VerifyUpdateStates(window);
            setOverlay.Invoke(window, [true, false]);
            if (theme == "Standard")
            {
                var enumType = typeof(MainWindow).GetNestedType("UpdateCheckState", BindingFlags.NonPublic)!;
                var setState = typeof(MainWindow).GetMethod("SetUpdateCheckState", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (var state in new[] { "Current", "Failed", "Available" })
                {
                    setState.Invoke(window, [Enum.Parse(enumType, state)]);
                    window.UpdateLayout();
                    Capture(window, Path.Combine(output, $"options-update-{state.ToLowerInvariant()}.png"));
                }
                setState.Invoke(window, [Enum.Parse(enumType, "Checking")]);
            }
            window.UpdateLayout();
            var rows = (Grid)window.FindName("SettingsRows")!;
            foreach (var labelName in new[] { "LanguageTitle", "ThemeTitle", "SizeTitle", "WarningTitle", "WarningSoundTitle", "WarningVolumeTitle", "AutoStartTitle", "ElevationTitle", "UpdateTitle" })
            {
                var label = (TextBlock)window.FindName(labelName)!;
                var right = label.TransformToAncestor(rows).Transform(new Point(label.ActualWidth, 0)).X;
                if (right > rows.ColumnDefinitions[0].ActualWidth - 1)
                    throw new InvalidOperationException($"{labelName} clipped in {theme}: {right:0.0} > {rows.ColumnDefinitions[0].ActualWidth:0.0}.");
            }
            Capture(window, Path.Combine(output, $"options-{theme.Replace(' ', '-')}.png"));
            setOverlay.Invoke(window, [false, true]);
            window.UpdateLayout();
            Capture(window, Path.Combine(output, $"notice-{theme.Replace(' ', '-')}.png"));
            setOverlay.Invoke(window, [false, false]);
            showUsers.Invoke(window, null);
            window.UpdateLayout();
            Capture(window, Path.Combine(output, $"users-{theme.Replace(' ', '-')}.png"));
            showRepair.Invoke(window, null);
            window.UpdateLayout();
            Capture(window, Path.Combine(output, $"repair-{theme.Replace(' ', '-')}.png"));
            var previous = (Button)window.FindName("PagePrevious")!;
            switchPage.Invoke(window, [previous, new RoutedEventArgs()]);
            switchPage.Invoke(window, [previous, new RoutedEventArgs()]);
            window.UpdateLayout();
            Capture(window, Path.Combine(output, $"home-{theme.Replace(' ', '-')}.png"));
            showUsers.Invoke(window, null);
            var editorType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.UserEditorDialog", true)!;
            var constructor = editorType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            CaptureDialog((Window)constructor.Invoke([window, null]), Path.Combine(output, $"user-create-{theme.Replace(' ', '-')}.png"));
            CaptureDialog((Window)constructor.Invoke([window, new UserEntry("demo", "Standardbenutzer", "Testkonto", "Aktiv", true, "Demo")]),
                Path.Combine(output, $"user-edit-{theme.Replace(' ', '-')}.png"));
            var resetType = typeof(MainWindow).Assembly.GetType("PowerControlProWpf.PasswordResetDialog", true)!;
            var resetConstructor = resetType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
            CaptureDialog((Window)resetConstructor.Invoke([window, "demo", false]),
                Path.Combine(output, $"password-reset-{theme.Replace(' ', '-')}.png"));
            Console.WriteLine($"PASS options theme {theme}");
        }
        setOverlay.Invoke(window, [false, false]);
        var selectedTheme = typeof(MainWindow).GetMethod("SelectedTheme", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, null);
        applyTheme.Invoke(window, [selectedTheme]);
        typeof(MainWindow).GetMethod("ApplyLayoutScale", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        window.UpdateLayout();
    }

    private static void VerifySharedThemeSizes(MainWindow window, string theme, string output)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var sizeField = typeof(MainWindow).GetField("_effectiveSizeIndex", flags)!;
        var originalIndex = sizeField.GetValue(window);
        var originalWidth = window.Width; var originalHeight = window.Height;
        var sizes = new[] { (860.0, 620.0), (900.0, 650.0), (1050.0, 670.0), (1200.0, 760.0) };
        for (var i = 0; i < sizes.Length; i++)
        {
            sizeField.SetValue(window, i);
            (window.Width, window.Height) = sizes[i];
            typeof(MainWindow).GetMethod("ApplyLayoutScale", flags)!.Invoke(window, null);
            typeof(MainWindow).GetMethod("ApplyHomeLayout", flags)!.Invoke(window, [theme]);
            typeof(MainWindow).GetMethod("ShowUsersPage", flags)!.Invoke(window, null);
            window.UpdateLayout();
            VerifyReferenceStripes(window, "UserCardSurface", "UserReferenceStripes", "UserList");
            var users = (FrameworkElement)window.FindName("UserCardSurface")!;
            var page = (FrameworkElement)window.FindName("UserPage")!;
            if (page.ActualHeight - users.ActualHeight > 15) throw new InvalidOperationException($"User card not full height: {theme} / {i}");
            typeof(MainWindow).GetMethod("ShowRepairPage", flags)!.Invoke(window, null);
            window.UpdateLayout();
            VerifyReferenceStripes(window, "RepairCardSurface", "RepairReferenceStripes", "RepairActionRow");
            var previous = (Button)window.FindName("PagePrevious")!;
            var switchPage = typeof(MainWindow).GetMethod("SwitchPage", flags)!;
            switchPage.Invoke(window, [previous, new RoutedEventArgs()]);
            switchPage.Invoke(window, [previous, new RoutedEventArgs()]);
            window.UpdateLayout();
            VerifyStandardPageOneLayout(window, i);
            if (theme == "Classic")
            {
                VerifyClassicNavigation(window, output, i);
                VerifyClassicCancellation(window, output, i);
                VerifyClassicDialogs(window, output, i);
            }
            Capture(window, Path.Combine(output, $"shared-{theme.Replace(' ', '-')}-{sizes[i].Item1:0}x{sizes[i].Item2:0}.png"));
            typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(window, [true, false]);
            window.UpdateLayout();
            VerifyOptionsComplete(window, $"{theme} {sizes[i].Item1:0}x{sizes[i].Item2:0}");
            Capture(window, Path.Combine(output, $"options-{theme.Replace(' ', '-')}-{sizes[i].Item1:0}x{sizes[i].Item2:0}.png"));
            typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(window, [false, false]);
            Console.WriteLine($"PASS shared layout {theme} / {sizes[i].Item1:0}x{sizes[i].Item2:0}: pages 1-3, stripes, button bounds");
        }
        sizeField.SetValue(window, originalIndex);
        (window.Width, window.Height) = (originalWidth, originalHeight);
        typeof(MainWindow).GetMethod("ApplyLayoutScale", flags)!.Invoke(window, null);
        typeof(MainWindow).GetMethod("ApplyHomeLayout", flags)!.Invoke(window, [theme]);
        window.UpdateLayout();
    }

    private static void VerifyNeonPulse(MainWindow window, string theme)
    {
        var pulseMethod = typeof(MainWindow).GetMethod("NeonPulse", BindingFlags.Static | BindingFlags.NonPublic)!;
        var pulse = (System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames)pulseMethod.Invoke(null, [0.0, 1.0])!;
        if (pulse.Duration.TimeSpan != TimeSpan.FromSeconds(2) || !pulse.RepeatBehavior.Equals(System.Windows.Media.Animation.RepeatBehavior.Forever))
            throw new InvalidOperationException($"Neon cycle is not 2 seconds in {theme}");
        var button = (Button)window.FindName("RestartButton")!;
        var effect = (System.Windows.Media.Effects.DropShadowEffect)button.Effect;
        if (SystemParameters.ClientAreaAnimation && !effect.HasAnimatedProperties)
            throw new InvalidOperationException($"Neon animation is not running in {theme}");
        button.IsEnabled = false;
        if (effect.Opacity != 0 || effect.HasAnimatedProperties) throw new InvalidOperationException("Disabled neon button still glows.");
        button.IsEnabled = true;
        VerifyNeonHover(window, theme);
        Console.WriteLine($"PASS neon {theme}: 2-second repeating clock, disabled buttons stop glowing");
    }

    private static void VerifyOptionsComplete(MainWindow window, string context)
    {
        window.UpdateLayout();
        var panel = (Border)window.FindName("SettingsPanel")!;
        var host = (FrameworkElement)window.FindName("PageContentHost")!;
        var panelBounds = panel.TransformToAncestor(window).TransformBounds(new Rect(panel.RenderSize));
        if (panel.ActualHeight < 449 || panel.ActualWidth < 399 || panelBounds.Top < 0 || panelBounds.Bottom > window.ActualHeight ||
            Grid.GetRowSpan((FrameworkElement)window.FindName("SettingsShell")!) != 2 || panel.ActualHeight > host.ActualHeight)
            throw new InvalidOperationException($"Options panel clipped: {context}, {panelBounds}");
        foreach (var name in new[] { "DoneButton", "GermanButton", "EnglishButton", "ThemeBox", "SizeBox", "WarningSound", "WarningSoundBox",
                     "WarningVolume", "AutoStart", "RequestAdministratorOnLaunch", "CheckUpdatesButton" })
            EnsureInside((FrameworkElement)window.FindName(name)!, panel, name, 0);
        var frames = (System.Collections.IEnumerable)typeof(MainWindow).GetField("_neonFrames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        foreach (var entry in frames)
        {
            var frame = (UIElement)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            if (frame.Opacity != 0) throw new InvalidOperationException("Background neon frames shine through the options dialog.");
        }
        Console.WriteLine($"PASS complete options {context}: all controls, Done button and overlay bounds");
    }

    private static void VerifyClassicDialogButton(Button button, Border frame)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        if (frame.Name == "RedCancelFrame")
        {
            var originalEnabled = button.IsEnabled;
            string Colour(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color.ToString();
            var resting = Colour((System.Windows.Media.Brush)button.FindResource("DialogDangerBackground"));
            var hovered = Colour((System.Windows.Media.Brush)button.FindResource("DialogDangerHover"));
            try
            {
                button.IsEnabled = true;
                if (Colour(frame.Background) != resting) throw new InvalidOperationException("Approved red cancel background missing.");
                var labels = VisualChildren<TextBlock>(button).ToArray();
                if (labels.Any(text => Colour(text.Foreground) != "#FFFFFFFF")) throw new InvalidOperationException("Red cancel caption is not white.");
                button.SetValue(key, true);
                button.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (Colour(frame.Background) != hovered || frame.Effect is null) throw new InvalidOperationException("Red cancel hover/glow missing.");
                foreach (var cross in VisualChildren<System.Windows.Shapes.Path>(button))
                {
                    cross.GetBindingExpression(System.Windows.Shapes.Path.StrokeProperty)?.UpdateTarget();
                    if (Colour(cross.Stroke) != "#FFFFFFFF") throw new InvalidOperationException($"Hovered red cancel X is not white: {Colour(cross.Stroke)}; frame={System.Windows.Documents.TextElement.GetForeground(frame)}; binding={cross.GetBindingExpression(System.Windows.Shapes.Path.StrokeProperty)?.Status}");
                }
                button.SetValue(key, false);
                if (Colour(frame.Background) != resting || frame.Effect is not null) throw new InvalidOperationException("Red cancel hover did not reset.");
                button.IsEnabled = false;
                if (frame.Opacity != .5) throw new InvalidOperationException("Disabled red cancel feedback missing.");
            }
            finally { button.SetValue(key, false); button.IsEnabled = originalEnabled; }
            return;
        }
        var enabled = button.IsEnabled;
        var caption = VisualChildren<TextBlock>(button).First(text => text.Text is "Abbrechen" or "Cancel" or "Stop repair");
        string Hex(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color.ToString();
        try
        {
            button.IsEnabled = true;
            caption.GetBindingExpression(TextBlock.ForegroundProperty)?.UpdateTarget();
            if (Hex(frame.Background) != "#FFFCE8EC" || Hex(caption.Foreground) != "#FFA41932") throw new InvalidOperationException("Classic dialog cancellation lacks approved resting colours.");
            button.SetValue(key, true);
            caption.GetBindingExpression(TextBlock.ForegroundProperty)?.UpdateTarget();
            if (Hex(frame.Background) != "#FFC82743" || Hex(caption.Foreground) != "#FFFFFFFF" || frame.Effect is null) throw new InvalidOperationException("Classic dialog cancellation lacks white text and red hover glow.");
            foreach (var path in VisualChildren<System.Windows.Shapes.Path>(button))
            {
                path.GetBindingExpression(System.Windows.Shapes.Path.StrokeProperty)?.UpdateTarget();
                if (Hex(path.Stroke) != "#FFFFFFFF") throw new InvalidOperationException("Classic dialog X does not turn white.");
            }
            button.IsEnabled = false;
            caption.GetBindingExpression(TextBlock.ForegroundProperty)?.UpdateTarget();
            if (Hex(frame.Background) != "#FFE3E7ED" || Hex(caption.Foreground) != "#FF566273" || frame.Effect is not null) throw new InvalidOperationException("Disabled Classic dialog button shows hover or is unreadable.");
            button.SetValue(key, false);
            button.IsEnabled = true;
            if (frame.Effect is not null || Hex(frame.Background) != "#FFFCE8EC") throw new InvalidOperationException("Classic dialog hover did not reset.");
        }
        finally { button.SetValue(key, false); button.IsEnabled = enabled; }
    }

    private static void VerifyClassicDialogs(MainWindow owner, string output, int sizeIndex)
    {
        var assembly = typeof(MainWindow).Assembly;
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var palette = assembly.GetType("PowerControlProWpf.ThemePalette", true)!.GetMethod("For", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["Classic"]);
        // Theme sweeps set resources directly. Dialogs must use an owner whose
        // actual selected theme is Classic, as they do during normal app use.
        var dialogOwner = new MainWindow { Left = -10000, Top = -10000, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual };
        typeof(MainWindow).GetProperty("SkipSavedPlacementForTests", flags)!.SetValue(dialogOwner, true);
        var themeBox = (ComboBox)dialogOwner.FindName("ThemeBox")!;
        themeBox.SelectedItem = themeBox.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Classic");
        ((ComboBox)dialogOwner.FindName("SizeBox")!).SelectedIndex = sizeIndex + 1;
        dialogOwner.Show();
        owner = dialogOwner;
        try
        {
        Window Create(string type, params object?[] args) => (Window)assembly.GetType("PowerControlProWpf." + type, true)!.GetConstructors(flags).Single().Invoke(args);
        var factories = new (string Name, Func<Window> Create)[]
        {
            ("repair", () => Create("RepairConfirmWindow", "DISM /Online /Cleanup-Image /RestoreHealth", palette, false)),
            ("confirmation", () => Create("ThemedMessageBoxWindow", "Ereignisprotokoll unwiderruflich löschen?", "Ereignisprotokoll löschen?", MessageBoxButton.YesNo, MessageBoxImage.Warning, palette, false)),
            ("user-create", () => Create("UserEditorDialog", owner, null)),
            ("user-edit", () => Create("UserEditorDialog", owner, new UserEntry("demo", "Standardbenutzer", "Testkonto", "Aktiv", true, "Demo"))),
            ("password", () => Create("PasswordResetDialog", owner, "demo", false))
        };
        foreach (var factory in factories)
        foreach (var escape in new[] { false, true })
        {
            var dialog = factory.Create();
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = dialog.Top = -10000;
            dialog.ShowActivated = false;
            Exception? failure = null;
            dialog.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    dialog.UpdateLayout();
                    var cancel = VisualChildren<Button>(dialog).Single(button => VisualChildren<TextBlock>(button).Any(text => text.Text == "Abbrechen"));
                    cancel.ApplyTemplate();
                    var frame = (cancel.Template.FindName("RedCancelFrame", cancel) ?? cancel.Template.FindName("CrossFrame", cancel)) as Border;
                    if (frame is null || frame.BorderThickness.Left < 1.3) throw new InvalidOperationException("Classic dialog cancellation template missing.");
                    EnsureInside(cancel, dialog, factory.Name, sizeIndex);
                    VerifyClassicDialogButton(cancel, frame);
                    if (!escape) Capture(dialog, Path.Combine(output, $"classic-dialog-{factory.Name}-{sizeIndex}.png"));
                    if (escape)
                    {
                        if (!cancel.IsCancel) throw new InvalidOperationException("Escape target no longer has IsCancel enabled.");
                        System.Windows.Input.AccessKeyManager.ProcessKey(PresentationSource.FromVisual(dialog), "\u001b", false);
                        dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    }
                    else cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancel));
                    if (dialog.IsVisible) throw new InvalidOperationException($"Dialog {factory.Name} did not close via {(escape ? "Escape" : "cancel click")}.");
                }
                catch (Exception exception) { failure = exception; dialog.Close(); }
            }, DispatcherPriority.ApplicationIdle);
            var result = dialog.ShowDialog();
            if (failure is not null) throw new InvalidOperationException($"Classic dialog {factory.Name} / size {sizeIndex}", failure);
            if (result == true) throw new InvalidOperationException("Cancel unexpectedly confirmed a dialog.");
        }
        Console.WriteLine($"PASS Classic dialogs size {sizeIndex}: five variants, bounds, disabled/hover/reset, cancel click and Escape; no system actions");
        }
        finally { dialogOwner.Close(); }
    }

    private static void VerifyClassicCancellation(MainWindow window, string output, int sizeIndex)
    {
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (var name in new[] { "RepairCancelButton", "AbortTimerButton" })
        {
            var button = (Button)window.FindName(name)!;
            var enabled = button.IsEnabled;
            button.ApplyTemplate();
            var frame = (Border)button.Template.FindName(name == "RepairCancelButton" ? "CrossFrame" : "AbortBorder", button);
            var caption = (TextBlock)window.FindName(name == "RepairCancelButton" ? "RepairCancelLabel" : "AbortLabel")!;
            string Hex(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color.ToString();
            try
            {
                button.IsEnabled = false;
                button.SetValue(key, true);
                caption.GetBindingExpression(TextBlock.ForegroundProperty)?.UpdateTarget();
                if (Hex(frame.Background) != "#FFE3E7ED" || Hex(caption.Foreground) != "#FF566273" || frame.Effect is not null || button.Opacity != 1)
                    throw new InvalidOperationException($"Classic disabled cancellation is unreadable or shows hover: {name}: bg={frame.Background}, caption={caption.Foreground}, opacity={button.Opacity}, effect={frame.Effect}");
                button.SetValue(key, false);
                button.IsEnabled = true;
                caption.GetBindingExpression(TextBlock.ForegroundProperty)?.UpdateTarget();
                if (Hex(frame.Background) != "#FFFCE8EC" || Hex(caption.Foreground) != "#FFA41932")
                    throw new InvalidOperationException($"Classic active cancellation is not red: {name}");
                button.SetValue(key, true);
                caption.GetBindingExpression(TextBlock.ForegroundProperty)?.UpdateTarget();
                if (Hex(frame.Background) != "#FFC82743" || Hex(caption.Foreground) != "#FFFFFFFF" || frame.Effect is null)
                    throw new InvalidOperationException($"Classic cancellation hover is incorrect: {name}");
                button.SetValue(key, false);
                if (frame.Effect is not null) throw new InvalidOperationException("Classic cancellation glow did not reset.");
            }
            finally { button.SetValue(key, false); button.IsEnabled = enabled; }
        }
        Capture(window, Path.Combine(output, $"classic-cancellation-{sizeIndex}.png"));
        Console.WriteLine($"PASS Classic cancellation size {sizeIndex}: readable disabled, red active, white hover, glow reset; no process started");
    }

    private static void VerifyClassicNavigation(MainWindow window, string output, int sizeIndex)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var previous = (Button)window.FindName("PagePrevious")!;
        var next = (Button)window.FindName("PageNext")!;
        var navigation = (FrameworkElement)window.FindName("PageNavigation")!;
        var header = (FrameworkElement)window.FindName("HeaderLayout")!;
        var indicator = (FrameworkElement)window.FindName("PageIndicator")!;
        var admin = (FrameworkElement)window.FindName("AdminRightsBadge")!;
        var switchPage = typeof(MainWindow).GetMethod("SwitchPage", flags)!;
        var key = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        for (var page = 0; page < 3; page++)
        {
            window.UpdateLayout();
            if (previous.IsEnabled != (page > 0) || next.IsEnabled != (page < 2)) throw new InvalidOperationException("Navigation availability changed.");
            foreach (var button in new[] { previous, next })
            {
                var color = ((System.Windows.Media.SolidColorBrush)button.Foreground).Color.ToString();
                if (color != (button.IsEnabled ? "#FF123F8C" : "#FF8D9CAF")) throw new InvalidOperationException("Classic arrow contrast is incorrect.");
                EnsureInside(button, navigation, "Classic arrow", sizeIndex);
                button.SetValue(key, true);
                var hover = ((System.Windows.Media.SolidColorBrush)button.Foreground).Color.ToString();
                if (hover != (button.IsEnabled ? "#FF082F75" : "#FF8D9CAF")) throw new InvalidOperationException("Classic hover/disabled colours are incorrect.");
                button.SetValue(key, false);
            }
            if (navigation.TranslatePoint(new Point(navigation.ActualWidth, 0), header).X >= indicator.TranslatePoint(new Point(), header).X ||
                indicator.TranslatePoint(new Point(indicator.ActualWidth, 0), header).X > admin.TranslatePoint(new Point(), header).X)
                throw new InvalidOperationException("Classic navigation overlaps the page indicator or admin badge.");
            Capture(window, Path.Combine(output, $"classic-navigation-page-{page + 1}-size-{sizeIndex}.png"));
            if (page < 2) switchPage.Invoke(window, [next, new RoutedEventArgs()]);
        }
        switchPage.Invoke(window, [previous, new RoutedEventArgs()]);
        switchPage.Invoke(window, [previous, new RoutedEventArgs()]);
        Console.WriteLine($"PASS Classic navigation size {sizeIndex}: page switching, enabled/disabled, hover and no overlap");
    }

    private static void VerifyClassicHeaderColors(MainWindow window, string output)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(MainWindow);
        var stateType = type.GetNestedType("UpdateCheckState", BindingFlags.NonPublic)!;
        var setState = type.GetMethod("SetUpdateCheckState", flags)!;
        var adminAppearance = type.GetMethod("UpdateAdminBadgeAppearance", flags)!;
        var bell = (System.Windows.Shapes.Path)window.FindName("UpdateNoticeIcon")!;
        var badge = (Button)window.FindName("AdminRightsBadge")!;
        static string Hex(System.Windows.Media.Brush brush) => ((System.Windows.Media.SolidColorBrush)brush).Color.ToString();
        foreach (var elevated in new[] { false, true })
        {
            adminAppearance.Invoke(window, [elevated, "Classic"]);
            if (Hex(badge.Background) != (elevated ? "#FF164A67" : "#FF17498E") ||
                Hex(badge.BorderBrush) != (elevated ? "#FF9BF3B5" : "#FFD5E8FF"))
                throw new InvalidOperationException("Classic admin colours differ from the approved preview.");
            foreach (var (state, expected) in new[] { ("Current", "#FF80F0AE"), ("Failed", "#FFFFD56A"), ("Available", "#FFFF91A3"), ("Checking", "#FFEAF3FF") })
            {
                setState.Invoke(window, [Enum.Parse(stateType, state)]);
                if (Hex(bell.Stroke) != expected) throw new InvalidOperationException($"Incorrect Classic bell colour: {state}");
                window.UpdateLayout();
                Capture(window, Path.Combine(output, $"classic-{state.ToLowerInvariant()}-{(elevated ? "admin" : "no-admin")}.png"));
            }
        }
        adminAppearance.Invoke(window, [false, "Classic"]);
        Console.WriteLine("PASS Classic header: four bell colours and both admin states match approved colours");
    }

    private static void VerifyNeonHover(MainWindow window, string theme)
    {
        static void Hover(Button button, bool entered) => button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = entered ? System.Windows.Input.Mouse.MouseEnterEvent : System.Windows.Input.Mouse.MouseLeaveEvent });
        foreach (var name in new[] { "RestartButton", "CompactTimerPlusMinute", "CreateUserButton", "RefreshUsersButton", "RepairDismButton", "RepairSfcButton", "CheckUpdatesButton", "DoneButton", "StandardRefreshIconButton" })
        {
            var button = (Button)window.FindName(name)!;
            if (button.Effect is not System.Windows.Media.Effects.DropShadowEffect effect) throw new InvalidOperationException($"Missing neon button {name} / {theme}");
            button.ApplyTemplate();
            var border = new[] { "ActionBorder", "StepSurface", "ButtonBorder", "NoticeSurface", "ArrowSurface" }
                .Select(part => button.Template.FindName(part, button)).OfType<Border>().First();
            var normal = border.Background;
            var originalEnabled = button.IsEnabled;
            button.IsEnabled = true;
            Hover(button, true);
            if (effect.Opacity != 1 || effect.BlurRadius != 18 || effect.HasAnimatedProperties ||
                !ReferenceEquals(border.Background, window.Resources["HoverBrush"]) || border.BorderThickness.Left != 2)
                throw new InvalidOperationException($"Missing neon hover: {name} / {theme}");
            Hover(button, false);
            if (!Equals(border.Background, normal)) throw new InvalidOperationException($"Hover did not restore template binding: {name} / {theme}");
            button.IsEnabled = false;
            Hover(button, true);
            if (effect.Opacity != 0 || effect.HasAnimatedProperties || !Equals(border.Background, normal))
                throw new InvalidOperationException($"Disabled neon hover still active: {name} / {theme}");
            Hover(button, false);
            button.IsEnabled = originalEnabled;
        }
        Console.WriteLine($"PASS neon hover {theme}: pages 1-3, options, log icons, hover/reset/disabled");
    }

    private static void VerifyLinkInputAncestors()
    {
        var run = new System.Windows.Documents.Run("Core Temp");
        var link = new System.Windows.Documents.Hyperlink(run);
        var text = new TextBlock(); text.Inlines.Add(link);
        var border = new Border { Child = text };
        var outside = new Border();
        foreach (var name in new[] { "IsInside", "IsWithinVisual" })
        {
            var method = typeof(MainWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var node in new DependencyObject[] { run, link, text, border })
                if (!(bool)method.Invoke(null, [node, border])! || (bool)method.Invoke(null, [node, outside])!)
                    throw new InvalidOperationException($"Wrong link input ancestry: {name}, {node.GetType().Name}");
            if ((bool)method.Invoke(null, [new DependencyObject(), border])!)
                throw new InvalidOperationException($"Standalone input incorrectly matched: {name}");
        }
        Console.WriteLine("PASS hyperlink input ancestry: Run, Hyperlink, TextBlock, Border and unrelated objects");
    }
}
