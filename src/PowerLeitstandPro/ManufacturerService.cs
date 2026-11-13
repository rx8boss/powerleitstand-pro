using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace PowerControlProWpf;

internal sealed class ManufacturerService : IManufacturerService
{
    private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly Func<ManufacturerHardware> _readHardware;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ManufacturerDisk>>> _readDisks;
    internal ManufacturerService(HttpClient? http = null, string? downloadRoot = null, Func<ManufacturerHardware>? readHardware = null,
        Func<CancellationToken, Task<IReadOnlyList<ManufacturerDisk>>>? readDisks = null)
    {
        _http = http ?? SharedHttp;
        _root = downloadRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLeitstand PRO", "ManufacturerDownloads");
        _readHardware = readHardware ?? ReadHardware;
        _readDisks = readDisks ?? ManufacturerDisks.ReadAsync;
    }
    public Task<IReadOnlyDictionary<string, ManufacturerState>> InspectAsync() => Task.Run<IReadOnlyDictionary<string, ManufacturerState>>(ReadInstalled);
    public async Task<bool> CheckWinGetHardwareAsync(ManufacturerApp app, CancellationToken token)
    {
        if (WinGetService.PackageId(app.Id) is null) return false;
        var hardware = await Task.Run(_readHardware, token);
        if (hardware.Architecture != Architecture.X64 || !hardware.ClientWindows || hardware.WindowsVersion < new Version(10, 0, 19044)) return false;
        if (app.Id == "intel") return hardware.SupportedBrowser && ManufacturerDownloadPolicy.IntelGraphicsSupported(hardware.Graphics);
        if (app.Id == "msi") return ManufacturerBoardPolicy.Candidate(app.Id, hardware);
        var disks = await _readDisks(token); token.ThrowIfCancellationRequested();
        return disks.Any(disk => ManufacturerSsdPolicy.Supports(app.Id, disk));
    }

    private static Dictionary<string, ManufacturerState> ReadInstalled()
    {
        var registrations = new List<(string Name, string Location)>(); var complete = true;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var id in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var key = uninstall!.OpenSubKey(id);
                    var name = key?.GetValue("DisplayName") as string;
                    if (!string.IsNullOrWhiteSpace(name)) registrations.Add((name, key?.GetValue("InstallLocation") as string ?? ""));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { complete = false; }
        }
        var result = new Dictionary<string, ManufacturerState>();
        foreach (var app in ManufacturerCatalog.Apps)
        {
            var package = app.MayBePackaged ? ManufacturerPackages.Inspect(app.Id) : null;
            if (package?.Installation == ManufacturerInstallState.Installed) { result[app.Id] = package; continue; }
            var matches = registrations.Where(x => app.Aliases.Any(alias => x.Name.Equals(alias, StringComparison.OrdinalIgnoreCase) || x.Name.StartsWith(alias + " ", StringComparison.OrdinalIgnoreCase))).ToArray();
            var folders = matches.Select(x => x.Location).Where(Path.IsPathFullyQualified)
                .Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }).Where(x => !string.IsNullOrWhiteSpace(x));
            var executable = folders.SelectMany(folder => app.RelativeExecutables.Select(name => Path.Combine(folder, name)))
                .Where(path => Path.GetFileName(path).EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).Contains("Server", StringComparison.OrdinalIgnoreCase)).FirstOrDefault(File.Exists);
            // A background service is not an application launch target.
            if (executable is not null && Path.GetFileName(executable).Contains("Server", StringComparison.OrdinalIgnoreCase)) executable = null;
            result[app.Id] = executable is not null ? new(ManufacturerInstallState.Installed, executable)
                : matches.Length > 0 ? new(ManufacturerInstallState.NoLaunchTarget)
                : package is not null && package.Installation != ManufacturerInstallState.Missing ? package
                : new(!complete ? ManufacturerInstallState.Unknown : ManufacturerInstallState.Missing);
        }
        return result;
    }

    internal static bool IsAllowedDownload(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("us.download.nvidia.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/nvapp/client/", StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(uri.AbsolutePath).StartsWith("NVIDIA_app_", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.Contains("arm", StringComparison.OrdinalIgnoreCase);

    internal static Uri? ParseNvidiaDownload(string html)
    {
        var links = Regex.Matches(html, "href\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))
            .Select(match => WebUtility.HtmlDecode(match.Groups["url"].Value))
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && IsAllowedDownload(uri)).Select(uri => uri!).Distinct().ToArray();
        return links.Length == 1 ? links[0] : null;
    }

    internal static bool NvidiaModelSupported(string graphics, string cpu, ulong ram, Version? driver) =>
        Regex.IsMatch(graphics, @"\bGeForce\s+RTX\s+(20|30|40|50)\d{2}\b", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(cpu, @"\b(Core.*i[3579]|Pentium.*G\d|Ryzen\s+(3|5|7|9)|Threadripper|AMD.*FX)", RegexOptions.IgnoreCase) &&
        ram >= 2UL * 1024 * 1024 * 1024 && driver is not null && driver >= new Version(551, 52);

    private static Version? ReadNvidiaDriver()
    {
        try
        {
            using var video = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\VIDEO");
            foreach (var name in video?.GetValueNames() ?? [])
            {
                var path = video!.GetValue(name)?.ToString() ?? "";
                const string prefix = @"\Registry\Machine\";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                using var key = Registry.LocalMachine.OpenSubKey(path[prefix.Length..]);
                if (!(key?.GetValue("DriverDesc")?.ToString() ?? "").Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) continue;
                if (Version.TryParse(key?.GetValue("DriverVersion")?.ToString(), out var version))
                { var encoded = version.Build % 10 * 10000 + version.Revision; return new(encoded / 100, encoded % 100); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }

    private async Task<string> ReadPage(Uri uri, CancellationToken token)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        if (uri.Host is not ("www.nvidia.com" or "www.amd.com" or "www.kingston.com" or "semiconductor.samsung.com" or "www.asus.com" or "www.msi.com" or "www.gigabyte.com") || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new IOException("Die Downloadquelle ist nicht freigegeben.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd(AppVersion.UserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new IOException("Die Seitenantwort ist zu groß.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var copy = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await OnlineConnection.ReadAsync(stream, buffer, token)) != 0)
        { if (copy.Length + count > 4 * 1024 * 1024) throw new IOException("Die Seitenantwort ist zu groß."); copy.Write(buffer, 0, count); }
        return System.Text.Encoding.UTF8.GetString(copy.ToArray());
    }

    public async Task<ManufacturerDownload?> ResolveAsync(ManufacturerApp app, CancellationToken token)
    {
        if (!ManufacturerDownloadPolicy.HasAdapter(app.Id)) return null;
        if (app.Id != "nvidia") return await ResolveAdditionalAsync(app, token);
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !OperatingSystem.IsWindowsVersionAtLeast(10)) return null;
        var hardware = await Task.Run(PcHardwareInfo.Read, token);
        var modules = await Task.Run(RamHardwareInfo.Read, token);
        var ram = modules.Modules.Aggregate(0UL, (sum, module) => sum + (module.Bytes ?? 0));
        if (!NvidiaModelSupported(hardware.Graphics, hardware.CpuModel, ram, ReadNvidiaDriver())) return null;
        if (new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace < 600L * 1024 * 1024) return null;
        // Re-read official requirements at click time. A changed hardware policy must not silently pass.
        var requirements = Regex.Replace(WebUtility.HtmlDecode(await ReadPage(new("https://www.nvidia.com/en-us/software/nvidia-app/system-requirements/"), token)), "<[^>]+>", " ");
        if (!Regex.IsMatch(requirements, @"GeForce\s+RTX\s+20,\s*30,\s*40,\s*(and\s*)?50\s+Series", RegexOptions.IgnoreCase)) return null;
        var uri = ParseNvidiaDownload(await ReadPage(new(app.Website), token));
        return uri is null ? null : new(uri, Path.GetFileName(uri.AbsolutePath), hardware.Graphics);
    }

    private static ManufacturerHardware ReadHardware()
    {
        var hardware = PcHardwareInfo.Read();
        using var options = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
        var client = string.Equals(options?.GetValue("ProductType") as string, "WinNT", StringComparison.OrdinalIgnoreCase);
        var browser = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
            .Select(Environment.GetFolderPath).Where(path => !string.IsNullOrWhiteSpace(path))
            .Any(folder => new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe", @"Mozilla Firefox\firefox.exe" }
                .Any(relative => File.Exists(Path.Combine(folder, relative))));
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return new(RuntimeInformation.OSArchitecture, Environment.OSVersion.Version, client, hardware.Graphics, browser,
            (bios?.GetValue("BaseBoardManufacturer")?.ToString() ?? "").Trim(), (bios?.GetValue("BaseBoardProduct")?.ToString() ?? "").Trim());
    }

    private async Task<ManufacturerDownload?> ResolveAdditionalAsync(ManufacturerApp app, CancellationToken token)
    {
        var hardware = await Task.Run(_readHardware, token);
        // Deliberately conservative: client Windows 10 21H2+ / Windows 11, x64 only.
        if (hardware.Architecture != Architecture.X64 || !hardware.ClientWindows || hardware.WindowsVersion < new Version(10, 0, 19044)) return null;
        if (app.Id is "asus" or "msi" or "gigabyte")
        {
            if (!ManufacturerBoardPolicy.Candidate(app.Id, hardware)) return null;
            if (app.Id == "asus")
            {
                var compatibility = ManufacturerDownloadPolicy.PlainText(await ReadPage(new("https://www.asus.com/support/faq/1042459/"), token));
                if (!compatibility.Contains("All other ASUS motherboards", StringComparison.OrdinalIgnoreCase) || !compatibility.Contains("X570 series", StringComparison.OrdinalIgnoreCase)) return null;
                return ManufacturerBoardPolicy.ParseAsus(await ReadPage(new(ManufacturerBoardPolicy.AsusDrivers), token), hardware.BoardModel);
            }
            if (app.Id == "msi")
            {
                var source = new Uri(ManufacturerDownloadPolicy.MsiInstaller);
                using var metadata = await GetBinary(app.Id, source, token, HttpMethod.Head);
                if (metadata.Content.Headers.ContentType?.MediaType is not ("application/zip" or "application/x-zip-compressed" or "application/octet-stream")) return null;
                return new(source, Path.GetFileName(source.AbsolutePath), hardware.BoardModel);
            }
            if (app.Id == "gigabyte" && !ManufacturerBoardPolicy.ListedGigabyteBoard(hardware.BoardModel,
                await ReadPage(new("https://www.gigabyte.com/WebPage/942/noLayout/table.html"), token))) return null;
            var page = app.Id == "gigabyte" ? "https://www.gigabyte.com/Support/Utility?kw=GIGABYTE+Control+Center&p=1" : app.Website;
            var download = ManufacturerDownloadPolicy.ParseDownload(app.Id, await ReadPage(new(page), token));
            return download is null ? null : new(download, Path.GetFileName(download.AbsolutePath), hardware.BoardModel);
        }
        if (app.Id is "kingston" or "samsung" or "sandisk")
        {
            var disks = await _readDisks(token); token.ThrowIfCancellationRequested();
            var disk = disks.FirstOrDefault(item => ManufacturerSsdPolicy.Supports(app.Id, item));
            if (disk is null) return null;
            Uri? download;
            if (app.Id == "sandisk")
            {
                download = new(ManufacturerDownloadPolicy.SandiskInstaller);
                using var metadata = await GetBinary(app.Id, download, token, HttpMethod.Head);
                if (metadata.Content.Headers.ContentType?.MediaType is not ("application/octet-stream" or "application/x-msdos-program" or "application/x-msdownload")) return null;
            }
            else download = ManufacturerDownloadPolicy.ParseDownload(app.Id, await ReadPage(new(app.Website), token));
            return download is null ? null : new(download, Path.GetFileName(download.AbsolutePath), disk.Model);
        }
        if (app.Id == "intel")
        {
            if (!hardware.SupportedBrowser || !ManufacturerDownloadPolicy.IntelGraphicsSupported(hardware.Graphics)) return null;
            var source = new Uri(ManufacturerDownloadPolicy.IntelInstaller);
            // Intel's official stable endpoint serves the current installer. HEAD reads no executable body.
            using var metadata = await GetBinary(app.Id, source, token, HttpMethod.Head);
            var name = metadata.Content.Headers.ContentDisposition?.FileNameStar ?? metadata.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            if (!string.Equals(name, ManufacturerDownloadPolicy.IntelFileName, StringComparison.OrdinalIgnoreCase)) return null;
            return new(source, ManufacturerDownloadPolicy.IntelFileName, hardware.Graphics);
        }
        var uri = ManufacturerDownloadPolicy.ParseDownload("amd", await ReadPage(new(app.Website), token));
        if (uri is null) return null;
        var release = await ReadPage(ManufacturerDownloadPolicy.AmdReleaseNotes(uri), token);
        var model = ManufacturerDownloadPolicy.AmdSupportedGraphics(hardware.Graphics, release);
        return model is null ? null : new(uri, Path.GetFileName(uri.AbsolutePath), model);
    }

    private async Task<HttpResponseMessage> GetBinary(string id, Uri source, CancellationToken token, HttpMethod? method = null)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (!ManufacturerDownloadPolicy.Allows(id, source)) throw new IOException("Die Downloadquelle ist nicht freigegeben.");
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, source);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { var next = new Uri(source, location); response.Dispose(); source = next; continue; }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new IOException("Zu viele Downloadweiterleitungen.");
    }

    public async Task<string> DownloadAsync(ManufacturerApp app, ManufacturerDownload download, IProgress<ManufacturerProgress> progress, CancellationToken token)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        if (!ManufacturerDownloadPolicy.Allows(app.Id, download.Source)) throw new IOException("Die Downloadquelle ist nicht freigegeben.");
        if (app.Id == "asus" && download.Sha256 is null) throw new IOException("Die Paketprüfsumme stimmt nicht überein.");
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var archive = download.Source.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var file = Path.Combine(folder, archive ? "package.zip" : "installer.exe");
        try
        {
            using var response = await GetBinary(app.Id, download.Source, token);
            var total = response.Content.Headers.ContentLength;
            if (total is > 2L * 1024 * 1024 * 1024) throw new IOException("Die Downloaddatei ist zu groß.");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536]; long bytes = 0; int count; var lastReport = Stopwatch.StartNew();
                while ((count = await OnlineConnection.ReadAsync(source, buffer, token)) > 0)
                {
                    bytes += count;
                    if (bytes > 2L * 1024 * 1024 * 1024) throw new IOException("Die Downloaddatei ist zu groß.");
                    await target.WriteAsync(buffer.AsMemory(0, count), token);
                    if (lastReport.ElapsedMilliseconds >= 80 || bytes == total)
                    { progress.Report(new("download", bytes, total)); lastReport.Restart(); }
                }
                if (bytes == 0 || total is > 0 && bytes != total) throw new IOException("Der Herstellerdownload ist unvollständig.");
            }
            progress.Report(new("verify"));
            if (download.Sha256 is { } expected)
            {
                await using var package = File.OpenRead(file);
                var actual = await SHA256.HashDataAsync(package, token);
                if (!Regex.IsMatch(expected, @"\A[0-9A-Fa-f]{64}\z") || !CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
                    throw new IOException("Die Paketprüfsumme stimmt nicht überein.");
            }
            if (archive) file = await ManufacturerArchive.ExtractAsync(app.Id, file, Path.Combine(folder, "setup"), token);
            if (!await Task.Run(() => ManufacturerSignature.Verify(file, app.Publisher), token)) throw new IOException("Die Herstellersignatur konnte nicht bestätigt werden.");
            token.ThrowIfCancellationRequested(); return file;
        }
        catch { Clean(file); throw; }
    }

    public async Task LaunchAsync(ManufacturerApp app, string file, bool installer, Action? onStarted = null)
    {
        if (!installer && file.StartsWith("appx:", StringComparison.Ordinal))
        { ManufacturerPackages.Activate(app.Id, file); onStarted?.Invoke(); return; }
        if (installer && !Path.GetFullPath(file).StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Der Installationspfad ist nicht freigegeben.");
        try
        {
            using var fileLock = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file) || !await Task.Run(() => ManufacturerSignature.Verify(file, app.Publisher)))
                throw new IOException("Die Herstellersignatur konnte nicht bestätigt werden.");
            using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) });
            if (process is null) throw new IOException("Programm konnte nicht gestartet werden.");
            onStarted?.Invoke();
            if (installer) await process.WaitForExitAsync();
        }
        finally { if (installer) Clean(file); }
    }

    private void Clean(string file)
    {
        try
        {
            var root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            var bucket = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar)[0];
            if (!Guid.TryParseExact(bucket, "N", out _)) return;
            var folder = Path.GetFullPath(Path.Combine(root, bucket));
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0) Directory.Delete(folder, true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void OpenWebsite(ManufacturerApp app) { OnlineConnection.RequireNetwork(); Process.Start(new ProcessStartInfo(app.ProductPage) { UseShellExecute = true }); }
}
