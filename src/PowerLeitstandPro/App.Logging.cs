using System.IO;

namespace PowerControlProWpf;

public partial class App
{
    private AppLogStore? _failureLogs;
    private readonly object _failureLogGate = new();
    private void LogUnhandledError(Exception error, string operation)
    {
        lock (_failureLogGate)
        {
            _failureLogs ??= new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Power Control PRO", "logs"));
            _failureLogs.Error(new("PowerLeitstand PRO", operation == "Startup" ? operation : "Unhandled", error.ToString(), "0x" + error.HResult.ToString("X8"), "unhandled-" + Environment.ProcessId));
        }
    }
}
