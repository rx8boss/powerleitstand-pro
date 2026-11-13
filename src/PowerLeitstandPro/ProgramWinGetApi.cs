using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace PowerControlProWpf;

internal enum ProgramApiPhase { Queued, Downloading, Installing, PostInstall, Finished }
internal sealed record ProgramApiProgress(ProgramApiPhase Phase, ulong Downloaded, ulong Required);

// The enum and byte counters come from Microsoft's API, not translated console output.
internal sealed class ProgramApiSteps(Action<ProgramStepEvent> step, Action<ProgramDownload> download)
{
    private bool _downloadStarted, _downloadDone, _downloadProven, _installStarted;
    private ProgramApiPhase _phase;
    private bool _complete;
    private readonly object _gate = new();
    internal void Report(ProgramApiProgress progress)
    { lock (_gate) ReportCore(progress); }
    private void ReportCore(ProgramApiProgress progress)
    {
        if (_complete || !Enum.IsDefined(progress.Phase)) return;
        if (progress.Phase < _phase) return; // Ignore delayed callbacks from a previous phase.
        _phase = progress.Phase;
        switch (progress.Phase)
        {
            case ProgramApiPhase.Downloading:
                if (!_downloadStarted) { _downloadStarted = true; step(new(ProgramStep.Download, ProgramStepState.Active)); }
                if (progress.Downloaded <= long.MaxValue && progress.Required <= long.MaxValue)
                    download(new((long)progress.Downloaded, progress.Required > 0 ? (long)progress.Required : null));
                break;
            case ProgramApiPhase.Installing:
                bool counted = progress.Required > 0 && progress.Downloaded == progress.Required && progress.Required <= long.MaxValue;
                if (_installStarted && !_downloadProven && counted)
                { step(new(ProgramStep.Download, ProgramStepState.Done)); _downloadProven = true; }
                if (!_installStarted)
                {
                    _installStarted = true;
                    // Entering Installing means the package manager has accepted the artifact.
                    if (counted && !_downloadStarted) download(new((long)progress.Downloaded, (long)progress.Required));
                    _downloadProven = _downloadStarted || counted;
                    step(new(ProgramStep.Download, _downloadProven ? ProgramStepState.Done : ProgramStepState.Unknown));
                    _downloadDone = true; step(new(ProgramStep.Install, ProgramStepState.Active));
                }
                break;
            case ProgramApiPhase.PostInstall:
            case ProgramApiPhase.Finished:
                // Result status and subsequent local inventory establish success, never the phase alone.
                break;
        }
    }
    internal void Complete(bool success)
    { lock (_gate) CompleteCore(success); }
    private void CompleteCore(bool success)
    {
        if (_complete) return;
        _complete = true;
        if (!success) return;
        if (!_downloadDone) step(new(ProgramStep.Download, _downloadStarted ? ProgramStepState.Done : ProgramStepState.Unknown));
        step(new(ProgramStep.Install, ProgramStepState.Done));
    }
}

internal sealed class ProgramApiUnavailableException(string message, Exception? inner = null) : IOException(message, inner);

internal static class ProgramWinGetApi
{
    // Used only before starting an operation. A failed/started operation is NEVER retried via CLI.
    internal static async Task<Func<Action, Action<ProgramStepEvent>, Action<ProgramDownload>, Task<int>>> PrepareAsync(string id, string version, bool upgrade, string? location = null, bool reinstall = false)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var manager = new PackageManager();
                var reference = manager.GetPackageCatalogByName("winget") ?? throw new ProgramApiUnavailableException("WinGet catalog unavailable");
                var composite = new CreateCompositePackageCatalogOptions { CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs };
                composite.Catalogs.Add(reference);
                var connection = await manager.CreateCompositePackageCatalog(composite).ConnectAsync();
                if (connection.Status != ConnectResultStatus.Ok) throw new ProgramApiUnavailableException("WinGet catalog: " + connection.Status);
                var query = new FindPackagesOptions { ResultLimit = 5 };
                query.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
                var result = connection.PackageCatalog.FindPackages(query);
                if (result.Status != FindPackagesResultStatus.Ok || result.WasLimitExceeded || result.Matches.Count != 1)
                    throw new ProgramApiUnavailableException("Exact WinGet catalog match unavailable");
                var package = result.Matches[0].CatalogPackage;
                if (!string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)) throw new IOException("Die WinGet-Paketkennung hat sich geändert.");
                if (!upgrade && !reinstall && package.InstalledVersion is not null) throw new IOException("Das Programm ist bereits installiert. Bitte den Bestand vor einem Update neu prüfen.");
                if (upgrade && package.InstalledVersion is null) throw new IOException("Das installierte WinGet-Ziel wurde nicht mehr bestätigt.");
                if (reinstall && (upgrade || location is not null || package.InstalledVersion is null
                    || ProgramVersions.ConfirmedVersion(package.InstalledVersion.Version) is { } installed
                        && !string.Equals(installed, version, StringComparison.OrdinalIgnoreCase)
                        && !ProgramPortableUpdates.SameVersion(installed, version) && !ProgramVersions.IsNewer(installed, version)))
                    throw new IOException("Die Version für die Neuinstallation hat sich geändert.");
                PackageVersionId? chosen = null;
                for (int n = 0; n < package.AvailableVersions.Count; n++)
                    if (package.AvailableVersions[n].Version == version)
                    { if (chosen is not null) throw new IOException("Ambiguous WinGet version"); chosen = package.AvailableVersions[n]; }
                if (chosen is null) throw new IOException("Die gewählte WinGet-Version ist nicht mehr verfügbar. Bitte erneut prüfen.");
                var options = new InstallOptions { PackageVersionId = chosen, PackageInstallMode = PackageInstallMode.Silent,
                    AllowHashMismatch = false, AcceptPackageAgreements = false };
                if (location is not null) options.PreferredInstallLocation = ProgramInstallTargets.Validate(location);
                if (reinstall) options.Force = true;
                return (Func<Action, Action<ProgramStepEvent>, Action<ProgramDownload>, Task<int>>)(async (started, step, download) =>
                {
                    // All activation/options validation is complete before this commit boundary.
                    started();
                    return await Task.Run(async () =>
                    {
                        // Keep COM calls in the MTA even when the caller is a WPF UI thread.
                        var tracker = new ProgramApiSteps(step, download);
                        var operation = upgrade ? manager.UpgradePackageAsync(package, options) : manager.InstallPackageAsync(package, options);
                        operation.Progress = (_, value) => tracker.Report(new((ProgramApiPhase)(int)value.State, value.BytesDownloaded, value.BytesRequired));
                        var outcome = await operation;
                        bool success = outcome.Status == InstallResultStatus.Ok;
                        tracker.Complete(success);
                        return ResultCode(success, outcome.RebootRequired, outcome.ExtendedErrorCode?.HResult);
                    });
                });
            }).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception e) when (e is COMException or TypeLoadException or DllNotFoundException or FileNotFoundException or PlatformNotSupportedException or TimeoutException)
        { throw new ProgramApiUnavailableException("WinGet API unavailable: " + e.GetType().Name, e); }
    }
    internal static int ResultCode(bool success, bool reboot, int? error)
        => success ? reboot ? 3010 : 0 : error is { } value && value != 0 ? value : unchecked((int)0x80004005);
    internal static async Task<int> PreferAsync(Func<Task<Func<Task<int>>>> prepare, Func<Task<int>> fallback, Action<string> diagnostic)
    {
        Func<Task<int>> operation;
        try { operation = await prepare(); }
        catch (ProgramApiUnavailableException e) { diagnostic(e.Message); return await fallback(); }
        // Outside the catch: even an API exception after this boundary cannot start a second installer.
        return await operation();
    }
    internal static string Probe()
    {
        var manager = new PackageManager();
        var reference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
        var result = reference.Connect();
        return "WinGet API read-only catalog status: " + result.Status;
    }
}
