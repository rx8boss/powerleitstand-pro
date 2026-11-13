using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class AppToolTipTests
{
    internal static int Run(App app, string output)
    {
        int checks = 0; var errors = new List<Exception>();
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs e) { errors.Add(e.Exception); e.Handled = true; }
        app.DispatcherUnhandledException += Unhandled;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Assert(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        CustomPopupPlacement Position(ToolTip tip) => tip.CustomPopupPlacementCallback!(new Size(240, 90), new Size(120, 30), new Point()).Single();
        void NoErrors() => Assert(errors.Count == 0, "Unexpected dispatcher errors: " + string.Join("; ", errors.Select(e => e.ToString())));
        var grid = new Grid(); var target = new Button { Content = "Hover", Width = 120, Height = 30, Margin = new(18), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(target);
        var owner = new Window { Content = grid, Width = 900, Height = 650, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
        try
        {
            owner.Show(); Pump();
            // Reproduce the exact failing WPF operation independently of our fixed callback.
            var before = AppToolTips.Create(target, "Vorheriger Bezug"); grid.Children.Remove(target); Pump();
            bool originalFault = false;
            try { _ = owner.PointFromScreen(target.PointToScreen(new Point())); }
            catch (InvalidOperationException) { originalFault = true; }
            Assert(originalFault, "Original PointToScreen operation reproduces disconnected-visual exception");
            var safe = Position(before); Assert(safe.Point == new Point(0, 0), "Detached target uses harmless placement instead of throwing");
            grid.Children.Add(target); Pump();
            foreach (string theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (bool english in new[] { false, true }) foreach (var size in new[] { (900d, 650d), (1500d, 900d) })
            {
                var palette = ThemePalette.For(theme);
                SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
                owner.Resources["StandardReferenceStatusBrush"] = Brush(palette.Colors[2]);
                owner.Resources["TextBrush"] = Brush(palette.Colors[5]); owner.Resources["AccentBrush"] = Brush(palette.Colors[7]);
                owner.Width = size.Item1; owner.Height = size.Item2; Pump();
                var text = english ? "Current progress and details" : "Aktueller Fortschritt und Details";
                var tip = AppToolTips.Create(target, text); target.ToolTip = tip;
                tip.IsOpen = true; Pump();
                Assert(tip.IsOpen, "Normal connected tooltip opens");
                Assert(((Border)tip.Content).BorderBrush == owner.Resources["AccentBrush"], "Theme accent preserved");
                var body = (TextBlock)((ScrollViewer)((Border)tip.Content).Child).Content;
                Assert(body.Text == text && body.TextWrapping == TextWrapping.Wrap, "Localized text and original layout preserved");
                var pos = Position(tip); var anchor = owner.PointFromScreen(target.PointToScreen(new Point()));
                Assert(double.IsFinite(pos.Point.X) && double.IsFinite(pos.Point.Y) && anchor.X + pos.Point.X >= 7.9 && anchor.Y + pos.Point.Y >= 7.9,
                    "Normal placement stays finite and within window margin");
                grid.Children.Remove(target); owner.Width += 1; owner.UpdateLayout();
                _ = Position(tip); Pump();
                Assert(!tip.IsOpen, "Removed target closes tooltip after layout");
                grid.Children.Add(target); Pump();
                tip.IsOpen = true; Pump(); Assert(tip.IsOpen, "Reconnected element can show help again");
                target.Visibility = Visibility.Collapsed; Pump();
                Assert(!tip.IsOpen, "Hidden target closes help");
                target.Visibility = Visibility.Visible; Pump();
                tip.IsOpen = true; Pump(); tip.IsOpen = false; Pump();
                Assert(!tip.IsOpen, "Ordinary close remains functional"); NoErrors();
            }
            // An old deferred close must not close a later opening of this same tooltip.
            var reused = AppToolTips.Create(target, "Wieder öffnen"); reused.IsOpen = true; Pump();
            grid.Children.Remove(target); _ = Position(reused); reused.IsOpen = false;
            grid.Children.Add(target); owner.UpdateLayout(); reused.IsOpen = true; Pump();
            Assert(reused.IsOpen, "Old scheduled close cannot dismiss a newer opening"); reused.IsOpen = false; Pump();
            // Moving between owners changes the coordinate space even if both windows remain connected.
            var moved = AppToolTips.Create(target, "Fensterwechsel"); moved.IsOpen = true; Pump();
            var otherGrid = new Grid(); var other = new Window { Content = otherGrid, Width = 400, Height = 300, ShowActivated = false };
            try
            {
                other.Show(); grid.Children.Remove(target); otherGrid.Children.Add(target); other.UpdateLayout(); _ = Position(moved); Pump();
                Assert(!moved.IsOpen, "Moving the target to another owner invalidates its old placement");
                otherGrid.Children.Remove(target); grid.Children.Add(target); Pump();
            }
            finally { other.Close(); Pump(); }
            target.IsEnabled = false; var disabled = AppToolTips.Create(target, "Deaktivierte Aktion"); disabled.IsOpen = true; Pump();
            Assert(disabled.IsOpen, "Disabled but visible controls retain their help"); disabled.IsOpen = false; target.IsEnabled = true;
            var closing = AppToolTips.Create(target, "Fenster schließen"); closing.IsOpen = true; Pump(); owner.Close(); _ = Position(closing); Pump();
            Assert(!closing.IsOpen, "Closing owner safely closes popup"); NoErrors();
        }
        finally { owner.Close(); Pump(); }

        var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; view.Service = new ProgramCatalogTests.Fake(); Pump();
            var setLog = typeof(MainWindow).GetMethod("SetExpandedLogOpen", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (int i = 0; i < 8; i++)
            {
                view.SetQuery(""); Pump();
                var tile = view.Tiles[0]; var help = AppToolTips.Create(tile, "Programmdetails"); tile.ToolTip = help; help.IsOpen = true; Pump();
                Assert(help.IsOpen, "Actual catalog tile help is open before rebuild");
                view.SetQuery("LibreOffice"); main.Width += i % 2 == 0 ? 1 : -1; main.UpdateLayout(); _ = Position(help); Pump();
                Assert(!help.IsOpen, "Actual catalog rebuild invalidates an open tooltip safely");
                var button = view.InstalledButton; var progress = AppToolTips.Create(button, "Installierte Programme"); progress.IsOpen = true; Pump();
                setLog.Invoke(main, [true, true]); main.UpdateLayout(); _ = Position(progress); Pump();
                setLog.Invoke(main, [false, true]); main.ShowRepairPage(); main.UpdateLayout(); _ = Position(progress); Pump();
                Assert(!progress.IsOpen, "Log overlay and page change close stale help");
                main.ShowWindowsUpdatesPage(); page.ShowPrograms(false); Pump(); NoErrors();
            }
        }
        finally { main.Close(); Pump(); app.DispatcherUnhandledException -= Unhandled; }
        Assert(errors.Count == 0, "No swallowed dispatcher exception in the full tooltip regression run");
        File.WriteAllText(Path.Combine(output, "Tooltip-Checks.json"), System.Text.Json.JsonSerializer.Serialize(new { Checks = checks, DispatcherErrors = errors.Count,
            OriginalExceptionReproduced = true, RealInstallersStarted = false, Themes = 7, Languages = 2, Sizes = 2 }));
        Console.WriteLine("PASS " + checks + " tooltip detach/reopen/owner/layout/catalog/log/theme checks; zero dispatcher errors."); return 0;
    }
}
