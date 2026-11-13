using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PowerControlProWpf;

internal static class RepairEngine
{
    private const string NetworkSettings = "POWERLEITSTAND_REPAIR_NETWORK";

    internal static async Task RunElevatedAsync(string mode, Action<RepairProtocol.Message> onMessage,
        CancellationToken cancellationToken = default, bool english = false, RepairNetworkRequest? network = null)
    {
        if (RepairTool.Find(mode) is not { OpensWindow: false } tool) throw new ArgumentOutOfRangeException(nameof(mode));
        RequireAdministrator();
        if (tool.NeedsAdapter)
            _ = (network ?? throw new ArgumentNullException(nameof(network))).Prelude(mode);
        var pipeName = "PowerControlProRepair-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Programmdatei nicht gefunden.");
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--repair-helper");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(pipeName);
        startInfo.Environment["POWERLEITSTAND_REPAIR_LANGUAGE"] = english ? "en" : "de";
        if (tool.NeedsAdapter) startInfo.Environment[NetworkSettings] = JsonSerializer.Serialize(network);
        using var helper = Process.Start(startInfo) ??
            throw new InvalidOperationException("Der Administrator-Hilfsprozess konnte nicht gestartet werden.");

        await RepairProcessLifetime.RunUntilExitedAsync(helper, async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                await pipe.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var cancelRegistration = cancellationToken.Register(() =>
                {
                    try { writer.WriteLine("cancel"); }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                var receivedExit = await RepairProtocol.ReadUntilTerminalAsync(reader, onMessage);
                await helper.WaitForExitAsync();
                if (!receivedExit) throw new InvalidOperationException(
                    $"Der Reparaturprozess wurde ohne Ergebnis beendet (Code {helper.ExitCode}).");
            }
            finally { pipe.Close(); }
        });
    }

    private static void RequireAdministrator()
    {
        if (!ElevationService.IsElevated())
            throw new UnauthorizedAccessException("Administratorrechte sind für Windows-Reparaturbefehle erforderlich.");
    }

    internal static async Task RunHelperAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--repair-helper" ||
            RepairTool.Find(args[1]) is not { OpensWindow: false } ||
            !args[2].StartsWith("PowerControlProRepair-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[2]["PowerControlProRepair-".Length..], "N", out _))
            return;

        RequireAdministrator();

        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(45000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        var writeLock = new SemaphoreSlim(1, 1);
        var connectionAvailable = true;
        async Task Send(string kind, string text, int code = 0)
        {
            await writeLock.WaitAsync();
            try
            {
                if (connectionAvailable)
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new RepairProtocol.Message(kind, text, code)));
            }
            catch (IOException) { connectionAvailable = false; }
            catch (ObjectDisposedException) { connectionAvailable = false; }
            finally { writeLock.Release(); }
        }

        using var stopListening = new CancellationTokenSource();
        using var cancelRequested = new CancellationTokenSource();
        var cancelListener = ListenForCancellationAsync();

        async Task ListenForCancellationAsync()
        {
            try
            {
                while (await reader.ReadLineAsync(stopListening.Token) is { } command)
                {
                    if (command == "cancel") break;
                }
                cancelRequested.Cancel();
            }
            catch (OperationCanceledException) when (stopListening.IsCancellationRequested) { }
            catch (IOException) { cancelRequested.Cancel(); }
        }

        async Task<int> RunProcessAsync(ProcessStartInfo startInfo, bool interruptible)
        {
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Windows konnte das Werkzeug nicht starten.");
            using var cancellation = interruptible ? cancelRequested.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex) { Trace.TraceError(ex.ToString()); }
            }) : default;

            await RepairProcessLifetime.RunUntilExitedAsync(process, async () =>
            {
                async Task CopyAsync(StreamReader stream)
                {
                    var buffer = new char[256];
                    var line = new StringBuilder();
                    int count;
                    while ((count = await stream.ReadAsync(buffer.AsMemory())) > 0)
                    {
                        for (var index = 0; index < count; index++)
                        {
                            if (buffer[index] is '\r' or '\n')
                            {
                                if (line.Length > 0) { await Send("output", line.ToString()); line.Clear(); }
                            }
                            else if (line.Length < 2048) line.Append(buffer[index]);
                        }
                    }
                    if (line.Length > 0) await Send("output", line.ToString());
                }

                try { await Task.WhenAll(CopyAsync(process.StandardOutput), CopyAsync(process.StandardError)); }
                catch (IOException) when (cancelRequested.IsCancellationRequested) { }
            });
            return process.ExitCode;
        }

        ProcessStartInfo ConsoleTool(string path, Encoding outputEncoding) => new(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding
        };

        ProcessStartInfo PowerShellTool(string system, string script)
        {
            var startInfo = ConsoleTool(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), Encoding.UTF8);
            const string outputSetup = "$ProgressPreference = 'SilentlyContinue'; [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); ";
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(outputSetup + "try {" + Environment.NewLine + script
                + Environment.NewLine + "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"));
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded })
                startInfo.ArgumentList.Add(argument);
            return startInfo;
        }

        try
        {
            var system = Environment.SystemDirectory;
            var tool = RepairTool.Find(args[1])!;
            await Send("start", tool.Caption);
            var exitCode = 0;
            var networkActionStarted = false;
            if (tool.IsReset && !cancelRequested.IsCancellationRequested)
            {
                // Allow the reset to restore service states even when the user requests cancellation.
                exitCode = await RunProcessAsync(PowerShellTool(system, RepairUpdateReset.Script), interruptible: false);
            }

            var resetFailed = exitCode != 0;
            if (exitCode == 0 && tool.Id != "update-reset" && !cancelRequested.IsCancellationRequested)
            {
                var sfc = tool.Executable == "sfc.exe";
                var cbsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");
                var cbsStart = sfc && File.Exists(cbsPath) ? new FileInfo(cbsPath).Length : 0;
                var english = Environment.GetEnvironmentVariable("POWERLEITSTAND_REPAIR_LANGUAGE") == "en";
                var network = tool.NeedsAdapter
                    ? JsonSerializer.Deserialize<RepairNetworkRequest>(Environment.GetEnvironmentVariable(NetworkSettings) ?? "")
                        ?? throw new ArgumentException("Network action settings are missing.")
                    : null;
                var command = tool.Executable == "powershell.exe" ? PowerShellTool(system, tool.CommandFor(english, network))
                    : ConsoleTool(Path.Combine(system, tool.Executable), RepairOutputEncoding.ForMode(sfc ? "sfc" : "dism"));
                if (tool.Executable != "powershell.exe") command.Arguments = tool.Arguments;
                networkActionStarted = tool.IsNetworkAction;
                exitCode = await RunProcessAsync(command, interruptible: !networkActionStarted);
                if (sfc && !cancelRequested.IsCancellationRequested && File.Exists(cbsPath))
                {
                    try
                    {
                        using var cbs = new FileStream(cbsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        cbs.Seek(Math.Min(cbsStart, cbs.Length), SeekOrigin.Begin);
                        using var cbsReader = new StreamReader(cbs);
                        var details = new Queue<string>();
                        while (await cbsReader.ReadLineAsync() is { } entry)
                        {
                            if (!entry.Contains("[SR]", StringComparison.OrdinalIgnoreCase)) continue;
                            if (details.Count == 120) details.Dequeue();
                            details.Enqueue(entry);
                        }
                        if (details.Count > 0) await Send("details", "SFC / CBS:");
                        foreach (var entry in details) await Send("details", entry);
                    }
                    catch (IOException) { await Send("details", "CBS.log konnte nicht gelesen werden."); }
                    catch (UnauthorizedAccessException) { await Send("details", "CBS.log: Zugriff verweigert."); }
                }
            }

            if (cancelRequested.IsCancellationRequested && !resetFailed && !networkActionStarted)
                await Send("cancelled", "Vorgang vom Benutzer abgebrochen.", -2);
            else
                await Send("exit", $"Exit-Code: {exitCode}", exitCode);
        }
        catch (Exception ex)
        {
            await Send("error", ex.Message);
            await Send("exit", "Windows-Werkzeug fehlgeschlagen.", -1);
        }
        finally
        {
            stopListening.Cancel();
            await cancelListener;
        }
    }
}
