using System.Runtime.InteropServices;

namespace PowerControlProWpf;

// Automation callbacks are deliberately empty. Poll progress on the owning MTA worker;
// no COM objects or callbacks cross into the WPF dispatcher.
[ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class WuaProgressCallback { [DispId(0)] public void Invoke(object job, object args) { } }
[ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
public sealed class WuaCompletedCallback { [DispId(0)] public void Invoke(object job, object args) { } }

internal static class WindowsUpdateAgent
{
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        internal dynamic Track(object value) { _objects.Add(value); return value; }
        internal dynamic Create(string id) => Track(Activator.CreateInstance(Type.GetTypeFromProgID(id, true)!)!);
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(_objects[i])) Marshal.ReleaseComObject(_objects[i]);
        }
    }

    private static dynamic Search(ComScope scope, dynamic session, string criteria, CancellationToken token, bool online = true, Action<string>? diagnostic = null)
    {
        token.ThrowIfCancellationRequested();
        dynamic searcher = scope.Track(session.CreateUpdateSearcher());
        searcher.Online = online;
        searcher.CanAutomaticallyUpgradeService = false;
        dynamic job = scope.Track(searcher.BeginSearch(criteria, new WuaCompletedCallback(), null));
        diagnostic?.Invoke("started");
        dynamic result = null!;
        try { Wait(job, null, null, token); }
        finally { result = scope.Track(searcher.EndSearch(job)); diagnostic?.Invoke("ended"); }
        token.ThrowIfCancellationRequested();
        var code = (int)result.ResultCode;
        if (code is not (2 or 3)) throw new InvalidOperationException("Windows-Updatesuche fehlgeschlagen.\nResultCode: " + code);
        return result;
    }

    internal static WindowsUpdateSearch Search(bool? drivers, CancellationToken token, bool online = true, Action<string>? diagnostic = null)
    {
        using var scope = new ComScope();
        dynamic session = scope.Create("Microsoft.Update.Session");
        session.ClientApplicationID = "PowerLeitstand PRO";
        var criteria = "IsInstalled=0 and IsHidden=0";
        if (drivers is bool onlyDrivers) criteria += onlyDrivers ? " and Type='Driver'" : " and Type='Software'";
        dynamic result = Search(scope, session, criteria, token, online, diagnostic);
        dynamic updates = scope.Track(result.Updates);
        var list = new List<WindowsUpdateItem>();
        for (var i = 0; i < (int)updates.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            dynamic item = scope.Track(updates.Item(i)); list.Add(Read(scope, item));
        }
        return new(list.ToArray(), (int)result.ResultCode == 3);
    }

    private static WindowsUpdateItem Read(ComScope scope, dynamic item)
    {
        dynamic identity = scope.Track(item.Identity);
        dynamic behavior = scope.Track(item.InstallationBehavior);
        var driver = (int)item.Type == 2;
        var category = "";
        dynamic categories = scope.Track(item.Categories);
        if ((int)categories.Count > 0) { dynamic cat = scope.Track(categories.Item(0)); category = (string)cat.Name; }
        var detail = driver ? (string)item.DriverManufacturer + " · " + (string)item.DriverModel : (string)item.Description;
        return new((string)identity.UpdateID, (int)identity.RevisionNumber, (string)item.Title, driver,
            category, detail, (double)item.MaxDownloadSize, (bool)item.IsDownloaded, (bool)item.BrowseOnly,
            (bool)behavior.CanRequestUserInput, (bool)item.EulaAccepted ? "" : (string)item.EulaText);
    }

    private static void Wait(dynamic job, Action<WindowsUpdateEvent>? progress, string? phase, CancellationToken token)
    {
        var abortSent = false;
        Exception? failure = null;
        while (!(bool)job.IsCompleted)
        {
            if ((token.IsCancellationRequested || failure is not null) && !abortSent)
            {
                try { job.RequestAbort(); abortSent = true; }
                catch (COMException) { /* A servicing operation can temporarily reject abort. Keep waiting. */ }
            }
            if (phase is not null && progress is not null && failure is null)
            {
                object? sample = null;
                try
                {
                    dynamic p = sample = job.GetProgress();
                    progress(new(phase, Percent: Math.Clamp((double)p.PercentComplete, 0, 100)));
                }
                catch (Exception exception) { failure = exception; }
                finally { if (sample is not null && Marshal.IsComObject(sample)) Marshal.ReleaseComObject(sample); }
            }
            Thread.Sleep(250);
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static void Run(WindowsUpdateRequest request, Action<WindowsUpdateEvent> send, CancellationToken token)
    {
        WindowsUpdatePolicy.Validate(request);
        token.ThrowIfCancellationRequested();
        using var scope = new ComScope();
        dynamic session = scope.Create("Microsoft.Update.Session"); session.ClientApplicationID = "PowerLeitstand PRO";
        send(new("validate"));
        dynamic result = Search(scope, session, "IsInstalled=0 and IsHidden=0", token);
        // A partial search cannot prove the full approved selection is still applicable.
        if ((int)result.ResultCode != 2) throw new InvalidOperationException("Unvollständige Windows-Updatesuche. Bitte erneut prüfen.");
        dynamic available = scope.Track(result.Updates);
        var candidates = new Dictionary<string, object>(); var metadata = new List<WindowsUpdateItem>();
        for (var i = 0; i < (int)available.Count; i++)
        {
            dynamic item = scope.Track(available.Item(i)); var info = Read(scope, item);
            candidates[info.Key] = item; metadata.Add(info);
        }
        WindowsUpdatePolicy.VerifySelection(request.Selection, metadata);
        dynamic selected = scope.Create("Microsoft.Update.UpdateColl");
        foreach (var choice in request.Selection)
        {
            dynamic item = candidates[choice.Id + ":" + choice.Revision];
            selected.Add(item);
        }
        dynamic installer = scope.Track(session.CreateUpdateInstaller());
        if ((bool)installer.IsBusy || (bool)installer.RebootRequiredBeforeInstallation)
            throw new InvalidOperationException("Windows benötigt zunächst den Abschluss eines anderen Vorgangs oder einen Neustart.");
        token.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        foreach (var choice in request.Selection)
        {
            dynamic item = candidates[choice.Id + ":" + choice.Revision];
            if (!(bool)item.EulaAccepted) item.AcceptEula();
        }
        dynamic downloader = scope.Track(session.CreateUpdateDownloader()); downloader.Updates = selected;
        send(new("download"));
        dynamic downloadJob = scope.Track(downloader.BeginDownload(new WuaProgressCallback(), new WuaCompletedCallback(), null));
        dynamic downloaded = null!;
        try { Wait(downloadJob, send, "download", token); }
        finally { downloaded = scope.Track(downloader.EndDownload(downloadJob)); }
        token.ThrowIfCancellationRequested();
        if ((int)downloaded.ResultCode != 2)
            throw new InvalidOperationException("Der Windows-Update-Download war nicht vollständig erfolgreich.");
        for (var i = 0; i < request.Selection.Length; i++)
        {
            dynamic item = candidates[request.Selection[i].Id + ":" + request.Selection[i].Revision];
            if (!(bool)item.IsDownloaded) throw new InvalidOperationException("Der Windows-Update-Download ist unvollständig.");
        }
        token.ThrowIfCancellationRequested();
        installer.Updates = selected; installer.ForceQuiet = true; installer.AllowSourcePrompts = false;
        send(new("install"));
        dynamic installJob = scope.Track(installer.BeginInstall(new WuaProgressCallback(), new WuaCompletedCallback(), null));
        dynamic installed = null!;
        try { Wait(installJob, send, "install", token); }
        finally { installed = scope.Track(installer.EndInstall(installJob)); }
        var reboot = (bool)installed.RebootRequired;
        for (var i = 0; i < request.Selection.Length; i++)
        {
            dynamic outcome = scope.Track(installed.GetUpdateResult(i));
            send(new("item", Key: request.Selection[i].Id + ":" + request.Selection[i].Revision,
                Code: (int)outcome.ResultCode, Text: "0x" + ((int)outcome.HResult).ToString("X8"), RebootRequired: reboot));
        }
        // Even after an abort request Windows may have installed individual updates.
        // Publish their real results before reporting cancellation, never imply rollback.
        if (token.IsCancellationRequested) { send(new("cancelled", RebootRequired: reboot)); return; }
        send(new("done", Code: (int)installed.ResultCode, RebootRequired: reboot));
    }

}
