using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PowerControlProWpf;

internal static partial class VlcZipTests
{
    internal static async Task<int> OfficialAsync(string output)
    {
        Directory.CreateDirectory(output); int checks = 0; void Assert(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
        var sources = Environment.GetEnvironmentVariable("POWERLEITSTAND_VLC_FIXTURES") ?? throw new InvalidOperationException("Set POWERLEITSTAND_VLC_FIXTURES to the directory containing both archives and captured vendor metadata.");
        string oldZip = Path.Combine(sources, "vlc-3.0.21-win64.zip"), nextZip = Path.Combine(sources, "vlc-3.0.24-win64.zip");
        Assert(new FileInfo(oldZip).Length == 77665682 && ProgramPortableUpdates.Hash(oldZip) == "A0B7EC02B50ADF6417EED014FB8DF50AF39690505A4225B85B3DC2ED17D14843", "official old archive pinned by published hash and size");
        Assert(new FileInfo(nextZip).Length == 82765861 && ProgramPortableUpdates.Hash(nextZip) == "FCF30850371AD10C9373CC4F0F4501E7DEE49E3E9AE9F20C72FB2661A1CA6323", "official new archive pinned by published hash and size");
        Task<byte[]> Read(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); bool old = uri.AbsolutePath.Contains("/3.0.21/");
            string file = uri.AbsolutePath.EndsWith(".sha256") ? old ? Path.Combine(sources, "SHA-alt.txt") : Path.Combine(sources, "VLC-Original.sha256")
                : Path.Combine(sources, old ? "Index-alt.html" : "Index-neu.html");
            return File.ReadAllBytesAsync(file, token);
        }
        Task Download(PortableUpdate request, string target, Action<ProgramDownload> progress, CancellationToken token)
        { token.ThrowIfCancellationRequested(); string source = request.Version == "3.0.21" ? oldZip : nextZip; File.Copy(source, target); progress(new(new FileInfo(source).Length, request.Size)); return Task.CompletedTask; }
        var updater = new VlcPortableUpdates(Read, Download, Path.Combine(output, "Backups"));
        string unpacked = Path.Combine(output, "Original"); await ProgramPortable.ExtractAsync(oldZip, unpacked, default);
        string root = Path.Combine(unpacked, "vlc-3.0.21"), exe = Path.Combine(root, "vlc.exe");
        Assert(VlcPortableUpdates.ReadBinary(exe) is { Version: "3.0.21", Machine: 0x8664 }, "real old PE metadata recognized");
        Assert(VlcPortableUpdates.Identify(exe) is null, "real unassociated archive is not silently authorized");
        VlcPortableUpdates.ConfirmZip(exe);
        Assert(VlcPortableUpdates.Identify(exe)?.Version == "3.0.21", "explicit real ZIP copy recognized");
        int oldOwned = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Count(p => Path.GetFileName(p) != VlcPortableUpdates.ReceiptName);
        var userPaths = new[] { "vlcrc", "plugins/custom-user.dll", "media/private.txt" }.Select(p => Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar))).ToArray();
        foreach (string path in userPaths) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "private user fixture"); }
        string userHash = string.Join('|', userPaths.Select(ProgramPortableUpdates.Hash));
        var proof = new ProgramInstallation("3.0.21", "Portable", exe, false, VlcPortableUpdates.Variant);
        var offer = await updater.ResolveAsync(proof, default) ?? throw new Exception("real archive update missing");
        Assert(offer.Version == "3.0.24", "real captured provider metadata identifies version upgrade");
        var steps = new List<ProgramStepEvent>(); await updater.ApplyAsync(offer, steps.Add, _ => { }, default);
        Assert(VlcPortableUpdates.Identify(exe)?.Version == "3.0.24" && VlcPortableUpdates.ReadBinary(exe)?.Machine == 0x8664, "real whole package upgrade 3.0.21 to 3.0.24 verified");
        Assert(userHash == string.Join('|', userPaths.Select(ProgramPortableUpdates.Hash)), "real upgrade preserves custom config/plugin/media");
        using (var zip = ZipFile.OpenRead(nextZip))
        {
            int owned = 0;
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                string relative = entry.FullName[("vlc-3.0.24/".Length)..];
                using var input = entry.Open(); string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
                if (ProgramPortableUpdates.Hash(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))) != expected) throw new Exception("real package member mismatch: " + relative);
                owned++;
            }
            Assert(owned == 584, "all 584 official new files match real archive");
            Assert(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == owned + userPaths.Length + 1, "no obsolete owned file remains");
        }
        string backup = Directory.GetDirectories(Path.Combine(output, "Backups")).Single();
        using (var zip = ZipFile.OpenRead(Path.Combine(backup, "previous-package.zip"))) Assert(zip.Entries.Count == oldOwned, "verified complete previous package backup");
        Assert(File.ReadAllText(Path.Combine(backup, "backup.json")).Contains("complete"), "real replacement journal complete");
        Assert(!Directory.GetDirectories(unpacked, ".PowerLeitstand-VLC-*").Any(), "real replacement stage cleaned");
        var entryVlc = ProgramCatalog.Entries.Single(e => e.Id == "vlc"); var selected = ProgramFileAssociations.Validate(entryVlc, exe);
        Assert(selected.Source == "Portable" && selected.Variant == VlcPortableUpdates.Variant && selected.Version == "3.0.24", "real file association keeps ZIP identity");
        var association = new ProgramFileAssociations(Path.Combine(output, "Association.json")); association.Save(entryVlc, exe);
        var sameNative = new Dictionary<string, ProgramInstallation> { ["vlc"] = new("3.0.21", "Registry", exe) }; association.Merge([entryVlc], sameNative, default);
        Assert(sameNative["vlc"].Source == "Registry" && sameNative["vlc"].Version == "3.0.21", "same registered location cannot be converted to ZIP by stored choice");
        string other = Path.Combine(output, "OtherRegistered"); Directory.CreateDirectory(other);
        var separate = new Dictionary<string, ProgramInstallation> { ["vlc"] = new("3.0.18", "Registry", other) }; association.Merge([entryVlc], separate, default);
        Assert(separate["vlc"].Variant == VlcPortableUpdates.Variant && separate["vlc"].Version == "3.0.24", "explicit separate ZIP copy does not mix registered version");
        var portable = new ProgramPortable(updater); var package = await portable.ResolveAsync(entryVlc, default) ?? throw new Exception("new real ZIP option missing");
        string parent = Path.Combine(output, "SelectedDisk"); Directory.CreateDirectory(parent);
        string newExe = await portable.InstallAsync(entryVlc, package, parent, new Progress<double?>(), default);
        Assert(newExe == Path.Combine(ProgramPortable.Destination(entryVlc, package, parent), "vlc.exe") && VlcPortableUpdates.Identify(newExe)?.Version == "3.0.24", "new real ZIP install uses selected parent");
        var store = new ProgramPortableStore(Path.Combine(output, "PortablePaths.json")); store.Remember("vlc", newExe);
        Assert(store.Read().GetValueOrDefault("vlc") is { Variant: VlcPortableUpdates.Variant, WinGetCompatible: false, Version: "3.0.24" }, "real new ZIP association persists");
        Console.WriteLine($"PASS {checks} official VLC archive, real binary, full 3.0.21-to-3.0.24 replacement (584 files), user preservation, backup, new installation and association checks; no VLC executed"); return 0;
    }
}
