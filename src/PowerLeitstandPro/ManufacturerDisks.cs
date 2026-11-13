using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerControlProWpf;

internal sealed record ManufacturerDisk(string Model, uint Bus);

// Device descriptors only: no SMART commands, serial collection, firmware or disk writes.
internal static class ManufacturerDisks
{
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<ManufacturerDisk>>? _pending;
    internal static async Task<IReadOnlyList<ManufacturerDisk>> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<IReadOnlyList<ManufacturerDisk>> task;
        lock (Gate) { if (_pending is null || _pending.IsCompleted) _pending = Task.Run(Read); task = _pending; }
        // A slow device driver cannot hold the dialog open or cause repeated parallel probes.
        try { return await task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        catch (TimeoutException) { return []; }
    }

    internal static ManufacturerDisk? Parse(byte[] data, int returned)
    {
        if (returned < 36 || returned > data.Length) return null;
        var size = BitConverter.ToUInt32(data, 4);
        if (size < 36 || size > returned || data[8] != 0) return null; // Direct-access disk only.
        string Field(int index)
        {
            var offset = BitConverter.ToUInt32(data, index);
            if (offset < 36 || offset >= size) return "";
            var end = Array.IndexOf(data, (byte)0, (int)offset, (int)(size - offset));
            if (end < 0 || end - offset > 256) return "";
            return Encoding.ASCII.GetString(data, (int)offset, end - (int)offset).Trim();
        }
        var vendor = Field(12); var product = Field(16);
        if (vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase) || vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase) || vendor.Equals("SCSI", StringComparison.OrdinalIgnoreCase)) vendor = "";
        if (product.Length == 0) return null;
        var model = product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? product : (vendor + " " + product).Trim();
        if (model.Any(char.IsControl)) return null;
        return new(model, BitConverter.ToUInt32(data, 28));
    }

    private static IReadOnlyList<ManufacturerDisk> Read()
    {
        var disks = new List<ManufacturerDisk>();
        var diskGuid = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
        var devices = SetupDiGetClassDevs(ref diskGuid, null, IntPtr.Zero, 0x12);
        if (devices == new IntPtr(-1)) return disks;
        try
        {
            for (uint index = 0; index < 256; index++)
            {
                var item = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref diskGuid, index, ref item)) break;
                SetupDiGetDeviceInterfaceDetail(devices, ref item, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                if (size is < 8 or > 16384) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(devices, ref item, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (path is null) continue;
                    using var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid) continue;
                    var query = new byte[12]; var header = new byte[8];
                    if (!DeviceIoControl(handle, 0x002d1400, query, 12, header, 8, out var count, IntPtr.Zero) || count < 8) continue;
                    var required = BitConverter.ToUInt32(header, 4);
                    if (required is < 36 or > 65536) continue;
                    var descriptor = new byte[required];
                    if (DeviceIoControl(handle, 0x002d1400, query, 12, descriptor, required, out count, IntPtr.Zero) && Parse(descriptor, (int)count) is { } disk)
                        disks.Add(disk);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return disks;
    }

    [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { internal uint Size; internal Guid ClassGuid; internal uint Flags; internal UIntPtr Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devices, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
