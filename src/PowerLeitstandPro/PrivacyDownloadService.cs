using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;

namespace PowerControlProWpf;

internal sealed record PrivacyProgress(long Received, long? Total);
internal interface IPrivacyDownloadService
{
    string DownloadsDirectory { get; }
    Task<string> DownloadAsync(bool licenseAccepted, IProgress<PrivacyProgress> progress, CancellationToken token);
    Task OpenAsync(string path);
}

internal sealed class PrivacyDownloadService : IPrivacyDownloadService
{
    internal const string CurrentPage = "https://www.oo-software.com/en/download/current/ooshutup10";
    internal const string ExpectedPublisher = "O&O Software GmbH";
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "www.oo-software.com", "dl5.oo-software.com" };
    private readonly Func<string, string, bool> _verify;
    private readonly Func<CancellationToken, Task<Uri>> _resolve;
    private readonly Func<Uri, CancellationToken, Task<System.Net.Http.HttpResponseMessage>> _download;
    public string DownloadsDirectory { get; }
    internal PrivacyDownloadService(string? directory = null, Func<string, string, bool>? verify = null,
        Func<CancellationToken, Task<Uri>>? resolve = null,
        Func<Uri, CancellationToken, Task<System.Net.Http.HttpResponseMessage>>? download = null)
    {
        DownloadsDirectory = directory ?? KnownDownloads(); _verify = verify ?? ManufacturerSignature.Verify;
        _resolve = resolve ?? ResolveAsync; _download = download ?? ((uri, token) => CatalogWeb.OpenAsync(uri, Hosts, token));
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
    private static string KnownDownloads()
    {
        var folder = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(ref folder, 0, IntPtr.Zero, out var pointer);
        try { Marshal.ThrowExceptionForHR(result); return Marshal.PtrToStringUni(pointer) ?? throw new IOException("Downloads folder unavailable"); }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }
    internal static Uri ResolvePage(string html)
    {
        foreach (Match match in Regex.Matches(html, "<a\\b[^>]*href=[\"'](?<url>https://dl5\\.oo-software\\.com/files/ooshutup10/[^\"'<>]+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
        {
            var uri = new Uri(WebUtility.HtmlDecode(match.Groups["url"].Value));
            var label = Regex.Replace(match.Groups["text"].Value, "<[^>]+>", " ");
            var file = Path.GetFileName(uri.AbsolutePath);
            if (CatalogWeb.IsAllowed(uri, Hosts) && !label.Contains("ARM", StringComparison.OrdinalIgnoreCase)
                && (label.Contains("64", StringComparison.Ordinal) || file.Equals("OOSU10.exe", StringComparison.OrdinalIgnoreCase))
                && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !file.Contains("32", StringComparison.Ordinal) && !file.Contains("arm", StringComparison.OrdinalIgnoreCase)) return uri;
        }
        throw new InvalidDataException("Aktueller x64-Download nicht eindeutig bestätigt. / Current x64 download not confirmed.");
    }
    internal static async Task<Uri> ResolveAsync(CancellationToken token)
    {
        var html = await CatalogWeb.ReadAsync(new(CurrentPage), Hosts, 2 * 1024 * 1024, token);
        return ResolvePage(Encoding.UTF8.GetString(html));
    }
    public async Task<string> DownloadAsync(bool licenseAccepted, IProgress<PrivacyProgress> progress, CancellationToken token)
    {
        using var connection = new OnlineRequest(token); token = connection.Token;
        if (!licenseAccepted) throw new InvalidOperationException("Lizenz zuerst bestätigen. / Accept the license first.");
        var uri = await _resolve(token);
        if (!CatalogWeb.IsAllowed(uri, Hosts) || !uri.AbsolutePath.StartsWith("/files/ooshutup10/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unapproved download source");
        using var response = await _download(uri, token); response.EnsureSuccessStatusCode();
        const long maximum = 256L * 1024 * 1024;
        var total = response.Content.Headers.ContentLength;
        if (total > maximum) throw new InvalidDataException("Download exceeds size limit");
        Directory.CreateDirectory(DownloadsDirectory);
        var stem = "OOSU10-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var target = Path.Combine(DownloadsDirectory, stem + ".exe"); var partial = target + ".partial";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long received = 0; int count;
                while ((count = await OnlineConnection.ReadAsync(input, buffer, token)) > 0)
                {
                    received += count; if (received > maximum) throw new InvalidDataException("Download exceeds size limit");
                    await output.WriteAsync(buffer.AsMemory(0, count), token); progress.Report(new(received, total is > 0 ? total : null));
                }
                if (received < 1024 || (total.HasValue && received != total.Value)) throw new InvalidDataException("Incomplete download");
            }
            token.ThrowIfCancellationRequested();
            // No executable is opened before Authenticode trust and the expected organization are confirmed.
            if (!await Task.Run(() => _verify(partial, ExpectedPublisher), token)) throw new InvalidDataException("Signatur/Herausgeber nicht bestätigt. / Signature/publisher not confirmed.");
            token.ThrowIfCancellationRequested(); File.Move(partial, target); return target;
        }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    public async Task OpenAsync(string path)
    {
        var directory = Path.GetFullPath(DownloadsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(directory, StringComparison.OrdinalIgnoreCase) || !await Task.Run(() => _verify(path, ExpectedPublisher))) throw new InvalidDataException("Signature/publisher not confirmed");
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) ?? throw new IOException("O&O konnte nicht geöffnet werden. / Could not open O&O.");
        await process.WaitForExitAsync();
    }
}
