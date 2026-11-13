using System.IO;

namespace PowerControlProWpf;

internal static class ProgramLocation
{
    internal static string? DirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Directory.Exists(path) ? Path.GetFullPath(path) : File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    internal static string Shorten(string path)
    {
        if (path.Length <= 34) return path;
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return root + "…\\" + string.Join('\\', parts.TakeLast(2));
    }
}
