using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Xml;
using Windows.ApplicationModel.Core;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace PowerControlProWpf;

internal sealed record StartupPackagedApp(string Id, string Name, bool? HasNativeTask, AppListEntry? Entry = null)
{
    internal string LinkName => "PowerLeitstand-App-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id.ToUpperInvariant())))[..24] + ".lnk";
}

internal static class StartupPackagedApps
{
    private const string AppsFolder = @"shell:AppsFolder\";
    private const string AppsFolderParsingName = @"::{4234d49b-0245-4df3-b780-3893943456e1}\";
    internal static bool ValidId(string? id) => id is { Length: > 0 and <= 256 }
        && Regex.IsMatch(id, @"\A[A-Za-z0-9.-]+_[A-Za-z0-9]+![A-Za-z0-9.-]+\z");

    internal static bool ReadFailure(Exception error) => StartupApps.ReadFailure(error)
        || error is COMException or XmlException or InvalidOperationException or NotSupportedException
            or TypeLoadException or ArgumentException or TimeoutException or System.ComponentModel.Win32Exception;

    internal static async Task<(IReadOnlyList<StartupPackagedApp> Apps, bool Partial)> ReadAsync(CancellationToken token, string? appId = null)
    {
        var apps = new List<StartupPackagedApp>();
        bool partial = false;
        var manager = new PackageManager();
        var packages = appId is null ? manager.FindPackagesForUser(string.Empty)
            : manager.FindPackagesForUser(string.Empty, appId.Split('!')[0]);
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle || package.IsOptional) continue;
                var entries = await package.GetAppListEntriesAsync().AsTask(token).ConfigureAwait(false);
                if (entries.Count == 0) continue;
                bool? nativeTask = null;
                try { nativeTask = HasNativeTask(Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml")); }
                catch (Exception error) when (ReadFailure(error))
                {
                    nativeTask = await ReadRegisteredManifestAsync(package.Id.FullName, token).ConfigureAwait(false);
                    partial |= nativeTask is null;
                }
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    string id = entry.AppUserModelId;
                    if (!ValidId(id) || !id.StartsWith(package.Id.FamilyName + "!", StringComparison.OrdinalIgnoreCase)
                        || appId is not null && !id.Equals(appId, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = entry.DisplayInfo.DisplayName;
                    apps.Add(new(id, string.IsNullOrWhiteSpace(name) ? package.Id.Name : name, nativeTask, entry));
                }
            }
            catch (Exception error) when (ReadFailure(error)) { partial = true; }
        }
        // If multiple registrations disagree, do not create a shortcut for that app.
        return (apps.GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var first = group.First();
            return first with { HasNativeTask = group.Any(app => app.HasNativeTask == true) ? true
                : group.All(app => app.HasNativeTask == false) ? false : null };
        }).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), partial);
    }

    internal static bool HasNativeTask(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
        });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Extension"
                && string.Equals(reader.GetAttribute("Category"), "windows.startupTask", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<bool?> ReadRegisteredManifestAsync(string packageFullName, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        start.Environment["POWERLEITSTAND_STARTUP_PACKAGE"] = packageFullName;
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; $m=Get-AppxPackageManifest -Package $env:POWERLEITSTAND_STARTUP_PACKAGE; " +
            "if ($m.DocumentElement.LocalName -ne 'Package') { exit 1 }; " +
            "if ($m.SelectSingleNode(\"//*[local-name()='Extension' and @Category='windows.startupTask']\")) { 'native' } else { 'none' }" })
            start.ArgumentList.Add(arg);
        try
        {
            var result = await UserQueryProcess.ReadAsync(start, TimeSpan.FromSeconds(4)).WaitAsync(token).ConfigureAwait(false);
            return result.Trim() switch { "native" => true, "none" => false, _ => null };
        }
        catch (Exception error) when (ReadFailure(error)) { return null; }
    }

    internal static async Task<BitmapSource?> ReadIconAsync(StartupPackagedApp app, CancellationToken token)
    {
        if (app.Entry is null) return null;
        try
        {
            using var stream = await app.Entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(32, 32)).OpenReadAsync().AsTask(token).ConfigureAwait(false);
            if (stream.Size is 0 or > 2_000_000) return null;
            using var reader = new DataReader(stream);
            if (await reader.LoadAsync((uint)stream.Size).AsTask(token).ConfigureAwait(false) != stream.Size) return null;
            var bytes = new byte[(int)stream.Size];
            reader.ReadBytes(bytes);
            using var data = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = data;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 32;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (ReadFailure(error) || error is FormatException or OperationCanceledException) { return null; }
    }

    internal static void CreateLink(string path, string id)
    {
        if (!ValidId(id)) throw new IOException("Invalid Windows app identity.");
        Marshal.ThrowExceptionForHR(SHParseDisplayName(AppsFolder + id, IntPtr.Zero, out var item, 0, out _));
        object? shellLink = null;
        try
        {
            shellLink = NewShellLink();
            ((IShellLink)shellLink).SetIDList(item);
            ((IPersistFile)shellLink).Save(path, false);
        }
        finally
        {
            if (shellLink is not null) Marshal.FinalReleaseComObject(shellLink);
            Marshal.FreeCoTaskMem(item);
        }
    }

    internal static bool CanResolveTarget(string id)
    {
        if (!ValidId(id)) return false;
        IntPtr item = IntPtr.Zero;
        try
        {
            return SHParseDisplayName(AppsFolder + id, IntPtr.Zero, out item, 0, out _) >= 0
                && string.Equals(ReadAppId(item), id, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (ReadFailure(error)) { return false; }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    internal static string? ReadLinkAppId(string path)
    {
        object? shellLink = null;
        IntPtr item = IntPtr.Zero;
        try
        {
            shellLink = NewShellLink();
            ((IPersistFile)shellLink).Load(path, 0);
            ((IShellLink)shellLink).GetIDList(out item);
            return ReadAppId(item);
        }
        catch (Exception error) when (ReadFailure(error)) { return null; }
        finally
        {
            if (shellLink is not null) Marshal.FinalReleaseComObject(shellLink);
            Marshal.FreeCoTaskMem(item);
        }
    }

    private static string? ReadAppId(IntPtr item)
    {
        if (item == IntPtr.Zero) return null;
        IntPtr name = IntPtr.Zero;
        try
        {
            if (SHGetNameFromIDList(item, 0x80028000, out name) < 0) return null;
            string? parsingName = Marshal.PtrToStringUni(name);
            string? id = parsingName;
            if (parsingName?.StartsWith(AppsFolderParsingName, StringComparison.OrdinalIgnoreCase) == true)
                id = parsingName[AppsFolderParsingName.Length..];
            else if (parsingName?.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) == true)
                id = parsingName[AppsFolder.Length..];
            return ValidId(id) ? id : null;
        }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    private static object NewShellLink() => Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        // Slot 0 after IUnknown is GetPath. This code only calls the next two slots.
        void GetPath();
        void GetIDList(out IntPtr item);
        void SetIDList(IntPtr item);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr item, uint attributes, out uint result);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetNameFromIDList(IntPtr item, uint format, out IntPtr name);
}
