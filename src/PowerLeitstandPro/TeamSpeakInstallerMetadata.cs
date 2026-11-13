using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerControlProWpf;

internal sealed record TeamSpeakMsiInfo(string Name, string Manufacturer, string Version, string ProductCode, string FileVersion, string Architecture, string PerUser, string AllUsers, string ComponentCode = "");

// Opens the MSI database read-only. No package actions or custom actions are executed.
internal static class TeamSpeakInstallerMetadata
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder value, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiGetSummaryInformationW(uint database, string? path, uint count, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiSummaryInfoGetPropertyW(uint summary, uint property, out uint type, out int number, out System.Runtime.InteropServices.ComTypes.FILETIME time, StringBuilder text, ref uint size);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiGetProductInfoW(string product, string property, StringBuilder text, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MsiGetComponentPathW(string product, string component, StringBuilder text, ref uint size);
    private static void Check(uint result) { if (result != 0) throw new Win32Exception((int)result); }
    internal static TeamSpeakMsiInfo Read(string path)
    {
        Check(MsiOpenDatabaseW(path, IntPtr.Zero, out uint database));
        try
        {
            string Property(string name) => Rows(database, "SELECT `Value` FROM `Property` WHERE `Property`='" + name + "'", 1).SingleOrDefault()?.Single() ?? "";
            var files = Rows(database, "SELECT `FileName`,`Version`,`Component_` FROM `File`", 3).Where(x => x[0].Split('|').Last().Equals("TeamSpeak.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length != 1) throw new System.IO.InvalidDataException("TeamSpeak client file is not unique.");
            Check(MsiGetSummaryInformationW(database, null, 0, out uint summary)); string architecture;
            try { uint size = 1024; var text = new StringBuilder(1025); Check(MsiSummaryInfoGetPropertyW(summary, 7, out _, out _, out _, text, ref size)); architecture = text.ToString().Split(';')[0]; }
            finally { MsiCloseHandle(summary); }
            string component = Rows(database, "SELECT `Component`,`ComponentId` FROM `Component`", 2).Single(x => x[0] == files[0][2])[1];
            return new(Property("ProductName"), Property("Manufacturer"), Property("ProductVersion"), Property("ProductCode"), files[0][1], architecture, Property("MSIINSTALLPERUSER"), Property("ALLUSERS"), component);
        }
        finally { MsiCloseHandle(database); }
    }
    internal static string? InstalledExecutable(TeamSpeakMsiInfo metadata)
    {
        if (!Guid.TryParse(metadata.ProductCode, out _) || !Guid.TryParse(metadata.ComponentCode, out _)) return null;
        uint size = 32767; var text = new StringBuilder(32768);
        return MsiGetComponentPathW(metadata.ProductCode, metadata.ComponentCode, text, ref size) == 3 ? text.ToString() : null;
    }
    internal static string? RegisteredExecutable(string productCode)
    {
        uint size = 32767; var text = new StringBuilder(32768);
        if (!Guid.TryParse(productCode, out _) || MsiGetProductInfoW(productCode, "LocalPackage", text, ref size) != 0) return null;
        string cache = text.ToString(); if (!System.IO.File.Exists(cache) || !ProgramPortableUpdates.PlainPath(cache)) return null;
        var metadata = Read(cache);
        return metadata.ProductCode.Equals(productCode, StringComparison.OrdinalIgnoreCase) && metadata.Name == "TeamSpeak" && metadata.Manufacturer == "TeamSpeak" ? InstalledExecutable(metadata) : null;
    }
    internal static string? RegisteredScope(string productCode)
    {
        uint size = 32; var text = new StringBuilder(33);
        if (!Guid.TryParse(productCode, out _) || MsiGetProductInfoW(productCode, "AssignmentType", text, ref size) != 0) return null;
        return text.ToString() switch { "0" => "user", "1" => "machine", _ => null };
    }
    private static List<string[]> Rows(uint database, string query, uint count)
    {
        Check(MsiDatabaseOpenViewW(database, query, out uint view)); var rows = new List<string[]>();
        try
        {
            Check(MsiViewExecute(view, 0));
            while (true)
            {
                uint result = MsiViewFetch(view, out uint record); if (result == 259) break; Check(result);
                try
                {
                    if (rows.Count > 20000) throw new System.IO.InvalidDataException("MSI metadata exceeds limit.");
                    var fields = new string[count];
                    for (uint i = 1; i <= count; i++) { uint size = 4096; var text = new StringBuilder(4097); Check(MsiRecordGetStringW(record, i, text, ref size)); fields[i - 1] = text.ToString(); }
                    rows.Add(fields);
                }
                finally { MsiCloseHandle(record); }
            }
            return rows;
        }
        finally { MsiCloseHandle(view); }
    }
}
