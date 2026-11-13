using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using PowerControlProWpf;

internal static class ManufacturerSsdTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls; internal HttpMethod? Method;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Method = request.Method; return Task.FromResult(respond(request)); }
    }
    internal static void Run(Action<bool, string> check, Action<Task> wait, string output)
    {
        const string kingston = "https://media.kingston.com/support/downloads/KSM_setup_1.5.6.5.exe";
        const string samsung = "https://download.semiconductor.samsung.com/resources/software-resources/Samsung_Magician_Installer_Official_9.0.1.950.exe";
        foreach (var (id, source) in new[] { ("kingston", kingston), ("samsung", samsung), ("sandisk", ManufacturerDownloadPolicy.SandiskInstaller) })
        {
            check(ManufacturerDownloadPolicy.Allows(id, new(source)), "SSD exact product source accepted");
            foreach (var invalid in new[] { source.Replace("https:", "http:"), source + "?redirect=1", source.Replace(new Uri(source).Host, "evil.test"), source.Replace(".exe", ".zip") })
                check(!ManufacturerDownloadPolicy.Allows(id, new(invalid)), "SSD wrong source or package rejected");
        }
        check(!ManufacturerDownloadPolicy.Allows("kingston", new(kingston.Replace("1.5.", "1.1."))), "Legacy Kingston installer not confused with x64 branch");
        var valid = new[] { ("kingston", "KINGSTON SKC3000S1024G", 17u), ("kingston", "KINGSTON SNV2S1000G", 17u), ("kingston", "KINGSTON SA400S37480G", 11u),
            ("kingston", "Kingston XS2000", 7u), ("samsung", "Samsung SSD 990 PRO 2TB", 17u), ("samsung", "Samsung SSD 870 EVO 1TB", 11u),
            ("samsung", "Samsung PSSD T7 Shield", 7u), ("sandisk", "SanDisk SDSSDA-480G", 11u), ("sandisk", "SanDisk SDSSDH3-1T00", 11u),
            ("sandisk", "WD_BLACK SN850X 1000GB", 17u), ("sandisk", "WDC WD Blue SN570 1TB", 17u), ("sandisk", "WD_BLACK P50 Game Drive", 7u) };
        foreach (var (id, model, bus) in valid) check(ManufacturerSsdPolicy.Supports(id, new(model, bus)), "Explicit SSD model and bus supported: " + model);
        foreach (var (id, model, bus) in new[] { ("kingston", "KINGSTON SKC3000S1024G", 7u), ("kingston", "KINGSTON SSD", 17u), ("kingston", "KINGSTON SKC400S512G", 11u),
            ("samsung", "Samsung MZVL21T0HCLR", 17u), ("samsung", "Samsung SSD 990 PRO 2TB", 8u), ("samsung", "Samsung RAM", 17u),
            ("sandisk", "WDC WD20EZAZ", 11u), ("sandisk", "SanDisk Cruzer", 7u), ("sandisk", "WDC PC SN730", 17u), ("sandisk", "WD_BLACK SN850X 1000GB", 14u) })
            check(!ManufacturerSsdPolicy.Supports(id, new(model, bus)), "Unknown/OEM/bridge/HDD/virtual model not assumed supported");

        var data = new byte[128]; BitConverter.GetBytes(36u).CopyTo(data, 0); BitConverter.GetBytes(128u).CopyTo(data, 4);
        BitConverter.GetBytes(36u).CopyTo(data, 12); BitConverter.GetBytes(50u).CopyTo(data, 16); BitConverter.GetBytes(17u).CopyTo(data, 28);
        Encoding.ASCII.GetBytes("Samsung").CopyTo(data, 36); Encoding.ASCII.GetBytes("SSD 990 PRO 2TB").CopyTo(data, 50);
        check(ManufacturerDisks.Parse(data, data.Length) == new ManufacturerDisk("Samsung SSD 990 PRO 2TB", 17), "Native descriptor combines vendor/product and bus");
        var corrupt = (byte[])data.Clone(); BitConverter.GetBytes(uint.MaxValue).CopyTo(corrupt, 16);
        check(ManufacturerDisks.Parse(corrupt, corrupt.Length) is null, "Invalid native string offset rejected");
        for (var size = 0; size < 36; size++) check(ManufacturerDisks.Parse(data, size) is null, "Truncated native descriptor rejected");
        var random = new Random(42);
        for (var i = 0; i < 100; i++) { var bytes = new byte[random.Next(0, 256)]; random.NextBytes(bytes); check(ManufacturerDisks.Parse(bytes, bytes.Length) is null, "Malformed descriptor cannot approve model"); }

        var hardware = new ManufacturerHardware(Architecture.X64, new(10, 0, 22631), true, "", false);
        foreach (var (id, source, model, bus) in new[] { ("kingston", kingston, "KINGSTON SKC3000S1024G", 17u), ("samsung", samsung, "Samsung SSD 990 PRO 2TB", 17u), ("sandisk", ManufacturerDownloadPolicy.SandiskInstaller, "WD_BLACK SN850X 1000GB", 17u) })
        {
            var app = ManufacturerCatalog.Apps.Single(x => x.Id == id);
            var handler = new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<a href='{source}'>Windows</a>") }; r.Content.Headers.ContentType = new("application/octet-stream"); return r; });
            using var http = new HttpClient(handler);
            Task<IReadOnlyList<ManufacturerDisk>> Disks(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<ManufacturerDisk>>([new(model, bus)]); }
            var backend = new ManufacturerService(http, null, () => hardware, Disks);
            var task = backend.ResolveAsync(app, CancellationToken.None); wait(task);
            check(task.Result?.Source.AbsoluteUri == source && task.Result.Hardware == model && handler.Calls == 1, "SSD resolution uses detected device and metadata only");
            check(handler.Method == (id == "sandisk" ? HttpMethod.Head : HttpMethod.Get), "SSD source read does not transfer installer");
            var noDevice = new ManufacturerService(http, null, () => hardware, _ => Task.FromResult<IReadOnlyList<ManufacturerDisk>>([])).ResolveAsync(app, CancellationToken.None); wait(noDevice);
            check(noDevice.Result is null && handler.Calls == 1, "Absent SSD causes no network request");
            using var binaryHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }));
            var root = Path.Combine(output, "Unsigned-" + id);
            try { wait(new ManufacturerService(binaryHttp, root).DownloadAsync(app, new(new(source), "ignored.exe", model), new Progress<ManufacturerProgress>(), CancellationToken.None)); throw new Exception("Unsigned SSD installer accepted"); }
            catch (IOException) { check(!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(), "Unsigned SSD installer removed without execution"); }
        }
        var officialSamsung = Path.GetFullPath(Path.Combine(output, "..", "Samsung.html"));
        if (File.Exists(officialSamsung)) check(ManufacturerDownloadPolicy.ParseDownload("samsung", File.ReadAllText(officialSamsung)) is not null, "Current official Samsung HTML resolves one Windows installer");
        // Read-only native API smoke: zero disks can mean denied/unknown; never invent a supported model.
        var native = ManufacturerDisks.ReadAsync(CancellationToken.None); wait(native);
        check(native.Result.All(x => !string.IsNullOrWhiteSpace(x.Model)), "Native read-only disk probe completes with valid descriptor records");
        Console.WriteLine("PASS read-only native disk probe: " + native.Result.Count + " descriptor(s); no serials or disk writes");
    }
}
