using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PowerControlProWpf;

internal enum WinGetAvailability { Unknown, Checking, Ready, Missing }
internal sealed record WinGetPackage(string Id, string Details);
internal sealed record WinGetResult(int ExitCode, string Output);
internal interface IWinGetService
{
    Task<bool> ProbeAsync(CancellationToken token);
    Task<WinGetPackage?> ResolveAsync(string manufacturer, CancellationToken token);
    Task<int> InstallAsync(string manufacturer, Action started);
    void OpenSetup();
}

internal sealed class WinGetService : IWinGetService
{
    internal const string SetupUrl = "https://apps.microsoft.com/detail/9nblggh4nns1";
    private readonly Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>> _query;
    private readonly Func<ProcessStartInfo, Action, Task<int>> _install;
    private readonly string _executable;
    internal WinGetService(Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>>? query = null,
        Func<ProcessStartInfo, Action, Task<int>>? install = null)
    {
        _query = query ?? QueryAsync; _install = install ?? InstallProcessAsync;
        // Do not resolve an arbitrary executable from the current folder or PATH.
        _executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
    }
    internal static string? PackageId(string manufacturer) => manufacturer switch
    {
        "kingston" => "Kingston.SSDManager", "intel" => "Intel.IntelDriverAndSupportAssistant",
        "msi" => "MSI.MSICenter", "sandisk" => "SanDisk.Dashboard", "chocolatey" => "Chocolatey.Chocolatey", _ => null
    };
    internal ProcessStartInfo Command(string manufacturer, bool install, bool unattended = false)
    {
        var id = PackageId(manufacturer) ?? throw new InvalidOperationException("Kein bestätigtes WinGet-Paket. / No verified WinGet package.");
        var start = new ProcessStartInfo(_executable) { UseShellExecute = install && !unattended, CreateNoWindow = !install || unattended };
        foreach (var arg in new[] { install ? "install" : "show", "--id", id, "--exact", "--source", "winget" }) start.ArgumentList.Add(arg);
        // The unattended setup dialog confirms the terms before calling this route.
        start.ArgumentList.Add(install && !unattended ? "--interactive" : "--disable-interactivity");
        if (unattended)
        {
            start.ArgumentList.Add("--accept-source-agreements");
            if (install)
                foreach (string arg in new[] { "--silent", "--accept-package-agreements", "--no-upgrade" }) start.ArgumentList.Add(arg);
        }
        return start;
    }
    public async Task<bool> ProbeAsync(CancellationToken token)
    {
        try
        {
            var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--version");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(12));
            var result = await _query(start, limit.Token);
            return result.ExitCode == 0 && Regex.IsMatch(result.Output.Trim(), @"^v?\d+\.\d+\.\d+(?:[^\r\n]*)?$", RegexOptions.CultureInvariant);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }
    public Task<WinGetPackage?> ResolveAsync(string manufacturer, CancellationToken token) => ResolveAsync(manufacturer, token, false);
    internal async Task<WinGetPackage?> ResolveAsync(string manufacturer, CancellationToken token, bool acceptSourceTerms)
    {
        var id = PackageId(manufacturer); if (id is null) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(35));
        var result = await _query(Command(manufacturer, false, acceptSourceTerms), limit.Token);
        return result.ExitCode == 0 && result.Output.Contains("[" + id + "]", StringComparison.OrdinalIgnoreCase)
            ? new(id, result.Output) : null;
    }
    public Task<int> InstallAsync(string manufacturer, Action started) => _install(Command(manufacturer, true), started);
    public void OpenSetup() { OnlineConnection.RequireNetwork(); Process.Start(new ProcessStartInfo(SetupUrl) { UseShellExecute = true }); }
    internal static async Task<WinGetResult> QueryAsync(ProcessStartInfo start, CancellationToken token)
    {
        bool online = start.ArgumentList.FirstOrDefault() is "search" or "show" or "source";
        if (online) await OnlineConnection.CheckAsync(token);
        using var connection = online ? new OnlineRequest(token) : null;
        if (connection is not null) token = connection.Token;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("WinGet konnte nicht gestartet werden. / Could not start WinGet.");
        async Task<string> Read(TextReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[2048]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
                if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
            return text.ToString();
        }
        var output = Read(process.StandardOutput); var error = Read(process.StandardError);
        try
        {
            await process.WaitForExitAsync(token);
            return new(process.ExitCode, await output + "\n" + await error);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            throw;
        }
    }
    internal static async Task<int> InstallProcessAsync(ProcessStartInfo start, Action started)
    {
        await OnlineConnection.CheckAsync(CancellationToken.None);
        // This is an interactive tool: its console displays package/source agreements.
        using var process = Process.Start(start) ?? throw new IOException("WinGet konnte nicht gestartet werden. / Could not start WinGet.");
        started(); await process.WaitForExitAsync(); return process.ExitCode;
    }
}
