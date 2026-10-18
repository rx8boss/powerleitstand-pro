using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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

    private static async Task<GpuDiagnostic[]> ReadAsync(CancellationToken stopping, string? registeredModels)
    {
        var displays = ReadDisplayAdapters();
        var fallback = (registeredModels ?? "").Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !displays.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Select(name => new GpuDiagnostic("registry:" + name, name, null, null, null, null, null, false, "Windows"));
        displays = displays.Concat(fallback).ToArray();
        var helper = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        if (!File.Exists(helper)) return displays;
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
            // Other vendors remain visible even when the driver offers no telemetry interface here.
            measured.AddRange(displays.Where(d => !measured.Any(g => g.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))));
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
        if (_read is { IsCompleted: false } pending) _ = pending.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        else _stop.Dispose();
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
