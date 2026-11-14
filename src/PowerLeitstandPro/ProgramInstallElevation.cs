using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Windows.Management.Deployment;

namespace PowerControlProWpf;

internal sealed class ProgramInstallElevation : IAsyncDisposable
{
    private const string Prefix = "PowerLeitstandProgramInstall-";
    internal sealed record Request(string Entry, string Package, string Version, string ManifestHash, string? Destination);
    private sealed record Reply(string? Output, int? ExitCode = null, string? Error = null);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);

    private readonly NamedPipeServerStream _pipe;
    private readonly Process _helper;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private ProgramInstallElevation(NamedPipeServerStream pipe, Process helper)
    {
        _pipe = pipe;
        _helper = helper;
        _reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    internal static bool Applies(ProgramSelection selection, ProgramInventory inventory, bool reinstall)
        => !reinstall && selection.Portable is null && !inventory.Installed.ContainsKey(selection.Entry.Id)
            && ProgramCatalog.Contains(selection.Entry)
            && selection.Installer is { VersionSource: "WinGet", TargetProof.RequiresMachineScope: true };

    internal static async Task<ProgramInstallElevation?> StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string pipeName = Prefix + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        Process? helper = null;
        bool connected = false;
        try
        {
            string executable = Environment.ProcessPath ?? throw new IOException("PowerLeitstand executable unavailable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
            foreach (string arg in new[] { "--program-install-helper", pipeName, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(arg);
            try { helper = Process.Start(start) ?? throw new IOException("Administrator process unavailable."); }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223) { return null; }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try { await pipe.WaitForConnectionAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("Administrator-Hilfsprozess hat sich nicht verbunden. / Administrator helper did not connect."); }
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != helper.Id)
                throw new IOException("Unexpected installer helper process.");
            var session = new ProgramInstallElevation(pipe, helper);
            connected = true;
            return session;
        }
        finally
        {
            if (!connected) { pipe.Dispose(); helper?.Dispose(); }
        }
    }

    internal async Task<int> InstallAsync(ProgramSelection choice, Action<string> output, CancellationToken token)
    {
        var package = choice.Installer!;
        var request = new Request(choice.Entry.Id, package.Id, package.Version!, package.TargetProof!.ManifestHash, choice.TargetPath);
        token.ThrowIfCancellationRequested();
        await _writer.WriteLineAsync(JsonSerializer.Serialize(request));
        while (await _reader.ReadLineAsync() is { } line)
        {
            var reply = JsonSerializer.Deserialize<Reply>(line) ?? throw new IOException("Invalid installer helper response.");
            if (reply.Error is { } error) throw new IOException(error);
            if (reply.ExitCode is { } exit) return exit;
            if (reply.Output is { } text) output(text);
        }
        throw new IOException("Administrator process ended without an installer result.");
    }

    public async ValueTask DisposeAsync()
    {
        _pipe.Dispose();
        try { await _helper.WaitForExitAsync(); }
        finally { _helper.Dispose(); }
    }

    internal static async Task RunHelperAsync(string[] args)
    {
        if (args.Length != 3 || !args[1].StartsWith(Prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(args[1][Prefix.Length..], "N", out _) || !int.TryParse(args[2], out int parentId)
            || !ElevationService.IsElevated()) return;

        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await pipe.ConnectAsync(timeout.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actualParent) || actualParent != parentId) return;
        using var parent = Process.GetProcessById(parentId);
        if (!string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        string? engine = null;
        while (await reader.ReadLineAsync() is { } line)
        {
            try
            {
                if (line.Length > 8192) throw new IOException("Invalid installer request.");
                var request = JsonSerializer.Deserialize<Request>(line) ?? throw new IOException("Invalid installer request.");
                using var check = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                void Report(string output)
                {
                    try { lock (writer) writer.WriteLine(JsonSerializer.Serialize(new Reply(output))); }
                    catch (IOException) { }
                }
                if (engine is null)
                {
                    var candidate = await Task.Run(FindMachineWinGet);
                    var source = new ProcessStartInfo(candidate) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
                    foreach (string arg in new[] { "source", "export", "winget", "--disable-interactivity" }) source.ArgumentList.Add(arg);
                    var exported = await WinGetService.QueryAsync(source, check.Token);
                    if (exported.ExitCode != 0 || !OfficialWinGetSource(exported.Output))
                        throw new IOException("Die offizielle WinGet-Quelle wurde nicht bestätigt. / The official WinGet source was not verified.");
                    engine = candidate;
                }
                var command = await VerifyCommandAsync(request, engine, check.Token);
                var result = await ProgramInstallService.RunHiddenAsync(command, () => { }, Report);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Reply(null, result.ExitCode)));
            }
            catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException or OperationCanceledException or JsonException or Win32Exception)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Reply(null, Error: e.Message)));
            }
        }
    }

    internal static bool OfficialWinGetSource(string value)
    {
        if (value.Length > 8192) return false;
        var line = value.Split('\n', StringSplitOptions.TrimEntries).SingleOrDefault(l => l.StartsWith('{') && l.EndsWith('}'));
        if (line is null) return false;
        using var json = JsonDocument.Parse(line);
        var source = json.RootElement;
        return ProgramManagedPackages.Text(source, "Name") == "winget"
            && ProgramManagedPackages.Text(source, "Arg").TrimEnd('/') == "https://cdn.winget.microsoft.com/cache"
            && ProgramManagedPackages.Text(source, "Type") == "Microsoft.PreIndexed.Package"
            && ProgramManagedPackages.Text(source, "Identifier") == "Microsoft.Winget.Source_8wekyb3d8bbwe";
    }

    private static string FindMachineWinGet()
    {
        var manager = new PackageManager();
        var package = manager.FindPackagesForUser(string.Empty)
            .Where(p => p.Id.FamilyName == "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe" && !p.IsResourcePackage
                && p.SignatureKind is Windows.ApplicationModel.PackageSignatureKind.Store or Windows.ApplicationModel.PackageSignatureKind.System)
            .OrderByDescending(p => new Version(p.Id.Version.Major, p.Id.Version.Minor, p.Id.Version.Build, p.Id.Version.Revision))
            .FirstOrDefault();
        string? directory = package?.InstalledLocation.Path;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string? engine = directory is null ? null : Path.Combine(directory, "winget.exe");
        if (engine is null || !ProgramPathResolver.InDirectory(root, engine) || !File.Exists(engine))
            throw new IOException("Microsoft App Installer ist für das Administratorkonto nicht verfügbar. / Microsoft App Installer is unavailable for the administrator account.");
        return engine;
    }

    internal static async Task<ProcessStartInfo> VerifyCommandAsync(Request request, string engine, CancellationToken token,
        Func<ProgramPackage, CancellationToken, Task<ProgramInstallTargetProof?>>? resolve = null)
    {
        var entry = ProgramCatalog.Entries.FirstOrDefault(e => e.Id == request.Entry);
        if (entry?.PackageId is not { } id || id != request.Package || string.IsNullOrWhiteSpace(request.Version))
            throw new IOException("WinGet package identity mismatch.");
        var package = new ProgramPackage(request.Package, request.Version, "", DateTime.UtcNow, VersionSource: "WinGet");
        var proof = await (resolve ?? ProgramInstallTargets.ResolveAsync)(package, token);
        if (proof is not { RequiresMachineScope: true } || proof.ManifestHash != request.ManifestHash
            || request.Destination is not null && !proof.SupportsLocation)
            throw new IOException("WinGet-Installationsdaten geändert. / WinGet installer metadata changed.");

        var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory };
        foreach (string arg in new[] { "install", "--id", request.Package, "--exact", "--source", "winget", "--version", request.Version,
            "--scope", "machine", "--silent", "--disable-interactivity", "--no-upgrade" }) start.ArgumentList.Add(arg);
        if (request.Destination is { } destination)
        {
            start.ArgumentList.Add("--location");
            start.ArgumentList.Add(ProgramInstallTargets.Validate(destination));
        }
        return start;
    }
}
