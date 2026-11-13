using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramDescriptionTests
{
    private static readonly ProgramEntry Entry = new("winget:test.application", "Test Application", ["tools"], "Test.Application", "Test Publisher", "Katalogtext", "Catalog text", "https://example.com", "", null, null, "session");
    private static ProgramPackage Package(string text = "English public description") => new("Test.Application", "1.0", "fixture", DateTime.UtcNow, text, CanInstall: true);
    private static string Query(Uri uri, string name) => Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(x => x.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    private static string Json(string text) => JsonSerializer.Serialize(new { responseStatus = 200, quotaFinished = false, responseData = new { translatedText = text } });
    internal static int Run(App app, string output, bool live, bool logicOnly)
    {
        int count = 0;
        void Assert(bool value, string why) { if (!value) throw new Exception(why); count++; }
        Task.Run(async () =>
        {
            int calls = 0, metadataCalls = 0;
            Task<string> Echo(Uri uri, CancellationToken token) { Interlocked.Increment(ref calls); return Task.FromResult(Json("Übersetzt: " + Query(uri, "q"))); }
            Task<ProgramDescriptionMetadata?> Metadata(string id, string version, string target, CancellationToken token)
            { Interlocked.Increment(ref metadataCalls); return Task.FromResult<ProgramDescriptionMetadata?>(new(new("English public description", "en-US"))); }
            var translations = new ProgramDescriptions(Metadata, Echo);
            var de = await translations.GetAsync(Entry, Package(), false, CancellationToken.None);
            Assert(de.State == ProgramDescriptionState.Translated && de.Text.StartsWith("Übersetzt:") && de.Original == "English public description", "Missing German text is translated with preserved original");
            var cached = await translations.GetAsync(Entry, Package(), false, CancellationToken.None);
            Assert(cached == de && calls == 1 && metadataCalls == 1, "Reopening uses session cache without repeat metadata or translation queries");
            var en = await translations.GetAsync(Entry, Package(), true, CancellationToken.None);
            Assert(en.State == ProgramDescriptionState.Native && calls == 1, "Matching source language never uses translator");
            await translations.GetAsync(Entry, Package("Changed text"), false, CancellationToken.None);
            Assert(calls == 2, "Changed source invalidates previous cache key");
            var native = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("English original", "en-US"), new("Deutscher Herstellertext", "de-DE"))), Echo);
            var nativeResult = await native.GetAsync(Entry, Package(), false, CancellationToken.None);
            Assert(nativeResult.Text == "Deutscher Herstellertext" && nativeResult.State == ProgramDescriptionState.Native && calls == 2, "Confirmed target locale wins without translation");
            var wrongLocale = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("French source", "fr-FR"), new("Wrong locale", "en-US"))), (uri, _) =>
            { Assert(Query(uri, "langpair") == "fr|de", "Uses confirmed French source, never assumes English"); return Task.FromResult(Json("Französischer Text")); });
            Assert((await wrongLocale.GetAsync(Entry, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.Translated, "Wrong target metadata locale falls back to correct language pair");
            var unknown = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(null), Echo);
            Assert((await unknown.GetAsync(Entry, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.UnknownLanguage && calls == 2, "Unknown source language retains original without translation");
            Assert((await unknown.GetAsync(Entry, null, false, CancellationToken.None)).State == ProgramDescriptionState.Missing, "Missing description explicit");
            int staticBefore = metadataCalls;
            Assert((await translations.GetAsync(Entry with { Id = "catalog" }, Package(), false, CancellationToken.None)).Text == "Katalogtext" && metadataCalls == staticBefore, "Bilingual catalog entries bypass all description queries");
            Assert(ProgramInstallService.ReadDescription("Name [Test.Application]\nDescription: First line\n  Second line: still text\n\n  Third line\nHomepage: https://example.com\nInstaller:\n  Type: exe") == "First line\nSecond line: still text\n\nThird line", "Multiline CLI description ends at next metadata field");
            Assert(ProgramInstallService.ReadDescription("  Beschreibung:\n    Mehrzeiliger Text\n  Lizenz: MIT") == "Mehrzeiliger Text", "Indented German metadata parsed without license leakage");
            Assert(ProgramInstallService.ReadDescription("Homepage: https://example.com") is null, "No invented description");
            var inherited = ProgramDescriptionMetadataReader.Select("en-US", "English long text", "English short", "de-DE", "English long text", "Deutscher Kurztext");
            Assert(inherited is { Original.Locale: "en-US", Localized.Text: "Deutscher Kurztext" }, "German short text wins over inherited English long description");
            Assert(ProgramDescriptionMetadataReader.Select("en-US", "English long text", "English short", "de-DE", "English long text", "English short")?.Localized is null, "Changed metadata locale alone never proves inherited description language");
            Assert(ProgramDescriptionMetadataReader.Select("en-US", "English long text", "English short", "de-DE", "Deutscher Langtext", "English short")?.Localized?.Text == "Deutscher Langtext", "Explicit localized long description retained");
            Assert(ProgramDescriptionMetadataReader.Select("en-US", "English long text", "English short", "en-GB", "English long text", "English short")?.Localized?.Text == "English long text", "Same primary source language remains native");
            string manifest = "PackageIdentifier: Test.Application\nPackageVersion: \"1.0\"\nDefaultLocale: en-US\nManifestType: version\n";
            string localeHeader = "PackageIdentifier: Test.Application\nPackageVersion: '1.0'\nPackageLocale: en-US\nManifestType: defaultLocale\n";
            Assert(ProgramDescriptionManifests.DefaultLocale("Test.Application", "1.0", manifest) == "en-US", "Version manifest supplies confirmed source locale");
            Assert(ProgramDescriptionManifests.DefaultLocale("Test.Application", "2.0", manifest) is null, "Wrong version manifest rejected");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "en-US", localeHeader + "Description: |-\n  First line\n  Second line\nShortDescription: Short\n")?.Text == "First line\nSecond line", "Literal multiline manifest description excludes neighboring fields");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "en-US", localeHeader + "Description: >-\n  First line\n  second line\n")?.Text == "First line second line", "Folded manifest description preserved as ordinary text");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "en-US", localeHeader + "ShortDescription: 'Editor''s tool' # comment\n")?.Text == "Editor's tool", "Single-quoted apostrophes and comments parsed safely");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "en-US", localeHeader + "ShortDescription: \"A \\\"quoted\\\" tool\" # comment\n")?.Text == "A \"quoted\" tool", "Double-quoted text parsed as data");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "de-DE", localeHeader + "ShortDescription: English\n") is null, "Filename locale cannot override mismatching manifest locale");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "en-US", localeHeader + "PackageVersion: 1.0\nShortDescription: Duplicate\n") is null, "Duplicate identity fields rejected");
            Assert(ProgramDescriptionManifests.Parse("Test.Application", "1.0", "en-US", localeHeader + "Description: *alias\nShortDescription: Safe short text\n")?.Text == "Safe short text", "Unsupported YAML alias cannot become a description; independent short text remains usable");
            int rawCalls = 0;
            Task<string> ManifestRead(Uri uri, CancellationToken token)
            {
                rawCalls++; Assert(uri.Host == "raw.githubusercontent.com" && uri.AbsolutePath.StartsWith("/microsoft/winget-pkgs/master/manifests/t/Test/Application/1.0/"), "Manifest URLs are bound to official source and exact package/version");
                return uri.AbsolutePath.EndsWith(".locale.en-US.yaml") ? Task.FromResult(localeHeader + "ShortDescription: Official original\n")
                    : uri.AbsolutePath.EndsWith("/Test.Application.yaml") ? Task.FromResult(manifest)
                    : throw new HttpRequestException("missing", null, HttpStatusCode.NotFound);
            }
            var raw = await ProgramDescriptionManifests.ReadAsync("Test.Application", "1.0", "de", CancellationToken.None, ManifestRead);
            Assert(raw is { Original.Text: "Official original", Original.Locale: "en-US", Localized: null } && rawCalls == 4, "Missing locale files retain official original for translator fallback");
            Assert(await ProgramDescriptionManifests.ReadAsync("../bad", "1.0", "de", CancellationToken.None, ManifestRead) is null && rawCalls == 4, "Invalid package cannot construct arbitrary manifest URL");
            string unicode = string.Join(" ", Enumerable.Repeat("Größe 😀 Unicode", 80));
            var chunks = ProgramDescriptions.Split(unicode);
            Assert(chunks.Count > 1 && chunks.All(c => Encoding.UTF8.GetByteCount(c.Text) <= 500), "Every UTF-8 chunk fits 500 bytes");
            Assert(string.Concat(chunks.Select(c => c.Text + c.After)) == unicode, "Splitting preserves complete Unicode text and whitespace");
            Assert(ProgramDescriptions.Split(new string('x', 501)).Count == 0, "Oversized unbroken words never split identifiers");
            string protectedSource = "Test Application 1.2.3 by Test Publisher Test.Application https://example.com/help C:\\Apps\\Test.exe";
            var protectedService = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new(protectedSource, "en-US"))), (uri, _) =>
            { var q = Query(uri, "q"); Assert(!q.Contains("Test Application") && !q.Contains("1.2.3") && !q.Contains("https://example.com") && !q.Contains("C:\\Apps"), "Names, versions, identifiers, paths and URLs protected in transmitted text"); return Task.FromResult(Json(q)); });
            Assert((await protectedService.GetAsync(Entry, Package(), false, CancellationToken.None)).Text == protectedSource, "Protected values restored exactly");
            var corrupt = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new(protectedSource, "en-US"))), (_, _) => Task.FromResult(Json("Lost placeholders")));
            var corruptResult = await corrupt.GetAsync(Entry, Package(), false, CancellationToken.None);
            Assert(corruptResult.State == ProgramDescriptionState.Unavailable && corruptResult.Text == protectedSource, "Corrupted token response falls back to full original");
            foreach (string bad in new[] { "invalid json", "[]", "{}", "{\"responseStatus\":500}", "{\"responseStatus\":200,\"responseData\":null}", Json(""), new string('x', 65537) })
            {
                var invalid = new ProgramDescriptions(Metadata, (_, _) => Task.FromResult(bad));
                Assert((await invalid.GetAsync(Entry, Package(), false, CancellationToken.None)) is { State: ProgramDescriptionState.Unavailable, Text: "English public description" }, "Malformed/provider-error response keeps complete original");
            }
            var quota = new ProgramDescriptions(Metadata, (_, _) => Task.FromResult("{\"responseStatus\":200,\"quotaFinished\":true}"));
            Assert((await quota.GetAsync(Entry, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.Limit, "HTTP 200 quota response not mislabelled translation");
            int quotaCalls = 0;
            var limited = new ProgramDescriptions(Metadata, (_, _) => { quotaCalls++; throw new HttpRequestException("limit", null, HttpStatusCode.TooManyRequests); });
            await limited.GetAsync(Entry, Package(), false, CancellationToken.None);
            var blocked = await limited.GetAsync(Entry with { Id = "winget:second" }, Package(), false, CancellationToken.None);
            Assert(blocked.State == ProgramDescriptionState.Limit && quotaCalls == 1, "Known quota blocks further requests in session day");
            var offline = new ProgramDescriptions(Metadata, (_, _) => throw new HttpRequestException("offline"));
            Assert((await offline.GetAsync(Entry, Package(), false, CancellationToken.None)) is { State: ProgramDescriptionState.Unavailable, Original: "English public description" }, "Offline fallback preserves known original");
            var timeout = new ProgramDescriptions(Metadata, async (_, token) => { await Task.Delay(5000, token); return Json("late"); }, TimeSpan.FromMilliseconds(60));
            Assert((await timeout.GetAsync(Entry, Package("CLI fallback"), false, CancellationToken.None)) is { State: ProgramDescriptionState.Timeout, Text: "English public description" }, "Timeout preserves known metadata original, not a different CLI fallback");
            var tooLong = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new(new string('a', 4001), "en-US"))), Echo);
            Assert((await tooLong.GetAsync(Entry, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.TooLong, "Long text stays complete original instead of silent truncation");
            var tooMany = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new(string.Join(" ", Enumerable.Repeat("😀", 900)), "en-US"))), Echo);
            Assert((await tooMany.GetAsync(Entry, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.TooLong, "Byte-heavy text cannot exceed eight requests");
            int sharedCalls = 0; var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = new ProgramDescriptions(Metadata, async (uri, token) => { sharedCalls++; await release.Task.WaitAsync(token); return Json("Geteilt"); });
            using var stop = new CancellationTokenSource();
            var first = shared.GetAsync(Entry, Package(), false, stop.Token);
            var second = shared.GetAsync(Entry, Package(), false, CancellationToken.None);
            stop.Cancel();
            try { await first; Assert(false, "Canceled reader should throw"); } catch (OperationCanceledException) { Assert(true, "One reader can cancel independently"); }
            release.SetResult();
            Assert((await second).Text == "Geteilt" && sharedCalls == 1, "Concurrent identical requests share one call; other reader survives cancellation");
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new ProgramDescriptions(Metadata, async (_, token) => { started.SetResult(); try { await Task.Delay(5000, token); return Json("late"); } catch (OperationCanceledException) { cancellationObserved.SetResult(); throw; } });
            using var close = new CancellationTokenSource(); var closed = canceled.GetAsync(Entry, Package(), false, close.Token);
            await started.Task; close.Cancel();
            try { await closed; } catch (OperationCanceledException) { }
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert(true, "Closing last reader cancels outstanding HTTP call");
            int serial = 0, maximum = 0;
            var serialService = new ProgramDescriptions(Metadata, async (_, token) => { maximum = Math.Max(maximum, ++serial); await Task.Delay(10, token); serial--; return Json("Seriell"); });
            await Task.WhenAll(Enumerable.Range(0, 6).Select(n => serialService.GetAsync(Entry with { Id = "winget:serial" + n }, Package(), false, CancellationToken.None)));
            Assert(maximum == 1, "Different descriptions have only one active provider request");
            int quotaBudgetCalls = 0;
            string budgetText = string.Concat(Enumerable.Repeat("public word ", 250)).Trim();
            var budget = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new(budgetText, "en-US"))), (uri, _) => { quotaBudgetCalls++; return Task.FromResult(Json(Query(uri, "q"))); });
            Assert((await budget.GetAsync(Entry, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.Translated, "Long public description translates within all bounds");
            int afterFirstBudget = quotaBudgetCalls;
            Assert((await budget.GetAsync(Entry with { Id = "winget:budget2" }, Package(), false, CancellationToken.None)).State == ProgramDescriptionState.Limit && quotaBudgetCalls == afterFirstBudget, "Own session budget refuses additional text before exceeding anonymous daily allowance");
            int partialCalls = 0;
            var partial = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new(budgetText, "en-US"))), (uri, _) => Task.FromResult(++partialCalls == 2 ? "{\"responseStatus\":500}" : Json(Query(uri, "q"))));
            Assert((await partial.GetAsync(Entry, Package(), false, CancellationToken.None)) is { State: ProgramDescriptionState.Unavailable } partialResult && partialResult.Text == budgetText && partialCalls == 2, "Failure in later chunk discards all partial translation and retains complete original");
            int cacheCalls = 0;
            var boundedCache = new ProgramDescriptions(Metadata, (_, _) => { cacheCalls++; return Task.FromResult(Json("Text")); });
            for (int i = 0; i < 129; i++) await boundedCache.GetAsync(Entry with { Id = "winget:cache" + i }, Package(), false, CancellationToken.None);
            await boundedCache.GetAsync(Entry with { Id = "winget:cache0" }, Package(), false, CancellationToken.None);
            Assert(cacheCalls == 130, "Session cache evicts old entries at 128 instead of growing indefinitely");
            int mismatchedCalls = metadataCalls;
            await translations.GetAsync(Entry, Package() with { Id = "Different.Package" }, false, CancellationToken.None);
            Assert(metadataCalls == mismatchedCalls, "Mismatched package identity cannot request localized metadata");
            Assert(ProgramDescriptions.Language("unconfirmed language") is null && ProgramDescriptions.Language("de-DE") == "de", "Language tags validated independently of UI language");
            var html = new ProgramDescriptions(Metadata, (_, _) => Task.FromResult(Json("Text &amp; Zeichen <b>kein Code</b>")));
            Assert((await html.GetAsync(Entry, Package(), false, CancellationToken.None)).Text == "Text & Zeichen <b>kein Code</b>", "HTML entities decoded as plain text, no HTML renderer");
            if (live)
            {
                var liveService = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("A free application for editing text documents.", "en-US"))));
                var liveResult = await liveService.GetAsync(Entry, Package(), false, CancellationToken.None);
                File.WriteAllText(Path.Combine(output, "MyMemory-Live.json"), JsonSerializer.Serialize(liveResult));
                Assert(liveResult.State == ProgramDescriptionState.Translated && liveResult.Text != liveResult.Original, "One public example translated by real MyMemory endpoint");
                var catalog = await ProgramDescriptionMetadataReader.ReadAsync("7zip.7zip", "26.05", "de", CancellationToken.None);
                File.WriteAllText(Path.Combine(output, "WinGet-Metadata-Live.json"), JsonSerializer.Serialize(catalog));
                Console.WriteLine("LIVE WinGet metadata " + (catalog is null ? "unavailable within read limit" : catalog.Original.Locale + " / " + catalog.Localized?.Locale));
                if (catalog is null)
                {
                    var trace = new List<string> { "Network available: " + OnlineConnection.Available };
                    await ProgramDescriptionManifests.ReadAsync("7zip.7zip", "26.05", "de", CancellationToken.None, async (uri, token) =>
                    {
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            var bytes = await CatalogWeb.ReadAsync(uri, new HashSet<string> { "raw.githubusercontent.com" }, 128 * 1024, token);
                            string body = Encoding.UTF8.GetString(bytes); trace.Add(uri + " / " + bytes.Length + " / " + watch.ElapsedMilliseconds + " ms / " + body);
                            return body;
                        }
                        catch (Exception e) { trace.Add(uri + " / " + watch.ElapsedMilliseconds + " ms / " + e); throw; }
                    });
                    File.WriteAllLines(Path.Combine(output, "WinGet-Metadata-Diagnose.txt"), trace);
                }
                Assert(catalog?.Original is { Text.Length: > 0, Locale: "en-US" }, "Actual exact-version public package metadata available even without COM registration");
                var packageEntry = Entry with { Id = "winget:7zip.7zip", Name = "7-Zip", PackageId = "7zip.7zip", Publisher = "Igor Pavlov" };
                var combined = await new ProgramDescriptions().GetAsync(packageEntry, new("7zip.7zip", "26.05", "live", DateTime.UtcNow), false, CancellationToken.None);
                File.WriteAllText(Path.Combine(output, "Vollstaendige-Anbieterprobe.json"), JsonSerializer.Serialize(combined));
                Assert(combined is { State: ProgramDescriptionState.Translated, Text.Length: > 0 }, "Entire production metadata-to-translation route works for public 7-Zip example");
            }
        }).GetAwaiter().GetResult();

        if (logicOnly) { Console.WriteLine("PASS " + count + " description provider, metadata and cancellation checks; no installers"); return 0; }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        var dispatcherErrors = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler capture = (_, args) => dispatcherErrors.Add(args.Exception);
        app.DispatcherUnhandledException += capture;
        var main = new MainWindow(Path.Combine(output, "UiFixture"), new AppSettings { Theme = "Standard", SizeIndex = 1 }) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Wait(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted && watch.Elapsed.TotalSeconds < 15) { Pump(); Thread.Sleep(3); } if (!task.IsCompleted) throw new TimeoutException("Description UI fixture timed out"); task.GetAwaiter().GetResult(); Pump(); }
        void Save(string name)
        {
            Pump(); var image = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
        try
        {
            main.Show(); main.ShowWindowsUpdatesPage(); Pump(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
            view.Assets.AllowOnlineIcons = false; var service = new InfoService(); view.Service = service;
            var themes = (ComboBox)main.FindName("ThemeBox");
            foreach (var theme in themes.Items.OfType<ComboBoxItem>().Where(x => x.Tag is string).ToArray())
            {
                themes.SelectedItem = theme;
                foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                    view.Descriptions = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("An application for editing documents.", "en-US"))), (_, _) => Task.FromResult(Json("Ein Programm zum Bearbeiten von Dokumenten.")));
                    var info = ProgramInfoCard.Open(view, Entry, view, false)!; Wait(info.Loading); Wait(info.DescriptionLoading);
                    var texts = UnifiedDialogAppearance.Children<TextBlock>(info.Frame).ToArray();
                    Assert(texts.Single(x => x.Name == "ProgramInfoDescription").Text == (english ? "An application for editing documents." : "Ein Programm zum Bearbeiten von Dokumenten."), "Description is translated only to requested UI language");
                    Assert(texts.Single(x => x.Name == "ProgramInfoDescriptionNote").Visibility == (english ? Visibility.Collapsed : Visibility.Visible), "Only automatic translations get visible attribution");
                    Assert(texts.Single(x => x.Name == "ProgramInfoAvailableVersion").Text == "1.0", "Version information remains independent");
                    Assert(Math.Abs(info.Frame.ActualWidth - (size == 1 ? 420 : 580)) < 1 && info.Frame.ActualHeight <= 540, "Original compact and large frame sizes preserved");
                    Assert(UnifiedDialogAppearance.Children<Button>(info.Frame).Any(b => b.Name == "ProgramInfoWebsite" && b.IsEnabled), "Existing actions stay usable");
                    if (theme.Tag?.ToString() == "Standard") Save("Beschreibung-" + (english ? "EN" : "DE") + "-" + size);
                    info.Close();
                }
            }
            page.SetLanguage(false); Pump();
            var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Descriptions = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("Original source", "en-US"))), (_, _) => blocked.Task);
            var closing = ProgramInfoCard.Open(view, Entry, view, false)!; Wait(closing.Loading);
            Assert(!closing.DescriptionLoading.IsCompleted && UnifiedDialogAppearance.Children<TextBlock>(closing.Frame).Single(x => x.Name == "ProgramInfoAvailableVersion").Text == "1.0", "Slow translation does not delay version and action display");
            closing.Close(); blocked.SetResult(Json("Verspätete Antwort")); Wait(closing.DescriptionLoading);
            Assert(!closing.IsOpen && main.IsVisible, "Late translation cannot reopen closed program info");
            foreach (bool languageChange in new[] { true, false })
            {
                page.SetLanguage(false); Pump();
                var later = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                view.Descriptions = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("Original during transition", "en-US"))), (_, _) => later.Task);
                view.ShowInfo(Entry, view, false); Pump(); var beforeChange = view.Info!; Wait(beforeChange.Loading);
                if (languageChange) page.SetLanguage(true); else view.Visibility = Visibility.Collapsed;
                Pump(); later.SetResult(Json("Alte Antwort")); Wait(beforeChange.DescriptionLoading);
                Assert(!beforeChange.IsOpen, "Language/page change closes current description and ignores late translation");
                view.Visibility = Visibility.Visible; Pump();
            }
            page.SetLanguage(false); Pump();
            foreach (int size in new[] { 1, 4 })
            {
                ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; Pump();
                view.Descriptions = new ProgramDescriptions((_, _, _, _) => Task.FromResult<ProgramDescriptionMetadata?>(new(new("Complete original description", "en-US"))), (_, _) => throw new HttpRequestException("offline"));
                var offlineInfo = ProgramInfoCard.Open(view, Entry, view, false)!; Wait(offlineInfo.Loading); Wait(offlineInfo.DescriptionLoading);
                var offlineTexts = UnifiedDialogAppearance.Children<TextBlock>(offlineInfo.Frame).ToArray();
                Assert(offlineTexts.Single(x => x.Name == "ProgramInfoDescription").Text == "Complete original description" && offlineTexts.Single(x => x.Name == "ProgramInfoDescriptionNote").Text.Contains("Internetverbindung"), "Offline UI retains original plus clear themed notice");
                Assert(offlineTexts.Single(x => x.Name == "ProgramInfoAvailableVersion").Text == "1.0", "Translation outage never becomes a version-check error");
                offlineInfo.Close();
            }
            Assert(service.Executed == 0, "No installer or update executed in description UI tests");
        }
        finally { main.Close(); Pump(); app.DispatcherUnhandledException -= capture; }
        Assert(dispatcherErrors.Count == 0, "No dispatcher exceptions were suppressed by the outer test host: " + dispatcherErrors.FirstOrDefault());
        Console.WriteLine("PASS " + count + " description translation, cancellation, metadata and UI checks; seven themes, DE/EN, compact/large; no installers");
        return 0;
    }
    private sealed class InfoService : IProgramInstallService
    {
        internal int Executed;
        public Task<ProgramInventory> InspectAsync(CancellationToken token) => Task.FromResult(new ProgramInventory(true, new Dictionary<string, string?>()));
        public Task<ProgramPackage?> ResolveAsync(ProgramEntry entry, CancellationToken token) => Task.FromResult<ProgramPackage?>(Package());
        public Task<int> InstallAsync(ProgramEntry entry, ProgramPackage package, Action started) { Executed++; return Task.FromResult(0); }
        public Task<int> UpgradeAsync(ProgramEntry entry, ProgramPackage package, Action started) { Executed++; return Task.FromResult(0); }
        public Task<IReadOnlyList<ProgramEntry>> SearchAsync(string query, CancellationToken token) => Task.FromResult<IReadOnlyList<ProgramEntry>>([]);
        public void OpenWebsite(ProgramEntry entry) { }
    }
}
