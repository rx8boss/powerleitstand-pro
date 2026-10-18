using System.Diagnostics;
using System.IO;

namespace PowerControlProWpf;

internal static class DrivePathLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string name)
    {
        var path = name.Length == 2 && char.IsAsciiLetter(name[0]) && name[1] == ':' ? name + @"\" : name;
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || path.Any(char.IsControl) || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\"))
            throw new ArgumentException("Invalid drive path.", nameof(name));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        return start;
    }
}
