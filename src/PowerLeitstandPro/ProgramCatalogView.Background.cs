using System.Windows.Threading;

namespace PowerControlProWpf;

internal sealed partial class ProgramCatalogView
{
    internal static readonly TimeSpan OnlineCheckAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan LocalCheckAge = TimeSpan.FromMinutes(2);
    private readonly DispatcherTimer _backgroundTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DateTime _backgroundNotBefore = DateTime.UtcNow.AddSeconds(30);
    private DateTime _lastLocalCheck = DateTime.UtcNow, _lastOnlineAttempt = DateTime.MinValue;
    private Task? _backgroundTask, _openTask;
    private CancellationTokenSource? _backgroundCancellation;
    private bool _backgroundOnline, _savedRead;
    private long _backgroundGeneration;
    internal bool BackgroundChecking => _backgroundTask is { IsCompleted: false };
    private void InitializeBackgroundInspection()
    {
        _backgroundTimer.Tick += async (_, _) => await CheckInBackgroundAsync();
        Loaded += (_, _) => _backgroundTimer.Start();
        Unloaded += (_, _) => { _backgroundTimer.Stop(); CancelBackgroundInspection(); };
    }
    internal Task OpenAsync()
    {
        if (_openTask is { IsCompleted: false }) return _openTask;
        return _openTask = OpenSavedOrInspectAsync();
    }
    private async Task OpenSavedOrInspectAsync()
    {
        if (Service is not ProgramInstallService { HasPersistentInspection: true } service) { await RefreshAsync(); return; }
        if (!_savedRead)
        {
            _savedRead = true;
            var previous = await service.ReadSavedAsync(CancellationToken.None);
            if (previous is not null && !_refreshing && !_searching && !IsWorking)
            { Inventory = previous; _checkStage = CheckStage.Idle; _checkHasFreshResults = false; RenderTiles(); return; }
        }
        // Reopening the page never starts another full scan merely because the view was hidden.
        if (Inventory.CheckedUtc is not null) { RenderTiles(); return; }
        await RefreshAsync();
    }
    private void CancelBackgroundInspection()
    {
        if (BackgroundChecking && _backgroundOnline) InterruptCheckStatus(false);
        _backgroundGeneration++;
        _backgroundCancellation?.Cancel();
    }
    internal async Task PauseBackgroundInspectionAsync()
    {
        CancelBackgroundInspection();
        var pending = _backgroundTask;
        if (pending is not null) await pending;
    }
    private bool CanBackgroundCheck => IsLoaded && !IsWorking && !HostBusy() && !SelectionOpen
        && !_refreshing && !_searching && _info?.IsOpen != true && OperationPanel?.Expanded != true && !HasLoadingOverlay && Selected.Count == 0;
    internal Task CheckInBackgroundAsync(DateTime? timeForTests = null)
    {
        var now = timeForTests ?? DateTime.UtcNow;
        if (!CanBackgroundCheck || BackgroundChecking || Service is not ProgramInstallService { HasPersistentInspection: true } service
            || now < _backgroundNotBefore || Inventory.CheckedUtc is not { } checkedUtc) return Task.CompletedTask;
        bool online = now - checkedUtc >= OnlineCheckAge && now - _lastOnlineAttempt >= OnlineCheckAge;
        bool local = now - _lastLocalCheck >= LocalCheckAge && KnownEntries.Any(e => Inventory.HasUpdate(e.Id) || Inventory.HasUpdateNotice(e.Id));
        if (!online && !local) return Task.CompletedTask;
        if (online) _lastOnlineAttempt = now; else _lastLocalCheck = now;
        var current = new CancellationTokenSource(TimeSpan.FromSeconds(90)); _backgroundCancellation = current;
        _backgroundOnline = online; var generation = ++_backgroundGeneration; var previous = Inventory;
        return _backgroundTask = RunBackgroundInspectionAsync(service, previous, current, generation, online);
    }
    private async Task RunBackgroundInspectionAsync(ProgramInstallService service, ProgramInventory previous,
        CancellationTokenSource current, long generation, bool online)
    {
        if (online) BeginCheckStatus(); RefreshAvailability();
        try
        {
            var result = online ? await service.InspectAsync(current.Token) : await service.RefreshKnownAsync(previous, current.Token);
            if (current.IsCancellationRequested || generation != _backgroundGeneration || !ReferenceEquals(Service, service) || !CanBackgroundCheck) return;
            Inventory = online ? PreserveLastCandidates(previous, result) : result;
            if (online) FinishCheckStatus(Inventory);
            RenderTiles();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation == _backgroundGeneration)
            { InterruptCheckStatus(true); ReportProgramError(error, L("Hintergrundprüfung", "Background check")); }
        }
        finally
        {
            if (_backgroundCancellation == current) { _backgroundCancellation = null; _backgroundOnline = false; RefreshAvailability(); }
            current.Dispose();
        }
    }
    internal static ProgramInventory PreserveLastCandidates(ProgramInventory previous, ProgramInventory fresh)
    {
        bool healthy = fresh.Complete && fresh.UpdateCheckComplete && fresh.StoreCheckComplete
            && fresh.Error is null && fresh.OnlineError is null && fresh.StoreError is null && !fresh.IsPartial;
        if (healthy && fresh.UpdateSourcesComplete) return fresh;
        var previousCandidates = new Dictionary<string, string>();
        foreach (var values in new[] { previous.PendingUpdates, previous.Updates, previous.GitHubUpdates })
            if (values is not null) foreach (var pair in values) previousCandidates.TryAdd(pair.Key, pair.Value);
        if (healthy)
        {
            // A failed optional provider cannot erase its old candidate or withhold other fresh offers.
            var retained = previousCandidates.Where(p => fresh.UnverifiedUpdateIds?.Contains(p.Key) == true
                && !ProgramInstallService.CurrentEnough(fresh.Installed.GetValueOrDefault(p.Key), p.Value)).ToDictionary(p => p.Key, p => p.Value);
            return fresh with { PendingUpdates = retained };
        }
        if (!fresh.IsPartial && (fresh.UpdateCheckComplete || fresh.Updates?.Count > 0))
        {
            // An unrelated export timeout must not revoke independently confirmed update routes.
            var currentInstalled = fresh.Installed.ToDictionary(p => p.Key, p => p.Value);
            var currentEvidence = fresh.Evidence?.ToDictionary(p => p.Key, p => p.Value) ?? [];
            var unverified = new List<string>(); var retained = new Dictionary<string, string>();
            foreach (var pair in previousCandidates)
            {
                if (fresh.HasUpdate(pair.Key) || fresh.GitHubUpdates?.ContainsKey(pair.Key) == true) continue;
                var old = previous.Evidence?.GetValueOrDefault(pair.Key); var actual = fresh.Evidence?.GetValueOrDefault(pair.Key);
                bool sameFile = old?.Path is not null && actual is not null && old.Source == actual.Source && old.Variant == actual.Variant
                    && string.Equals(old.Path, actual.Path, StringComparison.OrdinalIgnoreCase);
                if (sameFile && ProgramInstallService.CurrentEnough(fresh.Installed.GetValueOrDefault(pair.Key), pair.Value)) continue;
                retained[pair.Key] = pair.Value;
                if (!currentInstalled.ContainsKey(pair.Key) && previous.Installed.TryGetValue(pair.Key, out var version))
                {
                    currentInstalled[pair.Key] = version; unverified.Add(pair.Key);
                    if (old is not null) currentEvidence[pair.Key] = old;
                }
            }
            return fresh with { Installed = currentInstalled, Evidence = currentEvidence, PendingUpdates = retained,
                UnverifiedInstalledIds = unverified, IsStale = false };
        }
        if (!previous.IsStale && previousCandidates.Count == 0) return fresh;
        var installed = previous.Installed.ToDictionary(p => p.Key, p => p.Value);
        var evidence = previous.Evidence?.ToDictionary(p => p.Key, p => p.Value) ?? [];
        var pending = previousCandidates;
        foreach (var pair in fresh.Installed) installed[pair.Key] = pair.Value;
        if (fresh.Evidence is not null) foreach (var pair in fresh.Evidence) evidence[pair.Key] = pair.Value;
        foreach (var values in new[] { fresh.Updates, fresh.GitHubUpdates })
            if (values is not null) foreach (var pair in values) pending[pair.Key] = pair.Value;
        return fresh with { Installed = installed, Evidence = evidence, PendingUpdates = pending, CheckedUtc = previous.CheckedUtc,
            FromCache = true, IsStale = true, Updates = null, GitHubUpdates = null, GitHubPackages = null, PortableUpdates = null, ManagedPackages = null };
    }
    private string SavedStatus() => L("Letzter Prüfstand: ", "Last checked: ") + Inventory.CheckedUtc?.ToLocalTime().ToString("dd.MM. HH:mm")
        + (Inventory.SavedPartial ? L(" · Teilprüfung", " · partial check") : "")
        + (_backgroundCancellation is not null ? L(" · wird im Hintergrund geprüft", " · checking in background")
            : !OnlineConnection.Available ? L(" · offline · gespeicherte Daten", " · offline · saved data")
            : L(" · gespeicherte Daten · spätere Prüfung", " · saved data · scheduled recheck"));
}
