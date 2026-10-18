using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace PowerControlProWpf;

// One interactive instance per Windows user/session, including copies in other folders.
// Helpers bypass this gate; an elevated replacement waits for its predecessor to exit.
internal sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _owns;

    internal SingleInstanceService(string? testScope = null)
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            identity + ":" + Process.GetCurrentProcess().SessionId + ":" + testScope)))[..24];
        _pipeName = "PowerLeitstand-activate-" + scope;
        _mutex = new Mutex(false, "Local\\PowerLeitstand-" + scope);
    }

    internal bool TryAcquire()
    {
        try { _owns = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owns = true; }
        return _owns;
    }

    internal void Listen(Action activate) => _ = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await pipe.ReadAsync(command, timeout.Token) == 1 && command[0] == 1)
                {
                    activate();
                    await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    });

    internal async Task<bool> ActivateExistingAsync()
    {
        try
        {
            AllowSetForegroundWindow(uint.MaxValue);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            var response = new byte[1];
            return await pipe.ReadAsync(response, timeout.Token) == 1 && response[0] == 1;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    internal static async Task WaitForHandoffAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--instance-handoff");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid == Environment.ProcessId) return;
        try
        {
            using var parent = Process.GetProcessById(pid);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await parent.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_owns) { _mutex.ReleaseMutex(); _owns = false; }
        _mutex.Dispose();
        // The listener still observes this cancellation token while it winds down.
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
