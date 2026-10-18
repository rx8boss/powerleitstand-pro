using System.Diagnostics;
using System.IO.Pipes;
using PowerControlProWpf;

if (args.FirstOrDefault() == "ready")
{
    await UpdateStartupConfirmation.SendAsync(new[] { "--update-ready", args[1] });
    await Task.Delay(3000); return;
}
if (args.FirstOrDefault() == "exit") return;
if (args.FirstOrDefault() == "handoff-parent") { await Task.Delay(700); return; }
if (args.FirstOrDefault() == "silent") { await Task.Delay(10000); return; }
// Keep mutex acquisition and release on the same thread, matching the WPF dispatcher.
if (args.FirstOrDefault() == "instance")
{
    using var guard = new SingleInstanceService(args[1]);
    if (guard.TryAcquire()) { Console.WriteLine("PRIMARY"); guard.Listen(() => Console.WriteLine("ACTIVATED")); Console.Out.Flush(); Thread.Sleep(6000); }
    else Console.WriteLine(guard.ActivateExistingAsync().GetAwaiter().GetResult() ? "SECONDARY" : "NO_REPLY");
    return;
}
var root = Path.Combine(Path.GetTempPath(), "PowerLeitstand-Recovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var count = 0;
void Assert(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS " + label); count++; }
Process Child(params string[] command)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(UpdateRecovery).Assembly.Location);
    foreach (var arg in command) start.ArgumentList.Add(arg);
    return Process.Start(start)!;
}
try
{
    foreach (var scenario in new[] { "success", "no-confirmation", "start-throws", "stop-fails", "restore-blocked", "cleanup-blocked" })
    {
        var dir = Path.Combine(root, scenario); Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "app.exe"); var staged = Path.Combine(dir, ".app.exe.new"); var backup = Path.Combine(dir, ".app.exe.bak");
        File.WriteAllText(target, "old"); File.WriteAllText(staged, "new");
        bool stopped = false, restoredStarted = false; FileStream? held = null;
        try
        {
            var result = await UpdateRecovery.InstallAsync(target, staged, backup, () =>
            {
                if (scenario == "start-throws") throw new IOException("fixture cannot launch");
                if (scenario == "cleanup-blocked") held = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.FromResult(scenario is "success" or "cleanup-blocked");
            }, () =>
            {
                stopped = true;
                if (scenario == "stop-fails") throw new IOException("fixture cannot stop");
                if (scenario == "restore-blocked") held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Task.CompletedTask;
            }, () => { Assert(File.ReadAllText(target) == "old", scenario + " restored bytes before restart"); restoredStarted = true; });
            if (scenario == "success") Assert(!result.Restored && File.ReadAllText(target) == "new" && !File.Exists(backup) && !File.Exists(staged) && !stopped, "successful startup removes backup");
            else if (scenario == "cleanup-blocked") Assert(!result.Restored && result.Warning is not null && File.ReadAllText(target) == "new" && File.Exists(backup), "cleanup failure preserves healthy new app and reports backup");
            else Assert(result.Restored && stopped && restoredStarted && !File.Exists(backup) && !File.Exists(staged), scenario + " rolls back and removes adjacent backup");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && scenario is "stop-fails" or "restore-blocked")
        { Assert(File.ReadAllText(backup) == "old" && !restoredStarted, scenario + " retains recovery backup"); }
        finally { held?.Dispose(); }
    }
    var blocked = Path.Combine(root, "blocked.exe"); var input = Path.Combine(root, "input.new"); var saved = Path.Combine(root, "saved.bak");
    File.WriteAllText(blocked, "old"); File.WriteAllText(input, "new");
    using (var held = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        try { await UpdateRecovery.InstallAsync(blocked, input, saved, () => throw new Exception("must not start"), () => Task.CompletedTask, () => { }); throw new Exception("replacement should fail"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Assert(File.ReadAllText(blocked) == "old" && File.Exists(input) && !File.Exists(saved), "blocked replacement leaves original intact"); }
    }
    foreach (var mode in new[] { "ready", "exit", "silent" })
    {
        var name = "PowerLeitstand-ready-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var child = Child(mode, name);
        try { Assert(await UpdateStartupConfirmation.WaitAsync(pipe, child, TimeSpan.FromSeconds(2)) == (mode == "ready"), "startup IPC " + mode); }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
    File.WriteAllText(saved, "existing backup");
    try { await UpdateRecovery.InstallAsync(blocked, input, saved, () => throw new Exception("must not launch"), () => Task.CompletedTask, () => { }); throw new Exception("backup overwritten"); }
    catch (IOException) { Assert(File.ReadAllText(saved) == "existing backup" && File.ReadAllText(blocked) == "old", "existing backup is never overwritten"); }
    using (var parent = Child("handoff-parent"))
    {
        await SingleInstanceService.WaitForHandoffAsync(new[] { "--instance-handoff", parent.Id.ToString() });
        Assert(parent.HasExited, "elevation handoff waits for predecessor exit");
    }
    var scope = Guid.NewGuid().ToString("N");
    using (var primary = Child("instance", scope))
    {
        Assert(await primary.StandardOutput.ReadLineAsync() == "PRIMARY", "first instance acquires ownership");
        using var duplicate = Child("instance", scope);
        Assert(await duplicate.StandardOutput.ReadLineAsync() == "SECONDARY", "duplicate signals existing instance");
        Assert(await primary.StandardOutput.ReadLineAsync() == "ACTIVATED", "existing instance receives activation");
        await duplicate.WaitForExitAsync();
        primary.Kill(); await primary.WaitForExitAsync();
    }
    using (var restart = Child("instance", scope))
    { Assert(await restart.StandardOutput.ReadLineAsync() == "PRIMARY", "abandoned instance ownership recovered after crash"); restart.Kill(); await restart.WaitForExitAsync(); }
    Console.WriteLine($"PASS ALL {count} recovery/IPC checks. Only temporary fixture files/processes used.");
}
catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
finally { if (Path.GetFileName(root).StartsWith("PowerLeitstand-Recovery-")) Directory.Delete(root, true); }
