using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PowerControlProWpf;

internal enum AdapterConnection { Unknown, Disconnected, Connected, Disabled }

internal sealed record NetworkAdapterEntry(string InstanceId, string Name, bool? Present,
    Guid InterfaceId = default, string? Manufacturer = null, bool? DeviceDisabled = null,
    AdapterConnection Connection = AdapterConnection.Unknown, int InterfaceType = 0,
    bool InternetV4 = false, bool InternetV6 = false, bool DisableVerified = false)
{
    internal bool IsInternetRoute => InternetV4 || InternetV6;
    internal bool CanRemove => Present == false && !IsInternetRoute && NetworkAdapterEntries.IsInstanceId(InstanceId);
    internal bool CanDisable => Present == true && DeviceDisabled == false && Connection == AdapterConnection.Disconnected &&
        DisableVerified && !IsInternetRoute && NetworkAdapterEntries.IsInstanceId(InstanceId);
    internal bool CanEnable => Present == true && DeviceDisabled == true && !IsInternetRoute &&
        Connection == AdapterConnection.Disabled && NetworkAdapterEntries.IsInstanceId(InstanceId);
}

internal static class NetworkAdapterEntries
{
    private static readonly Guid NetworkClass = new("4d36e972-e325-11ce-bfc1-08002be10318");
    private const int MaxDeviceId = 200;
    private const int NoMoreItems = 259;
    private const int NoSuchDevNode = 0x0d;
    private const uint DeviceHasProblem = 0x400;
    private const uint DeviceDisabledProblem = 22;

    internal static bool RemovalSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    internal static bool IsInstanceId(string id) => id.Length is > 0 and < MaxDeviceId &&
        !id.Any(character => char.IsControl(character) || character is '/' or '"') &&
        id.Split('\\') is [ { Length: > 0 }, { Length: > 0 }, { Length: > 0 } ];

    internal static IReadOnlyList<NetworkAdapterEntry> Read()
    {
        var networkClass = NetworkClass;
        var handle = SetupDiGetClassDevsW(ref networkClass, null, IntPtr.Zero, 0);
        if (handle == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var adapters = new List<NetworkAdapterEntry>();
            for (uint index = 0; ; index++)
            {
                var data = new DeviceData { Size = (uint)Marshal.SizeOf<DeviceData>() };
                if (!SetupDiEnumDeviceInfo(handle, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NoMoreItems) return adapters;
                    throw new Win32Exception(error);
                }

                var idBuffer = new StringBuilder(MaxDeviceId);
                if (!SetupDiGetDeviceInstanceIdW(handle, ref data, idBuffer, (uint)idBuffer.Capacity, out _))
                    throw new Win32Exception();
                var id = idBuffer.ToString();
                var name = ReadName(handle, ref data, 12) ?? ReadName(handle, ref data, 0) ?? id;
                var present = ReadPresence(handle, ref data);
                bool? disabled = null;
                if (present == true && CM_Get_DevNode_Status(out var status, out var problem, data.DevInst, 0) == 0)
                    disabled = (status & DeviceHasProblem) == 0 ? false : problem == DeviceDisabledProblem ? true : null;
                adapters.Add(new(id, name, present, ReadInterfaceId(handle, ref data), ReadName(handle, ref data, 11), disabled));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(handle); }
    }

    private static Guid ReadInterfaceId(IntPtr handle, ref DeviceData data)
    {
        using var keyHandle = SetupDiOpenDevRegKey(handle, ref data, 1, 0, 2, 1);
        if (keyHandle.IsInvalid) return Guid.Empty;
        try
        {
            using var key = RegistryKey.FromHandle(keyHandle);
            return Guid.TryParse(key.GetValue("NetCfgInstanceId") as string, out var id) ? id : Guid.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Guid.Empty; }
    }

    private static string? ReadName(IntPtr handle, ref DeviceData data, uint property)
    {
        var value = new byte[2048];
        if (!SetupDiGetDeviceRegistryPropertyW(handle, ref data, property, out var type, value, (uint)value.Length, out var size) ||
            type != 1 || size > value.Length) return null;
        var name = Encoding.Unicode.GetString(value, 0, (int)size).TrimEnd('\0').Trim();
        return name.Length == 0 ? null : name;
    }

    private static bool? ReadPresence(IntPtr handle, ref DeviceData data)
    {
        var key = new DevicePropertyKey { Format = new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), Id = 5 };
        var value = new byte[1];
        return SetupDiGetDevicePropertyW(handle, ref data, ref key, out var type, value, 1, out var size, 0) &&
            type == 0x11 && size == 1
            ? value[0] switch { 0 => false, 255 => true, _ => null }
            : null;
    }

    internal static NetworkAdapterEntry? FindRemovable(NetworkAdapterEntry selected, IReadOnlyList<NetworkAdapterEntry> current)
    {
        if (!selected.CanRemove) return null;
        return FindCurrent(selected, current) is { CanRemove: true } match ? match : null;
    }

    internal static NetworkAdapterEntry? FindCurrent(NetworkAdapterEntry selected, IReadOnlyList<NetworkAdapterEntry> current)
    {
        var matches = current.Where(item => item.InstanceId.Equals(selected.InstanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches is [var match] ? match : null;
    }

    internal static async Task<int?> RemoveAsync(NetworkAdapterEntry selected)
    {
        if (!ElevationService.IsElevated()) throw new UnauthorizedAccessException();
        if (!RemovalSupported) throw new PlatformNotSupportedException();
        var current = FindRemovable(selected, NetworkAdapterConnections.Attach(Read()));
        if (current is null) return null;

        // A normal lookup succeeds only for devices in the current Windows device tree.
        if (CM_Locate_DevNodeW(out _, current.InstanceId, 0) != NoSuchDevNode) return null;
        return await RunAsync(CreateRemovalCommand(current)).ConfigureAwait(false);
    }

    internal static async Task<int?> ChangeEnabledAsync(NetworkAdapterEntry selected, bool enable)
    {
        if (!ElevationService.IsElevated()) throw new UnauthorizedAccessException();
        if (!RemovalSupported) throw new PlatformNotSupportedException();
        if (enable ? !selected.CanEnable : !selected.CanDisable) return null;

        var verified = enable ? null : await NetworkAdapterConnections.ReadDisableCandidatesAsync().ConfigureAwait(false);
        var current = FindCurrent(selected, NetworkAdapterConnections.Attach(Read(), verified));
        if (current is null || (enable ? !current.CanEnable : !current.CanDisable || current.InterfaceId != selected.InterfaceId) ||
            CM_Locate_DevNodeW(out _, current.InstanceId, 0) != 0) return null;
        return await RunAsync(CreateDeviceCommand(current.InstanceId, enable ? "/enable-device" : "/disable-device")).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(ProcessStartInfo command)
    {
        using var process = Process.Start(command) ?? throw new InvalidOperationException("PnPUtil");
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateRemovalCommand(NetworkAdapterEntry entry)
    {
        if (!entry.CanRemove) throw new ArgumentException("Invalid removal target.", nameof(entry));
        return CreateDeviceCommand(entry.InstanceId, "/remove-device");
    }

    private static ProcessStartInfo CreateDeviceCommand(string instanceId, string verb)
    {
        var command = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        command.ArgumentList.Add(verb);
        command.ArgumentList.Add(instanceId);
        return command;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevicePropertyKey
    {
        internal Guid Format;
        internal uint Id;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceData data, StringBuilder id, uint size, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceData data, uint property,
        out uint type, [Out] byte[] value, uint size, out uint required);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceData data, ref DevicePropertyKey key,
        out uint type, [Out] byte[] value, uint size, out uint required, uint flags);
    [DllImport("setupapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeRegistryHandle SetupDiOpenDevRegKey(IntPtr set, ref DeviceData data,
        uint scope, uint profile, uint keyType, int access);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(out uint device, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint device, uint flags);
}
