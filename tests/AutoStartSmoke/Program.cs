using Microsoft.Win32;
using PowerControlProWpf;

var directory = Path.Combine(Path.GetTempPath(), "PowerLeitstand-Autostart-Test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var registryPath = @"Software\PowerLeitstandTests\Autostart-" + Guid.NewGuid().ToString("N");
int count = 0;
void Assert(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    Console.WriteLine("PASS " + description);
    count++;
}
try
{
    var current = Path.Combine(directory, "Desktop Neuer Ordner", "Meine App.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(current)!);
    File.WriteAllText(current, "test fixture, never executed");
    var expected = $"\"{current}\" --tray";
    var memory = new MemoryRegistration();
    var service = new AutoStartService(memory);
    Assert(service.Check(current) is { Enabled: false, Updated: false, Error: null } && memory.Writes == 0,
        "absent registration remains disabled, no writes");
    memory.Command = "\"C:\\Missing Old App\\Old.exe\" --tray";
    Assert(service.Check(current) is { Enabled: true, Updated: true, Error: null } && memory.Command == expected,
        "missing old EXE repaired to relocated and renamed app with spaces");
    var writes = memory.Writes;
    Assert(service.Check(current) is { Updated: false, Error: null } && memory.Writes == writes,
        "repeated startup performs no redundant writes");
    var old = Path.Combine(directory, "Previous Version.exe");
    File.WriteAllText(old, "test fixture, never executed");
    memory.Command = $"\"{old}\" --tray";
    Assert(service.Check(current) is { Updated: true, Error: null } && memory.Command == expected && File.Exists(old),
        "existing old copy redirected without deleting the old EXE");
    memory.Command = expected.ToUpperInvariant();
    Assert(service.Check(current) is { Updated: false, Error: null }, "path comparison ignores case");
    foreach (var legacy in new[] { current, $"\"{current}\"", "", $"\"{current}\" --old-argument" })
    {
        memory.Command = legacy;
        Assert(service.Check(current) is { Updated: true, Error: null } && memory.Command == expected,
            "legacy command normalized to quoted current EXE and --tray: " + legacy);
    }
    memory.Command = "old";
    Assert(service.Check(current, repair: false) is { Updated: false, Command: "old" }, "read-only check does not repair");
    Assert(service.Check(null) is { Updated: false, Command: "old" }, "external host cannot register itself");
    foreach (var invalid in new[] { "relative.exe", Path.Combine(directory, "absent.exe"), Path.Combine(directory, "app.dll"), Path.Combine(directory, "dotnet.exe") })
    {
        memory.Command = "old";
        Assert(service.Check(invalid).Error is not null && memory.Command == "old", "invalid/missing executable leaves old entry intact: " + Path.GetFileName(invalid));
    }
    memory.Command = null;
    Assert(service.SetEnabled(true, null).Error is not null && memory.Command is null, "enable without executable reports failure");
    Assert(service.SetEnabled(true, current) is { Enabled: true, Error: null } && memory.Command == expected, "explicit enable verified by readback");
    Assert(service.SetEnabled(false, current) is { Enabled: false, Error: null }, "explicit disable removes entry");
    Assert(service.Check(current) is { Enabled: false, Updated: false }, "update/startup cannot re-enable disabled setting");
    memory.Command = "old"; memory.FailWrite = true;
    Assert(service.Check(current) is { Enabled: true, Updated: false, Error: not null } && memory.Command == "old", "write denied preserves prior registration and reports error");
    Assert(service.SetEnabled(true, current).Error is not null, "toggle write failure is reported without crashing");
    memory.FailWrite = false; memory.FailRead = true;
    Assert(service.Check(current) is { Enabled: null, Error: not null }, "read denied reports unknown state without crashing");
    memory.FailRead = false; memory.DropWrites = true;
    Assert(service.Check(current).Error is not null, "unsuccessful write detected by readback");
    memory.DropWrites = false; memory.FailDelete = true;
    Assert(service.SetEnabled(false, current).Error is not null && memory.Command == "old", "disable failure retains enabled registration");
    // Real registry adapter, isolated under a unique non-startup key.
    var store = new RegistryAutoStartRegistration(registryPath);
    var actual = new AutoStartService(store);
    Assert(actual.Check(current) is { Enabled: false }, "isolated registry absent key stays absent");
    var created = actual.SetEnabled(true, current);
    Assert(created.Error is null && store.Read() == expected, "registry create and quote roundtrip: " + (created.Error ?? "ok"));
    using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true)!) key.SetValue("Unrelated", "keep");
    store.Write("\"C:\\Old\\App.exe\" --tray");
    Assert(actual.Check(current) is { Updated: true, Error: null } && store.Read() == expected, "real registry stale path repaired to update target");
    Assert(actual.Check(current) is { Updated: false, Error: null }, "real registry correct path unchanged");
    Assert(actual.SetEnabled(false, current) is { Enabled: false, Error: null }, "real registry disable verified");
    using (var key = Registry.CurrentUser.OpenSubKey(registryPath)!)
        Assert((string?)key.GetValue("Unrelated") == "keep" && key.GetValueNames().Length == 1, "unrelated registry values preserved, own entry removed");
    using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true)!) key.SetValue(RegistryAutoStartRegistration.ValueName, 123, RegistryValueKind.DWord);
    Assert(actual.Check(current) is { Enabled: null, Error: not null }, "malformed registry type reported instead of overwritten");
    Console.WriteLine($"PASS ALL {count} autostart checks; real Windows Run key not changed; no app/system action executed");
}
catch (Exception exception)
{
    Console.Error.WriteLine("FAIL " + exception.Message);
    Environment.ExitCode = 1;
}
finally
{
    try { Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); }
    catch (Exception exception) { Console.Error.WriteLine("Registry fixture cleanup: " + exception.Message); Environment.ExitCode = 1; }
    // The test creates this exact unique directory; remove only its own fixtures.
    var resolved = Path.GetFullPath(directory);
    if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("PowerLeitstand-Autostart-Test-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unsafe fixture cleanup path");
    Directory.Delete(resolved, recursive: true);
}

sealed class MemoryRegistration : IAutoStartRegistration
{
    public string? Command;
    public int Writes;
    public bool FailRead, FailWrite, FailDelete, DropWrites;
    public string? Read() => FailRead ? throw new UnauthorizedAccessException("test read denied") : Command;
    public void Write(string command)
    {
        if (FailWrite) throw new UnauthorizedAccessException("test write denied");
        Writes++;
        if (!DropWrites) Command = command;
    }
    public void Delete()
    {
        if (FailDelete) throw new UnauthorizedAccessException("test delete denied");
        Command = null;
    }
}
