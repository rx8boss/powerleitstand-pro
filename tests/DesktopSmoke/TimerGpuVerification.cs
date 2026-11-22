using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class TimerGpuVerification
{
    internal static int Run(App app, string output)
    {
        {
            var hardware = new LibreHardwareMonitor.Hardware.Computer { IsGpuEnabled = true };
            hardware.Open();
            foreach (var gpu in hardware.Hardware.Where(h => h.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuAmd
                or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel or LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia))
            {
                gpu.Update();
                foreach (var sensor in gpu.Sensors.Where(s => s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature))
                    Console.WriteLine($"Sensorbibliothek: {gpu.Name} | {sensor.Name}: {sensor.Value?.ToString("0.0") ?? "nicht verfügbar"}");
            }
            hardware.Close();
        }
        var detected = new[] { new GpuDiagnostic("display", "NVIDIA GeForce", null, null, null, null, null, true, "Windows") };
        var windowsAdapters = new[]
        {
            new GpuDiagnostic(@"PCI\VEN_8086\GPU", "Intel UHD Graphics", null, null, null, null, null, false, "Windows"),
            new GpuDiagnostic(@"PCI\VEN_1002\GPU", "AMD Radeon", null, null, null, null, null, false, "Windows"),
            new GpuDiagnostic(@"PCI\VEN_10DE\GPU", "NVIDIA GeForce", null, null, null, null, null, false, "Windows")
        };
        var merged = GpuDiagnosticsSampler.MergeWindowsAdapters(detected, windowsAdapters);
        if (merged.Length != 3 || !merged.Any(g => g.Name == "Intel UHD Graphics") || !merged.Any(g => g.Name == "AMD Radeon"))
            throw new Exception("Windows graphics adapter merge lost a detected GPU or duplicated the NVIDIA display.");
        Console.WriteLine("Intel/AMD/NVIDIA Erkennung: Testdaten erfolgreich.");
        using var sampler = new GpuDiagnosticsSampler();
        IReadOnlyList<GpuDiagnostic> gpus = [];
        var deadline = DateTime.UtcNow.AddSeconds(25);
        do
        {
            gpus = sampler.Sample(Stopwatch.GetTimestamp());
            Thread.Sleep(300);
        } while (gpus.Count == 0 && DateTime.UtcNow < deadline);
        Thread.Sleep(3500);
        gpus = sampler.Sample(Stopwatch.GetTimestamp());
        foreach (var gpu in gpus)
            Console.WriteLine($"GPU: {gpu.Name} | Temperatur: {gpu.Temperature?.ToString("0.0") ?? "nicht verfügbar"} | Quelle: {gpu.Source}");

        var window = new MainWindow(Path.Combine(output, "Einstellungen"), new AppSettings { Theme = "Standard" }) { ShowActivated = false };
        try
        {
            window.Show();
            var sizes = (ComboBox)window.FindName("SizeBox");
            foreach (int index in Enumerable.Range(0, sizes.Items.Count))
            {
                sizes.SelectedIndex = index;
                app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                foreach (string name in new[] { "CompactTimerMinusMinute", "CompactTimerPlusMinute", "CompactTimerMinusHour", "CompactTimerPlusHour" })
                {
                    var button = (Button)window.FindName(name);
                    if (button.IsVisible && (button.FontSize > 12 || button.ActualWidth < 35))
                        throw new Exception($"Timer: Größe {index}, {name}, Schrift {button.FontSize}, Breite {button.ActualWidth}");
                }
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bmp.Render(window);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bmp));
                using var file = File.Create(Path.Combine(output, $"Timer-Groesse-{index}.png"));
                png.Save(file);
                Console.WriteLine($"Timer Größe {index}: OK");
            }
        }
        finally { window.Close(); }
        return 0;
    }
}