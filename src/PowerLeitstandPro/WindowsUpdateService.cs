using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace PowerControlProWpf;

internal sealed class WindowsUpdateService : IWindowsUpdateService
{
    public async Task<WindowsUpdateSearch> SearchAsync(bool? drivers, CancellationToken cancellation)
    {
        using var connection = new OnlineRequest(cancellation);
        await OnlineConnection.CheckAsync(connection.Token);
        return await Task.Run(() => WindowsUpdateAgent.Search(drivers, connection.Token), CancellationToken.None);
    }

    public async Task RunElevatedAsync(WindowsUpdateRequest request, Action<WindowsUpdateEvent> progress, CancellationToken cancellation)
    {
        OnlineConnection.RequireNetwork();
        WindowsUpdatePolicy.Validate(request);
        var name = "PowerLeitstandWindowsUpdate-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        using var helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Programmdatei nicht gefunden."))
        {
            Arguments = "--windows-update-helper " + name, Verb = "runas", UseShellExecute = true
        }) ?? throw new InvalidOperationException("Die Administratorabfrage konnte nicht gestartet werden.");
        // Never kill a helper that may be installing an OS update. Locks remain until it exits.
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));
            using var cancelRegistration = cancellation.Register(() =>
            {
                try { lock (writer) writer.WriteLine("cancel"); }
                catch (IOException) { } catch (ObjectDisposedException) { }
            });
            WindowsUpdateEvent? terminal = null;
            while (await reader.ReadLineAsync() is { } line)
            {
                var message = JsonSerializer.Deserialize<WindowsUpdateEvent>(line) ?? throw new IOException("Invalid update response.");
                progress(message);
                if (message.Phase is "done" or "cancelled" or "error") { terminal = message; break; }
            }
            if (terminal is null) throw new IOException("Windows-Update-Helfer ohne Ergebnis beendet.");
            if (terminal.Phase == "error")
            {
                throw new InvalidOperationException(terminal.Text);
            }
            if (terminal.Phase == "cancelled") throw new OperationCanceledException(cancellation);
        }
        finally
        {
            pipe.Close();
            await helper.WaitForExitAsync();
        }
    }

    internal static async Task RunHelperAsync(string[] args)
    {
        const string prefix = "PowerLeitstandWindowsUpdate-";
        if (args.Length != 2 || !args[1].StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[1][prefix.Length..], "N", out _) || !ElevationService.IsElevated()) return;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(45000);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var line = await reader.ReadLineAsync(timeout.Token);
        var request = JsonSerializer.Deserialize<WindowsUpdateRequest>(line ?? "") ?? throw new IOException("Missing request.");
        using var cancellation = new CancellationTokenSource();
        using var listening = new CancellationTokenSource();
        var listener = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync(listening.Token) is { } command)
                    if (command == "cancel") cancellation.Cancel();
                cancellation.Cancel(); // Parent disconnected: ask WUA to abort; do not kill servicing processes.
            }
            catch (OperationCanceledException) { } catch (IOException) { cancellation.Cancel(); }
        });
        void Send(WindowsUpdateEvent value) { lock (writer) writer.WriteLine(JsonSerializer.Serialize(value)); }
        try
        {
            await Task.Run(() => WindowsUpdateAgent.Run(request, Send, cancellation.Token));
        }
        catch (OperationCanceledException) { Send(new("cancelled")); }
        catch (Exception exception) { Send(new("error", exception.Message, Code: exception.HResult)); }
        finally { listening.Cancel(); await listener; }
    }
}
