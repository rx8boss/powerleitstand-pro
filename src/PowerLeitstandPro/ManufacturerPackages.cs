using System.IO;
using System.Runtime.InteropServices;

namespace PowerControlProWpf;

internal static class ManufacturerPackages
{
    // Identities verified against the public Microsoft Store product metadata.
    internal static string? Family(string id) => id switch
    {
        "asus" => "B9ECED6F.ArmouryCrate_qmba6cd70vzyy",
        "msi" => "9426MICRO-STARINTERNATION.MSICenter_kzh8wxbdkxb8p",
        _ => null
    };

    internal static ManufacturerState Inspect(string id)
    {
        var family = Family(id);
        if (family is null) return new(ManufacturerInstallState.Unknown);
        return InspectFamily(family);
    }

    internal static ManufacturerState InspectFamily(string family)
    {
        try { return QueryFamily(family); }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return new(ManufacturerInstallState.Unknown); }
    }

    private static ManufacturerState QueryFamily(string family)
    {
        uint count = 0, length = 0;
        var error = GetPackagesByPackageFamily(family, ref count, IntPtr.Zero, ref length, IntPtr.Zero);
        if (error == 0 && count == 0) return new(ManufacturerInstallState.Missing);
        if (error != 122 || count is 0 or > 64 || length is 0 or > 65536) return new(ManufacturerInstallState.Unknown);
        var pointerBytes = checked((int)count * IntPtr.Size);
        var pointers = Marshal.AllocHGlobal(pointerBytes); var names = Marshal.AllocHGlobal(checked((int)length * 2));
        try
        {
            var capacity = count;
            if (GetPackagesByPackageFamily(family, ref count, pointers, ref length, names) != 0 || count > capacity) return new(ManufacturerInstallState.Unknown);
            var applications = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers, i * IntPtr.Size));
                if (fullName is null || GetStagedPackageOrigin(fullName, out var origin) != 0 || origin != 3) continue; // Store origin only, not developer/sideloaded.
                if (OpenPackageInfoByFullName(fullName, 0, out var info) != 0) continue;
                try
                {
                    uint bytes = 0;
                    if (GetPackageApplicationIds(info, ref bytes, IntPtr.Zero, out var apps) != 122 || bytes is 0 or > 1048576 || apps is 0 or > 32) continue;
                    var capacityBytes = bytes; var buffer = Marshal.AllocHGlobal((int)bytes);
                    try
                    {
                        if (GetPackageApplicationIds(info, ref bytes, buffer, out apps) != 0 || bytes > capacityBytes || apps > 32 || apps * IntPtr.Size > bytes) continue;
                        for (var j = 0; j < apps; j++)
                        {
                            var pointer = Marshal.ReadIntPtr(buffer, j * IntPtr.Size);
                            var offset = pointer.ToInt64() - buffer.ToInt64();
                            if (offset < apps * IntPtr.Size || offset >= bytes || offset % 2 != 0) continue;
                            var text = Marshal.PtrToStringUni(pointer, (int)(bytes - offset) / 2)?.Split('\0')[0];
                            if (text is not null && text.StartsWith(family + "!", StringComparison.Ordinal) && !text.Contains("Server", StringComparison.OrdinalIgnoreCase)) applications.Add(text);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { ClosePackageInfo(info); }
            }
            return applications.Count == 1 ? new(ManufacturerInstallState.Installed, "appx:" + applications.Single()) : new(ManufacturerInstallState.NoLaunchTarget);
        }
        finally { Marshal.FreeHGlobal(pointers); Marshal.FreeHGlobal(names); }
    }

    internal static void Activate(string id, string target)
    {
        var current = Inspect(id);
        if (current.Installation != ManufacturerInstallState.Installed || current.Executable != target || !target.StartsWith("appx:", StringComparison.Ordinal))
            throw new IOException("Der Paketstart konnte nicht bestätigt werden.");
        var manager = (IActivationManager)new ActivationManager();
        try
        {
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(target[5..], null, 2, out _));
        }
        finally { Marshal.FinalReleaseComObject(manager); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length, IntPtr buffer);
    [DllImport("api-ms-win-appmodel-runtime-l1-1-1.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetStagedPackageOrigin(string name, out int origin);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int OpenPackageInfoByFullName(string name, uint reserved, out IntPtr info);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern int GetPackageApplicationIds(IntPtr info, ref uint length, IntPtr buffer, out uint count);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern int ClosePackageInfo(IntPtr info);
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ActivationManager { }
    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}
