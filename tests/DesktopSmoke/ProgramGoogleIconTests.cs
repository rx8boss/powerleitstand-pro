using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerControlProWpf;

internal static class ProgramGoogleIconTests
{
    internal static int Run(string output, bool live) => Task.Run(async () =>
    {
        int cases = 0;
        void Assert(bool ok, string text) { if (!ok) throw new Exception(text); cases++; Console.WriteLine("PASS " + text); }
        var entries = ProgramCatalog.Entries;
        Assert(entries.All(e => ProgramIconSources.IsGoogleIcon(ProgramIconSources.Favicon(e.Website))), "Every catalog app uses a Google URL");
        Assert(entries.All(e => e.IconUrl.Length == 0), "Old publisher icon download addresses removed");
        Assert(!ProgramIconSources.Favicon("https://example.com/private?token=secret")!.Contains("secret"), "Only site origin is transmitted");
        Assert(ProgramIconSources.Favicon("file:///C:/private") is null && ProgramIconSources.Favicon("https://127.0.0.1/") is null, "No local locations sent to Google");
        Assert(!ProgramIconSources.IsGoogleIcon("https://www.google.com.evil.example/s2/favicons"), "Lookalike Google host rejected");
        var e = entries[0]; string directory = Path.Combine(output, "Legacy Icons"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, e.Id + "-" + e.AssetVersion + ".png"), "old");
        File.WriteAllText(Path.Combine(directory, e.Id + "-2026-10-20d.png"), "old letter suffix");
        File.WriteAllText(Path.Combine(directory, "online-" + new string('a', 64) + "-session.png"), "old");
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "keep");
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(pixel)); using var buffer = new MemoryStream(); encoder.Save(buffer); var bytes = buffer.ToArray();
        int requests = 0; var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var assets = new ProgramAssets(directory, (uri, _) => { Assert(ProgramIconSources.IsGoogleIcon(uri.AbsoluteUri), "Request goes only to Google"); Interlocked.Increment(ref requests); return gate.Task; });
        Assert(Directory.GetFiles(directory).Select(Path.GetFileName).SequenceEqual(new[] { "keep.txt" }), "Only recognized old icon files removed");
        var first = assets.EnsureIconAsync(e); var second = assets.EnsureIconAsync(e); gate.SetResult(bytes); await Task.WhenAll(first, second);
        await assets.EnsureIconAsync(e);
        Assert(requests == 1 && assets.Icon(e.Id) is not null, "Concurrent and repeated requests share the session icon");
        Assert(Directory.GetFiles(directory).Length == 1, "No icon files written");
        var nextSession = new ProgramAssets(directory, (_, _) => throw new Exception("Offline must not request")) { AllowOnlineIcons = false };
        await nextSession.EnsureIconAsync(e); Assert(nextSession.Icon(e.Id) is null, "New offline session uses lightning fallback, no disk cache");
        var failed = new ProgramAssets(Path.Combine(output, "NeverCreated"), (_, _) => Task.FromException<byte[]>(new HttpRequestException("404")));
        await failed.EnsureIconAsync(e); Assert(failed.Icon(e.Id) is null && !Directory.Exists(Path.Combine(output, "NeverCreated")), "Failed request retains fallback and creates no folder");
        var invalid = new ProgramAssets(Path.Combine(output, "Invalid"), (_, _) => Task.FromResult(new byte[] { 1, 2, 3 }));
        await invalid.EnsureIconAsync(e); Assert(invalid.Icon(e.Id) is null, "Invalid response retains fallback without crash");
        var dynamicEntry = e with { Id = "winget:Example.App", PackageId = "Example.App" };
        assets.RegisterIcon(dynamicEntry, ProgramIconSources.Favicon("https://example.com/app")); await assets.EnsureIconAsync(dynamicEntry);
        Assert(assets.Icon(dynamicEntry.Id) is not null, "Online search results use the same Google path");
        await assets.CleanupAsync(); Assert(File.ReadAllText(Path.Combine(directory, "keep.txt")) == "keep", "Cleanup preserves unrelated files");
        if (live)
        {
            var liveAssets = new ProgramAssets(Path.Combine(output, "NoPersistentIcons"));
            await liveAssets.WarmupAsync();
            var results = entries.Select(x => x.Name + ": " + (liveAssets.Icon(x.Id) is null ? "Blitz / Google nicht verfügbar" : "Google-Icon geladen")).ToArray();
            File.WriteAllLines(Path.Combine(output, "Google-Ergebnisse.txt"), results);
            Console.WriteLine(string.Join(Environment.NewLine, results));
            Assert(liveAssets.Icon("google-chrome") is not null, "Real Google response decodes in app");
            Assert(!Directory.Exists(Path.Combine(output, "NoPersistentIcons")), "Live catalog load leaves no icon folder");
            Console.WriteLine("Google icons: " + entries.Count(x => liveAssets.Icon(x.Id) is not null) + "/" + entries.Length);
        }
        Console.WriteLine("PASS " + cases + " Google icon checks"); return 0;
    }).GetAwaiter().GetResult();
}

