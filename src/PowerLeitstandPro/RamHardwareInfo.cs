using System.Runtime.InteropServices;
using System.Text;

namespace PowerControlProWpf;

internal sealed record RamModule(string Slot, string Manufacturer, string PartNumber, string Type, ulong? Bytes, uint? ConfiguredSpeed, uint? RatedSpeed, string SpeedUnit = "MT/s");
internal sealed record RamHardwareInfo(int Slots, IReadOnlyList<RamModule> Modules)
{
    internal static RamHardwareInfo Empty { get; } = new(0, []);
    internal static RamHardwareInfo Read()
    {
        var size = GetSystemFirmwareTable(0x52534d42, 0, null, 0);
        if (size is < 8 or > 4194304) return Empty;
        var data = new byte[size];
        return GetSystemFirmwareTable(0x52534d42, 0, data, size) == size ? Parse(data) : Empty;
    }

    internal static RamHardwareInfo Parse(byte[] raw)
    {
        if (raw.Length < 8) return Empty;
        var size = BitConverter.ToUInt32(raw, 4);
        if (size > raw.Length - 8) return Empty;
        var end = 8 + (int)size; var modules = new List<RamModule>(); var slots = 0;
        for (var offset = 8; offset + 4 <= end;)
        {
            var type = raw[offset]; var length = raw[offset + 1];
            if (length < 4 || offset + length > end) break;
            var stringsEnd = offset + length;
            while (stringsEnd + 1 < end && (raw[stringsEnd] != 0 || raw[stringsEnd + 1] != 0)) stringsEnd++;
            if (stringsEnd + 1 >= end) break;
            if (type == 127) break;
            if (type == 17 && length >= 21)
            {
                slots++;
                ushort Word(int index) => index + 2 <= length ? BitConverter.ToUInt16(raw, offset + index) : (ushort)0;
                uint Dword(int index) => index + 4 <= length ? BitConverter.ToUInt32(raw, offset + index) : 0;
                string Text(int index)
                {
                    if (index >= length || raw[offset + index] == 0) return "";
                    var selected = raw[offset + index]; var cursor = offset + length;
                    for (var n = 1; n < selected && cursor < stringsEnd; n++) { while (cursor < stringsEnd && raw[cursor] != 0) cursor++; cursor++; }
                    if (cursor > stringsEnd) return "";
                    var finish = cursor; while (finish < stringsEnd && raw[finish] != 0) finish++;
                    var value = Encoding.Latin1.GetString(raw, cursor, finish - cursor).Trim();
                    return value is "Unknown" or "Not Specified" or "[Empty]" or "Undefined" ? "" : value;
                }
                uint? Speed(int index, int extended)
                {
                    var value = Word(index); var speed = value == ushort.MaxValue ? Dword(extended) : value;
                    return speed > 0 ? speed : null;
                }
                var capacity = Word(12);
                if (capacity != 0)
                {
                    ulong? bytes = capacity == ushort.MaxValue ? null : capacity == 0x7fff ? (Dword(28) & 0x7fffffff) * 1048576UL : (ulong)(capacity & 0x7fff) * ((capacity & 0x8000) != 0 ? 1024UL : 1048576UL);
                    if (bytes == 0) bytes = null;
                    var memoryType = raw[offset + 18] switch { 18 => "DDR", 19 => "DDR2", 20 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "" };
                    var unit = raw[1] > 3 || raw[1] == 3 && raw[2] >= 1 ? "MT/s" : "MHz";
                    modules.Add(new(Text(16), Text(23), Text(26), memoryType, bytes, Speed(32, 88), Speed(21, 84), unit));
                }
            }
            offset = stringsEnd + 2;
        }
        return new(slots, modules);
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetSystemFirmwareTable(uint provider, uint id, [Out] byte[]? buffer, uint length);
}

