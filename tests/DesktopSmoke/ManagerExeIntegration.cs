using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using PowerControlProWpf;

internal static class ManagerExeIntegration
{
    internal static int Run(string output, string executable) => Task.Run(async () =>
    {
        var store = new AppBackupStore();
        var backups = new List<AppBackup>(); var processes = new List<Process>();
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run", valueName = "PowerControlProWpf";
        using var initial = Registry.CurrentUser.OpenSubKey(runPath);
        var original = initial?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = original is null ? RegistryValueKind.String : initial!.GetValueKind(valueName);
        var folder = Path.Combine(output, "PortableVersionChange"); Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "PowerLeitstand-PRO.exe");
        try
        {
            if (Directory.Exists(store.Root) && Directory.EnumerateFileSystemEntries(store.Root).Any())
                throw new InvalidOperationException("Existing user backups detected; integration does not prune them.");
            var releases = await UpdateReleaseCatalog.LoadAsync(CancellationToken.None);
            if (releases.Count == 0 || releases.Any(r => string.IsNullOrWhiteSpace(r.Notes))) throw new Exception("Production catalog did not load release notes");
            Console.WriteLine("PASS real GitHub catalog and version-specific notes: " + string.Join(", ", releases.Select(r => r.Update.Version)));
            File.Copy(executable, target, true);
            var expectedNew = await AppBackupStore.HashAsync(executable);
            var legacy = "I:/Codex/PowerLeitstand-v1.1.2-Windows-x64/PowerLeitstand-PRO.exe";
            foreach (var restore in new[] { false, true })
            {
                var saved = await store.CreateAsync(target, AppBackupStore.FileVersion(target), "Integrationstest: isolierte Programmdatei");
                backups.Add(saved);
                var source = restore ? backups[0].BinaryPath : legacy;
                var staged = Path.Combine(folder, ".PowerLeitstand-PRO.exe." + Guid.NewGuid().ToString("N") + ".new");
                await AppBackupStore.CopyAsync(source, staged, CancellationToken.None);
                var expected = await AppBackupStore.HashAsync(source);
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (var arg in new[] { "--update-helper", target, staged, saved.BinaryPath, int.MaxValue.ToString() }) start.ArgumentList.Add(arg);
                var helper = Process.Start(start)!; processes.Add(helper);
                var deadline = DateTime.UtcNow.AddSeconds(110); Process? app = null;
                while (!helper.HasExited && DateTime.UtcNow < deadline)
                {
                    foreach (var p in Process.GetProcessesByName("PowerLeitstand-PRO"))
                    {
                        try
                        {
                            if (string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                            {
                                app = p; if (!processes.Any(x => x.Id == p.Id)) processes.Add(p);
                            }
                        }
                        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    }
                    await Task.Delay(300);
                }
                if (!helper.HasExited || app is null) throw new Exception("Update helper or replacement app did not complete startup");
                app.Refresh();
                if (app.HasExited || app.MainWindowHandle == IntPtr.Zero || !app.WaitForInputIdle(10000) || !app.Responding) throw new Exception("Replaced WPF app not responding");
                if (await AppBackupStore.HashAsync(target) != expected || restore && expected != expectedNew) throw new Exception("Replacement bytes differ");
                if (!await store.VerifyAsync(saved) || Directory.GetFiles(folder, "*.bak").Length != 0) throw new Exception("Persistent backup missing or adjacent backup left behind");
                using var run = Registry.CurrentUser.OpenSubKey(runPath);
                if (original is null ? run?.GetValue(valueName) is not null : !Equals(run?.GetValue(valueName), "\"" + target + "\" --tray")) throw new Exception("Autostart state mismatch");
                Console.WriteLine(restore
                    ? "PASS persistent local restore -> v1.1.3 -> explicit startup acknowledgement -> responding WPF app; backup kept in AppData, no adjacent .bak"
                    : "PASS compatible downgrade -> v1.1.2 -> legacy window readiness -> responding WPF app; backup kept in AppData, no adjacent .bak");
                app.CloseMainWindow(); if (!app.WaitForExit(5000)) { app.Kill(); await app.WaitForExitAsync(); }
            }
            Console.WriteLine("PASS portable persistent-backup integration and production GitHub catalog. Test EXE only; installed EXE unchanged.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally
        {
            foreach (var process in processes)
                try { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) { process.Kill(); process.WaitForExit(); } } } catch { }
            foreach (var backup in backups) try { store.Delete(backup); } catch (Exception e) { Console.WriteLine("Test backup retained: " + e.Message); }
            using var run = Registry.CurrentUser.OpenSubKey(runPath, true) ?? Registry.CurrentUser.CreateSubKey(runPath);
            if (original is null) run.DeleteValue(valueName, false); else run.SetValue(valueName, original, kind);
            Console.WriteLine("Original autostart registration restored; only owned test processes and backups cleaned.");
        }
    }).GetAwaiter().GetResult();
}
