using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PowerControlProWpf;

internal static class ProgramIconFallbackTests
{
    internal static int Run(string output, bool live) => Task.Run(async () =>
    {
        int cases = 0;
        void Assert(bool ok, string text) { if (!ok) throw new Exception(text); cases++; Console.WriteLine("PASS " + text); }
        var e = ProgramCatalog.Entries.First();
        var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp)); using var memory = new MemoryStream(); encoder.Save(memory); byte[] bytes = memory.ToArray();
        var requests = new List<string>();
        Task<string[]> Metadata(string? id, CancellationToken _) { Assert(id == e.PackageId, "Exact package identity passed to metadata reader"); return Task.FromResult(new[] { "https://publisher.example/icon.png" }); }
        Task<byte[]> Read(Uri uri, int limit, CancellationToken _) { requests.Add(uri.AbsoluteUri); return Task.FromResult(bytes); }
        var icon = await ProgramIconFallback.ResolveAsync(e, e.Website, CancellationToken.None, Metadata, Read);
        Assert(icon?.Source == "WinGet" && requests.SequenceEqual(new[] { "https://publisher.example/icon.png" }), "WinGet icon wins before website scan");
        requests.Clear();
        Task<byte[]> Site(Uri uri, int limit, CancellationToken _)
        {
            requests.Add(uri.AbsoluteUri);
            return Task.FromResult(uri.AbsolutePath == "/app" ? Encoding.UTF8.GetBytes("<link rel='stylesheet' href='/wrong.png'><link rel='icon' href='/logo.png'>") : bytes);
        }
        icon = await ProgramIconFallback.ResolveAsync(e, "https://publisher.example/app", CancellationToken.None, (_, _) => Task.FromResult(Array.Empty<string>()), Site);
        Assert(icon?.Source == "Hersteller" && requests.SequenceEqual(new[] { "https://publisher.example/app", "https://publisher.example/logo.png" }), "Official page icon found automatically, no CSS download");
        var candidates = ProgramIconFallback.HtmlIcons(new("https://publisher.example/app"), "<link REL=ICON HREF='/i.ico?a=1&amp;b=2'><link rel=icon href='https://127.0.0.1/secret'><link rel=icon href='file:///C:/secret'><link rel=icon href='/only.svg'>").ToArray();
        Assert(candidates.Length == 1 && candidates[0].Query == "?a=1&b=2", "HTML entities decoded; local files, IPs and SVG excluded");
        Assert(!ProgramIconFallback.PublicUrl("https://user:pass@publisher.example/") && !ProgramIconFallback.PublicUrl("https://localhost/") && !ProgramIconFallback.PublicUrl("http://publisher.example/"), "Only public credential-free HTTPS candidates");
        requests.Clear();
        icon = await ProgramIconFallback.ResolveAsync(e, "https://publisher.example/app", CancellationToken.None,
            (_, _) => Task.FromResult(new[] { "https://publisher.example/bad.png" }),
            (uri, _, _) => { requests.Add(uri.AbsoluteUri); return Task.FromResult(uri.AbsolutePath == "/favicon.ico" ? bytes : Encoding.UTF8.GetBytes("not an image")); });
        Assert(icon?.Source == "Hersteller" && requests.Last().EndsWith("/favicon.ico"), "Invalid WinGet image falls through to site root icon");
        var dir = Path.Combine(output, "NoIconFiles"); int fallbacks = 0;
        var assets = new ProgramAssets(dir, (_, _) => Task.FromException<byte[]>(new HttpRequestException("404")), fallback: async (entry, website, token) =>
        { fallbacks++; return await ProgramIconFallback.ResolveAsync(entry, website, token, Metadata, Read); });
        await assets.EnsureIconAsync(e); await assets.EnsureIconAsync(e);
        Assert(assets.Icon(e.Id) is not null && assets.IconSource(e.Id) == "WinGet" && fallbacks == 1, "App uses fallback once and retains it in session memory");
        Assert(!Directory.Exists(dir), "Fallback writes no icon folder");
        int retryCalls = 0;
        var retry = new ProgramAssets(dir, (_, _) => ++retryCalls == 1
            ? Task.FromException<byte[]>(new HttpRequestException("temporary failure")) : Task.FromResult(bytes));
        await retry.EnsureIconAsync(e); await retry.EnsureIconAsync(e);
        Assert(retry.IconSource(e.Id) == "Google" && retryCalls == 2, "One bounded retry recovers temporary failure and is reused");
        int failures = 0;
        var unavailable = new ProgramAssets(dir, (_, _) => { failures++; return Task.FromException<byte[]>(new HttpRequestException("unavailable")); });
        await unavailable.EnsureIconAsync(e); await unavailable.EnsureIconAsync(e);
        Assert(failures == 2 && unavailable.Icon(e.Id) is null, "Persistent failure stops after two requests and keeps lightning cooldown");
        var dynamicEntry = e with { Id = "winget:Example.NoHomepage", PackageId = "Example.NoHomepage" };
        var dynamicAssets = new ProgramAssets(dir, (_, _) => throw new Exception("No Google source"), fallback: (_, _, _) => Task.FromResult(icon));
        dynamicAssets.RegisterIcon(dynamicEntry, null); await dynamicAssets.EnsureIconAsync(dynamicEntry);
        Assert(dynamicAssets.Icon(dynamicEntry.Id) is not null, "Search entry without homepage can still use package icon");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); bool stopped = false;
        try { await ProgramIconFallback.ResolveAsync(e, e.Website, canceled.Token, (_, token) => Task.FromCanceled<string[]>(token), Read); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "Cancellation stops fallback chain");
        if (live)
        {
            var liveAssets = new ProgramAssets(Path.Combine(output, "LiveNoDiskCache")) { Diagnostic = Console.WriteLine };
            await liveAssets.WarmupAsync();
            var rows = ProgramCatalog.Entries.Select(x => x.Name + ": " + (liveAssets.IconSource(x.Id) ?? "Blitz")).ToArray();
            File.WriteAllLines(Path.Combine(output, "Icon-Quellen.txt"), rows); Console.WriteLine(string.Join(Environment.NewLine, rows));
            Assert(liveAssets.Icon("google-chrome") is not null, "Google stage live");
            foreach (string id in new[] { "vlc", "gimp", "inkscape" })
                Assert(liveAssets.Icon(id) is not null, "Formerly missing icon loaded: " + id);
            Assert(!Directory.Exists(Path.Combine(output, "LiveNoDiskCache")), "Live fallback creates no cache directory");
            Console.WriteLine("Loaded: " + ProgramCatalog.Entries.Count(x => liveAssets.Icon(x.Id) is not null) + "/" + ProgramCatalog.Entries.Length);
        }
        Console.WriteLine("PASS " + cases + " icon fallback checks"); return 0;
    }).GetAwaiter().GetResult();
}

