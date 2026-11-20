using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class AppUpdateChannelTests
{
    internal static int Run(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        UiSoundService.MuteForTests = true;
        int checks = 0;
        void Assert(bool ok, string text) { if (!ok) throw new Exception(text); checks++; }
        var stable = AppVersion.Installed;
        var upcoming = new Version(stable.Major, stable.Minor, stable.Build + 1);
        string hotfix9 = $"PowerLeitstand-PRO-{stable}-Hotfix-9.zip";
        string hotfix10 = $"PowerLeitstand-PRO-{stable}-Hotfix-10.zip";
        string beta1 = $"PowerLeitstand-PRO-{upcoming}-Beta-1.zip";
        string beta2 = $"PowerLeitstand-PRO-{upcoming}-Beta-2.zip";
        object Asset(string tag, string name, string hash = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") =>
            new { name, browser_download_url = $"https://github.com/{ProjectLinks.RepositoryPath}/releases/download/{tag}/{name}", digest = hash };
        object Release(string tag, bool prerelease, string body, params object[] assets) =>
            new { tag_name = tag, draft = false, prerelease, body, assets };
        string stableTag = "v" + stable;
        string betaTag = $"v{upcoming}-beta.2";
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            Release(stableTag, false, "## Hotfix 9\nÄlterer Hinweis\n## Hotfix 10\nKorrigiert portable Programme.",
                Asset(stableTag, "PowerLeitstand-PRO.exe"), Asset(stableTag, hotfix9), Asset(stableTag, hotfix10),
                Asset(stableTag, $"PowerLeitstand-PRO-{stable}-Hotfix-11.zip", "invalid"), Asset("unrelated", $"PowerLeitstand-PRO-{stable}-Hotfix-12.zip")),
            Release($"v{upcoming}-beta.1", true, "Testfassung", Asset($"v{upcoming}-beta.1", beta1)),
            Release(betaTag, true, "Vorabversion für den nächsten Stand.", Asset(betaTag, beta2)),
            Release($"v{upcoming}-beta.3", false, "", Asset($"v{upcoming}-beta.3", $"PowerLeitstand-PRO-{upcoming}-Beta-3.zip"))
        }));
        var releases = UpdateReleaseCatalog.Parse(doc.RootElement);
        Assert(releases.Count == 5, "Reject invalid checksums, wrong tag URLs and unsignalled prereleases.");
        var betaBuilds = UpdateReleaseCatalog.Visible(releases, new(stable), UpdatePreference.Beta);
        var hotfixBuilds = UpdateReleaseCatalog.Visible(releases, new(stable), UpdatePreference.Hotfixes);
        Assert(betaBuilds.Count == 1 && betaBuilds[0].Update.Channel == UpdateChannel.Beta && betaBuilds[0].Update.Revision == 2,
            "Beta shows only the newest beta.");
        Assert(hotfixBuilds.Count == 2 && hotfixBuilds.Any(r => r.Update.Channel == UpdateChannel.Stable)
            && hotfixBuilds.Any(r => r.Update.Channel == UpdateChannel.Hotfix && r.Update.Revision == 10), "Stable plus the newest valid hotfix.");
        Assert(hotfixBuilds.Single(r => r.Update.Channel == UpdateChannel.Hotfix).Notes == "Korrigiert portable Programme.", "Hotfix-specific release notes.");
        Assert(UpdateReleaseCatalog.Visible(releases.Where(r => r.Update.Channel != UpdateChannel.Beta).ToArray(), new(stable), UpdatePreference.Beta).Count == 0,
            "Beta remains empty when no beta is published.");
        Assert(UpdateReleaseCatalog.Visible(releases, new(stable, UpdateChannel.Hotfix, 10), UpdatePreference.Hotfixes).All(r => r.Update.Channel == UpdateChannel.Stable), "An installed hotfix is not offered again.");
        Assert(UpdateReleaseCatalog.Visible(releases, new(stable), UpdatePreference.Stable).All(r => r.Update.Channel == UpdateChannel.Stable), "Stable is the default selection.");
        Assert(new AppBuild(upcoming).CompareTo(new(upcoming, UpdateChannel.Beta, 2)) > 0, "Stable supersedes beta for the same version.");
        Assert(AppVersion.ReadBuild(typeof(AppVersion).Assembly.Location) == AppVersion.Build, "PE metadata identifies the compiled build.");
        using (var hotfixRelease = JsonDocument.Parse(JsonSerializer.Serialize(new[]
        {
            Release($"v{stable}-hotfix.10", true, "Separate Hotfix", Asset($"v{stable}-hotfix.10", hotfix10))
        }))) Assert(UpdateReleaseCatalog.Parse(hotfixRelease.RootElement).Single().Update.Build == new AppBuild(stable, UpdateChannel.Hotfix, 10),
            "A separate GitHub Hotfix release is recognised.");
        var settingsPath = Path.Combine(output, "update-choice.json");
        new AppSettings { UpdatePreference = UpdatePreference.Hotfixes }.Save(settingsPath);
        Assert(AppSettings.Load(settingsPath).UpdatePreference == UpdatePreference.Hotfixes, "Preference is persisted.");
        var zipPath = Path.Combine(output, "update-fixture.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var stream = new StreamWriter(zip.CreateEntry("PowerLeitstand-PRO.exe").Open()))
                stream.Write("testing");
            zip.CreateEntry("../outside-file");
        }
        var extracted = Path.Combine(output, "staged.exe");
        string digest = Task.Run(() => AppBackupStore.HashAsync(zipPath)).GetAwaiter().GetResult();
        Task.Run(() => UpdateService.ExtractUpdateAsync(zipPath, extracted, digest, CancellationToken.None)).GetAwaiter().GetResult();
        var invalidDestination = Path.Combine(output, "invalid-stage.exe");
        try
        {
            Task.Run(() => UpdateService.ExtractUpdateAsync(zipPath, invalidDestination, new string('0', 64), CancellationToken.None)).GetAwaiter().GetResult();
            throw new Exception("A wrong checksum was accepted.");
        }
        catch (InvalidDataException) { Assert(!File.Exists(invalidDestination), "A corrupt archive is rejected before any executable is created."); }
        Assert(File.ReadAllText(extracted) == "testing" && !File.Exists(Path.GetFullPath(Path.Combine(output, "../outside-file"))), "Only the exact executable is extracted.");

        var main = new MainWindow(Path.Combine(output, "BellFixture"),
            new AppSettings { SizeIndex = 1, UpdatePreference = UpdatePreference.Beta }) { ShowActivated = false };
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); main.UpdateLayout(); }
        void Wait(Func<bool> done)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < end) { Pump(); Thread.Sleep(10); }
            Assert(done(), "Expected UI state.");
        }
        void Screenshot(Visual view, Size size, string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name));
            png.Save(file);
        }
        try
        {
            main.Show(); Pump();
            main.LoadBellReleases = _ => Task.FromResult(releases);
            var button = (Button)main.FindName("UpdateNoticeButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var popup = (Popup)main.FindName("OptionalUpdatePopup");
            var text = (TextBlock)main.FindName("UpdateOptionalCount");
            var icon = (System.Windows.Shapes.Path)main.FindName("UpdateNoticeIcon");
            Wait(() => popup.IsOpen && PresentationSource.FromVisual(popup.Child) is not null);
            Assert(text.IsVisible && text.Text == "1" && Equals(icon.Stroke, main.FindResource("HeaderBellCurrentBrush")), "Only the beta is counted and the bell remains green.");
            Assert(((TextBlock)main.FindName("OptionalUpdateStatus")).Text == "Beta-Vorabversionen", "The popup names only the selected channel.");
            var child = (FrameworkElement)popup.Child;
            Assert(UnifiedDialogAppearance.Children<TextBlock>(child).Any(t => t.Text == "Vorabversion für den nächsten Stand.")
                && UnifiedDialogAppearance.Children<TextBlock>(child).All(t => !t.Text.Contains("Hotfix") && !t.Text.Contains("Stabil")),
                "The popup shows the beta description without entries from other channels.");
            var childImage = new RenderTargetBitmap((int)Math.Ceiling(child.ActualWidth), (int)Math.Ceiling(child.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            childImage.Render(child);
            var mainImage = new RenderTargetBitmap((int)Math.Ceiling(main.ActualWidth), (int)Math.Ceiling(main.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            mainImage.Render(main);
            var composite = new DrawingVisual();
            using (var drawing = composite.RenderOpen())
            {
                drawing.DrawImage(mainImage, new Rect(new Point(), main.RenderSize));
                drawing.DrawImage(childImage, new Rect(main.PointFromScreen(child.PointToScreen(new Point())), child.RenderSize));
            }
            Screenshot(composite, main.RenderSize, "Glocke-Optionale-Updates.png");
            popup.IsOpen = false;
            var history = releases.Concat(Enumerable.Range(1, 4).Select(n =>
                new UpdateRelease(new(new Version(stable.Major, stable.Minor, Math.Max(0, stable.Build - n)), "", ""),
                    "", DateTimeOffset.MinValue))).ToArray();
            UpdatePreference? saved = null;
            ScrollViewer? FindScroll(DependencyObject parent)
            {
                if (parent is ScrollViewer scroll) return scroll;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } childScroll) return childScroll;
                return null;
            }
            var dialog = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false,
                load: _ => Task.FromResult<IReadOnlyList<UpdateRelease>>(history), preference: UpdatePreference.Hotfixes, savePreference: p => saved = p) { Owner = main, ShowActivated = false };
            try
            {
                dialog.Show();
                Wait(() => dialog.ReleaseLoadSucceeded);
                var list = (ListBox)dialog.FindName("VersionList");
                Assert(list.Items.Count == 6 && list.SelectedItem is UpdateManagerWindow.Choice { Payload: UpdateRelease r } && r.Update.Channel == UpdateChannel.Hotfix,
                    "The hotfix setting shows the stable release and selects the applicable hotfix.");
                dialog.UpdateLayout();
                Assert(dialog.ActualWidth == 548 && dialog.ActualHeight == 540 && dialog.ResizeMode == ResizeMode.NoResize,
                    "The update manager opens at a fixed size.");
                var scroll = FindScroll(list) ?? throw new Exception("Version list scroller missing.");
                Screenshot(dialog, dialog.RenderSize, "Viewport.png");
                Assert(scroll.ViewportHeight == 3 && scroll.ExtentHeight == 6,
                    $"Exactly three full rows: viewport {scroll.ViewportHeight}, extent {scroll.ExtentHeight}, height {list.ActualHeight}, content scrolling {scroll.CanContentScroll}.");
                scroll.ScrollToEnd();
                Wait(() => scroll.VerticalOffset == 3);
                scroll.ScrollToTop();
                Wait(() => scroll.VerticalOffset == 0);
                var beta = (RadioButton)dialog.FindName("BetaChannel");
                Assert((string)beta.Content == "Beta", "The third selector is labelled Beta.");
                Screenshot(dialog, dialog.RenderSize, "Update-Auswahl.png");
                beta.IsChecked = true;
                Assert(list.Items.Count == 1 && saved == UpdatePreference.Beta
                    && list.Items[0] is UpdateManagerWindow.Choice { Payload: UpdateRelease b } && b.Update.Channel == UpdateChannel.Beta,
                    "Beta hides stable and hotfix rows and the selection is saved.");
                dialog.UpdateLayout();
                Screenshot(dialog, dialog.RenderSize, "Update-Auswahl-Beta.png");
                ((RadioButton)dialog.FindName("StableChannel")).IsChecked = true;
                Wait(() => list.Items.Count == 5 && saved == UpdatePreference.Stable);
                dialog.UpdateLayout();
                Screenshot(dialog, dialog.RenderSize, "Update-Auswahl-Nur-Stabil.png");
            }
            finally { dialog.Close(); }

            var noBeta = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false,
                load: _ => Task.FromResult<IReadOnlyList<UpdateRelease>>(history.Where(r => r.Update.Channel != UpdateChannel.Beta).ToArray()),
                preference: UpdatePreference.Beta) { Owner = main, ShowActivated = false };
            try
            {
                noBeta.Show();
                Wait(() => noBeta.ReleaseLoadSucceeded);
                Assert(((ListBox)noBeta.FindName("VersionList")).Items.Count == 0
                    && ((TextBlock)noBeta.FindName("LatestText")).Text == "Keine Beta verfügbar"
                    && !((TextBlock)noBeta.FindName("InstalledText")).IsVisible,
                    "No stable version is shown in the empty beta view.");
                noBeta.UpdateLayout();
                Screenshot(noBeta, noBeta.RenderSize, "Keine-Beta-verfügbar.png");
            }
            finally { noBeta.Close(); }
            main.LoadBellReleases = _ => Task.FromResult<IReadOnlyList<UpdateRelease>>([new(new(upcoming, "", ""), "", DateTimeOffset.UtcNow)]);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!popup.IsOpen && ((TextBlock)main.FindName("BellStatusText")).Text == "Keine Beta verfügbar",
                "The bell does not offer the newer stable release in beta mode.");

            ((Button)main.FindName("SettingsHeaderButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            var german = (Button)main.FindName("GermanButton");
            Assert(UnifiedDialogAppearance.Children<System.Windows.Shapes.Rectangle>(german)
                .Count(r => r.IsVisible && r.ActualHeight >= 3) == 3, "All three German flag stripes are visible in settings.");
            Screenshot(main, main.RenderSize, "Einstellungen-DE-Flagge.png");
            Console.WriteLine($"PASS {checks} update channel, archive and UI checks.");
            return 0;
        }
        finally { main.Close(); Pump(); }
    }
}
