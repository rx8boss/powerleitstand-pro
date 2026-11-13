using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerControlProWpf;

internal static class ManufacturerBoardTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(respond(request)); }
    }
    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in files) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        return stream.ToArray();
    }
    internal static void Run(Action<bool, string> check, Action<Task> wait, string output)
    {
        const string asusSource = "https://dlcdnets.asus.com/pub/ASUS/mb/14Utilities/ArmouryCrateInstallTool.zip";
        const string msiSource = "https://download.msi.com/uti_exe/desktop/MSI-Center.zip";
        const string gigaSource = "https://download.gigabyte.com/FileList/Utility/GCC_1.2.3.4.zip"; // Deliberately synthetic version, not a live-source claim.
        var hardware = new ManufacturerHardware(Architecture.X64, new(10, 0, 22631), true, "", true, "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-F GAMING WIFI");
        check(ManufacturerBoardPolicy.Candidate("asus", hardware), "Supported ASUS motherboard family");
        check(ManufacturerBoardPolicy.Candidate("asus", hardware with { BoardModel = "TUF GAMING X670E-PLUS WIFI" }), "ASUS E-suffix board");
        check(!ManufacturerBoardPolicy.Candidate("asus", hardware with { BoardManufacturer = "Other" }), "Chipset alone does not identify ASUS");
        check(!ManufacturerBoardPolicy.Candidate("asus", hardware with { BoardModel = "ROG Ally" }), "SE/handheld not treated as desktop installer");
        check(ManufacturerBoardPolicy.Candidate("msi", hardware with { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardModel = "MAG B650 TOMAHAWK WIFI (MS-7D75)" }), "Documented MSI Center board family");
        check(!ManufacturerBoardPolicy.Candidate("msi", hardware with { BoardManufacturer = "MSI", BoardModel = "MS-1234" }), "Unknown MSI board rejected");
        const string table = "<table><tr><td>Motherboard</td><td>Intel Z790</td><td>Z790 AORUS ELITE</td></tr><tr><td>Graphics Card</td><td>x</td><td>Z790 AORUS ELITE X</td></tr></table>";
        check(ManufacturerBoardPolicy.ListedGigabyteBoard("Z790 AORUS ELITE", table), "GIGABYTE exact motherboard row");
        check(!ManufacturerBoardPolicy.ListedGigabyteBoard("Z790 AORUS ELITE X", table), "GPU/model near-match not accepted as motherboard");
        check(!ManufacturerBoardPolicy.ListedGigabyteBoard("Z790", table), "Chipset substring not compatibility evidence");
        var payload = Zip(("setup/ArmouryCrateInstaller.exe", "Unsigned fixture"), ("readme.txt", "fixture"));
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        string AsusJson(string title, string sha) => JsonSerializer.Serialize(new { Result = new { Obj = new[] { new { Files = new[] { new { Title = title, DownloadUrl = new { Global = "/pub/ASUS/mb/14Utilities/ArmouryCrateInstallTool.zip" }, sha256 = sha } } } } } });
        var json = AsusJson("Armoury Crate & Aura Creator Installer", hash);
        check(ManufacturerBoardPolicy.ParseAsus(json, hardware.BoardModel)?.Sha256 == hash, "ASUS API binds package and hash");
        check(ManufacturerBoardPolicy.ParseAsus(AsusJson("Armoury Crate SE Installer", hash), "fixture") is null, "SE package excluded");
        check(ManufacturerBoardPolicy.ParseAsus(AsusJson("Armoury Crate & Aura Creator Installer", "invalid"), "fixture") is null, "Missing valid ASUS hash blocks source");
        var official = Path.GetFullPath(Path.Combine(output, "..", "ASUSDrivers.json"));
        if (File.Exists(official)) check(ManufacturerBoardPolicy.ParseAsus(File.ReadAllText(official), hardware.BoardModel)?.Sha256?.Length == 64, "Current official ASUS API parsed");

        foreach (var (id, source) in new[] { ("asus", asusSource), ("msi", msiSource), ("gigabyte", gigaSource) })
        {
            check(ManufacturerDownloadPolicy.Allows(id, new(source)), "Manufacturer package policy");
            check(!ManufacturerDownloadPolicy.Allows(id, new(source.Replace("https:", "http:"))), "HTTP package blocked");
            check(!ManufacturerDownloadPolicy.Allows(id, new(source.Replace(new Uri(source).Host, "evil.test"))), "Foreign package host blocked");
        }
        check(!ManufacturerDownloadPolicy.Allows("asus", new(asusSource.Replace("ArmouryCrateInstallTool", "Armoury_Crate_Uninstall_Tool"))), "Uninstaller cannot substitute installer");
        check(!ManufacturerArchive.InstallerName("msi", "MSI.CentralServer.exe"), "MSI service is not installer/UI target");

        var safeZip = Path.Combine(output, "SafeArchive.zip"); File.WriteAllBytes(safeZip, payload);
        var safeDestination = Path.Combine(output, "SafeArchive-" + Guid.NewGuid().ToString("N"));
        var extracted = ManufacturerArchive.ExtractAsync("asus", safeZip, safeDestination, CancellationToken.None); wait(extracted);
        check(File.Exists(extracted.Result) && File.Exists(Path.Combine(safeDestination, "readme.txt")), "Archive preserves companion files within private directory");
        var index = 0;
        foreach (var invalid in new[] { "../escape.exe", "C:/escape.exe", "/escape.exe", "setup/file:stream", "setup/CON.txt", "setup/file. ", "setup/./file" })
        {
            var file = Path.Combine(output, "Unsafe-" + index + ".zip"); File.WriteAllBytes(file, Zip(("ArmouryCrateInstaller.exe", "fixture"), (invalid, "fixture")));
            try { wait(ManufacturerArchive.ExtractAsync("asus", file, Path.Combine(output, "Unsafe-" + index++), CancellationToken.None)); throw new Exception("Unsafe archive accepted"); }
            catch (IOException) { check(!File.Exists(Path.Combine(output, "escape.exe")), "Unsafe archive paths rejected before extraction"); }
        }
        var duplicate = Path.Combine(output, "Duplicate.zip"); File.WriteAllBytes(duplicate, Zip(("ArmouryCrateInstaller.exe", "x"), ("ArmouryCrateInstallTool.exe", "y")));
        try { wait(ManufacturerArchive.ExtractAsync("asus", duplicate, Path.Combine(output, "Duplicate"), CancellationToken.None)); throw new Exception("Ambiguous installer accepted"); }
        catch (IOException) { check(true, "Ambiguous installer archive rejected"); }

        var handler = new Handler(request => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("GetPDDrivers") ? json : "All other ASUS motherboards: X570 series and all subsequent models") });
        using var http = new HttpClient(handler);
        var asus = ManufacturerCatalog.Apps.Single(x => x.Id == "asus");
        var resolved = new ManufacturerService(http, null, () => hardware).ResolveAsync(asus, CancellationToken.None); wait(resolved);
        check(resolved.Result?.Source.AbsoluteUri == asusSource && handler.Calls == 2, "ASUS metadata and current compatibility checked before download");
        var notSupported = new ManufacturerService(http, null, () => hardware with { BoardManufacturer = "Other" }).ResolveAsync(asus, CancellationToken.None); wait(notSupported);
        check(notSupported.Result is null && handler.Calls == 2, "Wrong motherboard makes no network request");
        var msiHandler = new Handler(request => { check(request.Method == HttpMethod.Head, "MSI metadata request uses HEAD only"); var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentType = new("application/zip"); return response; });
        using var msiHttp = new HttpClient(msiHandler);
        var msiHardware = hardware with { BoardManufacturer = "Micro-Star International Co., Ltd.", BoardModel = "MAG B650 TOMAHAWK WIFI" };
        var msi = ManufacturerCatalog.Apps.Single(x => x.Id == "msi");
        var msiTask = new ManufacturerService(msiHttp, null, () => msiHardware).ResolveAsync(msi, CancellationToken.None); wait(msiTask);
        check(msiTask.Result?.Source.AbsoluteUri == msiSource && msiHandler.Calls == 1, "MSI stable official endpoint resolves without HTML fallback or binary transfer");
        var gigaHardware = hardware with { BoardManufacturer = "Gigabyte Technology Co., Ltd.", BoardModel = "Z790 AORUS ELITE" };
        var gigaHandler = new Handler(request => new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.Contains("table.html") ? table : $"<a href='{gigaSource}'>GCC</a>") });
        using var gigaHttp = new HttpClient(gigaHandler);
        var giga = ManufacturerCatalog.Apps.Single(x => x.Id == "gigabyte");
        var gigaTask = new ManufacturerService(gigaHttp, null, () => gigaHardware).ResolveAsync(giga, CancellationToken.None); wait(gigaTask);
        check(gigaTask.Result?.Source.AbsoluteUri == gigaSource && gigaHandler.Calls == 2, "GIGABYTE resolver requires exact row and unique source (synthetic fixture)");
        using var forbiddenHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("blocked") }));
        try { wait(new ManufacturerService(forbiddenHttp, null, () => gigaHardware).ResolveAsync(giga, CancellationToken.None)); throw new Exception("GIGABYTE forbidden response ignored"); }
        catch(HttpRequestException) { check(true, "Blocked source produces no guessed/pinned fallback"); }
        using var binaryHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var root = Path.Combine(output, "BoardDownloads"); var backend = new ManufacturerService(binaryHttp, root);
        foreach (var checksum in new[] { new string('0', 64), hash })
        {
            try { wait(backend.DownloadAsync(asus, new(new(asusSource), "fixture.zip", "fixture", checksum), new Progress<ManufacturerProgress>(), CancellationToken.None)); throw new Exception("Invalid archive/signature accepted"); }
            catch (IOException) { check(!Directory.EnumerateFileSystemEntries(root).Any(), "Hash/signature failure removes owned archive and extracted files"); }
        }
        foreach (var id in new[] { "asus", "msi" })
        {
            var state = ManufacturerPackages.Inspect(id);
            check(state.Installation is ManufacturerInstallState.Missing or ManufacturerInstallState.Installed or ManufacturerInstallState.NoLaunchTarget or ManufacturerInstallState.Unknown, "Native vendor package query returns known state without launching");
            try { ManufacturerPackages.Activate(id, "appx:foreign!App"); throw new Exception("Foreign AUMID accepted"); }
            catch (IOException) { check(true, "Wrong or stale package identity never activates"); }
        }
        var calculator = ManufacturerPackages.InspectFamily("Microsoft.WindowsCalculator_8wekyb3d8bbwe");
        check(calculator.Installation != ManufacturerInstallState.Unknown, "Native package query smoke for known Microsoft Store family");
        Console.WriteLine("PASS native Store query (no activation): Calculator " + calculator.Installation);
    }
}
