using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerControlProWpf;

internal static class UpdateService
{
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PowerLeitstand-PRO/1.1.1");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(ProjectLinks.ReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 1, 0);
        return ReleaseAssetSelector.Select(data.RootElement, current);
    }

    internal static async Task PrepareAndLaunchAsync(AvailableUpdate update, Func<bool> isBusy, CancellationToken cancellationToken = default)
    {
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("EXE-Pfad nicht ermittelbar.");
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Updates benötigen die veröffentlichte Windows-EXE.");
        var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Installationsordner nicht ermittelbar.");
        var staged = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.new");
        var helper = Path.Combine(Path.GetTempPath(), $"PowerLeitstand-update-helper-{Guid.NewGuid():N}.exe");
        var backup = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.bak");
        try
        {
            using var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, cancellationToken);
            await using (var file = File.OpenRead(staged))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256-Prüfsumme stimmt nicht überein. Das Update wurde verworfen.");
            }
            if (isBusy()) throw new InvalidOperationException("Ein Timer oder eine Reparatur wurde während des Downloads gestartet.");
            File.Copy(target, helper);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--update-helper");
            start.ArgumentList.Add(target);
            start.ArgumentList.Add(staged);
            start.ArgumentList.Add(backup);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Update-Helfer konnte nicht gestartet werden.");
        }
        catch
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            try { if (File.Exists(helper)) File.Delete(helper); } catch { }
            throw;
        }
    }

    internal static async Task RunHelperAsync(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[4], out var parentId)) return;
        var target = Path.GetFullPath(args[1]);
        var staged = Path.GetFullPath(args[2]);
        var backup = Path.GetFullPath(args[3]);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(staged)) return;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException) { }
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    File.Replace(staged, target, backup);
                    try
                    {
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! });
                        // Use the installed target, never this temporary update helper.
                        var autoStart = AutoStartService.Current.Check(target);
                        if (autoStart.Error is not null) Trace.TraceWarning("Autostart nach Update: " + autoStart.Error);
                    }
                    catch
                    {
                        File.Copy(backup, target, true);
                        throw;
                    }
                    return;
                }
                catch (IOException) when (attempt < 19) { await Task.Delay(500); }
                catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(500); }
            }
        }
        catch (Exception exception)
        {
            try { if (!File.Exists(target) && File.Exists(backup)) File.Copy(backup, target); } catch { }
            System.Windows.MessageBox.Show("Update fehlgeschlagen. Die vorherige EXE wurde nach Möglichkeit wiederhergestellt.\n\n" + exception.Message,
                "PowerLeitstand PRO", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            if (File.Exists(target))
            {
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! }); } catch { }
            }
        }
    }
}
