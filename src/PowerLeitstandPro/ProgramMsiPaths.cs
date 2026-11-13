using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerControlProWpf;

internal static class ProgramMsiPaths
{
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiGetProductInfoExW(string product, string? sid, uint context, string property, StringBuilder text, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int MsiGetComponentPathExW(string product, string component, string? sid, uint context, StringBuilder text, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder text, ref uint size);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(uint handle);

    internal static IReadOnlyList<ProgramPathCandidate> Read(ProgramPathRegistration registration, CancellationToken token)
    {
        var found = new List<ProgramPathCandidate>();
        if (!registration.WindowsInstaller || !Guid.TryParse(registration.ProductKey, out _)) return found;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try { return ReadCore(registration, budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return found; }
    }
    private static IReadOnlyList<ProgramPathCandidate> ReadCore(ProgramPathRegistration registration, CancellationToken token)
    {
        var found = new List<ProgramPathCandidate>();
        // User SID=null addresses the current user only. Explicit contexts avoid the
        // ambiguous legacy API when a GUID is registered both per-user and per-machine.
        uint[] contexts = registration.Hive == Microsoft.Win32.RegistryHive.CurrentUser ? [2, 1] : [4, 2, 1];
        foreach (uint context in contexts)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string? cache = ProductProperty(registration.ProductKey, context, "LocalPackage");
                if (cache is null || !File.Exists(cache) || !ProgramPortableUpdates.PlainPath(cache)) continue;
                if (MsiOpenDatabaseW(cache, IntPtr.Zero, out uint database) != 0) continue;
                try
                {
                    string? actualCode = Rows(database, "SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'", 1, token).FirstOrDefault()?[0];
                    if (!string.Equals(actualCode, registration.ProductKey, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var row in Rows(database, "SELECT `File`,`FileName`,`Component_` FROM `File`", 3, token,
                        row => row[1].Split('|').Last().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        token.ThrowIfCancellationRequested();
                        string filename = row[1].Split('|').Last();
                        string componentKey = row[2].Replace("'", "''", StringComparison.Ordinal);
                        var component = Rows(database, "SELECT `ComponentId`,`KeyPath` FROM `Component` WHERE `Component`='" + componentKey + "'", 2, token).SingleOrDefault();
                        if (component is null || !Guid.TryParse(component[0], out _) || component[1] != row[0]) continue; // Only an exact file key path.
                        uint size = 32767; var text = new StringBuilder(32768);
                        if (MsiGetComponentPathExW(registration.ProductKey, component[0], null, context, text, ref size) != 3) continue;
                        string path = text.ToString();
                        if (!System.IO.Path.GetFileName(path).Equals(filename, StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add(new(path, context == 4 ? "MSI component (machine)" : "MSI component (current user)", 1, true));
                        if (found.Count >= 64) break;
                    }
                }
                finally { MsiCloseHandle(database); }
                // An existing machine product takes precedence for its HKLM entry.
                // Fall back to current-user context only when that registration is absent.
                break;
            }
            catch (Exception e) when (ProgramPathResolver.ReadFailure(e)) { }
        }
        return found;
    }
    private static string? ProductProperty(string product, uint context, string property)
    {
        uint size = 32767; var text = new StringBuilder(32768);
        return MsiGetProductInfoExW(product, null, context, property, text, ref size) == 0 ? text.ToString() : null;
    }
    private static List<string[]> Rows(uint database, string query, uint fields, CancellationToken token, Func<string[], bool>? include = null)
    {
        var rows = new List<string[]>();
        if (MsiDatabaseOpenViewW(database, query, out uint view) != 0) return rows;
        try
        {
            if (MsiViewExecute(view, 0) != 0) return rows;
            int fetched = 0;
            while (fetched++ < 20000)
            {
                token.ThrowIfCancellationRequested();
                if (MsiViewFetch(view, out uint record) != 0) break;
                try
                {
                    var values = new string[fields]; bool valid = true;
                    for (uint i = 1; i <= fields; i++)
                    {
                        uint size = 255; var text = new StringBuilder(256);
                        uint result = MsiRecordGetStringW(record, i, text, ref size);
                        if (result == 234 && size < 4096) { text = new StringBuilder((int)size + 1); uint capacity = size + 1; result = MsiRecordGetStringW(record, i, text, ref capacity); }
                        if (result != 0) { valid = false; break; }
                        values[i - 1] = text.ToString();
                    }
                    if (valid && (include is null || include(values))) rows.Add(values);
                }
                finally { MsiCloseHandle(record); }
            }
        }
        finally { MsiCloseHandle(view); }
        return rows;
    }
}
