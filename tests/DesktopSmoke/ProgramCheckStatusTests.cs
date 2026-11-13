using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramCheckStatusTests
{
    internal static int Run(App app, string output, bool captureImages = true)
    {
        int checks = 0;
        void Assert(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task)
        {
            var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed.TotalSeconds < 25) { Pump(); Thread.Sleep(3); }
            if (!task.IsCompleted) throw new TimeoutException("Check-status fixture"); task.GetAwaiter().GetResult(); Pump();
        }
        void Until(Func<bool> ready)
        {
            var timer = Stopwatch.StartNew(); while (!ready() && timer.Elapsed.TotalSeconds < 25) { Pump(); Thread.Sleep(3); }
            if (!ready()) throw new TimeoutException("Check-status search continuation"); Pump();
        }
        var main = new MainWindow(Path.Combine(output, "Settings"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump();
            var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false;
            var fake = new Fake(); view.Service = fake;
            var status = UnifiedDialogAppearance.Children<TextBlock>(view).Single(t => t.Name == "ProgramInspectionStatus");
            var complete = new ProgramInventory(true, new Dictionary<string, string?> { ["firefox"] = "1.0" }, UpdateCheckComplete: true);
            var partial = complete with { Complete = false, UpdateCheckComplete = false, IsPartial = true };
            Color Foreground() => ((SolidColorBrush)status.Foreground).Color;
            bool Red(Color c) => c.R > c.B && c.B > c.G;
            bool Orange(Color c) => c.R > c.G && c.G > c.B;
            bool Green(Color c) => c.G > c.R && c.G > c.B;
            void Capture(string name)
            {
                if (!captureImages) return;
                main.UpdateLayout(); var bmp = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(main);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
            }
            double Luminance(Color c)
            {
                double Channel(byte v) => v / 255d <= .04045 ? v / 255d / 12.92 : Math.Pow((v / 255d + .055) / 1.055, 2.4);
                return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
            }
            void Contrast()
            {
                Color[] backgrounds = view.FindResource("StandardReferenceCardBrush") switch
                {
                    SolidColorBrush solid => [solid.Color],
                    GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
                    _ => throw new Exception("Unknown footer background")
                };
                foreach (var background in backgrounds)
                {
                    double first = Luminance(background), second = Luminance(Foreground());
                    Assert((Math.Max(first, second) + .05) / (Math.Min(first, second) + .05) >= 4.5, "Footer text contrast below 4.5: " + view.Palette.FontFamily + " " + Foreground() + " / " + background);
                }
            }
            var themes = ((ComboBox)main.FindName("ThemeBox")).Items.OfType<ComboBoxItem>().Where(t => t.Tag is string).ToArray();
            foreach (var theme in themes) foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
            {
                ((ComboBox)main.FindName("ThemeBox")).SelectedItem = theme; ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                fake.Reset(); var task = view.RefreshAsync(); Pump();
                var checkTitle = UnifiedDialogAppearance.Children<TextBlock>(view).Single(t => t.Name == "ProgramCheckProgressTitle");
                var checkDot = UnifiedDialogAppearance.Children<System.Windows.Shapes.Ellipse>(view).Single(x => x.Name == "ProgramCheckProgressDot");
                Assert(Red(Foreground()) && !task.IsCompleted, "New check is red even when previous inventory was complete"); Contrast();
                Assert(status.Text == (english ? "Checking installed programs …" : "Installierte Programme werden geprüft …"), "New check has matching localized caption");
                Assert(checkTitle.Text == (english ? "Scan in progress" : "Suche läuft") && Red(((SolidColorBrush)checkDot.Fill).Color), "Dialog identifies active scan with red status");
                fake.Emit(partial); Pump();
                Assert(Orange(Foreground()) && !task.IsCompleted, "Fresh partial results are orange while check is pending"); Contrast();
                Assert(status.Text.Contains(english ? "Partially checked" : "Teilweise geprüft"), "Partial phase has matching localized caption");
                Assert(checkTitle.Text == (english ? "Partial results found" : "Teilergebnisse gefunden") && Orange(((SolidColorBrush)checkDot.Fill).Color), "Dialog switches to orange when partial results arrive");
                var legend = UnifiedDialogAppearance.Children<StackPanel>(view).Single(x => x.Name == "ProgramCheckProgressLegend");
                var scroll = UnifiedDialogAppearance.Children<ScrollViewer>(view).Single(x => x.Name == "ProgramCheckProgressScroll");
                double legendBottom = legend.TransformToAncestor(scroll).TransformBounds(new Rect(legend.RenderSize)).Bottom;
                Assert(legendBottom <= scroll.ViewportHeight + 1, "All three color explanations fit in the progress dialog");
                if (theme.Tag?.ToString() == "Standard" && !english && size == 1) Capture("App-Teilweise-Kompakt");
                fake.Pending.SetResult(complete); Wait(task);
                Assert(Green(Foreground()), "Green only after complete inventory and update check"); Contrast();
                Assert(status.Text.Contains(english ? "updates checked" : "Updates geprüft"), "Successful localized final caption");
                fake.Emit(partial); Pump(); Assert(Green(Foreground()) && view.Inventory.Complete, "Late progress cannot overwrite successful final state");
                Assert(status.FontSize == 10 && status.TextTrimming == TextTrimming.CharacterEllipsis && status.ActualHeight > 0, "Existing footer font and layout preserved");
                if (theme.Tag?.ToString() == "Standard" && !english) Capture("App-Fertig-" + size);
            }
            void Stop(bool afterPartial = false)
            {
                if (afterPartial)
                {
                    // Partial results dismiss the loading card; model cancellation of its still-running check.
                    var field = typeof(ProgramCatalogView).GetField("_inspectionCancellation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    ((CancellationTokenSource)field.GetValue(view)!).Cancel();
                }
                else
                {
                    var button = UnifiedDialogAppearance.Children<Button>((Grid)main.FindName("Root")).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) is "Prüfung beenden" or "Stop checking");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Pump();
            }
            foreach (var theme in themes)
            {
                ((ComboBox)main.FindName("ThemeBox")).SelectedItem = theme; Pump();
                Assert(Green(Foreground()), "Theme change keeps completed check green without a new scan"); Contrast();
            }
            fake.Reset(); var stopped = view.RefreshAsync(); Pump(); Stop(); Wait(stopped);
            Assert(Red(Foreground()) && status.Text.Contains(view.English ? "incomplete" : "nicht vollständig"), "Stop without fresh results cannot reuse old green success");
            fake.Reset(); stopped = view.RefreshAsync(); fake.Emit(partial); Pump(); Stop(afterPartial: true); Wait(stopped);
            Assert(Orange(Foreground()), "Stop after partial results stays orange");
            fake.Reset(); var failed = view.RefreshAsync(); fake.Pending.SetException(new IOException("Fixture read failure")); Wait(failed);
            Assert(Red(Foreground()), "Failed fresh check cannot reuse old green inventory");
            fake.Reset(); failed = view.RefreshAsync(); fake.Emit(partial); Pump(); fake.Pending.SetException(new IOException("Fixture partial failure")); Wait(failed);
            Assert(Orange(Foreground()), "Failure after partial results is orange");
            foreach (var incomplete in new[] { complete with { UpdateCheckComplete = false }, complete with { StoreCheckComplete = false, StoreError = "Fixture Store failure" }, complete with { OnlineError = "Fixture offline" }, complete with { Error = "Fixture partial error" }, complete with { IsPartial = true } })
            {
                fake.Reset(); var task = view.RefreshAsync(); fake.Pending.SetResult(incomplete); Wait(task); Assert(!Green(Foreground()), "Incomplete/error-bearing inventory never green");
            }
            fake.Reset(); var empty = view.RefreshAsync(); fake.Pending.SetResult(complete with { Installed = new Dictionary<string, string?>() }); Wait(empty);
            Assert(Green(Foreground()), "Completed check with zero installed apps is still complete");
            fake.Reset(); fake.SearchGate = new(TaskCreationOptions.RunContinuationsAsynchronously); view.SetQuery("firefox");
            var search = view.SearchOnlineAsync(); Pump(); Assert(Red(Foreground()), "New online search starts red");
            int beforeSearchInventory = fake.Inspections;
            fake.SearchGate.SetResult([ProgramCatalog.Entries.Single(e => e.Id == "firefox")]); Until(() => fake.Inspections > beforeSearchInventory);
            Assert(Orange(Foreground()), "Online matches turn orange while installation verification continues");
            fake.Pending.SetResult(complete); Wait(search); Assert(Green(Foreground()), "Online search green only after associated inventory check");
            fake.Reset(); fake.SearchGate = new(TaskCreationOptions.RunContinuationsAsynchronously); search = view.SearchOnlineAsync(); Pump(); view.CancelSearch(); Wait(search);
            Assert(Red(Foreground()), "Canceled online search cannot show previous green success");
            Console.WriteLine($"PASS {checks} footer inspection color/state/contrast checks; seven designs, DE/EN, compact/large; controlled inventory only.");
            return 0;
        }
        finally { main.Close(); Pump(); }
    }
    private sealed class Fake : IProgramInstallService, IProgramInspectionProgress
    {
        public event Action<ProgramInventory>? InventoryChanged;
        internal TaskCompletionSource<ProgramInventory> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<IReadOnlyList<ProgramEntry>>? SearchGate;
        internal int Inspections;
        internal void Reset() { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously); SearchGate = null; }
        internal void Emit(ProgramInventory inventory) => InventoryChanged?.Invoke(inventory);
        public Task<ProgramInventory> InspectAsync(CancellationToken token) { Inspections++; return Pending.Task.WaitAsync(token); }
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry e, CancellationToken token) => Task.FromResult<ProgramPackage?>(null);
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token) => SearchGate?.Task.WaitAsync(token) ?? Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public Task<int> InstallAsync(ProgramEntry e, ProgramPackage p, Action started) => throw new Exception("No installer");
        public Task<int> UpgradeAsync(ProgramEntry e, ProgramPackage p, Action started) => throw new Exception("No installer");
        public void OpenWebsite(ProgramEntry e) { }
    }
}
