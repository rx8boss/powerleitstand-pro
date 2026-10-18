using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerControlProWpf;

internal static class MountedDriveDiscovery
{
    // DriveInfo includes USB, removable and mapped network letters. Add folder-only mounts.
    internal static IEnumerable<DriveDiagnostic> FolderMounts(ISet<string> known)
    {
        var volume = new StringBuilder(1024);
        var handle = FindFirstVolume(volume, (uint)volume.Capacity);
        if (handle == new IntPtr(-1)) yield break;
        try
        {
            do
            {
                GetVolumePathNamesForVolumeName(volume.ToString(), null, 0, out var required);
                if (required is 0 or > 1048576) continue;
                var paths = new char[required];
                if (!GetVolumePathNamesForVolumeName(volume.ToString(), paths, required, out _)) continue;
                var mounts = new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (mounts.Any(p => known.Contains(p.TrimEnd('\\')))) continue;
                var mount = mounts.FirstOrDefault(p => Path.IsPathFullyQualified(p));
                if (mount is null || !known.Add(mount.TrimEnd('\\'))) continue;
                var label = new StringBuilder(1024);
                GetVolumeInformation(mount, label, (uint)label.Capacity, out _, out _, out _, null, 0);
                yield return GetDiskFreeSpaceEx(mount, out _, out var total, out var free) && total <= long.MaxValue && free <= total
                    ? new(mount.TrimEnd('\\'), label.ToString(), (long)total, (long)free)
                    : new(mount.TrimEnd('\\'), label.ToString(), null, null);
            } while (FindNextVolume(handle, volume, (uint)volume.Capacity));
        }
        finally { FindVolumeClose(handle); }
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", CharSet = CharSet.Unicode)] private static extern IntPtr FindFirstVolume(StringBuilder name, uint length);
    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextVolume(IntPtr handle, StringBuilder name, uint length);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindVolumeClose(IntPtr handle);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[]? paths, uint length, out uint required);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformation(string root, StringBuilder label, uint size, out uint serial, out uint maxLength, out uint flags, StringBuilder? fileSystem, uint fileSystemSize);
}
