using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace PowerControlProWpf;

internal sealed record PcHardwareInfo(string Windows, string WindowsDetails, string CpuModel,
    int Packages, int Cores, int LogicalProcessors, string Graphics, string Mainboard)
{
    internal static string WindowsName(string product, int build) => product.Contains("Server", StringComparison.OrdinalIgnoreCase)
        ? product : build >= 22000 ? "Windows 11" : "Windows 10";

    internal static PcHardwareInfo Read()
    {
        string ReadValue(string path, string name)
        {
            try { using var key = Registry.LocalMachine.OpenSubKey(path); return (key?.GetValue(name)?.ToString() ?? "").Trim(); }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { return ""; }
        }
        const string osPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = ReadValue(osPath, "ProductName");
        int.TryParse(ReadValue(osPath, "CurrentBuildNumber"), out var build);
        if (build == 0) build = Environment.OSVersion.Version.Build;
        var windows = WindowsName(product, build);
        var edition = ReadValue(osPath, "EditionID");
        var details = $"{windows} · {edition} · {ReadValue(osPath, "DisplayVersion")} · Build {build}";
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var processors = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
            foreach (var key in processors?.GetSubKeyNames() ?? [])
            {
                var model = ReadValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\" + key, "ProcessorNameString");
                if (model.Length > 0) models.Add(model);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        var graphics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 32; i++)
        {
            var location = ReadValue(@"HARDWARE\DEVICEMAP\VIDEO", @"\Device\Video" + i);
            const string prefix = @"\Registry\Machine\";
            if (!location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var name = ReadValue(location[prefix.Length..], "DriverDesc");
            if (name.Length > 0) graphics.Add(name);
        }
        const string bios = @"HARDWARE\DESCRIPTION\System\BIOS";
        var board = string.Join(" ", new[] { ReadValue(bios, "BaseBoardManufacturer"), ReadValue(bios, "BaseBoardProduct") }
            .Where(s => s.Length > 0 && !s.Contains("O.E.M.", StringComparison.OrdinalIgnoreCase) && !s.Equals("Default string", StringComparison.OrdinalIgnoreCase)));
        return new(windows, details, string.Join(" / ", models), TopologyCount(3), TopologyCount(0),
            checked((int)GetActiveProcessorCount(0xffff)), string.Join(" / ", graphics), board);
    }

    private static int TopologyCount(int relation)
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(relation, IntPtr.Zero, ref length);
        if (length == 0 || length > 16 * 1024 * 1024) return 0;
        var memory = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relation, memory, ref length)) return 0;
            var count = 0;
            for (var offset = 0; offset + 8 <= length;)
            {
                var size = Marshal.ReadInt32(memory, offset + 4);
                if (size < 8 || size > length - offset) break;
                if (Marshal.ReadInt32(memory, offset) == relation) count++;
                offset += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    [DllImport("kernel32.dll")] private static extern uint GetActiveProcessorCount(ushort group);
}

internal sealed record ProcessorDiagnostic(string Id, double? Usage);
