using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Microsoft.Management.Deployment;

namespace PowerControlProWpf;

internal sealed record ProgramResolvedIcon(BitmapSource Image, string Source);

internal static class ProgramIconFallback
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, token) =>
        {
            foreach (var address in (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token)).Where(ProgramIconSources.IsPublic))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); }
            }
            throw new IOException("No public icon endpoint");
        }
    }) { Timeout = TimeSpan.FromSeconds(5) };
    private static int _apiUnavailable;
    private static readonly SemaphoreSlim ApiGate = new(1);
    internal static bool PublicUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && !uri.IsLoopback
        && uri.IdnHost.Contains('.') && !IPAddress.TryParse(uri.Host, out _);

    // Metadata only: this method never downloads an installer or starts a package operation.
    internal static async Task<string[]> WinGetIconsAsync(string? id, CancellationToken token)
    {
        if (id is null || Volatile.Read(ref _apiUnavailable) != 0) return [];
        await ApiGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _apiUnavailable) != 0) return [];
            return await Task.Run(async () =>
            {
                var manager = new PackageManager();
                var reference = manager.GetPackageCatalogByName("winget");
                if (reference is null) return Array.Empty<string>();
                var connection = await reference.ConnectAsync();
                if (connection.Status != ConnectResultStatus.Ok) return Array.Empty<string>();
                var options = new FindPackagesOptions { ResultLimit = 2 };
                options.Filters.Add(new PackageMatchFilter { Field = PackageMatchField.Id, Option = PackageFieldMatchOption.EqualsCaseInsensitive, Value = id });
                var result = connection.PackageCatalog.FindPackages(options);
                if (result.Status != FindPackagesResultStatus.Ok || result.WasLimitExceeded || result.Matches.Count != 1) return Array.Empty<string>();
                var package = result.Matches[0].CatalogPackage;
                if (!string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
                return package.DefaultInstallVersion?.GetCatalogPackageMetadata()?.Icons
                    .Select(icon => icon.Url).Where(PublicUrl).Distinct().Take(3).ToArray() ?? [];
            }, token).WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or DllNotFoundException or TypeLoadException
            or PlatformNotSupportedException or TimeoutException or IOException or InvalidOperationException)
        { Interlocked.Exchange(ref _apiUnavailable, 1); return []; }
        finally { ApiGate.Release(); }
    }
    internal static async Task<byte[]> ReadPublicAsync(Uri uri, int maximum, CancellationToken token)
    {
        using var network = new OnlineRequest(token);
        for (int hop = 0; hop < 5; hop++)
        {
            if (!PublicUrl(uri.AbsoluteUri)) throw new IOException("Invalid publisher icon URL");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(AppVersion.UserAgent);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, network.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            { uri = new Uri(uri, location); continue; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximum) throw new IOException("Icon source too large");
            await using var stream = await response.Content.ReadAsStreamAsync(network.Token);
            using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, network.Token)) > 0)
            { if (memory.Length + count > maximum) throw new IOException("Icon source too large"); memory.Write(buffer, 0, count); }
            return memory.ToArray();
        }
        throw new IOException("Too many icon redirects");
    }
    internal static IEnumerable<Uri> HtmlIcons(Uri website, string html)
    {
        foreach (Match link in Regex.Matches(html, @"<link\b[^>]{0,4096}>", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(150)))
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attr in Regex.Matches(link.Value, "(?<key>[a-zA-Z-]+)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))"))
                attrs[attr.Groups["key"].Value] = WebUtility.HtmlDecode(attr.Groups["value"].Value);
            string rel = attrs.GetValueOrDefault("rel", "");
            if (!rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(x => x.Equals("icon", StringComparison.OrdinalIgnoreCase)
                || x.Equals("apple-touch-icon", StringComparison.OrdinalIgnoreCase))) continue;
            if (attrs.TryGetValue("href", out var href) && Uri.TryCreate(website, href, out var url) && PublicUrl(url.AbsoluteUri)
                && !url.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) yield return url;
        }
    }
    internal static async Task<ProgramResolvedIcon?> ResolveAsync(ProgramEntry entry, string? website, CancellationToken token,
        Func<string?, CancellationToken, Task<string[]>>? metadata = null,
        Func<Uri, int, CancellationToken, Task<byte[]>>? read = null)
    {
        read ??= ReadPublicAsync; metadata ??= WinGetIconsAsync;
        async Task<ProgramResolvedIcon?> TryImage(Uri uri, string source)
        {
            try { return new(ProgramAssets.Decode(await read(uri, ProgramAssets.MaxImageBytes, token).ConfigureAwait(false), 64), source); }
            catch (Exception e) when (Recoverable(e)) { token.ThrowIfCancellationRequested(); return null; }
        }
        try
        {
            foreach (var url in (await metadata(entry.PackageId, token).ConfigureAwait(false)).Where(PublicUrl).Take(3))
                if (await TryImage(new(url), "WinGet") is { } image) return image;
        }
        catch (Exception e) when (Recoverable(e)) { token.ThrowIfCancellationRequested(); }
        if (!PublicUrl(website)) return null;
        var page = new Uri(website!); var candidates = new List<Uri>();
        try
        {
            var html = System.Text.Encoding.UTF8.GetString(await read(page, 256 * 1024, token).ConfigureAwait(false));
            candidates.AddRange(HtmlIcons(page, html).Distinct().Take(4));
        }
        catch (Exception e) when (Recoverable(e)) { token.ThrowIfCancellationRequested(); }
        candidates.Add(new(page.GetLeftPart(UriPartial.Authority) + "/favicon.ico"));
        foreach (var url in candidates.Distinct()) if (await TryImage(url, "Hersteller") is { } image) return image;
        return null;
    }
    internal static bool Recoverable(Exception e) => e is IOException or HttpRequestException or OperationCanceledException
        or FormatException or NotSupportedException or ArgumentException or InvalidOperationException
        or System.Runtime.InteropServices.COMException or RegexMatchTimeoutException;
}
