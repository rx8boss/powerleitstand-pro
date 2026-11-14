using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed record ProgramInventory(bool Complete, IReadOnlyDictionary<string, string?> Installed, string? Error = null,
    IReadOnlyDictionary<string, string>? Updates = null, bool UpdateCheckComplete = false, string? OnlineError = null, IReadOnlyDictionary<string, string>? InstalledSources = null,
    IReadOnlyDictionary<string, ProgramInstallation>? Evidence = null, IReadOnlyList<ProgramManagerPresence>? Managers = null,
    IReadOnlyDictionary<string, PortableUpdate>? PortableUpdates = null, DateTime? CheckedUtc = null, bool FromCache = false, bool IsPartial = false,
    IReadOnlyDictionary<string, string>? GitHubUpdates = null, IReadOnlyDictionary<string, GitHubProgramUpdate>? GitHubPackages = null,
    bool StoreCheckComplete = true, string? StoreError = null, string? StoreProvider = null,
    IReadOnlyDictionary<string, IReadOnlyList<ProgramManagedPackage>>? ManagedPackages = null,
    bool IsStale = false, IReadOnlyDictionary<string, string>? PendingUpdates = null, IReadOnlyList<string>? UnverifiedUpdateIds = null,
    bool SavedPartial = false, IReadOnlyList<string>? UnverifiedInstalledIds = null, string? TerminalMsixUpdate = null)
{
    internal bool UpdateSourcesComplete => UnverifiedUpdateIds is not { Count: > 0 };
    internal bool HasUpdate(string id) => !IsStale && UnverifiedInstalledIds?.Contains(id) != true && Installed.ContainsKey(id) && Updates?.ContainsKey(id) == true
        && !ProgramPublisherLauncher.RequiresPublisherUpdate(id, Evidence?.GetValueOrDefault(id))
        && (ManagedPackages?.GetValueOrDefault(id)?.Any(p => p.IsUpdate) == true
            || GitHubPackages?.ContainsKey(id) == true || PortableUpdates?.ContainsKey(id) == true
            || id == "windows-terminal" && StoreCheckComplete && Evidence?.GetValueOrDefault(id)?.Source == "Windows-App"
            || Evidence?.GetValueOrDefault(id) is not { WinGetCompatible: false });
    internal bool HasUpdateNotice(string id) => Installed.ContainsKey(id) && !HasUpdate(id)
        && (GitHubUpdates?.ContainsKey(id) == true || PendingUpdates?.ContainsKey(id) == true);
}
internal sealed record ProgramPackage(string Id, string? Version, string Details, DateTime CheckedUtc, string? Description = null, string? OnlineVersion = null, string? VersionSource = null, bool CanInstall = true, string? IconUrl = null, PortableUpdate? PortableUpdate = null, string? IconWebsite = null, GitHubProgramUpdate? GitHubUpdate = null, ProgramInstallTargetProof? TargetProof = null, ProgramReinstallProof? ReinstallProof = null, ProgramManagedPackage? ManagedPackage = null);
internal interface IProgramInstallService
{
    Task<ProgramInventory> InspectAsync(CancellationToken token);
    Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token);
    Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started);
    Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started);
    Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token);
    void OpenWebsite(ProgramEntry entry);
}
internal interface IProgramCancelableUpdate { void StopPortableDownload(); }
internal interface IProgramDiagnosticSource { event Action<string>? Diagnostic; }
internal interface IProgramInspectionProgress { event Action<ProgramInventory>? InventoryChanged; }
internal interface IProgramSearchInventory { Task<ProgramInventory> InspectForSearchAsync(CancellationToken token); }

internal sealed partial class ProgramInstallService : IProgramInstallService, IProgramStepSource, IProgramDownloadSource, IProgramCancelableUpdate, IProgramInspectionProgress, IProgramSearchInventory, IProgramDiagnosticSource
{
    private CancellationTokenSource? _portableCancellation;
    public void StopPortableDownload() => _portableCancellation?.Cancel();
    private readonly ProgramPortableUpdates _portableUpdates;
    private readonly Func<ProgramPackage, CancellationToken, Task<ProgramInstallTargetProof?>> _targetResolver;
    private readonly bool _readPortableUpdates;
    private IReadOnlyDictionary<string, ProgramInstallation> _lastEvidence = new Dictionary<string, ProgramInstallation>();
    public event Action<ProgramDownload>? DownloadChanged;
    private readonly Dictionary<string, ProgramEntry> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _websites = [];
    private readonly Dictionary<string, string> _installedPackageIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _installedPackageVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousReinstallIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlyList<VlcRegisteredCopy>>? _vlcCopies;
    private readonly Func<ProgramPackage, VlcRegisteredCopy, CancellationToken, Task<string?>> _vlcManifest;
    private string? PackageId(ProgramEntry entry) => _installedPackageIds.GetValueOrDefault(entry.Id) ?? entry.PackageId;
    internal static bool PackageMatches(ProgramEntry entry, string id) => string.Equals(entry.PackageId, id, StringComparison.OrdinalIgnoreCase)
        || entry.Id == "google-chrome" && id is "Google.Chrome.EXE";
    private ProgramEntry[] KnownEntries => ProgramCatalog.Entries.Concat(_discovered.Values).ToArray();
    private bool Approved(ProgramEntry entry) => ProgramCatalog.Contains(entry)
        || (_discovered.TryGetValue(entry.Id, out var known) && ReferenceEquals(known, entry));
    private readonly Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>> _query;
    private readonly Func<ProgramEntry, CancellationToken, Task<ProgramPackage?>> _github;
    private readonly SemaphoreSlim _queryGate = new(1);
    private readonly Func<ProcessStartInfo, Action, Task<int>> _install;
    private readonly Func<ProgramEntry, ProgramInstallation?, CancellationToken, Task<bool>> _closeApplications;
    private readonly bool _realInventory;
    private readonly ProgramManagedPackages? _managedPackages;
    private readonly bool _scanGitHub;
    private readonly string? _portableFile;
    private readonly string? _locationsFile;
    private readonly string _executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
    internal ProgramInstallService(Func<ProcessStartInfo, CancellationToken, Task<WinGetResult>>? query = null,
        Func<ProcessStartInfo, Action, Task<int>>? install = null, string? portableFile = null,
        Func<ProgramEntry, CancellationToken, Task<ProgramPackage?>>? github = null, string? locationsFile = null,
        Func<CancellationToken, Task<ProgramStoreSnapshot>>? storeReader = null, ProgramManagedPackages? managedPackages = null,
        string? cacheFile = null, Func<ProgramEntry[], CancellationToken, Task<Dictionary<string, ProgramInstallation>>>? localReader = null,
        Action<string, TimeSpan>? inspectionTiming = null, ProgramPortableUpdates? portableUpdates = null,
        Func<ProgramPackage, CancellationToken, Task<ProgramInstallTargetProof?>>? targetResolver = null,
        Func<IReadOnlyList<VlcRegisteredCopy>>? vlcCopies = null,
        Func<ProgramPackage, VlcRegisteredCopy, CancellationToken, Task<string?>>? vlcManifest = null,
        Func<ProgramEntry, ProgramInstallation?, CancellationToken, Task<bool>>? closeApplications = null)
    {
        _portableUpdates = portableUpdates ?? new();
        _targetResolver = targetResolver ?? ProgramInstallTargets.ResolveAsync;
        _portableFile = portableFile; _locationsFile = locationsFile; _realInventory = query is null; _scanGitHub = _realInventory || github is not null;
        _closeApplications = closeApplications ?? (_realInventory ? ProgramApplicationCloser.CloseAsync : (_, _, _) => Task.FromResult(false));
        _vlcCopies = vlcCopies ?? (_realInventory ? ProgramVlcReinstallation.Read : null);
        _vlcManifest = vlcManifest ?? ProgramVlcReinstallation.ManifestAsync;
        _readPortableUpdates = _realInventory || portableUpdates is not null;
        _inspectionCache = cacheFile is null ? null : new(cacheFile); _localReader = localReader; _inspectionTiming = inspectionTiming;
        _managedPackages = managedPackages ?? (_realInventory ? new ProgramManagedPackages() : null);
        _readStoreInventory = _realInventory || storeReader is not null;
        _storeReader = storeReader ?? (token => ProgramStoreInventory.ReadAsync(token));
        _github = github ?? ProgramVersions.GithubAsync; _install = install ?? InstallHiddenAsync;
        var reader = query ?? WinGetService.QueryAsync;
        _query = async (start, token) =>
        {
            await _queryGate.WaitAsync(token).ConfigureAwait(false);
            try { return await reader(start, token).ConfigureAwait(false); }
            finally { _queryGate.Release(); }
        };
    }

    internal bool UsesWindowsInstallers => _realInventory;
    public event Action<ProgramStepEvent>? StepChanged;
    internal Task<bool> CloseForUpdateAsync(ProgramEntry entry, ProgramInstallation? proof, CancellationToken token)
    {
        if (!Approved(entry)) throw new InvalidOperationException("Unverified program identity");
        token.ThrowIfCancellationRequested();
        return proof?.Source == "Windows-App" || ProgramPublisherLauncher.RequiresPublisherUpdate(entry.Id, proof)
            ? Task.FromResult(false) : _closeApplications(entry, proof, token);
    }
    public event Action<string>? Diagnostic;
    internal string LastInstallOutput { get; private set; } = "";
    private async Task<int> InstallHiddenAsync(ProcessStartInfo start, Action started)
    {
        LastInstallOutput = "";
        await OnlineConnection.CheckAsync(CancellationToken.None);
        int idIndex = start.ArgumentList.IndexOf("--id");
        int versionIndex = start.ArgumentList.IndexOf("--version");
        int locationIndex = start.ArgumentList.IndexOf("--location");
        if (idIndex >= 0 && versionIndex >= 0 && !(start.ArgumentList[idIndex + 1] == "Microsoft.WindowsTerminal"
            && start.ArgumentList[0] == "install" && !start.ArgumentList.Contains("--no-upgrade")))
        {
            return await ProgramWinGetApi.PreferAsync(async () =>
            {
                var operation = await ProgramWinGetApi.PrepareAsync(start.ArgumentList[idIndex + 1], start.ArgumentList[versionIndex + 1], start.ArgumentList[0] == "upgrade",
                    locationIndex >= 0 ? start.ArgumentList[locationIndex + 1] : null, start.ArgumentList.Contains("--force"));
                return () => { Diagnostic?.Invoke("WinGet API · " + start.ArgumentList[idIndex + 1]); return operation(started, e => StepChanged?.Invoke(e), p => DownloadChanged?.Invoke(p)); };
            }, () => InstallCliAsync(start, started, idIndex), message => { LastInstallOutput = message + "\nCLI fallback before operation start.\n"; Diagnostic?.Invoke(message); });
        }
        return await InstallCliAsync(start, started, idIndex);
    }
    private async Task<int> InstallCliAsync(ProcessStartInfo start, Action started, int idIndex)
    {
        Diagnostic?.Invoke("WinGet CLI · " + (idIndex >= 0 ? start.ArgumentList[idIndex + 1] : "—"));
        var tracker = new ProgramWinGetSteps(e => StepChanged?.Invoke(e), idIndex >= 0 ? start.ArgumentList[idIndex + 1] : null,
            p => DownloadChanged?.Invoke(p));
        var result = await RunHiddenAsync(start, started, tracker.Line);
        LastInstallOutput += result.Output;
        if (!tracker.DownloadConfirmed) Diagnostic?.Invoke("Downloadfortschritt nicht einzeln gemeldet. / Download progress not reported separately.");
        return result.ExitCode;
    }
    internal static async Task<WinGetResult> RunHiddenAsync(ProcessStartInfo start, Action started, Action<string>? lineReceived = null)
    {
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        // WinGet reports UTF-8 even when its parent has no console. Match our read-only queries.
        start.StandardOutputEncoding = start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var process = Process.Start(start) ?? throw new IOException("WinGet konnte nicht gestartet werden. / Could not start WinGet.");
        async Task<string> Drain(TextReader reader, bool report)
        {
            var pending = new System.Text.StringBuilder(); bool overflow = false;
            var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int length;
            while ((length = await reader.ReadAsync(buffer.AsMemory())) > 0)
            {
                text.Append(buffer, 0, length);
                if (report && lineReceived is not null) foreach (char c in buffer.AsSpan(0, length))
                {
                    if (c is '\r' or '\n')
                    { if (!overflow && pending.Length > 0) lineReceived(pending.ToString()); pending.Clear(); overflow = false; }
                    else if (pending.Length < 8192 && !overflow) pending.Append(c);
                    else { pending.Clear(); overflow = true; }
                }
                if (text.Length > 32768) text.Remove(0, text.Length - 32768);
            }
            if (report && !overflow && pending.Length > 0) lineReceived?.Invoke(pending.ToString());
            return text.ToString();
        }
        var output = Drain(process.StandardOutput, true); var error = Drain(process.StandardError, true);
        started();
        // A queue stop never kills a running installer. Read both pipes concurrently to avoid deadlocks.
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output + "\n" + await error);
    }

    internal ProcessStartInfo Command(ProgramEntry entry, bool install, string? version = null, bool force = false, string? location = null)
    {
        if (!Approved(entry) || entry.PackageId is null) throw new InvalidOperationException("Unverified catalog entry");
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { install ? "install" : "show", "--id", PackageId(entry)!, "--exact", "--source", "winget" }) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(install ? "--silent" : "--disable-interactivity");
        if (install) start.ArgumentList.Add("--disable-interactivity");
        if (install && force) start.ArgumentList.Add("--force");
        else if (install) start.ArgumentList.Add("--no-upgrade");
        if (version is not null && Regex.IsMatch(version, @"^[A-Za-z0-9._+\-]+$"))
        { start.ArgumentList.Add("--version"); start.ArgumentList.Add(version); }
        if (location is not null)
        { if (!install || force) throw new IOException("Location changes are not supported during reinstallation"); start.ArgumentList.Add("--location"); start.ArgumentList.Add(ProgramInstallTargets.Validate(location)); }
        return start;
    }
    internal bool CanReinstall(ProgramEntry entry, ProgramInventory inventory)
    {
        return ReinstallCapability(entry, inventory).Automatic;
    }
    internal ProgramPackage ResolveReinstall(ProgramEntry entry, ProgramInventory inventory)
    {
        if (ReinstallCapability(entry, inventory).Route != ProgramReinstallRoute.WinGet || !_installedPackageIds.TryGetValue(entry.Id, out var id)
            || ProgramVersions.ConfirmedVersion(_recentExport.GetValueOrDefault(id)) is not { } version)
            throw new InvalidOperationException("The installed WinGet package and version are not confirmed");
        return new(id, version, "", DateTime.UtcNow, VersionSource: "WinGet", ReinstallProof: new(ReinstallCapability(entry, inventory), true));
    }
    internal async Task<int> ReinstallAsync(ProgramEntry entry, ProgramPackage package, ProgramInventory inventory, Action started, CancellationToken token = default)
    {
        InvalidateSaved(entry.Id);
        token.ThrowIfCancellationRequested();
        if (!ReinstallStillMatches(entry, package, inventory)) throw new InvalidOperationException("Reinstallation identity/version changed");
        if (package.ManagedPackage is not null) return await ApplyManagedAsync(entry, package, true, started, token);
        if (package.GitHubUpdate is { } github)
        {
            if (package.ReinstallProof!.Installed.Route != ProgramReinstallRoute.GitHub || github.Id != entry.Id || package.Version != github.Version)
                throw new InvalidOperationException("GitHub reinstallation identity changed");
            started(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); _portableCancellation = cancel;
            try { return await ProgramGitHubUpdates.ApplyAsync(entry, github, e => StepChanged?.Invoke(e), p => DownloadChanged?.Invoke(p), cancel.Token, reinstall: true); }
            finally { _portableCancellation = null; _recentInventory = null; }
        }
        if (package.PortableUpdate is { } direct)
        {
            if (package.ReinstallProof!.Installed.Route != ProgramReinstallRoute.Portable || direct.Id != entry.Id || package.Version != direct.Version)
                throw new InvalidOperationException("Portable reinstallation identity changed");
            started(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); _portableCancellation = cancel;
            try { await _portableUpdates.ApplyAsync(direct, e => StepChanged?.Invoke(e), p => DownloadChanged?.Invoke(p), cancel.Token, reinstall: true); return 0; }
            finally { _portableCancellation = null; _recentInventory = null; }
        }
        if (package.ReinstallProof!.Installed.Route != ProgramReinstallRoute.WinGet) throw new InvalidOperationException("Reinstallation source changed");
        if (_realInventory)
        {
            var probe = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true }; probe.ArgumentList.Add("--version");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5)); var result = await _query(probe, timeout.Token);
            if (result.ExitCode != 0 || !Version.TryParse(result.Output.Trim().TrimStart('v'), out var version) || version < new Version(1, 8))
                throw new IOException("Für die Neuinstallation bitte WinGet auf mindestens Version 1.8 aktualisieren. / Update WinGet to at least version 1.8 for reinstallation.");
            if (package.ReinstallProof!.Installed.VlcCopy is not null)
            {
                var help = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true };
                help.ArgumentList.Add("install"); help.ArgumentList.Add("--help");
                var support = await _query(help, timeout.Token);
                if (support.ExitCode != 0 || new[] { "--architecture", "--installer-type", "--scope", "--location" }.Any(flag => !support.Output.Contains(flag, StringComparison.Ordinal)))
                    throw new IOException("WinGet unterstützt die eindeutige VLC-Installerwahl nicht. Bitte WinGet aktualisieren. / WinGet does not support exact VLC installer selection. Update WinGet.");
            }
            var offered = await ReadReinstallWinGetAsync(entry, package.Version, token);
            if (offered is null || offered.Id != package.Id || offered.Version != package.Version)
                throw new IOException("Die gewählte Version ist nicht mehr verfügbar. Bitte erneut prüfen. / The selected version is no longer available. Check again.");
        }
        token.ThrowIfCancellationRequested();
        _recentInventory = null;
        var command = Command(entry, true, package.Version, force: true);
        if (package.ReinstallProof!.Installed.VlcCopy is { } copy)
        {
            string? hash = await _vlcManifest(package, copy, token);
            if (hash is null || hash != package.ReinstallProof.VlcManifestHash || !ReinstallStillMatches(entry, package, inventory))
                throw new IOException("Passender VLC-Installer oder Installationskopie geändert. Bitte erneut prüfen. / Matching VLC installer or installation copy changed. Check again.");
            ProgramVlcReinstallation.Restrict(command, copy);
        }
        token.ThrowIfCancellationRequested();
        return await _install(command, started);
    }
    internal async Task<int> InstallAtAsync(ProgramEntry entry, ProgramPackage package, string location, Action started, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        InvalidateSaved(entry.Id);
        if (!Approved(entry) || package.Id != PackageId(entry) || package.TargetProof is not { SupportsLocation: true } || !package.CanInstall
            || package.VersionSource != "WinGet" || package.ManagedPackage is not null
            || package.TargetProof.PackageId != package.Id || package.TargetProof.Version != package.Version
            || package.GitHubUpdate is not null || package.PortableUpdate is not null || _lastEvidence.ContainsKey(entry.Id))
            throw new IOException("Custom installation destination is not confirmed");
        var current = await ResolveTargetAsync(package, token);
        if (current != package.TargetProof) throw new IOException("Die Unterstützung des Zielordners hat sich geändert. Bitte erneut prüfen. / Destination support changed. Check again.");
        string target;
        try { target = ProgramInstallTargets.Validate(location, probeWrite: true); }
        catch (UnauthorizedAccessException e) { throw new IOException("Schreibrechte im Zielordner fehlen. Bitte anderen Ordner wählen oder die App als Administrator starten. / No write permission at the destination. Choose another folder or run the app as administrator.", e); }
        token.ThrowIfCancellationRequested();
        _recentInventory = null;
        return await _install(Command(entry, true, package.Version, location: target), started);
    }
    internal Task<ProgramInstallTargetProof?> ResolveTargetAsync(ProgramPackage package, CancellationToken token) => _targetResolver(package, token);
    public async Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token)
    {
        if (_managedPackages is not null && _lastEvidence.GetValueOrDefault(entry.Id) is { ManagedCopies.Count: > 0 } managed)
            return (await Task.Run(() => _managedPackages.ExistingAsync(entry, managed, token), token)).OrderByDescending(p => p.IsUpdate).FirstOrDefault()?.Package();
        if (_realInventory && _lastEvidence.GetValueOrDefault(entry.Id) is { } local && ProgramGitHubUpdates.Eligible(entry.Id, local))
        {
            var direct = await ProgramGitHubUpdates.ResolveAsync(entry, local, token);
            string? selectedVersion = _recentInventory?.Updates?.GetValueOrDefault(entry.Id);
            if (direct is not null && (selectedVersion is null || ProgramPortableUpdates.SameVersion(selectedVersion, direct.Version)))
                return new(entry.Id, direct.Version, direct.Url, DateTime.UtcNow, VersionSource: "GitHub", GitHubUpdate: direct);
        }
        if (_lastEvidence.GetValueOrDefault(entry.Id) is { WinGetCompatible: false } proof && (proof.Source is "Portable" or "Portable-Setup"
            || entry.Id == "tukui-client" && ProgramTukuiUpdates.Eligible(proof)))
        {
            var direct = await _portableUpdates.ResolveAsync(entry.Id, proof, token);
            if (direct is not null) return new(entry.PackageId ?? entry.Id, direct.Version, direct.Url, DateTime.UtcNow,
                VersionSource: "Publisher", PortableUpdate: direct);
            if (entry.Id == "tukui-client") return new(entry.Id, null, entry.Website, DateTime.UtcNow, VersionSource: "Tukui-Standalone", CanInstall: false);
            var info = await _github(entry, token) ?? await ProgramPublisherVersions.ReadAsync(entry, token);
            if (info is null && entry.Id == "adwcleaner")
                info = await ReadReinstallWinGetAsync(entry, null, token);
            return info is null ? null : info with { CanInstall = false };
        }
        if (entry.PackageId is null) return await ResolveManagedFallbackAsync(entry, token) ?? await _github(entry, token) ?? await ProgramPublisherVersions.ReadAsync(entry, token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(20));
        WinGetResult result;
        try { result = await _query(Command(entry, false), limit.Token); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
            var manager = await ResolveManagedFallbackAsync(entry, token); if (manager is not null) return manager;
            var github = await _github(entry, token); if (github is not null) return github;
            var publisher = await VlcPublisherVersionAsync(entry, token); if (publisher is not null) return publisher;
            throw;
        }
        if (result.ExitCode != 0 || !result.Output.Contains("[" + PackageId(entry) + "]", StringComparison.Ordinal)) return await ResolveManagedFallbackAsync(entry, token) ?? await _github(entry, token) ?? await VlcPublisherVersionAsync(entry, token);
        var version = Regex.Match(result.Output, @"(?im)^\s*(?:Version)\s*:\s*([A-Za-z0-9._+\-]+)\s*$");
        string? description = ReadDescription(result.Output);
        var website = ReadField(result.Output, "Homepage", "Publisher Url", "Publisher URL", "Herausgeber-URL", "Startseite");
        if (_discovered.ContainsKey(entry.Id) && Uri.TryCreate(website, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0)
            _websites[entry.Id] = uri.AbsoluteUri;
        var package = new ProgramPackage(PackageId(entry)!, version.Success ? version.Groups[1].Value : null, result.Output, DateTime.UtcNow, description, VersionSource: "WinGet", IconUrl: ProgramIconSources.Favicon(website), IconWebsite: website);
        if (package.Version is null)
        {
            try { var github = await _github(entry, token); if (github is not null) package = package with { OnlineVersion = github.OnlineVersion, VersionSource = "GitHub" }; }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        }
        return await BindVlcUpdateAsync(entry, package, token);
    }
    private static string? ReadField(string output, params string[] names)
    {
        var match = Regex.Match(output, @"(?im)^\s*(?:" + string.Join("|", names.Select(Regex.Escape)) + @")\s*:\s*([^\r\n]+)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
    internal static string? ReadDescription(string output)
    {
        var match = Regex.Match(output, @"(?m)^(?<indent>[ \t]*)(?:Description|Beschreibung)[ \t]*:[ \t]*(?<value>[^\r\n]*)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        int indent = match.Groups["indent"].Length;
        var lines = new List<string> { match.Groups["value"].Value.TrimEnd() };
        foreach (string line in output[(match.Index + match.Length)..].Replace("\r", "").Split('\n').Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) { lines.Add(""); continue; }
            if (line.TakeWhile(c => c is ' ' or '\t').Count() <= indent) break;
            lines.Add(line.Trim());
            if (lines.Sum(l => l.Length) > 32768) return null;
        }
        string text = string.Join('\n', lines).Trim();
        return text.Length == 0 || text.Length > 32768 ? null : text;
    }
    internal ProcessStartInfo SearchCommand(string query)
    {
        query = query.Trim();
        if (query.Length < 2 || query.Length > 100 || query.Any(char.IsControl)) throw new ArgumentException("Search requires 2–100 printable characters");
        var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "search", "--query", query, "--source", "winget", "--count", "50", "--disable-interactivity" }) start.ArgumentList.Add(value);
        return start;
    }
    internal static IReadOnlyList<(string Name, string Id, string Version)> ParseSearch(string output)
    {
        var rows = new List<(string, string, string)>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int idColumn = -1, versionColumn = -1;
        // CLI columns are separated by padding; no truncated identity is accepted.
        foreach (var line in output.Split('\n'))
        {
            var headerId = Regex.Match(line, @"(?i)(?<=\s)Id(?=\s)"); var headerVersion = Regex.Match(line, @"\bVersion\b");
            if (headerId.Success && headerVersion.Success && headerVersion.Index > headerId.Index)
            { idColumn = headerId.Index; versionColumn = headerVersion.Index; continue; }
            if (idColumn > 0 && line.Length > versionColumn)
            {
                var cellId = line[idColumn..versionColumn].Trim();
                var cellVersion = line[versionColumn..].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                var cellName = line[..idColumn].Trim();
                if (Regex.IsMatch(cellId, @"^[A-Za-z0-9][A-Za-z0-9._-]*\.[A-Za-z0-9._-]+$") && cellId.Length <= 160 && !cellId.Contains("..")
                    && Regex.IsMatch(cellVersion, @"^[A-Za-z0-9._+\-]+$") && cellName.Length > 0 && !cellName.Any(char.IsControl) && seen.Add(cellId))
                { rows.Add((cellName.Contains('…') ? cellId : cellName, cellId, cellVersion)); if (rows.Count == 50) break; continue; }
            }
            var match = Regex.Match(line.TrimEnd('\r'), @"^\s*(?<name>[^\r\n]+?)\s{2,}(?<id>[A-Za-z0-9][A-Za-z0-9._-]*\.[A-Za-z0-9._-]+)\s{2,}(?<version>[A-Za-z0-9._+\-]+)(?:\s{2,}[^\r\n]*)?$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value.Trim(); var id = match.Groups["id"].Value;
            if (name.Length > 160 || name.Any(char.IsControl) || name.Contains('…') || id.Length > 160 || id.Contains("..") || !seen.Add(id)) continue;
            rows.Add((name, id, match.Groups["version"].Value)); if (rows.Count == 50) break;
        }
        return rows;
    }
    public async Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(25));
        var result = await _query(SearchCommand(query), limit.Token); token.ThrowIfCancellationRequested();
        if (result.ExitCode == unchecked((int)0x8A150014)) return [];
        if (result.ExitCode != 0) throw new IOException("WinGet search: 0x" + result.ExitCode.ToString("X8"));
        var found = new List<ProgramEntry>();
        foreach (var row in ParseSearch(result.Output))
        {
            var item = ProgramCatalog.Entries.FirstOrDefault(x => string.Equals(x.PackageId, row.Id, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                var id = "winget:" + row.Id.ToLowerInvariant();
                if (!_discovered.TryGetValue(id, out item))
                {
                    if (_discovered.Count >= 1000) break;
                    item = new(id, row.Name, ["online"], row.Id, row.Id.Split('.')[0],
                        "WinGet-Treffer · Herstellerbeschreibung wird geprüft", "WinGet result · checking publisher description",
                        "https://github.com/microsoft/winget-pkgs", "", null, "https://github.com/microsoft/winget-pkgs", "session");
                    _discovered.Add(id, item);
                }
            }
            found.Add(item);
        }
        return found;
    }
    public Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started)
    {
        InvalidateSaved(entry.Id);
        _recentInventory = null;
        if (package.ManagedPackage is not null) return ApplyManagedAsync(entry, package, false, started, CancellationToken.None);
        if (package.PortableUpdate is not null) throw new InvalidOperationException("Portable replacement requires an existing verified file");
        if (_lastEvidence.GetValueOrDefault(entry.Id) is { WinGetCompatible: false }) throw new InvalidOperationException("Installed source is not WinGet-compatible");
        if (!package.CanInstall || package.Id != PackageId(entry)) throw new InvalidOperationException("Package identity mismatch");
        if (_discovered.ContainsKey(entry.Id) && (package.Version is null || !package.Version.Any(char.IsDigit)))
            throw new InvalidOperationException("No confirmed version for discovered package");
        return _install(Command(entry, true, package.Version), started);
    }
    private string? _terminalMsixUpdate;

    internal ProcessStartInfo UpgradeCommand(ProgramEntry entry, ProgramPackage package)
    {
        if (package.VersionSource is "msstore" or "terminal-msix")
        {
            if (!Approved(entry) || entry.Id != "windows-terminal" || !package.CanInstall
                || (package.VersionSource == "msstore" ? package.Id != TerminalStoreId || _terminalMsixUpdate is not null
                    : package.Id != entry.PackageId || package.Version != _terminalMsixUpdate)
                || _lastEvidence.GetValueOrDefault(entry.Id) is not { Source: "Windows-App", Version: { } installed }
                || !ProgramVersions.IsNewer(installed, package.Version))
                throw new InvalidOperationException("Windows-Terminal-Store-Update nicht bestätigt. / Windows Terminal Store update not verified.");
            if (package.VersionSource == "msstore") return TerminalStoreCommand(upgrade: true);
            var msix = Command(entry, true, package.Version);
            msix.ArgumentList.Remove("--no-upgrade");
            return msix;
        }
        if (ProgramPublisherLauncher.RequiresPublisherUpdate(entry.Id, _lastEvidence.GetValueOrDefault(entry.Id)))
            throw new InvalidOperationException("EA App zum Aktualisieren öffnen. / Open EA app to update.");
        if (package.PortableUpdate is not null || _lastEvidence.GetValueOrDefault(entry.Id) is { WinGetCompatible: false }) throw new InvalidOperationException("Update source mismatch");
        if (!package.CanInstall || package.Id != PackageId(entry) || package.Version is null || !Regex.IsMatch(package.Version, @"^[A-Za-z0-9._+\-]+$"))
            throw new InvalidOperationException("Unconfirmed update identity/version");
        var start = Command(entry, true, package.Version);
        start.ArgumentList[0] = "upgrade"; start.ArgumentList.Remove("--no-upgrade");
        return start;
    }
    public async Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started)
    {
        InvalidateSaved(entry.Id);
        _recentInventory = null;
        if (package.ManagedPackage is not null) return await ApplyManagedAsync(entry, package, false, started, CancellationToken.None);
        if (package.GitHubUpdate is { } github)
        {
            if (!Approved(entry) || !package.CanInstall || github.Id != entry.Id || package.Version != github.Version) throw new InvalidOperationException("GitHub identity mismatch");
            started(); using var cancel = new CancellationTokenSource(); _portableCancellation = cancel;
            try { return await ProgramGitHubUpdates.ApplyAsync(entry, github, e => StepChanged?.Invoke(e), p => DownloadChanged?.Invoke(p), cancel.Token); }
            finally { _portableCancellation = null; }
        }
        if (package.PortableUpdate is { } direct)
        {
            if (!Approved(entry) || direct.Id != entry.Id || package.Version != direct.Version || !package.CanInstall
                || !_lastEvidence.TryGetValue(entry.Id, out var proof) || !ProgramPortableUpdates.Eligible(entry.Id, proof)
                || !string.Equals(proof.Path, direct.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unconfirmed portable update identity");
            started();
            using var cancellation = new CancellationTokenSource(); _portableCancellation = cancellation;
            try { await _portableUpdates.ApplyAsync(direct, e => StepChanged?.Invoke(e), p => DownloadChanged?.Invoke(p), cancellation.Token); }
            finally { _portableCancellation = null; }
            return 0;
        }
        if (entry.Id == "vlc" && _vlcCopies is not null && _lastEvidence.GetValueOrDefault(entry.Id)?.Source == "Registry")
        {
            if (package.ReinstallProof?.Installed.VlcCopy is null) throw new InvalidOperationException("VLC-Updatekopie nicht bestätigt. / VLC update copy is unconfirmed.");
            return await ReinstallAsync(entry, package, VlcInventory(), started);
        }
        return await _install(UpgradeCommand(entry, package), started);
    }

    // Only parse exact catalog identities from a read-only `list --upgrade-available` response.
    // Unknown, truncated, pinned or malformed rows never create an actionable update.
    internal static Dictionary<string, string> ParseUpdates(string output, IDictionary<string, string?> installed, IEnumerable<ProgramEntry>? entries = null,
        IDictionary<string, string>? identities = null)
    {
        var updates = new Dictionary<string, string>();
        // A source-filtered WinGet table can omit the Source column entirely.
        // Match exact IDs, reject pins/unknown versions/truncated rows and never infer a downgrade.
        foreach (var entry in entries ?? ProgramCatalog.Entries)
        {
            var ids = entry.Id == "google-chrome" ? new[] { entry.PackageId, "Google.Chrome.EXE" } : new[] { entry.PackageId };
            foreach (var id in ids.Where(x => x is not null))
            {
                var matches = Regex.Matches(output, @"(?m)^[^\r\n]*?\s+" + Regex.Escape(id!)
                    + @"\s+(?<old>[A-Za-z0-9._+\-]+)\s+(?<new>[A-Za-z0-9._+\-]+)(?:[ \t]+winget)?[ \t]*\r?$", RegexOptions.IgnoreCase);
                if (matches.Count != 1) continue;
                var old = matches[0].Groups["old"].Value; var next = matches[0].Groups["new"].Value;
                if (!ProgramVersions.IsNewer(old, next)) continue;
                if (updates.ContainsKey(entry.Id)) { updates.Remove(entry.Id); identities?.Remove(entry.Id); break; }
                installed[entry.Id] = old; updates[entry.Id] = next;
                if (identities is not null) identities[entry.Id] = id!;
            }
        }
        return updates;
    }
    public void OpenWebsite(ProgramEntry entry)
    {
        OnlineConnection.RequireNetwork();
        if (!Approved(entry) || new Uri(entry.Website).Scheme != "https") throw new InvalidOperationException("Unapproved website");
        Process.Start(new ProcessStartInfo(_websites.GetValueOrDefault(entry.Id) ?? entry.Website) { UseShellExecute = true });
    }
    internal static IReadOnlyDictionary<string, string?> ParseExport(string json)
    {
        var installed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        foreach (var source in document.RootElement.GetProperty("Sources").EnumerateArray())
        {
            // Only the fixed community source is used for our install identities.
            if (!source.TryGetProperty("SourceDetails", out var details) || !details.TryGetProperty("Name", out var name) || name.GetString() != "winget") continue;
            foreach (var item in source.GetProperty("Packages").EnumerateArray())
                if (item.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { } key)
                    installed[key] = item.TryGetProperty("Version", out var v) ? v.GetString() : null;
        }
        return installed;
    }
    private async Task<ProgramInventory> InspectCoreAsync(CancellationToken token, ProgramEntry[]? selection = null)
    {
        _recentInventory = null;
        var entries = selection ?? KnownEntries;
        var selectedIds = entries.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selection is null)
        { _recentExport = new Dictionary<string, string?>(); _recentUpdatesText = ""; _installedPackageIds.Clear(); _installedPackageVersions.Clear(); _ambiguousReinstallIds.Clear(); }
        var storeTask = selection is null || entries.Any(e => ProgramVersions.StoreFamilies.ContainsKey(e.Id))
            ? TimedInspectionAsync("Store", () => ReadStoreForInspectionAsync(token))
            : Task.FromResult(new ProgramStoreSnapshot(new Dictionary<string, ProgramInstallation>(), true));
        var localTask = TimedInspectionAsync("Local", () => _localReader is not null ? _localReader(entries, token)
            : Task.Run(() => ProgramLocalInventory.Read(entries, token, includeManaged: false), token));
        var managedTask = TimedInspectionAsync("Manager-local", () => Task.Run(() => ProgramManagedInventory.Read(entries, token), token));
        await Task.WhenAll(localTask, managedTask);
        var evidence = await localTask;
        if (_locationsFile is not null) await Task.Run(() => new ProgramFileAssociations(_locationsFile, _vlcCopies).Merge(entries, evidence, token), token);
        var managed = await managedTask;
        ProgramManagedInventory.Merge(evidence, managed);
        if (_portableFile is not null) foreach (var pair in new ProgramPortableStore(_portableFile).Read()) if ((selection is null || selectedIds.Contains(pair.Key)) && !evidence.ContainsKey(pair.Key)) evidence[pair.Key] = pair.Value;
        var installed = evidence.ToDictionary(x => x.Key, x => x.Value.Version);
        var directUpdates = new Dictionary<string, PortableUpdate>();
        var githubUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var githubPackages = new Dictionary<string, GitHubProgramUpdate>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, IReadOnlyList<ProgramManagedPackage>> managerOffers = new Dictionary<string, IReadOnlyList<ProgramManagedPackage>>();
        void UpdateServiceEvidence() => _lastEvidence = selection is null ? evidence
            : _lastEvidence.Where(p => !selectedIds.Contains(p.Key)).Concat(evidence).ToDictionary(p => p.Key, p => p.Value);
        UpdateServiceEvidence();
        string? localError = null, storeError = null, storeProvider = null; bool storeComplete = !_readStoreInventory;
        ProgramInventory Snapshot(bool complete, string? error = null, string? onlineError = null, IReadOnlyDictionary<string, string>? updates = null, bool updatesComplete = false)
            => new(complete && storeComplete, new Dictionary<string, string?>(installed), error,
                Updates: updates is null ? null : new Dictionary<string, string>(updates), UpdateCheckComplete: updatesComplete, OnlineError: onlineError,
                InstalledSources: evidence.ToDictionary(x => x.Key, x => x.Value.Source), Evidence: new Dictionary<string, ProgramInstallation>(evidence),
                Managers: managed.Managers, PortableUpdates: new Dictionary<string, PortableUpdate>(directUpdates), CheckedUtc: DateTime.UtcNow,
                GitHubUpdates: new Dictionary<string, string>(githubUpdates), GitHubPackages: new Dictionary<string, GitHubProgramUpdate>(githubPackages),
                StoreCheckComplete: storeComplete, StoreError: storeError, StoreProvider: storeProvider, ManagedPackages: managerOffers, TerminalMsixUpdate: _terminalMsixUpdate);
        if (entries.Any(e => e.Id == "windows-terminal")) _terminalMsixUpdate = null;
        bool storeMerged = !_readStoreInventory;
        async Task MergeStoreAsync()
        {
            if (storeMerged) return;
            var store = await storeTask; localError ??= store.Error;
            storeComplete = store.Complete; storeError = store.Error; storeProvider = store.Provider;
            foreach (var pair in store.Entries.Where(p => selection is null || selectedIds.Contains(p.Key)))
                if (!installed.ContainsKey(pair.Key) || evidence.GetValueOrDefault(pair.Key)?.Source == "SelectedFile")
                {
                    var selectedPath = evidence.GetValueOrDefault(pair.Key)?.Path;
                    installed[pair.Key] = pair.Value.Version; evidence[pair.Key] = pair.Value with { Path = pair.Value.Path ?? selectedPath };
                }
            storeMerged = true;
            InventoryChanged?.Invoke(Snapshot(false) with { IsPartial = true });
        }
        if (!storeMerged && storeTask.IsCompleted) await MergeStoreAsync();
        else InventoryChanged?.Invoke(Snapshot(false) with { IsPartial = true });
        if (_realInventory)
        {
            try { await OnlineConnection.CheckAsync(token); }
            catch (OnlineConnectionException e) { await MergeStoreAsync(); return Snapshot(false, localError ?? e.Message, e.Message); }
        }
        var updates = new Dictionary<string, string>(); bool updatesComplete = false, exportComplete = false;
        var uncertainUpdates = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        void Uncertain(string id) => uncertainUpdates.TryAdd(id, 0);
        var earlyInstalled = new Dictionary<string, string?>(installed);
        var earlyGithub = _scanGitHub ? TimedInspectionAsync("GitHub-releases", () => ReadGithubNoticesAsync(entries, earlyInstalled, token, Uncertain))
            : Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var managerOffersTask = TimedInspectionAsync("Manager-offers", () => ReadManagedOffersAsync(entries, new Dictionary<string, ProgramInstallation>(evidence), token, Uncertain));
        var portableEvidence = new Dictionary<string, ProgramInstallation>(evidence);
        var portableTask = _readPortableUpdates
            ? TimedInspectionAsync("Portable", () => Task.Run(() => ReadPortableForInspectionAsync(portableEvidence, token, Uncertain), token))
            : Task.FromResult(new Dictionary<string, PortableUpdate>());
        // Update proof is independent of the expensive full export. Do not discard confirmed
        // upgrades merely because an unrelated package delays inventory enrichment.
        try
        {
            async Task<WinGetResult> ListUpdatesAsync(CancellationToken queryToken)
            {
                var ids = selection is null ? new string?[] { null } : entries.Where(e => e.PackageId is not null && evidence.GetValueOrDefault(e.Id) is not { WinGetCompatible: false })
                    .SelectMany(e => e.Id == "google-chrome" ? new[] { e.PackageId, "Google.Chrome.EXE" } : new[] { e.PackageId }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var output = new System.Text.StringBuilder();
                foreach (var id in ids)
                {
                    var list = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var arg in new[] { "list", "--upgrade-available", "--source", "winget", "--disable-interactivity" }) list.ArgumentList.Add(arg);
                    if (id is not null) { list.ArgumentList.Add("--id"); list.ArgumentList.Add(id); list.ArgumentList.Add("--exact"); }
                    var response = await _query(list, queryToken);
                    if (response.ExitCode != 0 && response.ExitCode != unchecked((int)0x8A15002B)
                        && !(selection is not null && response.ExitCode == unchecked((int)0x8A150014))) return response;
                    output.AppendLine(response.Output);
                }
                return new(0, output.ToString());
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(25));
            var pendingQuery = TimedInspectionAsync("WinGet-updates", () => ListUpdatesAsync(limit.Token));
            if (!storeMerged)
            {
                await Task.WhenAny(pendingQuery, storeTask);
                if (storeTask.IsCompleted) await MergeStoreAsync();
            }
            var available = await pendingQuery;
            // Store discovery runs alongside WinGet, but is merged before update identities are accepted.
            await MergeStoreAsync();
            if (available.ExitCode == 0 || available.ExitCode == unchecked((int)0x8A15002B))
            {
                if (selection is null) _recentUpdatesText = available.Output;
                updates = ParseUpdates(available.Output, installed, entries.Where(e => !evidence.TryGetValue(e.Id, out var proof) || proof.WinGetCompatible), _installedPackageIds);
                foreach (var id in updates.Keys.ToArray())
                {
                    if (selection is not null && evidence.TryGetValue(id, out var localProof) && localProof.Version is { } localVersion
                        && !ProgramPortableUpdates.SameVersion(localVersion, installed[id] ?? ""))
                    { updates.Remove(id); installed[id] = localVersion; _installedPackageIds.Remove(id); Uncertain(id); continue; }
                    if (_installedPackageIds.TryGetValue(id, out var packageId)) _installedPackageVersions[packageId] = installed[id];
                    evidence[id] = evidence.TryGetValue(id, out var proof) ? proof with { Version = installed[id] } : new(installed[id], "WinGet");
                }
                updatesComplete = true;
            }
            else localError = "Update check: 0x" + available.ExitCode.ToString("X8");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        { token.ThrowIfCancellationRequested(); localError = "Update check: " + e.Message; }
        await MergeStoreAsync();
        if (storeComplete && entries.Any(e => e.Id == "windows-terminal")
            && evidence.GetValueOrDefault("windows-terminal") is { Source: "Windows-App", Version: { } terminalVersion })
        {
            try
            {
                using var storeLimit = CancellationTokenSource.CreateLinkedTokenSource(token);
                storeLimit.CancelAfter(TimeSpan.FromSeconds(20));
                var storeOffer = await _query(TerminalStoreCommand(upgrade: false), storeLimit.Token);
                if (storeOffer.ExitCode == 0 && ReadTerminalStoreOffer(storeOffer.Output, terminalVersion) is { } target)
                    updates["windows-terminal"] = target;
                else if (storeOffer.ExitCode is not (0 or unchecked((int)0x8A150014) or unchecked((int)0x8A15002B)))
                    localError ??= "WinGet (msstore): 0x" + storeOffer.ExitCode.ToString("X8");
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
            { token.ThrowIfCancellationRequested(); localError ??= "WinGet (msstore): " + e.Message; }
        }
        var githubTask = TimedInspectionAsync("GitHub-late", () => CompleteGithubNoticesAsync(entries, earlyInstalled, new Dictionary<string, string?>(installed), earlyGithub, token, Uncertain));
        InventoryChanged?.Invoke(Snapshot(false, localError, updates: updates, updatesComplete: updatesComplete) with { IsPartial = true });
        if (selection is null)
        {
        // Only a whole-catalog inspection may replace the complete WinGet export.
        string temporary = Path.Combine(Path.GetTempPath(), "PowerLeitstand-Inventory-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var start = new ProcessStartInfo(_executable) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "export", "--output", temporary, "--include-versions", "--source", "winget", "--disable-interactivity" }) start.ArgumentList.Add(arg);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(15));
            var result = await TimedInspectionAsync("WinGet-export", () => _query(start, limit.Token));
            if (result.ExitCode != 0 || !File.Exists(temporary)) localError ??= "WinGet inventory: 0x" + result.ExitCode.ToString("X8");
            else
            {
                if (new FileInfo(temporary).Length > 10 * 1024 * 1024) throw new InvalidDataException("Inventory too large");
                _recentExport = ParseExport(await File.ReadAllTextAsync(temporary, token));
                foreach (var pair in _recentExport)
                    foreach (var entry in entries.Where(x => PackageMatches(x, pair.Key)))
                    {
                        if (evidence.GetValueOrDefault(entry.Id) is not { WinGetCompatible: false })
                        {
                            if (_installedPackageIds.TryGetValue(entry.Id, out var previousId) && !string.Equals(previousId, pair.Key, StringComparison.OrdinalIgnoreCase)) _ambiguousReinstallIds.Add(entry.Id);
                            _installedPackageIds.TryAdd(entry.Id, pair.Key);
                        }
                        if (!installed.ContainsKey(entry.Id))
                        { installed[entry.Id] = ProgramVersions.ConfirmedVersion(pair.Value); evidence[entry.Id] = new(installed[entry.Id], "WinGet"); }
                        else if (installed[entry.Id] is null && !string.IsNullOrWhiteSpace(pair.Value) && evidence[entry.Id].WinGetCompatible)
                        { installed[entry.Id] = ProgramVersions.ConfirmedVersion(pair.Value); evidence[entry.Id] = evidence[entry.Id] with { Version = installed[entry.Id] }; }
                    }
                exportComplete = true;
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or InvalidOperationException)
        { token.ThrowIfCancellationRequested(); localError ??= "WinGet inventory: " + e.Message; }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        else exportComplete = true;
        foreach (var pair in await portableTask)
        {
            var direct = pair.Value;
            if (!evidence.TryGetValue(pair.Key, out var proof) || !ProgramPortableUpdates.Eligible(pair.Key, proof)
                || !string.Equals(proof.Path, direct.Path, StringComparison.OrdinalIgnoreCase)) continue;
            if (pair.Key != "melonloader") installed[pair.Key] = direct.InstalledVersion;
            if (ProgramVersions.IsNewer(direct.InstalledVersion, direct.Version))
            { directUpdates[pair.Key] = direct; updates[pair.Key] = direct.Version; }
        }
        var githubVersions = await githubTask;
        foreach (var pair in githubVersions)
        {
            if (!installed.ContainsKey(pair.Key) || updates.ContainsKey(pair.Key) || directUpdates.ContainsKey(pair.Key)
                || !ProgramVersions.IsNewer(installed.GetValueOrDefault(pair.Key), pair.Value)) continue;
            githubUpdates[pair.Key] = pair.Value;
        }
        if (_realInventory)
        {
            var confirmed = await TimedInspectionAsync("GitHub-routes", () => ConfirmGithubRoutesAsync(entries,
                new Dictionary<string, ProgramInstallation>(evidence), githubVersions, token, updates.Keys.ToHashSet()));
            foreach (var item in confirmed)
            {
                if (item.Direct is { } direct) { githubPackages[item.Id] = direct; updates[item.Id] = direct.Version; githubUpdates.Remove(item.Id); }
                else if (item.WinGet is { } version) { updates[item.Id] = version; githubUpdates.Remove(item.Id); }
            }
        }
        if (_realInventory && storeComplete && !updates.ContainsKey("windows-terminal")
            && evidence.GetValueOrDefault("windows-terminal") is { Source: "Windows-App", Version: { } installedTerminal }
            && githubUpdates.GetValueOrDefault("windows-terminal") is { } targetTerminal
            && Version.TryParse(targetTerminal, out var msixVersion) && msixVersion.Revision >= 0
            && ProgramVersions.IsNewer(installedTerminal, targetTerminal))
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(12));
                var url = new Uri($"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/m/Microsoft/WindowsTerminal/{targetTerminal}/Microsoft.WindowsTerminal.installer.yaml");
                var manifest = System.Text.Encoding.UTF8.GetString(await CatalogWeb.ReadAsync(url,
                    new HashSet<string> { "raw.githubusercontent.com" }, 128 * 1024, limit.Token));
                if (TerminalMsixMatches(manifest, targetTerminal))
                {
                    _terminalMsixUpdate = updates["windows-terminal"] = targetTerminal;
                    githubUpdates.Remove("windows-terminal");
                }
            }
            catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException)
            { token.ThrowIfCancellationRequested(); localError ??= "Windows Terminal MSIX: " + e.Message; }
        }
        managerOffers = await managerOffersTask;
        foreach (var pair in managerOffers)
            if (pair.Value.FirstOrDefault(p => p.IsUpdate) is { } offered) updates[pair.Key] = offered.Version;
        var snapshot = Snapshot(exportComplete, localError, updates: updates, updatesComplete: updatesComplete)
            with { UnverifiedUpdateIds = uncertainUpdates.Keys.ToArray() };
        UpdateServiceEvidence();
        if (selection is null) { _recentInventory = snapshot; _recentIds = entries.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        return snapshot;
    }
    internal static bool MatchesRegistry(ProgramEntry entry, string? name, string? publisher)
        => ProgramLocalInventory.Matches(entry, name, publisher);
}
