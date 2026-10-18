using System.Diagnostics;

namespace PowerControlProWpf;

internal static class RepairProcessLifetime
{
    // A transport failure or cancellation must not release the UI while its
    // owned process is still alive. Wait without the cancelled operation token.
    internal static async Task RunUntilExitedAsync(Process process, Func<Task> operation)
    {
        try { await operation(); }
        finally { await process.WaitForExitAsync(); }
    }
}
