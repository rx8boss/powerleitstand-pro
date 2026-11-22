using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace PowerControlProWpf;

internal sealed record GpuDiagnostic(string Id, string Name, double? Usage, double? Temperature,
    ulong? TotalMemory, ulong? UsedMemory, string? MemoryType, bool IsPrimary, string Source)
{
    internal double? MemoryPercent => TotalMemory is > 0 && UsedMemory is { } used && used <= TotalMemory
        ? used * 100d / TotalMemory.Value : null;
}

// Runs independently of the main sampler. A driver helper cannot stall CPU/network updates.
internal sealed class GpuDiagnosticsSampler : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task<GpuDiagnostic[]>? _read;
    private GpuDiagnostic[] _latest = [];
    private long _nextRead, _lastSuccess;
    private bool _disposed;
    private Computer? _sensors;

    internal IReadOnlyList<GpuDiagnostic> Sample(long now, string? registeredModels = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_read is { IsCompleted: true })
        {
            if (_read.IsCompletedSuccessfully) { _latest = _read.Result; _lastSuccess = now; }
            _read = null;
            _nextRead = now + 3 * Stopwatch.Frequency;
        }
        if (_read is null && now >= _nextRead) _read = Task.Run(() => ReadAsync(_stop.Token, registeredModels));
        return _lastSuccess != 0 && now - _lastSuccess > 8 * Stopwatch.Frequency
            ? _latest.Select(g => g with { Usage = null, Temperature = null, UsedMemory = null }).ToArray() : _latest;
    }

    private async Task<GpuDiagnostic[]> ReadAsync(CancellationToken stopping, string? registeredModels)
    {
        var displays = ReadDisplayAdapters();
        displays = MergeWindowsAdapters(displays, ReadWindowsAdapters());
        var fallback = (registeredModels ?? "").Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !displays.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => new GpuDiagnostic("registry:" + name, name, null, null, null, null, null, false, "Windows"));
        displays = MergeSensors(displays.Concat(fallback).ToArray());
        var helper = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        }.FirstOrDefault(File.Exists);
        if (helper is null) return displays;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        process.StartInfo.ArgumentList.Add("--query-gpu=uuid,name,utilization.gpu,temperature.gpu,memory.total,memory.used");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        var started = false;
        try
        {
            stopping.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) return displays;
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) return displays;
            var primaryName = displays.FirstOrDefault(g => g.IsPrimary)?.Name;
            var measured = ParseNvidiaOutput(await output, primaryName).ToList();
            for (int i = 0; i < measured.Count; i++)
            {
                var gpu = measured[i];
                var matches = displays.Where(d => d.Name.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (gpu.Temperature is null && matches.Length == 1 && measured.Count(g => g.Name.Equals(gpu.Name, StringComparison.OrdinalIgnoreCase)) == 1)
                    measured[i] = gpu with { Temperature = matches[0].Temperature };
            }
            // Do not merge distinct GPUs solely by a shared model name.
            measured.AddRange(displays.Where(d => !measured.Any(g => g.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))
                || measured.Count(g => g.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)) > 1));
            return measured.OrderByDescending(g => g.IsPrimary).ThenBy(g => g.Id, StringComparer.Ordinal).ToArray();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return displays; }
        finally
        {
            try { if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    internal static GpuDiagnostic[] MergeWindowsAdapters(GpuDiagnostic[] displays, GpuDiagnostic[] windows)
    {
        return displays.Concat(windows.Where(adapter =>
            !displays.Any(existing => existing.Id.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase)
                || existing.Name.Equals(adapter.Name, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    private GpuDiagnostic[] MergeSensors(GpuDiagnostic[] displays)
    {
        try
        {
            if (_sensors is null)
            {
                var computer = new Computer { IsGpuEnabled = true };
                try { computer.Open(); _sensors = computer; }
                catch { computer.Close(); throw; }
            }
            var sensors = new List<GpuDiagnostic>();
            foreach (var hardware in _sensors.Hardware.Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel))
            {
                hardware.Update();
                var temperature = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature
                    && (s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) || s.Name.Equals("GPU", StringComparison.OrdinalIgnoreCase))
                    && s.Value is >= 0 and <= 150)?.Value;
                sensors.Add(new(hardware.Identifier.ToString(), hardware.Name, null, temperature, null, null, null, false, "LibreHardwareMonitor"));
            }
            foreach (var sensor in sensors)
            {
                var matches = displays.Where(d => d.Name.Equals(sensor.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1 && sensors.Count(s => s.Name.Equals(sensor.Name, StringComparison.OrdinalIgnoreCase)) == 1)
                {
                    var index = Array.IndexOf(displays, matches[0]);
                    displays[index] = matches[0] with { Temperature = sensor.Temperature, Source = sensor.Source };
                }
                else
                {
                    displays = displays.Append(sensor).ToArray();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or DllNotFoundException or TypeInitializationException)
        {
            System.Diagnostics.Trace.TraceWarning("GPU sensors unavailable: " + e.Message);
        }
        return displays;
    }

    internal static GpuDiagnostic[] ParseNvidiaOutput(string text, string? primaryName)
    {
        static double? Number(string value, double maximum) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 && n <= maximum ? n : null;
        static ulong? Bytes(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 && n <= ulong.MaxValue / 1048576d ? (ulong)(n * 1048576d) : null;
        var result = new List<GpuDiagnostic>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = CsvFields(line);
            if (fields.Count != 6 || !fields[0].StartsWith("GPU-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(fields[1]) || result.Any(g => g.Id == fields[0])) continue;
            var total = Bytes(fields[4]); var used = Bytes(fields[5]);
            if (total is null or 0 || used > total) used = null;
            result.Add(new(fields[0], fields[1], Number(fields[2], 100), Number(fields[3], 150), total, used, null,
                fields[1].Equals(primaryName, StringComparison.OrdinalIgnoreCase), "NVIDIA"));
        }
        // Two identical cards cannot be assigned to a monitor by model name alone.
        if (result.Count(g => g.IsPrimary) > 1) result = result.Select(g => g with { IsPrimary = false }).ToList();
        return result.ToArray();
    }

    private static List<string> CsvFields(string line)
    {
        var fields = new List<string>(); var text = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { text.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (line[i] == ',' && !quoted) { fields.Add(text.ToString().Trim()); text.Clear(); }
            else text.Append(line[i]);
        }
        if (quoted) return [];
        fields.Add(text.ToString().Trim()); return fields;
    }

    private static GpuDiagnostic[] ReadWindowsAdapters()
    {
        var result = new List<GpuDiagnostic>();
        try
        {
            var scope = new System.Management.ManagementScope(@"\.
oot\cimv2",
                new System.Management.ConnectionOptions { Timeout = TimeSpan.FromSeconds(3) });
            var query = new System.Management.ObjectQuery("SELECT Name, PNPDeviceID FROM Win32_VideoController");
            using var searcher = new System.Management.ManagementObjectSearcher(scope, query,
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) });
            using var devices = searcher.Get();
            foreach (System.Management.ManagementObject device in devices)
            {
                using (device)
                {
                    var name = device["Name"]?.ToString()?.Trim();
                    var id = device["PNPDeviceID"]?.ToString()?.Trim();
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id) ||
                        !id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new(id, name, null, null, null, null, null, false, "Windows"));
                }
            }
        }
        catch (Exception e) when (e is System.Management.ManagementException or System.Runtime.InteropServices.COMException
            or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            Trace.TraceWarning("Windows graphics adapter query unavailable: " + e.Message);
        }
        return result.ToArray();
    }

    private static GpuDiagnostic[] ReadDisplayAdapters()
    {
        var result = new List<GpuDiagnostic>();
        for (uint i = 0; ; i++)
        {
            var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref device, 0)) break;
            if ((device.Flags & 8) != 0 || string.IsNullOrWhiteSpace(device.Description) || !device.Id.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(g => g.Name.Equals(device.Description, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new(device.Key, device.Description, null, null, null, null, null, (device.Flags & 4) != 0, "Windows"));
        }
        return result.OrderByDescending(g => g.IsPrimary).ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel();
        if (_read is { IsCompleted: false } pending) _ = pending.ContinueWith(_ => { _sensors?.Close(); _stop.Dispose(); }, TaskScheduler.Default);
        else { _sensors?.Close(); _stop.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Description;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Key;
    }
    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
}
