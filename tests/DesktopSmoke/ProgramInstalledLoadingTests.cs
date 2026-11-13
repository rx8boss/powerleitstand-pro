using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramInstalledLoadingTests
{
    internal static int Run(App app, string output, bool capture = true)
    {
        int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Until(Func<bool> ready)
        { var timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed.TotalSeconds < 20) { Pump(); Thread.Sleep(4); } if (!ready()) throw new TimeoutException("Installed-program loading fixture"); Pump(); }
        void Wait(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        var main = new MainWindow(Path.Combine(output, "IsolatedSettings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        var previous = ProgramRemovalDialog.Reader;
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons = false; view.Service = new ProgramCatalogTests.Fake(); Pump();
            var host = (Grid)main.FindName("Root");
            Grid Overlay() => host.Children.OfType<Grid>().Single(x => x.Name == "InstalledProgramsOverlay");
            Button Close(Grid layer) => UnifiedDialogAppearance.Children<Button>(layer).Single(b => b.Content is TextBlock t && t.Text == (view.English ? "Close" : "Schließen"));
            Button HeaderClose(Grid layer) => UnifiedDialogAppearance.Children<Button>(layer).Single(b => b.Width == 28 && b.Height == 28);
            RotateTransform Rotation(ProgramInstalledLoading loading) => (RotateTransform)UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(loading).Single(p => p.Name == "InstalledProgramsLoadingArc").RenderTransform;
            bool Removed(Grid layer) => !host.Children.Contains(layer);
            foreach (var theme in ((ComboBox)main.FindName("ThemeBox")).Items.OfType<ComboBoxItem>().Where(t => t.Tag is string).ToArray())
            {
                ((ComboBox)main.FindName("ThemeBox")).SelectedItem = theme;
                foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    File.AppendAllText(Path.Combine(output, "Testphasen.txt"), $"{theme.Tag}; en={english}; size={size}\n");
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                    object buttonContent = view.InstalledButton.Content; view.RefreshAvailability(); view.RefreshAvailability();
                    Assert(ReferenceEquals(buttonContent, view.InstalledButton.Content), "Availability refresh reuses drawn icon instead of replacing it with old glyph");
                    Assert(UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(buttonContent).Count() == 2, "Button has window outline and separate check-mark geometry");
                    Assert(AutomationProperties.GetName(view.InstalledButton) == (english ? "Installed programs" : "Installierte Programme"), "Button accessibility name is localized text, not control type");
                    Assert(view.InstalledButton.Width == 158 && view.InstalledButton.Height == 34, "Approved button dimensions retained");
                    var icon = UnifiedDialogAppearance.Children<Grid>(buttonContent).Single(g => g.Name == "InstalledProgramsIcon");
                    var outline = UnifiedDialogAppearance.Children<System.Windows.Shapes.Path>(icon).First();
                    Assert(((SolidColorBrush)outline.Stroke).Color == ((SolidColorBrush)view.InstalledButton.Foreground).Color, "Icon outline follows current design text color");
                    var contentBounds = ((FrameworkElement)buttonContent).TransformToAncestor(view.InstalledButton).TransformBounds(new Rect(((FrameworkElement)buttonContent).RenderSize));
                    Assert(contentBounds.Left >= -1 && contentBounds.Right <= view.InstalledButton.ActualWidth + 1, "Icon and caption fit compact and large button");
                    using var release = new ManualResetEventSlim(); using var started = new ManualResetEventSlim(); using var finished = new ManualResetEventSlim();
                    ProgramRemovalDialog.Reader = token => { started.Set(); try { release.Wait(token); return [new("fixture", "Loading Fixture App", "Fixture", "1.0", null, "Registry", Protected: true)]; } finally { finished.Set(); } };
                    var dialog = ProgramRemovalDialog.ShowAsync(view); var layer = Overlay();
                    var loading = UnifiedDialogAppearance.Children<ProgramInstalledLoading>(layer).Single(); Until(() => started.IsSet && loading.IsLoaded);
                    var rotation = Rotation(loading);
                    Assert(loading.Visibility == Visibility.Visible && loading.IsVisible, "Spinner displayed while inventory read is held");
                    Assert(rotation.HasAnimatedProperties == SystemParameters.ClientAreaAnimation, "Spinner honors Windows animation preference");
                    Assert(!UnifiedDialogAppearance.Children<TextBox>(layer).Single().IsEnabled && !UnifiedDialogAppearance.Children<ComboBox>(layer).Single().IsEnabled, "Filters disabled until initial inventory is available");
                    Assert(Close(layer).IsEnabled && HeaderClose(layer).IsEnabled, "Both closing actions remain enabled while reading");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(loading).Any(t => t.Text == (english ? "Loading installed programs …" : "Installierte Programme werden geladen …")), "Centered loading caption localized");
                    var group = loading.Children.OfType<StackPanel>().Single(); var center = group.TranslatePoint(new Point(group.ActualWidth / 2, group.ActualHeight / 2), loading);
                    Assert(Math.Abs(center.X - loading.ActualWidth / 2) < 1 && Math.Abs(center.Y - loading.ActualHeight / 2) < 1, "Spinner and explanatory text centered in actual list region");
                    var groupBounds = group.TransformToAncestor(loading).TransformBounds(new Rect(group.RenderSize));
                    Assert(groupBounds.Left >= -1 && groupBounds.Top >= -1 && groupBounds.Right <= loading.ActualWidth + 1 && groupBounds.Bottom <= loading.ActualHeight + 1, "Loading group fits smallest and largest layouts");
                    var card = layer.Children.OfType<Border>().Single(); var closeBounds = Close(layer).TransformToAncestor(card).TransformBounds(new Rect(Close(layer).RenderSize));
                    Assert(closeBounds.Bottom <= card.ActualHeight && closeBounds.Top >= 0, "Closing button stays within dialog frame");
                    if (capture && !english && size == 1 && theme.Tag?.ToString() == "Standard") Save(main, Path.Combine(output, "Installierte-Programme-Laedt-Kompakt.xps"));
                    release.Set(); Until(() => finished.IsSet && loading.Visibility == Visibility.Collapsed);
                    Assert(!rotation.HasAnimatedProperties, "Successful read removes animation clock");
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(layer).Any(t => t.Text == "Loading Fixture App"), "Successful read renders actual result instead of perpetual spinner");
                    Assert(UnifiedDialogAppearance.Children<TextBox>(layer).Single().IsEnabled && UnifiedDialogAppearance.Children<ComboBox>(layer).Single().IsEnabled, "Completed read enables filters");
                    Close(layer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(dialog); Assert(Removed(layer), "Read-complete dialog closes cleanly");
                }
            }
            page.SetLanguage(false); ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump();
            ProgramRemovalDialog.Reader = _ => [];
            var empty = ProgramRemovalDialog.ShowAsync(view); var emptyLayer = Overlay(); var emptyLoader = UnifiedDialogAppearance.Children<ProgramInstalledLoading>(emptyLayer).Single();
            Until(() => emptyLoader.Visibility == Visibility.Collapsed);
            Assert(UnifiedDialogAppearance.Children<TextBlock>(emptyLayer).Any(t => t.Text.StartsWith("0 Programme gefunden")), "Empty success is explicit zero result and does not keep spinning");
            Close(emptyLayer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(empty);
            ProgramRemovalDialog.Reader = _ => throw new IOException("Controlled reader failure");
            var failed = ProgramRemovalDialog.ShowAsync(view); var failedLayer = Overlay(); var failedLoader = UnifiedDialogAppearance.Children<ProgramInstalledLoading>(failedLayer).Single();
            Until(() => failedLoader.Visibility == Visibility.Collapsed);
            Assert(!Rotation(failedLoader).HasAnimatedProperties && UnifiedDialogAppearance.Children<TextBlock>(failedLayer).Any(t => t.Text.StartsWith("Programme konnten nicht gelesen werden:")), "Read failure stops animation and displays themed error");
            Assert(!UnifiedDialogAppearance.Children<TextBlock>(failedLayer).Any(t => t.Text.StartsWith("0 Programme gefunden")), "Failed read is never misreported as empty installed inventory");
            Assert(Close(failedLayer).IsEnabled && !UnifiedDialogAppearance.Children<TextBox>(failedLayer).Single().IsEnabled, "Failure remains closable without offering misleading filters");
            Close(failedLayer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(failed);
            foreach (string reason in new[] { "footer", "x", "hidden-page", "unload", "owner-close" })
            {
                using var release = new ManualResetEventSlim(); using var started = new ManualResetEventSlim(); using var finished = new ManualResetEventSlim(); CancellationToken observed = default;
                ProgramRemovalDialog.Reader = token => { observed = token; started.Set(); try { release.Wait(); return [new("late", "Late result must stay hidden", "Fixture", null, null, "Registry", Protected: true)]; } finally { finished.Set(); } };
                var closing = ProgramRemovalDialog.ShowAsync(view); var layer = Overlay(); var loading = UnifiedDialogAppearance.Children<ProgramInstalledLoading>(layer).Single(); Until(() => started.IsSet && loading.IsLoaded);
                if (reason == "footer") Close(layer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (reason == "x") HeaderClose(layer).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else if (reason == "hidden-page") view.Visibility = Visibility.Collapsed;
                else if (reason == "unload") view.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                else main.Close();
                Wait(closing);
                Assert(Removed(layer) && observed.IsCancellationRequested, reason + " immediately removes overlay and cancels reader");
                Assert(!Rotation(loading).HasAnimatedProperties && loading.Visibility == Visibility.Collapsed, reason + " stops and hides spinner before a slow reader returns");
                release.Set(); Until(() => finished.IsSet); Pump();
                Assert(Removed(layer), reason + " late response does not recreate dialog");
                if (reason == "hidden-page") { view.Visibility = Visibility.Visible; Pump(); }
            }
            Console.WriteLine("PASS " + checks + " installed-program icon/loading/lifecycle checks; seven themes, DE/EN, compact/large; controlled inventory only, no installation or removal.");
            return 0;
        }
        finally { ProgramRemovalDialog.Reader = previous; main.Close(); Pump(); }
    }
    private static void Save(Window main, string path)
    {
        using var document = new System.Windows.Xps.Packaging.XpsDocument(path, FileAccess.ReadWrite);
        System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(document).Write((Visual)main.Content,
            new System.Printing.PrintTicket { PageMediaSize = new System.Printing.PageMediaSize(main.ActualWidth, main.ActualHeight) });
    }
}
