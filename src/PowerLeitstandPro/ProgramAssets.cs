using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;

namespace PowerControlProWpf;

// Only catalogued HTTPS assets may be requested. Redirects cannot escape the approved hosts.
internal static class CatalogWeb
{
    internal static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(25), DefaultRequestHeaders = { { "User-Agent", "PowerLeitstand-PRO/1.1" } } };

    internal static bool IsAllowed(Uri uri, ISet<string> hosts) => uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && hosts.Contains(uri.IdnHost);

    internal static async Task<HttpResponseMessage> OpenAsync(Uri uri, ISet<string> hosts, CancellationToken token)
    {
        OnlineConnection.RequireNetwork();
        for (int hop = 0; hop < 5; hop++)
        {
            if (!IsAllowed(uri, hosts)) throw new HttpRequestException("Unapproved HTTPS source");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new() { NoStore = true };
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); response.Dispose(); continue; }
            try { response.EnsureSuccessStatusCode(); return response; } catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Too many redirects");
    }
    internal static async Task<byte[]> ReadAsync(Uri uri, ISet<string> hosts, int maxBytes, CancellationToken token)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        using var response = await OpenAsync(uri, hosts, token);
        if (response.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("Image too large");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var target = new MemoryStream(); var buffer = new byte[16384]; int count;
        while ((count = await OnlineConnection.ReadAsync(source, buffer, token)) > 0)
        {
            if (target.Length + count > maxBytes) throw new InvalidDataException("Image too large");
            target.Write(buffer, 0, count);
        }
        return target.ToArray();
    }
}

internal sealed class ProgramAssets
{
    internal const int MaxImageBytes = 4 * 1024 * 1024;
    private readonly string _directory;
    private readonly SemaphoreSlim _downloads = new(4);
    private readonly Dictionary<string, BitmapSource> _icons = [];
    private readonly Dictionary<string, string> _sources = [];
    private readonly Dictionary<string, (ProgramEntry Entry, string? Url)> _dynamic = [];
    private readonly Dictionary<string, string> _websites = [];
    private readonly Func<Uri, CancellationToken, Task<byte[]>> _read;
    private readonly Func<ProgramEntry, string?, CancellationToken, Task<ProgramResolvedIcon?>> _fallback;
    private readonly object _requestGate = new();
    private readonly Dictionary<string, Task> _pending = [];
    private readonly Dictionary<string, DateTime> _nextAttempt = [];
    internal string MarksFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_directory))!, "Program Installations.json");
    internal string LocationsFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_directory))!, "Program File Locations.json");
    internal string PortableFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_directory))!, "Portable Programs.json");
    internal string InspectionFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_directory))!, "Program Inspection.json");
    internal bool AllowOnlineIcons { get; set; } = true;
    internal event Action<string>? IconChanged;
    internal Action<string>? Diagnostic { get; set; }
    // directory is retained only to migrate old caches and locate the independent inventory files.
    // Icons live in RAM for this session. No browser image cache or local EXE extraction is used.
    internal ProgramAssets(string directory, Func<Uri, CancellationToken, Task<byte[]>>? read = null, bool readLocalIcons = true,
        Func<ProgramEntry, string?, CancellationToken, Task<ProgramResolvedIcon?>>? fallback = null)
    {
        _directory = Path.GetFullPath(directory);
        _read = read ?? ProgramIconSources.ReadAsync;
        _fallback = fallback ?? (read is null ? (entry, website, token) => ProgramIconFallback.ResolveAsync(entry, website, token)
            : (_, _, _) => Task.FromResult<ProgramResolvedIcon?>(null));
        RemoveLegacyCache();
    }
    internal BitmapSource? Icon(string id) { lock (_icons) return _icons.GetValueOrDefault(id); }
    internal string? IconSource(string id) { lock (_icons) return _sources.GetValueOrDefault(id); }
    internal static BitmapSource Decode(byte[] bytes, int width)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 64000000)
            throw new InvalidDataException("Invalid source image dimensions");
        stream.Position = 0;
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        if (bitmap.PixelHeight > 4096 || bitmap.PixelWidth > 4096) throw new InvalidDataException("Invalid image dimensions");
        return bitmap;
    }
    internal Task WarmupAsync() => Task.WhenAll(ProgramCatalog.Entries.Select(EnsureIconAsync));
    internal Task RetryMissingAsync()
    {
        lock (_requestGate) foreach (var id in _nextAttempt.Keys.ToArray()) if (Icon(id) is null) _nextAttempt.Remove(id);
        return WarmupAsync();
    }
    internal void RegisterIcon(ProgramEntry entry, string? url, string? website = null)
    {
        if (!entry.Id.StartsWith("winget:", StringComparison.Ordinal) || entry.PackageId is null
            || url is not null && !ProgramIconSources.IsGoogleIcon(url)) return;
        lock (_requestGate)
        {
            _dynamic[entry.Id] = (entry, url!);
            if (ProgramIconFallback.PublicUrl(website)) _websites[entry.Id] = website!;
            else if (url is not null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(new Uri(url!).Query, @"(?:[?&])domain_url=([^&]+)");
                var origin = Uri.UnescapeDataString(match.Groups[1].Value);
                if (ProgramIconFallback.PublicUrl(origin)) _websites[entry.Id] = origin;
            }
        }
    }
    internal async Task CleanupAsync()
    {
        Task[] tasks; lock (_requestGate) tasks = _pending.Values.ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        RemoveLegacyCache();
    }
    private void RemoveLegacyCache()
    {
        try
        {
            if (!Directory.Exists(_directory)) return;
            // Do not follow junctions in the cache path or delete arbitrary files.
            for (DirectoryInfo? parent = new(_directory); parent is not null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var root = new DirectoryInfo(_directory);
            foreach (var file in root.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool known = ProgramCatalog.Entries.Any(e => System.Text.RegularExpressions.Regex.IsMatch(file.Name,
                    "^" + System.Text.RegularExpressions.Regex.Escape(e.Id) + @"-\d{4}-\d{2}-\d{2}[a-z]?\.png(?:\.[a-f0-9]{32}\.tmp)?$"));
                bool online = System.Text.RegularExpressions.Regex.IsMatch(file.Name,
                    @"^online-[a-f0-9]{64}-session\.png(?:\.[a-f0-9]{32}\.tmp)?$");
                if (known || online) file.Delete();
            }
            if (!root.EnumerateFileSystemInfos().Any()) root.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Diagnostic?.Invoke("Legacy icon cache: " + e.Message); }
    }
    internal Task EnsureIconAsync(ProgramEntry entry)
    {
        if (!ProgramCatalog.Contains(entry)) { lock (_requestGate) if (!_dynamic.TryGetValue(entry.Id, out var known) || !ReferenceEquals(known.Entry, entry)) return Task.CompletedTask; }
        lock (_requestGate)
        {
            if (Icon(entry.Id) is not null || !AllowOnlineIcons) return Task.CompletedTask;
            if (_pending.TryGetValue(entry.Id, out var pending)) return pending;
            if (_nextAttempt.TryGetValue(entry.Id, out var next) && next > DateTime.UtcNow) return Task.CompletedTask;
            var task = Task.Run(async () =>
            {
                try { await LoadIconAsync(entry).ConfigureAwait(false); }
                finally { lock (_requestGate) _pending.Remove(entry.Id); }
            });
            _pending[entry.Id] = task; return task;
        }
    }
    private async Task LoadIconAsync(ProgramEntry entry)
    {
        await _downloads.WaitAsync();
        try
        {
            if (!AllowOnlineIcons) return;
            string? url = ProgramCatalog.Contains(entry) ? ProgramIconSources.Favicon(entry.Website) : null;
            if (url is null) { lock (_requestGate) if (_dynamic.TryGetValue(entry.Id, out var registered)) url = registered.Url; }
            ProgramResolvedIcon? resolved = null;
            if (url is not null) try
            {
                using var googleLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var bytes = await _read(new(url), googleLimit.Token).ConfigureAwait(false);
                resolved = new(await Task.Run(() => Decode(bytes, 64)).ConfigureAwait(false), "Google");
            }
            catch (Exception e) when (ProgramIconFallback.Recoverable(e)) { Diagnostic?.Invoke(entry.Name + " Google: " + e.Message); }
            if (resolved is null && AllowOnlineIcons)
            {
                string? website = ProgramCatalog.Contains(entry) ? entry.Website : null;
                if (website is null) { lock (_requestGate) website = _websites.GetValueOrDefault(entry.Id); }
                using var fallbackLimit = new CancellationTokenSource(TimeSpan.FromSeconds(18));
                resolved = await _fallback(entry, website, fallbackLimit.Token).ConfigureAwait(false);
            }
            // One final bounded retry after the alternative sources, never an endless request loop.
            if (resolved is null && url is not null && AllowOnlineIcons)
            {
                try
                {
                    using var retryLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var bytes = await _read(new(url), retryLimit.Token).ConfigureAwait(false);
                    resolved = new(await Task.Run(() => Decode(bytes, 64)).ConfigureAwait(false), "Google");
                }
                catch (Exception e) when (ProgramIconFallback.Recoverable(e)) { Diagnostic?.Invoke(entry.Name + " retry: " + e.Message); }
            }
            if (resolved is null || !AllowOnlineIcons) return;
            lock (_icons) { _icons[entry.Id] = resolved.Image; _sources[entry.Id] = resolved.Source; }
            IconChanged?.Invoke(entry.Id);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException
            or FormatException or NotSupportedException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { Diagnostic?.Invoke(entry.Name + ": " + e.Message); /* The themed lightning remains visible. */ }
        finally
        {
            lock (_requestGate) _nextAttempt[entry.Id] = DateTime.UtcNow.AddMinutes(5);
            _downloads.Release();
        }
    }
}

