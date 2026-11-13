using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class NeonPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        int checks = 0;
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler captureError = (_, e) => { dispatcherErrors.Add(e.Exception); e.Handled = true; };
        app.DispatcherUnhandledException += captureError;

        void Assert(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            checks++;
        }

        void Pump()
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
        }

        static NeonFrameAdorner? Glow(Border border) =>
            AdornerLayer.GetAdornerLayer(border)?.GetAdorners(border)?.OfType<NeonFrameAdorner>().SingleOrDefault();

        void WaitFor(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 40 && !condition(); attempt++) Pump();
        }

        var main = new MainWindow(Path.Combine(output, "Fixtures"), loadUsersForTests: false) { ShowActivated = false };
        AnimationClock? Clock() => (AnimationClock?)typeof(MainWindow).GetField("_neonClock", Private)!.GetValue(main);
        void Call(string method, params object[] arguments) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(main, arguments);

        try
        {
            var first = MainWindow.NeonPulse(5, .55);
            var later = MainWindow.NeonPulse(30, 1.2);
            Assert(first.KeyFrames[^1].KeyTime.TimeSpan < later.KeyFrames[^1].KeyTime.TimeSpan, "Leuchtphase kann 5 bis 30 Sekunden dauern");
            Assert(first.KeyFrames[^1].Value == 0 && first.KeyFrames[^2].Value == 0, "echte Pause bei Stärke null");
            main.Show();
            Pump();
            var sizes = (ComboBox)main.FindName("SizeBox");
            var themes = (ComboBox)main.FindName("ThemeBox");
            void Theme(string name)
            {
                themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(i => (i.Tag ?? i.Content).ToString() == name);
                Pump();
            }
            var outline = (Border)main.Template.FindName("OuterWindowOutline", main);
            foreach (var theme in new[] { "Neon Blau", "Neon Pink", "Neon Grün" })
            {
                Call("ShowPage", 0);
                Theme(theme);
                var outer = Glow(outline);
                Assert(outer is { IsHitTestVisible: false }, theme + " Außenrahmen");
                var running = Clock();
                Assert((running is not null) == SystemParameters.ClientAreaAnimation, theme + " folgt den Windows-Animationseinstellungen");
                if (running is not null)
                {
                    Call("BeginWindowDragPresentation");
                    Pump();
                    Assert(running.IsPaused, theme + " pausiert beim Verschieben");
                    Call("EndWindowDragPresentation");
                    Pump();
                    Assert(!running.IsPaused, theme + " leuchtet nach dem Verschieben weiter");
                    running.Controller!.Pause();
                }
                foreach (var size in new[] { 1, 4 })
                {
                    sizes.SelectedIndex = size;
                    foreach (var (index, panel) in new[] { (0, "ImmediateCardSurface"), (1, "UserCardSurface"), (2, "RepairCardSurface") })
                    {
                        Call("ShowPage", index);
                        Pump();
                        Assert(Glow((Border)main.FindName(panel)) is { IsVisible: true }, theme + " leuchtet auf Seite " + (index + 1));
                        Assert(ReferenceEquals(Glow(outline), outer), theme + " behält den Außenrahmen beim Seitenwechsel");
                    }
                    main.ShowWindowsUpdatesPage();
                    var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
                    var header = (Border)page.FindName("HeaderSurface");
                    var update = (Border)page.FindName("UpdatesSurface");
                    WaitFor(() => Glow(header) is { IsVisible: true } && Glow(update) is { IsVisible: true });
                    string FrameState(Border card) =>
                        $"{card.Name} visible={card.IsVisible}, layer={AdornerLayer.GetAdornerLayer(card) is not null}, frame={Glow(card)?.IsVisible}";
                    Assert(Glow(header) is { IsVisible: true } && Glow(update) is { IsVisible: true },
                        $"{theme} Seite 4 hat leuchtende Karten: {FrameState(header)}, {FrameState(update)} {dispatcherErrors.FirstOrDefault()}");
                    Assert(((Button)page.FindName("ProgramsButton")).Effect is DropShadowEffect, theme + " Seite-4-Schaltfläche hat Neon");
                    foreach (var open in new Func<FrameworkElement>[] { () => page.ShowManufacturerSoftware(inspect: false), () => page.ShowPrograms(inspect: false) })
                    {
                        var view = open();
                        bool HasGlow() => page.CardSurfaces.Any(card => card.IsVisible && view.IsAncestorOf(card) && Glow(card) is { IsVisible: true });
                        WaitFor(HasGlow);
                        Assert(HasGlow(), theme + " leuchtet auch in " + view.GetType().Name);
                    }
                    page.ShowUpdateOverview();
                    Pump();
                    Assert(Glow(header) is { IsVisible: true } && ReferenceEquals(Glow(outline), outer), theme + " Übersicht behält Neon");
                }
                Assert(ReferenceEquals(Clock(), running), theme + " startet beim Seitenwechsel nicht erneut");
                if (theme == "Neon Blau" && running is not null) CapturePreview();
                Theme("Standard");
                Assert(Glow(outline) is null && Clock() is null, "Standard entfernt Rahmen und Animation");
                Console.WriteLine("PASS " + theme + ": Seiten 1–4, Unteransichten, Kompakt und Groß");
            }
            Assert(dispatcherErrors.Count == 0, dispatcherErrors.FirstOrDefault()?.ToString() ?? "kein Dispatcherfehler");
            Console.WriteLine($"PASS NEON: {checks} Prüfungen.");
            return 0;
        }
        finally
        {
            main.Close();
            app.DispatcherUnhandledException -= captureError;
        }

        void CapturePreview()
        {
            ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1;
            main.ShowWindowsUpdatesPage();
            Pump();
            var clock = Clock()!;
            var controller = clock.Controller!;
            controller.Pause();
            Pump();
            var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage");
            var outer = Glow((Border)main.Template.FindName("OuterWindowOutline", main))!;
            var inner = Glow((Border)page.FindName("HeaderSurface"))!;
            var duration = ((DoubleAnimationUsingKeyFrames)clock.Timeline).KeyFrames[^1].KeyTime.TimeSpan.TotalSeconds;
            controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(.64), TimeSeekOrigin.BeginTime);
            Assert(outer.Strength > .95 && Math.Abs(outer.Strength - inner.Strength) < .01, "Außenrahmen und Seite 4 zünden gemeinsam");
            controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(duration - .25), TimeSeekOrigin.BeginTime);
            Assert(outer.Strength == 0 && inner.Strength == 0, "beide Glüheffekte gehen während der Pause aus");

            var frames = Directory.CreateDirectory(Path.Combine(output, "Frames")).FullName;
            var samples = Enumerable.Range(0, 16).Select(i => (i * .08, 80))
                .Concat(Enumerable.Range(0, 18).Select(i => (1.28 + i * .2, 200)))
                .Concat(new[] { (duration - 1.63, 90), (duration - 1.38, 90), (duration - 1.15, 90),
                    (duration - .30, 750) })
                .Concat(Enumerable.Range(0, 16).Select(i => (i * .08, 80))).ToArray();
            for (int i = 0; i < samples.Length; i++)
            {
                var (second, delay) = samples[i];
                controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(second), TimeSeekOrigin.BeginTime);
                main.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(main);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(frames, $"{i:D3}-{delay}.png"));
                encoder.Save(file);
            }
            controller.Resume();
            controller.SeekAlignedToLastTick(TimeSpan.FromSeconds(duration - .06), TimeSeekOrigin.BeginTime);
            for (int attempt = 0; attempt < 20 && ReferenceEquals(Clock(), clock); attempt++) Pump();
            Assert(Clock() is not null && !ReferenceEquals(Clock(), clock), "nach der Pause startet automatisch eine neue Zündung");
        }
    }
}
