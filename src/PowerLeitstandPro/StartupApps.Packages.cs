using System.IO;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal sealed partial class StartupApps
{
    internal async Task<(IReadOnlyList<StartupApp> Apps, bool Partial)> ReadWithPackagesAsync(CancellationToken token)
    {
        var (classic, partial) = Read();
        static bool Switchable(StartupApp row) => row.CanToggle && row.Enabled is not null;
        if (partial) return (classic.Where(Switchable).ToArray(), true);
        try
        {
            var (apps, incomplete) = await Task.Run(() => ReadPackagedAppsAsync(token), token).WaitAsync(token).ConfigureAwait(false);
            var rows = PackageRows(apps, classic);
            var represented = rows.Where(row => row.StartupFile is not null).Select(row => row.StartupFile!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return (classic.Where(row => row.StartupFile is null || !represented.Contains(row.StartupFile))
                .Concat(rows).Where(Switchable).ToArray(), incomplete);
        }
        catch (Exception error) when (StartupPackagedApps.ReadFailure(error) || error is OperationCanceledException)
        { return (classic.Where(Switchable).ToArray(), true); }
    }

    private Task<(IReadOnlyList<StartupPackagedApp> Apps, bool Partial)> ReadPackagedAppsAsync(CancellationToken token, string? id = null)
        => packages is not null ? packages(token, id)
            : userFolder is null ? StartupPackagedApps.ReadAsync(token, id)
            : Task.FromResult<(IReadOnlyList<StartupPackagedApp>, bool)>(([], false));

    private IReadOnlyList<StartupApp> PackageRows(IEnumerable<StartupPackagedApp> apps, IReadOnlyList<StartupApp> classic)
    {
        var links = classic.Where(row => row.StartupFile is { } file && file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && IsFixedFile(file))
            .Select(row => (Row: row, Id: StartupPackagedApps.ReadLinkAppId(row.StartupFile!))).ToArray();
        var common = userFolder is null ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) : "";
        var commonIds = Path.IsPathFullyQualified(common) && IsPlainLocalDirectory(common)
            ? FolderNames(common).Where(name => name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                .Select(name => Path.Combine(common, name)).Where(IsFixedFile).Select(StartupPackagedApps.ReadLinkAppId)
                .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var approvals = Registry.CurrentUser.OpenSubKey(approvedFolder);
        return apps.Select(app =>
        {
            var matches = links.Where(link => string.Equals(link.Id, app.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            var existing = classic.FirstOrDefault(row => row.Source == StartupSource.UserFolder
                && row.Name.Equals(app.LinkName, StringComparison.OrdinalIgnoreCase));
            bool owned = existing is not null && matches.Any(link => link.Row == existing);
            bool duplicate = commonIds.Contains(app.Id) || matches.Any(link => link.Row != existing)
                || classic.Any(row => row.Command?.Contains(@"shell:AppsFolder\" + app.Id, StringComparison.OrdinalIgnoreCase) == true);
            var block = app.HasNativeTask == true ? StartupBlock.NativePackageTask
                : app.HasNativeTask is null ? StartupBlock.UnknownWindowsState
                : duplicate || existing is not null && !owned ? StartupBlock.ConflictingCopy
                : !StartupPackagedApps.CanResolveTarget(app.Id) ? StartupBlock.UnsupportedEntry
                : existing?.Block ?? (ApprovalState(ReadApproval(approvals, app.LinkName)) != true || !CanMoveShortcut(UserFolder, SavedFolder)
                    ? StartupBlock.UnsupportedEntry : StartupBlock.None);
            // Removing our own link remains available if an app update adds a native task.
            bool canToggle = block == StartupBlock.None || owned && existing is { Enabled: true, CanToggle: true };
            return new StartupApp(app.Name, StartupSource.WindowsPackage, owned ? existing!.Enabled : block == StartupBlock.None ? false : null,
                canToggle, StartupFile: owned ? existing!.StartupFile : null, Block: block, Package: app);
        }).ToArray();
    }

    internal async Task<StartupApp> SetAndReadAsync(StartupApp app, bool enabled)
    {
        if (app.Package is not { } package)
        {
            Set(app, enabled);
            return (app.Source == StartupSource.UserRun ? ReadUser() : ReadFolder())
                .Single(row => row.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (!StartupPackagedApps.ValidId(package.Id) || !app.CanToggle || app.Enabled != !enabled)
            throw new IOException("Invalid Windows app startup change.");
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (apps, _) = await Task.Run(() => ReadPackagedAppsAsync(limit.Token, package.Id), limit.Token)
            .WaitAsync(limit.Token).ConfigureAwait(false);
        var currentPackage = apps.SingleOrDefault(item => item.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Windows app is no longer installed.");
        var (classic, partial) = Read();
        var current = PackageRows([currentPackage], classic).Single();
        if (partial || !current.CanToggle || current.Enabled != app.Enabled || current.StartupFile != app.StartupFile
            || enabled && current.Block != StartupBlock.None)
            throw new IOException("Windows app startup state changed since it was read.");

        if (current.StartupFile is not null)
        {
            var shortcut = classic.Single(row => row.Source == StartupSource.UserFolder && row.Name.Equals(package.LinkName, StringComparison.OrdinalIgnoreCase));
            Set(shortcut, enabled);
        }
        else
        {
            string folder = UserFolder, backup = SavedFolder;
            if (!enabled || !CanMoveShortcut(folder, backup)) throw new IOException("Cannot create a startup shortcut in this folder.");
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(backup);
            string staged = Path.Combine(backup, Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                StartupPackagedApps.CreateLink(staged, package.Id);
                if (!string.Equals(StartupPackagedApps.ReadLinkAppId(staged), package.Id, StringComparison.OrdinalIgnoreCase)
                    || !CanMoveShortcut(folder, backup)) throw new IOException("Windows app shortcut could not be verified.");
                File.Move(staged, Path.Combine(folder, package.LinkName), overwrite: false);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        var updated = PackageRows([currentPackage], Read().Apps).Single();
        if (updated.Enabled != enabled) throw new IOException("Windows app startup state could not be verified.");
        return updated;
    }
}
