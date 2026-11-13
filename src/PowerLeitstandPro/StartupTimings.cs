using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PowerControlProWpf;

// Opt-in local diagnostics. No timing file or logging work during ordinary starts.
internal static class StartupTimings
{
    internal sealed record Stage(string Name, double ElapsedMs, double DurationMs, double ProcessElapsedMs);
    private static Stopwatch? _watch;
    private static readonly List<Stage> _stages = [];
    private static string? _path;
    private static double _processOffsetMs;
    internal static void Begin(string? path = null)
    {
        _stages.Clear(); _path = path; _watch = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        _processOffsetMs = Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds);
    }
    internal static void Mark(string name)
    {
        if (_watch is null) return;
        var elapsed = _watch.Elapsed.TotalMilliseconds;
        _stages.Add(new(name, elapsed, elapsed - (_stages.LastOrDefault()?.ElapsedMs ?? 0), _processOffsetMs + elapsed));
    }
    internal static Stage[] Snapshot() => _stages.ToArray();
    internal static void Save()
    {
        if (_path is null) return;
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Snapshot(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Trace.TraceWarning("Startup timing: " + e.Message); }
    }
}
