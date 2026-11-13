using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace PowerControlProWpf;

// Website metadata may identify a public publisher icon, never a local service.
internal static class ProgramIconSources
{
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xE0) == 0x20; // Global unicast only.
        return b[0] is not (0 or 10 or 127) && b[0] < 224 && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) && !(b[0] == 192 && b[1] == 168)
            && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }
    internal static string? Favicon(string? website)
    {
        if (!Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.IsLoopback || IPAddress.TryParse(uri.Host, out _)
            || !uri.IdnHost.Contains('.')) return null;
        // Send only the public site origin, never paths, queries or local file locations.
        return "https://www.google.com/s2/favicons?sz=64&domain_url="
            + Uri.EscapeDataString(uri.GetLeftPart(UriPartial.Authority));
    }
    internal static bool IsGoogleIcon(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.IdnHost == "www.google.com" && uri.AbsolutePath == "/s2/favicons";
    internal static Task<byte[]> ReadAsync(Uri uri, CancellationToken token)
    {
        if (!IsGoogleIcon(uri.AbsoluteUri)) throw new IOException("Unconfirmed Google icon source");
        // Google may redirect its icon response to its own image service.
        return CatalogWeb.ReadAsync(uri, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "www.google.com", "t0.gstatic.com", "t1.gstatic.com", "t2.gstatic.com", "t3.gstatic.com" },
            ProgramAssets.MaxImageBytes, token);
    }
}

