using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using PowerControlProWpf;

internal static class ActivityStatusTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Save(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
        try
        {
            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            {
                Console.WriteLine("Checking " + theme + (english ? " EN" : " DE"));
                var main = new MainWindow(System.IO.Path.Combine(output, theme + english)) { ShowActivated = false };
                try
                {
                    void Field(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(main, value);
                    void Call(string name, params object[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(main, args);
                    Field("_english", english); Call("ApplyLanguage");
                    var themes = (ComboBox)main.FindName("ThemeBox");
                    themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().First(i => (i.Tag?.ToString() ?? i.Content?.ToString()) == theme);
                    main.Show(); Pump();
                    var dot = (Ellipse)main.FindName("ActivityStatusIndicator");
                    void ColorIs(string key, string state) { Pump(); Assert(dot.Fill.ToString() == ((SolidColorBrush)main.FindResource(key)).ToString(), theme + "/" + english + ": " + state); }
                    Field("_lastRepairCancelled", true); Call("UpdateRepairStatus");
                    ColorIs("ReadyBrush", "completed cancellation is ready");
                    Assert(((TextBlock)main.FindName("StatusText")).Text == (english ? "Cancelled" : "Abgebrochen"), "historical result retained");
                    Field("_repairRunning", true); Call("UpdateRepairStatus"); ColorIs("WarningBrush", "repair still running during cancellation");
                    Field("_repairRunning", false); Call("SetTimerState", false); ColorIs("ReadyBrush", "fully stopped repair is ready");
                    Call("StartTimer", "shutdown"); ColorIs("WarningBrush", "active timer");
                    Call("AbortTimer", main, new RoutedEventArgs()); ColorIs("ReadyBrush", "timer cancellation after old repair cancellation");
                    Field("_updateInProgress", true); Call("UpdateActivityIndicator"); ColorIs("WarningBrush", "update active");
                    Field("_updateInProgress", false); Call("UpdateActivityIndicator"); ColorIs("ReadyBrush", "update closed");
                    Assert(((TextBlock)((ToolTip)dot.ToolTip).Content).Text == (english ? "Ready · no action active" : "Bereit · keine Aktion aktiv"), "localized current tooltip");
                    main.ShowRepairPage();
                    Field("_selectedRepairTool", RepairTool.Find("dism")!);
                    Call("UpdateRepairRunLabels");
                    ((Border)main.FindName("RepairRunningPanel")).Visibility = Visibility.Visible;
                    foreach (var size in new[] { 0, 1, 2, 3, 4 })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump();
                        var stop = (Button)main.FindName("RepairCancelButton");
                        Assert(stop.IsVisible && stop.ActualWidth > 0 && stop.ActualHeight > 0, "stop button is visible during repair " + size);
                        var label = (TextBlock)main.FindName("RepairCancelLabel");
                        Assert(label.Text == (english ? "Stop repair" : "Reparatur beenden"), "repair action localized");
                        var labelBounds = label.TransformToAncestor(stop).TransformBounds(new Rect(label.RenderSize));
                        Assert(labelBounds.Left >= 0 && labelBounds.Right <= stop.ActualWidth + 1, "repair caption fits " + size);
                        Assert(stop.Style == main.FindResource("UnifiedDialogDanger"), "framed repair stop button " + size);
                    }
                    using var cancellation = new CancellationTokenSource(); Field("_repairCancellation", cancellation);
                    Field("_repairRunning", true); Call("SetTimerState", false);
                    foreach (var endRepair in new[] { false, true })
                    {
                        Exception? modalFailure = null;
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                        timer.Tick += (_, _) =>
                        {
                            var dialog = app.Windows.OfType<ThemedMessageBoxWindow>().FirstOrDefault(w => w.IsVisible);
                            if (dialog is null) return;
                            timer.Stop();
                            try
                            {
                                var confirm = (Button)dialog.FindName("ConfirmButton");
                                var keep = (Button)dialog.FindName("CancelButton");
                                var confirmText = ((StackPanel)confirm.Content).Children.OfType<TextBlock>().Single().Text;
                                var keepText = ((StackPanel)keep.Content).Children.OfType<TextBlock>().Single().Text;
                                Assert(confirmText == (english ? "Stop repair" : "Reparatur beenden") && keepText == (english ? "Continue" : "Fortfahren"), "explicit repair dialog actions");
                                Assert(confirm.Style == dialog.FindResource("UnifiedDialogDanger") && keep.Style == dialog.FindResource("UnifiedDialogAction"), "stop red and continue regular");
                                if (theme == "Standard" && !english && endRepair) Save(dialog, "Reparatur-Beenden-Bestaetigung");
                                (endRepair ? confirm : keep).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            }
                            catch (Exception failure) { modalFailure = failure; dialog.Close(); }
                        };
                        timer.Start();
                        try { Call("CancelRepair", main, new RoutedEventArgs()); }
                        finally { timer.Stop(); }
                        Pump();
                        if (modalFailure is not null) throw modalFailure;
                        Assert(cancellation.IsCancellationRequested == endRepair, "continue retains process, stop requests cancellation");
                        ColorIs("WarningBrush", "repair remains busy until fully stopped");
                    }
                    Assert(main.GetTraySnapshot().RepairStopping, "tray receives stopping state");
                    Assert(((TextBlock)((ToolTip)dot.ToolTip).Content).Text == (english ? "Stopping repair…" : "Reparatur wird beendet…"), "stopping tooltip");
                    var palette = ThemePalette.For(theme);
                    foreach (var state in new[] {
                        new TraySnapshot(false, false, false, "", ""), new TraySnapshot(true, true, false, "05:00", "Example"),
                        new TraySnapshot(true, false, true, "", ""), new TraySnapshot(true, false, true, "", "", RepairStopping: true),
                        new TraySnapshot(true, false, false, "", ""), new TraySnapshot(false, false, false, "", "") })
                    {
                        var current = state;
                        var popup = new TrayQuickWindow(theme, english, () => current, _ => throw new Exception("No actions allowed")) { ShowActivated = false };
                        popup.Show(); Pump();
                        var trayDot = (Ellipse)popup.FindName("StateDot");
                        Assert(trayDot.Fill.ToString() == new SolidColorBrush((Color)ColorConverter.ConvertFromString(state.Busy ? palette.Actions[5] : palette.Ready)).ToString(), "tray state colour " + state);
                        var tip = (ToolTip)trayDot.ToolTip;
                        Assert(tip.Background.ToString() == new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[4])).ToString() && tip.Template is not null, "themed tooltip background and template");
                        if (theme == "Standard" && !english && state.RepairStopping) Save(popup, "Schnellzugriff-Reparatur-wird-beendet");
                        current = new TraySnapshot(false, false, false, "", ""); popup.RefreshState();
                        Assert(trayDot.Fill.ToString() == new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Ready)).ToString(), "open tray returns ready without reopening");
                        popup.Close(); Pump();
                    }
                    var mainTip = (ToolTip)dot.ToolTip;
                    mainTip.PlacementTarget = dot; mainTip.IsOpen = true; Pump();
                    if (!english) Save(mainTip, "Status-Hinweis-" + theme.Replace(" ", "-"));
                    mainTip.IsOpen = false;
                    if (theme == "Standard" && !english) { ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump(); Save(main, "Reparatur-Kompakt"); }
                    Field("_repairRunning", false); Call("SetTimerState", false); ColorIs("ReadyBrush", "finished stopped repair");
                }
                finally
                {
                    typeof(MainWindow).GetField("_repairRunning", flags)!.SetValue(main, false);
                    typeof(MainWindow).GetField("_updateInProgress", flags)!.SetValue(main, false);
                    ((DispatcherTimer)typeof(MainWindow).GetField("_timer", flags)!.GetValue(main)!).Stop();
                    main.Close(); Pump();
                }
            }
            Console.WriteLine($"PASS ACTIVITY STATUS: {checks} assertions, seven themes and DE/EN; no repair commands or power actions executed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
