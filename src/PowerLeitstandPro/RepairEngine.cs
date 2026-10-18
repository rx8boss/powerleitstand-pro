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
    internal static async Task RunElevatedAsync(string mode, Action<RepairProtocol.Message> onMessage, CancellationToken cancellationToken = default)
    {
        if (mode is not ("dism" or "sfc")) throw new ArgumentOutOfRangeException(nameof(mode));
        var pipeName = "PowerControlProRepair-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath ??
            throw new InvalidOperationException("Programmdatei nicht gefunden."))
        {
            Arguments = $"--repair-helper {mode} {pipeName}",
            Verb = "runas",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Die Administratorabfrage konnte nicht gestartet werden.");

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

    internal static async Task RunHelperAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--repair-helper" ||
            args[1] is not ("dism" or "sfc") ||
            !args[2].StartsWith("PowerControlProRepair-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[2]["PowerControlProRepair-".Length..], "N", out _))
            return;

        using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut,
            PipeOptions.Asynchronous);
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

        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var file = Path.Combine(system, args[1] == "dism" ? "dism.exe" : "sfc.exe");
            var commandArgs = args[1] == "dism" ? "/Online /Cleanup-Image /RestoreHealth" : "/scannow";
            // SFC writes UTF-16LE to redirected pipes; treating it as the OEM code page
            // produces interleaved NULs, broken umlauts and invisible progress values.
            var outputEncoding = RepairOutputEncoding.ForMode(args[1]);
            var cbsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");
            var cbsStart = args[1] == "sfc" && File.Exists(cbsPath) ? new FileInfo(cbsPath).Length : 0;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(file, commandArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = outputEncoding,
                    StandardErrorEncoding = outputEncoding
                }
            };
            if (!process.Start()) throw new InvalidOperationException("Windows konnte das Reparaturprogramm nicht starten.");
            await RepairProcessLifetime.RunUntilExitedAsync(process, async () =>
            {
                await Send("start", $"{Path.GetFileName(file)} {commandArgs}");
                using var stopListening = new CancellationTokenSource();
                using var cancelRequested = new CancellationTokenSource();
                var cancelListener = Task.Run(async () =>
                {
                    try
                    {
                        while (await reader.ReadLineAsync(stopListening.Token) is { } command)
                        {
                            if (command != "cancel") continue;
                            try
                            {
                                if (!process.HasExited)
                                {
                                    process.Kill(entireProcessTree: true);
                                    cancelRequested.Cancel();
                                }
                            }
                            catch (InvalidOperationException) { }
                            catch (System.ComponentModel.Win32Exception ex)
                            {
                                await Send("error", "Reparatur konnte nicht abgebrochen werden: " + ex.Message);
                            }
                            break;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException) { }
                });
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
                try
                {
                    try { await Task.WhenAll(CopyAsync(process.StandardOutput), CopyAsync(process.StandardError)); }
                    catch (IOException) when (cancelRequested.IsCancellationRequested) { }
                    await process.WaitForExitAsync();
                }
                finally
                {
                    stopListening.Cancel();
                    await cancelListener;
                }
                if (cancelRequested.IsCancellationRequested)
                {
                    await Send("cancelled", "Reparatur vom Benutzer abgebrochen.", -2);
                    return;
                }
                if (args[1] == "sfc" && File.Exists(cbsPath))
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
                await Send("exit", $"Exit-Code: {process.ExitCode}", process.ExitCode);
            });
        }
        catch (Exception ex)
        {
            await Send("error", ex.Message);
            await Send("exit", "Reparatur fehlgeschlagen.", -1);
        }
    }
}
