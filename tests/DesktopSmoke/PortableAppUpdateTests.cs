using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class PortableAppUpdateTests
{
    internal static async Task<int> RunAsync(string output, string oldExe, string newExe)
    {
        int checks = 0;
        void Assert(bool ok, string description)
        {
            if (!ok) throw new InvalidOperationException(description);
            checks++;
            Console.WriteLine("PASS " + description);
        }

        string fixture = Directory.CreateDirectory(Path.Combine(output, Guid.NewGuid().ToString("N"))).FullName;
        string target = Path.Combine(fixture, "PowerLeitstand-PRO.exe");
        string staged = Path.Combine(fixture, ".PowerLeitstand-PRO.exe.test.new");
        string settingsFile = Path.Combine(fixture, "settings.json");
        var store = new AppBackupStore(Path.Combine(fixture, "Backups"));
        AppBackup? backup = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        string oldHash = await AppBackupStore.HashAsync(oldExe, token);
        string newHash = await AppBackupStore.HashAsync(newExe, token);
        Assert(AppBackupStore.FileVersion(oldExe) == "1.1.6" && AppBackupStore.FileVersion(newExe) == "1.1.7",
            "source binaries are 1.1.6 and 1.1.7");
        var release = JsonSerializer.SerializeToElement(new
        {
            tag_name = "v1.1.7",
            assets = new[] { new
            {
                name = "PowerLeitstand-PRO.exe",
                digest = "sha256:" + newHash,
                browser_download_url = ProjectLinks.RepositoryUrl + "/releases/download/v1.1.7/PowerLeitstand-PRO.exe"
            } }
        });
        var update = ReleaseAssetSelector.Select(release, new Version(1, 1, 4))
            ?? throw new InvalidOperationException("Version 1.1.7 was not offered.");
        using var client = new HttpClient(new ReleaseFixture(newExe));
        try
        {
            File.Copy(oldExe, target);
            new AppSettings { Language = "en", Theme = "Classic", SizeIndex = 1, CheckUpdatesOnFirstOpen = false, EsuRegistrationConfirmed = true }
                .Save(settingsFile);
            string settingsHash = await AppBackupStore.HashAsync(settingsFile, token);
            backup = await store.CreateAsync(target, "1.1.6", "Isolated updater test", token);
            Assert(await store.VerifyAsync(backup, token) && backup.Sha256 == oldHash, "backup matches the original 1.1.6 EXE");

            try
            {
                await UpdateService.DownloadAndVerifyAsync(client, update with { Sha256 = new string('0', 64) }, staged, null, token);
                throw new InvalidOperationException("Invalid SHA-256 was accepted.");
            }
            catch (InvalidDataException)
            {
                Assert(!File.Exists(staged) && await AppBackupStore.HashAsync(target, token) == oldHash,
                    "wrong SHA-256 rejects the download and keeps 1.1.6 intact");
            }

            async Task Stage()
            {
                await UpdateService.DownloadAndVerifyAsync(client, update, staged, null, token);
                Assert(await AppBackupStore.HashAsync(staged, token) == newHash && AppBackupStore.FileVersion(staged) == "1.1.7",
                    "download and SHA-256 match the genuine 1.1.7 EXE");
            }

            await Stage();
            var success = await UpdateRecovery.InstallAsync(target, staged, backup.BinaryPath,
                () => ConfirmWindowAsync(fixture, target, "1.1.7"), () => Task.CompletedTask,
                () => throw new InvalidOperationException("A healthy update unexpectedly rolled back."), retainBackup: true);
            Assert(!success.Restored && await AppBackupStore.HashAsync(target, token) == newHash,
                "replacement retained 1.1.7 after the WPF test window confirmed startup");
            Assert(await AppBackupStore.HashAsync(settingsFile, token) == settingsHash, "settings file remained identical after update");

            File.Copy(backup.BinaryPath, target, overwrite: true);
            await Stage();
            Task<bool>? restarted = null;
            var failure = await UpdateRecovery.InstallAsync(target, staged, backup.BinaryPath, async () =>
            {
                Assert(await AppBackupStore.HashAsync(target, token) == newHash, "1.1.7 was in place before the simulated failed start");
                using var pipe = NewPipe(out _);
                using var child = StartChild(fixture, "--portable-update-failed-start");
                bool ready = await UpdateStartupConfirmation.WaitAsync(pipe, child, TimeSpan.FromSeconds(12));
                await child.WaitForExitAsync(token);
                Assert(!ready && child.ExitCode == 7, "process exited without a startup acknowledgement");
                return false;
            }, () => Task.CompletedTask, () => restarted = ConfirmWindowAsync(fixture, target, "1.1.6"), retainBackup: true);
            Assert(failure.Restored && restarted is not null && await restarted && await AppBackupStore.HashAsync(target, token) == oldHash,
                "rollback restored the exact 1.1.6 EXE before the WPF test window confirmed restart");
            Assert(await store.VerifyAsync(backup, token) && !File.Exists(staged) && !Directory.EnumerateFiles(fixture, "*.bak").Any(),
                "verified backup retained without adjacent backup or staging files");
            Assert(await AppBackupStore.HashAsync(settingsFile, token) == settingsHash, "settings file remained identical after rollback");
            Console.WriteLine($"PASS {checks} checks; genuine EXE files, simulated download and WPF test process.");
            return 0;
        }
        finally
        {
            if (backup is not null) store.Delete(backup);
            File.Delete(staged);
            File.Delete(target);
        }
    }

    private static NamedPipeServerStream NewPipe(out string name)
    {
        name = "PowerLeitstand-ready-" + Guid.NewGuid().ToString("N");
        return new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static Process StartChild(string fixture, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(fixture);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Test process could not start.");
    }

    private static async Task<bool> ConfirmWindowAsync(string fixture, string target, string version)
    {
        await using var pipe = NewPipe(out string name);
        using var child = StartChild(fixture, "--portable-update-window", name, target, version, "--images-only");
        try { return await UpdateStartupConfirmation.WaitAsync(pipe, child, TimeSpan.FromSeconds(55)); }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }

    internal static int RunWindow(App app, string fixture, string pipe, string target, string version)
    {
        if (AppBackupStore.FileVersion(target) != version) return 5;
        var settings = AppSettings.Load(Path.Combine(fixture, "settings.json"));
        if (settings.Language != "en" || settings.Theme != "Classic" || settings.SizeIndex != 1 ||
            settings.CheckUpdatesOnFirstOpen || !settings.EsuRegistrationConfirmed) return 6;
        var window = new MainWindow(Path.Combine(fixture, "Window"), settings, loadUsersForTests: false) { ShowActivated = false };
        window.ContentRendered += async (_, _) =>
        {
            await Task.Delay(2000);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window.IsLoaded && window.IsVisible)
                await UpdateStartupConfirmation.SendAsync(["--update-ready", pipe]);
        };
        return app.Run(window);
    }

    private sealed class ReleaseFixture(string path) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var file = File.OpenRead(path);
            var content = new StreamContent(file);
            content.Headers.ContentLength = file.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
