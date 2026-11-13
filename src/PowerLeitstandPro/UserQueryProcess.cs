using System.Diagnostics;
using System.IO;
using System.Text;

namespace PowerControlProWpf;

internal static class UserQueryProcess
{
    internal static async Task<string> ReadAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        using var deadline = new CancellationTokenSource(timeout);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        var reads = Task.WhenAll(output, error);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await reads.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output.Result)) throw new InvalidOperationException();
            return output.Result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Windows-Benutzerabfrage hat zu lange gedauert.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(stopping.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
            try { await reads.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        }
    }
}
