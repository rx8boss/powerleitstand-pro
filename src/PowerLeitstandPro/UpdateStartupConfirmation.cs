using System.IO;
using System.IO.Pipes;
using System.Diagnostics;

namespace PowerControlProWpf;

internal static class UpdateStartupConfirmation
{
    internal static async Task SendAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--update-ready");
        if (index < 0 || index + 1 >= args.Length) return;
        var name = args[index + 1];
        if (!name.StartsWith("PowerLeitstand-ready-", StringComparison.Ordinal) || name.Length > 100) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, leaveOpen: true);
        await writer.WriteLineAsync(Environment.ProcessId.ToString().AsMemory(), timeout.Token);
        await writer.FlushAsync(timeout.Token);
    }

    internal static async Task<bool> WaitAsync(NamedPipeServerStream pipe, Process candidate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var ready = ReadAsync();
        var exited = candidate.WaitForExitAsync(cancellation.Token);
        try
        {
            if (await Task.WhenAny(ready, exited) != ready) return false;
            return await ready && !candidate.HasExited;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException) { return false; }
        finally { cancellation.Cancel(); }

        async Task<bool> ReadAsync()
        {
            await pipe.WaitForConnectionAsync(cancellation.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var pid = await reader.ReadLineAsync(cancellation.Token);
            return int.TryParse(pid, out var actual) && actual == candidate.Id;
        }
    }
}
