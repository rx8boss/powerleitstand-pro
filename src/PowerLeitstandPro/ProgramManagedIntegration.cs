using System.IO;

namespace PowerControlProWpf;

internal sealed partial class ProgramInstallService
{
    internal string? ManagedProblem(string id) => _managedPackages?.Problem(id);
    private async Task<ProgramPackage?> ResolveManagedFallbackAsync(ProgramEntry entry, CancellationToken token)
    {
        if (_managedPackages is null || _lastEvidence.ContainsKey(entry.Id)) return null;
        return (await Task.Run(() => _managedPackages.NewAsync(entry, token), token))?.Package();
    }
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ProgramManagedPackage>>> ReadManagedOffersAsync(
        ProgramEntry[] entries, IReadOnlyDictionary<string, ProgramInstallation> evidence, CancellationToken token, Action<string>? failed = null)
    {
        if (_managedPackages is null) return new Dictionary<string, IReadOnlyList<ProgramManagedPackage>>();
        var result = await Task.WhenAll(entries.Where(e => evidence.GetValueOrDefault(e.Id)?.ManagedCopies is { Count: > 0 }).Select(async entry =>
        {
            var packages = await Task.Run(() => _managedPackages.ExistingAsync(entry, evidence[entry.Id], token, failed: failed), token);
            return (entry.Id, Packages: packages);
        }));
        return result.Where(p => p.Packages.Count > 0).ToDictionary(p => p.Id, p => p.Packages);
    }
    internal async Task PrepareManagedAsync(ProgramPackage package, Action started, Action<string> output)
    {
        if (_managedPackages is null || package.ManagedPackage is not { } managed) return;
        await Task.Run(() => _managedPackages.PrepareAsync(managed, started, output));
        _recentInventory = null;
    }
    private async Task<int> ApplyManagedAsync(ProgramEntry entry, ProgramPackage package, bool reinstall, Action started, CancellationToken token)
    {
        if (_managedPackages is null || !Approved(entry) || !package.CanInstall || package.ManagedPackage is not { } managed
            || managed.Target.Id != package.Id || managed.Version != package.Version || !_managedPackages.Matches(entry, managed, _lastEvidence))
            throw new IOException("Paketauftrag nicht bestätigt. / Package operation not verified.");
        _recentInventory = null;
        var result = await Task.Run(() => _managedPackages.ApplyAsync(entry, managed, reinstall, started, e => StepChanged?.Invoke(e),
            p => DownloadChanged?.Invoke(p), message => Diagnostic?.Invoke(message), token), token);
        LastInstallOutput = result.Output;
        return result.ExitCode;
    }
    internal static bool ManagedReinstallSupported(ProgramManagedPackage package) => package.Manager == "Scoop"
        || package.Manager == "Chocolatey" && package.Target.Id.EndsWith(".portable", StringComparison.OrdinalIgnoreCase);
    internal static string PackageSource(ProgramPackage? package, bool english) => package?.ManagedPackage?.Manager
        ?? (package?.GitHubUpdate is not null ? "GitHub" : package?.PortableUpdate is not null ? english ? "Publisher · Portable" : "Hersteller · Portable" : "WinGet");
}
