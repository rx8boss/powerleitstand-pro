using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using PowerControlProWpf;

internal static class ManufacturerAdapterTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<(HttpMethod Method, Uri Uri)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests.Add((request.Method, request.RequestUri!)); return Task.FromResult(respond(request)); }
    }
    internal static void Run(Action<bool, string> check, Action<Task> wait, string output)
    {
        const string amdUrl = "https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.8.1-minimalsetup-260818_web.exe";
        const string release = "<h2>Radeon Product Compatibility</h2><p>AMD Radeon RX 9070 / 9060 / 9050 Series Graphics</p><p>AMD Radeon RX 7900/7800/7700/7650/7600 Series Graphics</p><h2>Mobility Radeon Product Compatibility</h2>AMD Radeon RX 7900M";
        var amd = ManufacturerCatalog.Apps.Single(x => x.Id == "amd");
        var intel = ManufacturerCatalog.Apps.Single(x => x.Id == "intel");
        var snapshot = new ManufacturerHardware(Architecture.X64, new(10, 0, 22631), true, "AMD Radeon RX 7900 XTX", true);
        check(ManufacturerDownloadPolicy.ParseDownload("amd", $"<a href='{amdUrl}'>Download</a>")?.AbsoluteUri == amdUrl, "AMD current auto-detect source parsed");
        check(ManufacturerDownloadPolicy.ParseDownload("amd", $"<a href='{amdUrl}'/><a href='{amdUrl.Replace("26.8.1", "26.9.1")}'/>") is null, "Ambiguous AMD sources rejected");
        foreach (var bad in new[] { amdUrl.Replace("https:", "http:"), amdUrl.Replace("drivers.amd.com", "drivers.amd.com.evil.test"), amdUrl.Replace("minimalsetup", "cleanup"), amdUrl + "?url=https://evil.test", amdUrl.Replace("drivers.amd.com", "evil.test@drivers.amd.com"), amdUrl.Replace("drivers.amd.com", "drivers.amd.com:444") })
            check(!ManufacturerDownloadPolicy.Allows("amd", new(bad)), "Wrong AMD product/source rejected");
        check(!ManufacturerDownloadPolicy.Allows("intel", new(amdUrl)), "Vendor sources cannot be crossed");
        check(!ManufacturerDownloadPolicy.Allows("intel", new("https://downloadmirror.intel.com/1/Other-Installer.exe")), "Intel unrelated product rejected");
        check(ManufacturerDownloadPolicy.Allows("intel", new(ManufacturerDownloadPolicy.IntelInstaller)), "Official Intel stable endpoint accepted");
        check(ManufacturerDownloadPolicy.Allows("intel", new("https://downloadmirror.intel.com/12345/Intel-Driver-and-Support-Assistant-Installer.exe")), "Intel exact product redirect accepted");
        check(ManufacturerDownloadPolicy.AmdReleaseNotes(new(amdUrl)).AbsolutePath.EndsWith("RN-RAD-WIN-26-8-1.html"), "Requirements follow resolved release version");
        check(ManufacturerDownloadPolicy.AmdSupportedGraphics(snapshot.Graphics, release) is not null, "Desktop RX model matches release matrix");
        foreach (var graphics in new[] { "AMD Ryzen 9 7950X", "AMD Radeon RX 580", "AMD Radeon RX 7900M", "AMD Radeon RX 7500", "AMD Radeon Graphics", "" })
            check(ManufacturerDownloadPolicy.AmdSupportedGraphics(graphics, release) is null, "Unknown/mobile/CPU/legacy AMD device not assumed supported");
        check(ManufacturerDownloadPolicy.AmdSupportedGraphics(snapshot.Graphics, "RX 7900") is null, "Missing compatibility section fails closed");
        var officialPage = Path.GetFullPath(Path.Combine(output, "..", "Official-AMD-Drivers.html"));
        var officialNotes = Path.GetFullPath(Path.Combine(output, "..", "Official-AMD-Notes.html"));
        if (File.Exists(officialPage) && File.Exists(officialNotes))
        {
            var current = ManufacturerDownloadPolicy.ParseDownload("amd", File.ReadAllText(officialPage));
            check(current is not null, "Current official AMD HTML has one usable source");
            check(ManufacturerDownloadPolicy.AmdSupportedGraphics(snapshot.Graphics, File.ReadAllText(officialNotes)) is not null, "Current official AMD matrix confirms fixture model");
        }
        foreach (var graphics in new[] { "Intel(R) Arc(TM) A770 Graphics", "Intel Arc B580 Graphics", "Intel(R) Iris(R) Xe Graphics" })
            check(ManufacturerDownloadPolicy.IntelGraphicsSupported(graphics), "Known Intel graphics recognized");
        foreach (var graphics in new[] { "Intel Core i7-12700K", "Intel GMA 500", "Intel HD Graphics", "NVIDIA RTX 4080", "Intel Arc A770M", "" })
            check(!ManufacturerDownloadPolicy.IntelGraphicsSupported(graphics), "Unsupported or unknown Intel device not assumed supported");
        var handler = new Handler(request => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("release-notes") ? release : $"<a href='{amdUrl}'>Download</a>") });
        using var http = new HttpClient(handler);
        var service = new ManufacturerService(http, Path.Combine(output, "Adapters"), () => snapshot);
        var resolved = service.ResolveAsync(amd, CancellationToken.None); wait(resolved);
        check(resolved.Result?.Source.AbsoluteUri == amdUrl && handler.Requests.Count == 2 && handler.Requests.All(x => x.Method == HttpMethod.Get && x.Uri.Host == "www.amd.com"), "AMD resolves metadata only before confirmation");
        foreach (var unsupported in new[] { snapshot with { ClientWindows = false }, snapshot with { Architecture = Architecture.Arm64 }, snapshot with { WindowsVersion = new(10, 0, 17763) } })
        {
            var calls = handler.Requests.Count;
            var blocked = new ManufacturerService(http, null, () => unsupported).ResolveAsync(amd, CancellationToken.None); wait(blocked);
            check(blocked.Result is null && handler.Requests.Count == calls, "Unsupported platform issues no network request");
        }
        var intelHardware = snapshot with { Graphics = "Intel(R) Arc(TM) A770 Graphics" };
        var intelHandler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = ManufacturerDownloadPolicy.IntelFileName };
            return response;
        });
        using var intelHttp = new HttpClient(intelHandler);
        var intelResolved = new ManufacturerService(intelHttp, null, () => intelHardware).ResolveAsync(intel, CancellationToken.None); wait(intelResolved);
        check(intelResolved.Result?.Source.AbsoluteUri == ManufacturerDownloadPolicy.IntelInstaller && intelHandler.Requests.Single().Method == HttpMethod.Head, "Intel HEAD does not transfer executable");
        var noBrowser = new ManufacturerService(intelHttp, null, () => intelHardware with { SupportedBrowser = false }).ResolveAsync(intel, CancellationToken.None); wait(noBrowser);
        check(noBrowser.Result is null && intelHandler.Requests.Count == 1, "Intel requires supported browser before request");
        using var wrongMetadataHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("not an installer") }));
        var wrongMetadata = new ManufacturerService(wrongMetadataHttp, null, () => intelHardware).ResolveAsync(intel, CancellationToken.None); wait(wrongMetadata);
        check(wrongMetadata.Result is null, "Missing product filename blocks Intel availability");
        foreach (var product in new[] { amd, intel })
        {
            var source = product.Id == "amd" ? amdUrl : ManufacturerDownloadPolicy.IntelInstaller;
            var binaryHandler = new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
            using var binaryHttp = new HttpClient(binaryHandler); var root = Path.Combine(output, "Invalid-" + product.Id);
            try { wait(new ManufacturerService(binaryHttp, root).DownloadAsync(product, new(new(source), "ignored.exe", "Fixture"), new Progress<ManufacturerProgress>(), CancellationToken.None)); throw new Exception("Unsigned fixture accepted"); }
            catch (IOException) { check(binaryHandler.Requests.Count == 1 && !Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(), "Unsigned vendor file rejected and cleaned"); }
            var redirectHandler = new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://evil.test/Installer.exe"); return r; });
            using var redirectHttp = new HttpClient(redirectHandler);
            try { wait(new ManufacturerService(redirectHttp, root).DownloadAsync(product, new(new(source), "ignored.exe", "Fixture"), new Progress<ManufacturerProgress>(), CancellationToken.None)); throw new Exception("Foreign redirect accepted"); }
            catch (IOException) { check(redirectHandler.Requests.Count == 1, "Foreign redirect blocked before next request"); }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { wait(service.ResolveAsync(amd, cancelled.Token)); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "Cancelled resolution does not transfer installer"); }
    }
}
