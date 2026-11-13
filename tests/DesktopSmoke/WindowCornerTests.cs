using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class WindowCornerTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Bounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Bounds rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(App app, string output, string? onlyTheme = null)
    {
        Directory.CreateDirectory(output); UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks = 0, windows = 0;
        var errors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        app.DispatcherUnhandledException += capture;
        void Assert(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        void Pump()
        {
            // Continuous neon frames can starve ApplicationIdle with software rendering.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        }
        void NativeOutline(Window window, string label)
        {
            window.UpdateLayout();
            var hwnd = new WindowInteropHelper(window).Handle;
            Assert(GetWindowRect(hwnd, out var rect), label + ": native bounds available");
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert(GetWindowRgn(hwnd, region) == 3, label + ": actual native contour is rounded, not rectangular");
                int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
                Assert(!PtInRegion(region, 0, 0) && !PtInRegion(region, w - 1, 0) && !PtInRegion(region, 0, h - 1) && !PtInRegion(region, w - 1, h - 1), label + ": four outer corner pixels excluded");
                Assert(!PtInRegion(region, (int)Math.Floor(6 * GetDpiForWindow(hwnd) / 96d), 0), label + ": visible rounding matches preview rather than a half-radius contour");
                Assert(PtInRegion(region, w / 2, 1) && PtInRegion(region, 1, h / 2) && PtInRegion(region, w - 2, h / 2) && PtInRegion(region, w / 2, h - 2), label + ": straight edges preserved");
                Assert(PtInRegion(region, w / 2, h / 2), label + ": interior remains inside native contour");
                if (window.ResizeMode == ResizeMode.CanResizeWithGrip)
                {
                    Assert(NativeWindowCapture.LeftResizeHit(window) == 10, label + ": native left resize border remains usable");
                    var scale = GetDpiForWindow(hwnd) / 96d;
                    int inset = (int)Math.Ceiling(4 * scale);
                    Assert(PtInRegion(region, inset, inset) && PtInRegion(region, w - inset, h - inset), label + ": diagonal resize areas remain inside contour");
                }
            }
            finally { DeleteObject(region); }
            Assert(NativeWindowCapture.HasNoNativeTopStrip(window), label + ": no extra native title strip or client inset");
        }
        void Save(Window window, string name)
        {
            // Export the actual WPF content using the verified HWND contour. PrintWindow
            // can return an incomplete software-rendered surface just after restore.
            var dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96d), (int)Math.Ceiling(window.ActualHeight * dpi / 96d), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int w = source.PixelWidth, h = source.PixelHeight, stride = w * 4;
            var pixels = new byte[stride * h]; source.CopyPixels(pixels, stride, 0);
            var region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert(GetWindowRgn(new WindowInteropHelper(window).Handle, region) == 3, name + ": capture uses actual native region");
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y * stride + x * 4 + 3] = PtInRegion(region, x, y) ? (byte)255 : (byte)0;
            }
            finally { DeleteObject(region); }
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
        void Audit(Window window, string label, bool save = false)
        {
            void Stage(string step) => File.WriteAllText(Path.Combine(output, "Aktueller-Fall.txt"), label + " / " + step);
            Stage("before show");
            window.ShowActivated = false;
            window.PreviewMouseDown += (_, e) => e.Handled = true; window.PreviewKeyDown += (_, e) => e.Handled = true;
            try
            {
                window.Show(); Stage("shown"); Pump(); Stage("pumped"); window.UpdateLayout(); windows++;
                Assert(!window.AllowsTransparency, label + ": remains opaque");
                NativeOutline(window, label);
                var width = window.ActualWidth; var height = window.ActualHeight;
                Stage("move"); window.Left += 17; window.Top += 13; Pump(); NativeOutline(window, label + " moved");
                Assert(Math.Abs(width - window.ActualWidth) < .5 && Math.Abs(height - window.ActualHeight) < .5, label + ": moving preserves content size");
                Stage("hide"); window.Hide(); Stage("reshow"); window.Show(); Stage("reshown"); Pump(); NativeOutline(window, label + " reopened");
                if (window.ResizeMode == ResizeMode.CanResizeWithGrip)
                {
                    window.Width = width + 36; window.Height = height + 24; Pump(); NativeOutline(window, label + " resized");
                    Assert(window.ActualWidth >= width + 35, label + ": width changes remain available");
                    window.Width = width; window.Height = height; Pump(); NativeOutline(window, label + " restored size");
                }
                if (save) Save(window, label);
            }
            finally { Stage("close"); window.Close(); Stage("closed"); Pump(); Stage("complete"); }
        }
        try
        {
            foreach (var theme in (onlyTheme is not null ? new[] { onlyTheme } : new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" }))
            foreach (var english in new[] { false, true })
            {
                var suffix = theme.Replace(' ', '-') + (english ? "-EN" : "-DE"); Console.WriteLine("Checking corners " + suffix);
                var palette = ThemePalette.For(theme);
                var main = new MainWindow(Path.Combine(output, "Isoliert-" + suffix), new AppSettings { Theme = theme, Language = english ? "en" : "de", SizeIndex = 1 }) { ShowActivated = false };
                main.PreviewMouseDown += (_, e) => e.Handled = true; main.PreviewKeyDown += (_, e) => e.Handled = true;
                try
                {
                    app.MainWindow = main; main.Show(); Pump(); windows++;
                    Assert(!main.AllowsTransparency && WindowChrome.GetWindowChrome(main)?.CornerRadius == new CornerRadius(24), suffix + ": approved visual 12 DIP radius without transparency");
                    Console.WriteLine("Actual window DPI " + GetDpiForWindow(new WindowInteropHelper(main).Handle));
                    foreach (var size in new[] { 1, 4 })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump();
                        NativeOutline(main, suffix + "/" + size);
                        Assert(main.ActualWidth == (size == 1 ? 860 : 1200) && main.ActualHeight == (size == 1 ? 620 : 760), suffix + ": existing compact/large dimensions");
                        main.WindowState = WindowState.Minimized; Pump(); main.WindowState = WindowState.Normal; Pump();
                        NativeOutline(main, suffix + "/" + size + " restored");
                        if (theme == "Standard" && !english) Save(main, "Hauptfenster-" + (size == 1 ? "Kompakt" : "Gross"));
                    }
                    if (theme == "Standard" && !english)
                    {
                        main.WindowState = WindowState.Maximized; Pump();
                        var region = CreateRectRgn(0, 0, 0, 0);
                        try { Assert(GetWindowRgn(new WindowInteropHelper(main).Handle, region) == 2, "maximized native contour is rectangular"); }
                        finally { DeleteObject(region); }
                        main.WindowState = WindowState.Normal; Pump(); NativeOutline(main, "main restored from maximized");
                    }
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = 1; Pump();
                    Audit(new UserEditorDialog(main, null), "Benutzer-Erstellen-" + suffix);
                    Audit(new UserEditorDialog(main, new UserEntry("Beispielkonto", "Standardbenutzer", "Beispieldaten", "Aktiv", true, "Beispielkonto")), "Benutzer-Bearbeiten-" + suffix);
                    Audit((Window)Activator.CreateInstance(typeof(PasswordResetDialog), Private, null, new object[] { main, "Beispielkonto", english }, null)!, "Kennwort-" + suffix);
                    Audit(new RepairConfirmWindow("SFC /scannow", palette, english) { Owner = main }, "Reparaturstart-" + suffix);
                    Audit(new RepairResultWindow(RepairResultSummary.Create("sfc", 0, false, "successfully repaired", "", TimeSpan.FromSeconds(42), english), palette, english) { Owner = main }, "Reparaturergebnis-" + suffix);
                    Audit(new RepairLogWindow([new("12:00", "SFC", "INFO", "Beispieldaten / fixture")], palette, english, null) { Owner = main }, "Reparaturprotokoll-" + suffix, theme == "Standard" && !english);
                    Audit(new ThemedMessageBoxWindow("Beispieldaten / fixture", "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning, palette, english) { Owner = main }, "Bestaetigung-" + suffix);
                    var update = new AvailableUpdate(new Version(1, 1, 6), "https://example.invalid/fixture", new string('a', 64));
                    Audit(new UpdateInstallWindow(palette, english, AppVersion.Installed, update) { Owner = main }, "Updateinstallation-" + suffix);
                    var download = new UpdateDownloadWindow(update, palette, english, () => false) { Owner = main };
                    typeof(UpdateDownloadWindow).GetField("_started", Private)!.SetValue(download, true);
                    typeof(UpdateDownloadWindow).GetField("_finished", Private)!.SetValue(download, true);
                    Audit(download, "Updatedownload-" + suffix);
                    Audit(new UpdateManagerWindow(palette, english, () => false, new AppBackupStore(Path.Combine(output, "Sicherungen-" + suffix)), autoLoad: false) { Owner = main }, "Updatemanager-" + suffix);
                    Audit(new ManufacturerDownloadWindow(ManufacturerCatalog.Apps[0], new NoManufacturerActions(), palette, english) { Owner = main }, "Herstellerdownload-" + suffix);
                    // Existing layered windows retain their transparent visual rounding.
                    var page = new WindowsUpdatesPage();
                    var privacy = new PrivacyDownloadWindow(main, page, palette, english, new NoPrivacyActions());
                    try { privacy.ShowActivated = false; privacy.Show(); Pump(); Assert(privacy.AllowsTransparency && privacy.Background == Brushes.Transparent, suffix + ": existing privacy transparency preserved"); Assert(WindowChrome.GetWindowChrome(privacy) is null, suffix + ": no new chrome over layered privacy frame"); windows++; }
                    finally { privacy.Close(); Pump(); }
                    var tray = new TrayQuickWindow(theme, english, () => new TraySnapshot(false, false, false, "", "", true), _ => throw new Exception("Unexpected tray action"));
                    try { tray.ShowActivated = false; tray.Show(); Pump(); Assert(tray.AllowsTransparency && WindowChrome.GetWindowChrome(tray) is null, suffix + ": existing tray contour preserved"); windows++; }
                    finally { tray.Close(); Pump(); }
                }
                finally { main.Close(); Pump(); }
            }
        }
        finally { app.DispatcherUnhandledException -= capture; }
        Assert(errors.Count == 0, "No hidden dispatcher errors: " + errors.FirstOrDefault());
        Console.WriteLine($"PASS {checks} native corner, resize, lifecycle and rendering checks across {windows} windows; {(onlyTheme ?? "seven themes")}, DE/EN, compact/large; no downloads, installations or power actions. DPI evidence is the actual host DPI, not simulated monitor coverage."); return 0;
    }
    private sealed class NoManufacturerActions : IManufacturerService
    {
        public Task<IReadOnlyDictionary<string, ManufacturerState>> InspectAsync() => throw new Exception("Unexpected inspect");
        public Task<ManufacturerDownload?> ResolveAsync(ManufacturerApp app, CancellationToken token) => Task.FromResult<ManufacturerDownload?>(null);
        public Task<string> DownloadAsync(ManufacturerApp app, ManufacturerDownload download, IProgress<ManufacturerProgress> progress, CancellationToken token) => throw new Exception("Unexpected download");
        public Task LaunchAsync(ManufacturerApp app, string file, bool installer, Action? onStarted = null) => throw new Exception("Unexpected launch");
        public void OpenWebsite(ManufacturerApp app) => throw new Exception("Unexpected website action");
    }
    private sealed class NoPrivacyActions : IPrivacyDownloadService
    {
        public string DownloadsDirectory => "I:\\Fixture-Downloads";
        public Task<string> DownloadAsync(bool licenseAccepted, IProgress<PrivacyProgress> progress, CancellationToken token) => throw new Exception("Unexpected privacy download");
        public Task OpenAsync(string path) => throw new Exception("Unexpected privacy launch");
    }
}
