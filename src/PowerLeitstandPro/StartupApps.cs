using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace PowerControlProWpf;

internal enum StartupSource { UserRun, UserFolder, WindowsPackage }
internal enum StartupBlock { None, UnknownWindowsState, UnsupportedEntry, ConflictingCopy, WindowsPolicy, NativePackageTask }
internal sealed record StartupApp(string Name, StartupSource Source, bool? Enabled, bool CanToggle,
    string? Command = null, RegistryValueKind Kind = RegistryValueKind.Unknown, string? StartupFile = null,
    string? Approval = null, StartupBlock Block = StartupBlock.None, StartupPackagedApp? Package = null);

internal sealed partial class StartupApps(string userRun = RegistryAutoStartRegistration.KeyPath,
    string savedRun = @"Software\PowerLeitstand PRO\DisabledStartup",
    string approvedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
    string? userFolder = null, string? savedFolder = null,
    string approvedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
    Func<CancellationToken, string?, Task<(IReadOnlyList<StartupPackagedApp> Apps, bool Partial)>>? packages = null)
{
    private const string EnabledMarker = "020000000000000000000000";
    private string UserFolder => userFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private string SavedFolder => savedFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "Disabled Startup");
    internal static bool ReadFailure(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    internal (IReadOnlyList<StartupApp> Apps, bool Partial) Read()
    {
        var apps = new List<StartupApp>(); bool partial = false;
        void Collect(Action read)
        {
            try { read(); }
            catch (Exception error) when (ReadFailure(error)) { partial = true; }
        }
        Collect(() => apps.AddRange(ReadUser()));
        Collect(() => apps.AddRange(ReadFolder()));
        return (apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(app => app.Source).ToArray(), partial);
    }

    internal IReadOnlyList<StartupApp> ReadUser()
    {
        using var run = Registry.CurrentUser.OpenSubKey(userRun);
        using var saved = Registry.CurrentUser.OpenSubKey(savedRun);
        using var approved = Registry.CurrentUser.OpenSubKey(approvedRun);
        using var policy = userRun == RegistryAutoStartRegistration.KeyPath
            ? Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer") : null;
        bool policyBlocksRun = policy?.GetValue("DisableCurrentUserRun") is 1;
        var currentNames = run?.GetValueNames() ?? [];
        var savedNames = saved?.GetValueNames() ?? [];
        var names = currentNames.Concat(savedNames).Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool ownAvailable = userRun == RegistryAutoStartRegistration.KeyPath && AutoStartService.CurrentExecutable is not null;
        if (ownAvailable) names.Add(RegistryAutoStartRegistration.ValueName);

        return names.Select(name =>
        {
            bool active = currentNames.Contains(name, StringComparer.OrdinalIgnoreCase);
            bool own = ownAvailable && name.Equals(RegistryAutoStartRegistration.ValueName, StringComparison.OrdinalIgnoreCase);
            var value = ReadCommand(active ? run : saved, name);
            var backup = ReadCommand(saved, name);
            var approval = ReadApproval(approved, name);
            var state = ApprovalState(approval);
            var block = policyBlocksRun ? StartupBlock.WindowsPolicy : state is null ? StartupBlock.UnknownWindowsState
                : active && saved?.GetValue(name) is not null && backup != value || !active && state == false && !own ? StartupBlock.ConflictingCopy
                : value.Command is null && !(own && !active) ? StartupBlock.UnsupportedEntry : StartupBlock.None;
            return new StartupApp(name, StartupSource.UserRun, policyBlocksRun ? false : state is null ? null : active && state == true,
                block == StartupBlock.None, value.Command, value.Kind, Approval: approval, Block: block);
        }).ToArray();
    }

    internal IReadOnlyList<StartupApp> ReadFolder()
    {
        string folder = UserFolder, backup = SavedFolder;
        if (!Path.IsPathFullyQualified(folder) || !Path.IsPathFullyQualified(backup)) return [];
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        if (userFolder is null && common.Length > 0 && ProgramPathResolver.InDirectory(common, folder)) return [];
        var active = FolderNames(folder);
        var saved = IsPlainLocalDirectory(backup) ? FolderNames(backup) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var approved = Registry.CurrentUser.OpenSubKey(approvedFolder);
        return active.Union(saved, StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            bool present = active.Contains(name);
            string file = Path.Combine(present ? folder : backup, name);
            var approval = ReadApproval(approved, name);
            var state = ApprovalState(approval);
            var block = state is null ? StartupBlock.UnknownWindowsState
                : present && saved.Contains(name) || !present && state == false ? StartupBlock.ConflictingCopy
                : !Path.GetExtension(name).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || !IsFixedFile(file)
                    || ((!present || approval is null) && !CanMoveShortcut(folder, backup)) ? StartupBlock.UnsupportedEntry : StartupBlock.None;
            return new StartupApp(name, StartupSource.UserFolder, state is null ? null : present && state == true,
                block == StartupBlock.None, StartupFile: file, Approval: approval, Block: block);
        }).ToArray();
    }

    internal void Set(StartupApp app, bool enable)
    {
        if (!app.CanToggle || app.Enabled != !enable)
            throw new IOException("Startup entry cannot be changed.");
        if (app.Source == StartupSource.WindowsPackage) throw new IOException("Windows app requires a verified package identity.");
        if (app.Source == StartupSource.UserFolder) { SetShortcut(app, enable); return; }
        if (ReadUser().SingleOrDefault(item => item.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)) != app)
            throw new IOException("Startup entry changed since it was read.");
        using var approved = Registry.CurrentUser.OpenSubKey(approvedRun, writable: true);
        if (ReadApproval(approved, app.Name) != app.Approval || ApprovalState(app.Approval) is null)
            throw new IOException("Windows startup state changed since it was read.");
        using var run = Registry.CurrentUser.CreateSubKey(userRun);
        var expected = (app.Command, app.Kind);
        if (app.Approval is not null && app.Command is not null && ReadCommand(run, app.Name) == expected)
        {
            WriteApproval(approved!, app, enable);
            return;
        }
        if (userRun == RegistryAutoStartRegistration.KeyPath && app.Name.Equals(RegistryAutoStartRegistration.ValueName, StringComparison.OrdinalIgnoreCase))
        {
            if (ReadCommand(run, app.Name) != (enable ? (null, RegistryValueKind.Unknown) : expected))
                throw new IOException("Startup entry changed since it was read.");
            var status = AutoStartService.Current.SetEnabled(enable, AutoStartService.CurrentExecutable);
            if (status.Error is not null) throw new IOException(status.Error);
            if (enable && ApprovalState(app.Approval) == false) WriteApproval(approved!, app, true);
            return;
        }
        if (app.Command is null || app.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
            throw new IOException("Unsupported startup command.");
        using var saved = Registry.CurrentUser.CreateSubKey(savedRun);
        var active = ReadCommand(run, app.Name);
        var backup = ReadCommand(saved, app.Name);
        if (enable)
        {
            if (run.GetValue(app.Name) is not null || backup != expected || ReadApproval(approved, app.Name) != app.Approval)
                throw new IOException("Startup entry changed since it was read.");
            run.SetValue(app.Name, app.Command, app.Kind);
            if (ReadCommand(run, app.Name) != expected) throw new IOException("Restored startup command could not be verified.");
            if (ReadCommand(saved, app.Name) == expected) saved.DeleteValue(app.Name, false);
        }
        else
        {
            if (active != expected || saved.GetValue(app.Name) is not null && backup != expected)
                throw new IOException("Startup entry changed since it was read.");
            saved.SetValue(app.Name, app.Command, app.Kind);
            if (ReadCommand(saved, app.Name) != expected || ReadCommand(run, app.Name) != expected || ReadApproval(approved, app.Name) != app.Approval)
                throw new IOException("Startup backup could not be verified.");
            run.DeleteValue(app.Name, false);
            if (run.GetValue(app.Name) is not null) throw new IOException("Startup command could not be disabled.");
        }
    }

    private void SetShortcut(StartupApp app, bool enable)
    {
        string folder = UserFolder, backup = SavedFolder;
        var current = ReadFolder().SingleOrDefault(item => item.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
        if (current != app) throw new IOException("Startup shortcut changed since it was read.");
        using var approved = Registry.CurrentUser.OpenSubKey(approvedFolder, writable: true);
        if (ReadApproval(approved, app.Name) != app.Approval) throw new IOException("Windows startup state changed since it was read.");
        string original = Path.Combine(folder, app.Name);
        if (app.Approval is not null && string.Equals(app.StartupFile, original, StringComparison.OrdinalIgnoreCase))
        {
            WriteApproval(approved!, app, enable);
            return;
        }
        string destination = Path.Combine(enable ? folder : backup, app.Name);
        if (!CanMoveShortcut(folder, backup)) throw new IOException("Startup shortcut cannot be moved safely.");
        Directory.CreateDirectory(enable ? folder : backup);
        if (!CanMoveShortcut(folder, backup) || !IsFixedFile(app.StartupFile!) || File.Exists(destination)
            || ReadApproval(approved, app.Name) != app.Approval) throw new IOException("Startup shortcut cannot be moved safely.");
        File.Move(app.StartupFile!, destination, overwrite: false);
        if (!File.Exists(destination) || File.Exists(app.StartupFile!)) throw new IOException("Startup shortcut move could not be verified.");
    }

    private static HashSet<string> FolderNames(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>()
            .Where(name => !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new(StringComparer.OrdinalIgnoreCase);

    private static bool CanMoveShortcut(string folder, string backup) => IsPlainLocalDirectory(folder) && IsPlainLocalDirectory(backup)
        && string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(backup), StringComparison.OrdinalIgnoreCase)
        && !ProgramPathResolver.InDirectory(folder, backup) && !ProgramPathResolver.InDirectory(backup, folder);

    private static bool IsPlainLocalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: 3 } root
            || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
        for (string? dir = Path.GetFullPath(path); dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static string? ReadApproval(RegistryKey? key, string name) => key?.GetValue(name) switch
    {
        null => null,
        byte[] { Length: 12 } bytes when key!.GetValueKind(name) == RegistryValueKind.Binary => Convert.ToHexString(bytes),
        _ => ""
    };

    // shortcut: recognize the exact Explorer states observed here; new encodings need separate verification.
    private static bool? ApprovalState(string? approval) => approval switch
    {
        null or EnabledMarker => true,
        { Length: 24 } state when state.StartsWith("03000000", StringComparison.Ordinal) => false,
        _ => null
    };

    private static void WriteApproval(RegistryKey key, StartupApp app, bool enable)
    {
        if (ReadApproval(key, app.Name) != app.Approval) throw new IOException("Windows startup state changed since it was read.");
        var bytes = Convert.FromHexString(EnabledMarker);
        if (!enable)
        {
            bytes[0] = 3;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        }
        key.SetValue(app.Name, bytes, RegistryValueKind.Binary);
        if (ReadApproval(key, app.Name) != Convert.ToHexString(bytes)) throw new IOException("Windows startup state could not be verified.");
    }

    internal static BitmapSource? ReadIcon(StartupApp app)
    {
        try
        {
            string? target = ProgramPathResolver.ExecutableToken(app.Command);
            if (app.StartupFile is { } file && IsFixedFile(file))
                target = Path.GetExtension(file).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    ? ProgramPathResolver.ReadShortcut(file) : ProgramPathResolver.ExecutableToken('"' + file + '"');
            if (target is null || !IsFixedFile(target)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(target);
            if (icon is null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(24, 24));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (ReadFailure(error) || error is ArgumentException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        { return null; }
    }

    private static bool IsFixedFile(string path) => Path.GetPathRoot(path) is { Length: 3 } root
        && new DriveInfo(root).DriveType == DriveType.Fixed && ProgramPortableUpdates.PlainPath(path) && File.Exists(path);

    private static (string? Command, RegistryValueKind Kind) ReadCommand(RegistryKey? key, string name)
    {
        if (key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command || string.IsNullOrWhiteSpace(command))
            return (null, RegistryValueKind.Unknown);
        var kind = key.GetValueKind(name);
        return kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? (command, kind) : (null, RegistryValueKind.Unknown);
    }
}
