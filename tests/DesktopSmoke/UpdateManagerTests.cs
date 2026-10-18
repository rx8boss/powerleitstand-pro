using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class UpdateManagerTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        int checks = 0, cases = 0;
        var errors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new TimeoutException("Fixture task");
            task.GetAwaiter().GetResult(); Pump();
        }
        void Save(Window w, string name)
        {
            w.UpdateLayout(); var bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); bmp.Render(w);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp)); using var f = File.Create(Path.Combine(output, name + ".png")); png.Save(f);
            var native = new PngBitmapEncoder(); native.Frames.Add(BitmapFrame.Create(NativeWindowCapture.Capture(w)));
            using var full = File.Create(Path.Combine(output, name + "-Fenster.png")); native.Save(full);
        }
        T Result<T>(Task<T> task) { Wait(task); return task.Result; }
        var store = new AppBackupStore(Path.Combine(output, "FixtureBackups"));
        var source = Path.Combine(output, "fixture.exe");
        File.WriteAllBytes(source, Enumerable.Range(0, 10000).Select(i => (byte)i).ToArray());
        try
        {
            var fixtureNotes = "Beispieldaten\n" + string.Join("\n", Enumerable.Range(1, 50).Select(n => "• Änderung " + n + " dieser Version"));
            for (var i = 0; i < 6; i++) Wait(store.CreateAsync(source, "1.1." + i, fixtureNotes));
            var backups = store.List();
            Assert(backups.Count == 4 && backups.Select(b => b.Version).SequenceEqual(new[] { "1.1.5", "1.1.4", "1.1.3", "1.1.2" }), "four newest verified backups retained");
            Assert(backups.All(b => File.Exists(b.BinaryPath)), "backup binaries and manifests exist");
            Assert(Result(store.VerifyAsync(backups[0])), "stored hash matches binary");
            File.AppendAllText(backups[0].BinaryPath, "corrupt");
            Assert(!Result(store.VerifyAsync(backups[0])), "modified backup rejected");
            File.Copy(source, backups[0].BinaryPath, true);
            try { store.Delete(backups[0] with { Folder = Path.GetDirectoryName(store.Root)! }); throw new Exception("Traversal accepted"); }
            catch (InvalidDataException) { Assert(File.Exists(source), "delete outside backup root rejected"); }
            var sentinel = Path.Combine(backups[0].Folder, "unrelated.txt"); File.WriteAllText(sentinel, "keep");
            try { store.Delete(backups[0]); throw new Exception("Unknown file removed"); }
            catch (InvalidDataException) { Assert(File.Exists(sentinel) && File.Exists(backups[0].BinaryPath), "unknown backup contents protected from deletion"); }
            File.Delete(sentinel);
            var missing = Path.Combine(output, "missing.exe");
            try { Wait(store.CreateAsync(missing, "1.1.9", "")); throw new Exception("Missing source accepted"); }
            catch (FileNotFoundException) { Assert(store.List().Count == 4 && Directory.GetDirectories(store.Root).Length == 4, "failed snapshot preserves retention and removes incomplete folder"); }

            object Release(string tag, bool draft = false, bool prerelease = false, bool badHash = false) => new
            {
                tag_name = tag, draft, prerelease, body = "## Beispieldaten\n" + string.Join("\n", Enumerable.Range(1, 50).Select(n => "• Verbesserung " + n + ": kompakter Hinweis zur ausgewählten Version")),
                published_at = "2026-10-18T12:00:00Z", assets = new[] { new { name = "PowerLeitstand-PRO.exe", digest = badHash ? "bad" : "sha256:" + new string('a', 64), browser_download_url = "https://github.com/rx8boss/powerleitstand-pro/releases/download/" + tag + "/PowerLeitstand-PRO.exe" } }
            };
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new[] { Release("v1.1.4"), Release("v1.1.3"), Release("v1.1.2"), Release("v1.1.1"), Release("v1.1.0"), Release("v2.0.0", draft: true), Release("v3.0.0", prerelease: true), Release("v4.0.0", badHash: true) }));
            var releases = UpdateReleaseCatalog.Parse(json.RootElement);
            Assert(releases.Count == 4 && releases.Last().Update.Version == new Version(1, 1, 1), "catalog filters drafts prereleases unsupported versions and invalid digests");
            Assert(releases[0].Update.Notes == releases[0].Notes && releases[0].Notes.Contains("50"), "all version-specific notes retained for download choice");

            foreach (var theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            foreach (var english in new[] { false, true })
            foreach (var small in new[] { false, true })
            {
                bool busy = false;
                var w = new UpdateManagerWindow(ThemePalette.For(theme), english, () => busy, store, _ => Task.FromResult(releases), autoLoad: false) { ShowActivated = false };
                try
                {
                    w.Show(); Wait(w.ReloadAsync());
                    if (small) { w.Width = 420; w.Height = 400; }
                    Pump(); w.UpdateLayout();
                    var list = (ListBox)w.FindName("VersionList"); var notes = (ScrollViewer)w.FindName("NotesScroll");
                    Wait(w.PendingSelection);
                    Assert(w.Width <= 620 && w.Height <= 480, "compact window size");
                    Assert(NativeWindowCapture.HasNoNativeTopStrip(w), "native window has no extra top strip");
                    Assert(NativeWindowCapture.LeftResizeHit(w) == 10, "native left border still resizes");
                    var back = (Button)w.FindName("BackButton"); var close = (Button)w.FindName("HeaderCloseButton");
                    Assert(back.Width == 28 && back.Height == 28 && close.ActualWidth == 28 && close.ActualHeight == 28, "compact square header buttons");
                    Assert(back.Content is System.Windows.Shapes.Path && close.Content is System.Windows.Shapes.Path, "header icons use vector geometry");
                    Assert(!w.IsNotesView && ((FrameworkElement)w.FindName("DetailPanel")).Visibility == Visibility.Collapsed, "notes hidden behind button");
                    ((Button)w.FindName("ChangesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(w.IsNotesView && ((Button)w.FindName("InstallButton")).Visibility == Visibility.Collapsed, "changes button opens same-window notes view");
                    Assert(list.Items.Count == 4 && ((TextBlock)w.FindName("NotesText")).Text.Contains("50"), "release list and full notes " + theme + " / " + english + " / " + small);
                    notes.ScrollToTop(); Pump();
                    Assert(notes.ScrollableHeight > 0 && ((Button)w.FindName("NotesDown")).Visibility == Visibility.Visible && ((Button)w.FindName("NotesUp")).Visibility == Visibility.Collapsed, "initial overflow arrows");
                    notes.ScrollToBottom(); Pump();
                    Assert(((Button)w.FindName("NotesUp")).Visibility == Visibility.Visible && ((Button)w.FindName("NotesDown")).Visibility == Visibility.Collapsed, "bottom overflow arrows");
                    Assert(notes.ComputedVerticalScrollBarVisibility == Visibility.Collapsed, "notes scrollbar remains invisible");
                    if (!english && !small && theme == "Standard") Save(w, "Aenderungen-Standard");
                    ((Button)w.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(!w.IsNotesView && !w.IsRecoveryView && list.SelectedIndex == 0, "back from changes retains version selection");
                    foreach (var name in new[] { "ChangesButton", "InstallButton", "CloseButton", "HeaderCloseButton" })
                    {
                        var el = (FrameworkElement)w.FindName(name); var r = el.TransformToAncestor(w).TransformBounds(new Rect(el.RenderSize));
                        Assert(r.Left >= 0 && r.Top >= 0 && r.Right <= w.ActualWidth + 1 && r.Bottom <= w.ActualHeight + 1, "footer/header reachable " + name);
                    }
                    busy = true; typeof(UpdateManagerWindow).GetMethod("RefreshActions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(w, null);
                    Assert(!((Button)w.FindName("InstallButton")).IsEnabled, "active operation blocks installation"); busy = false;
                    typeof(UpdateManagerWindow).GetMethod("RefreshActions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(w, null);
                    notes.ScrollToTop(); Pump();
                    if (!english && !small && theme is "Standard" or "Classic" or "Neon Blau") Save(w, "Updates-" + theme.Replace(" ", "-"));
                    if (!english && small && theme == "Standard") Save(w, "Updates-Klein");
                    w.ShowRecoveryView(); Wait(w.PendingSelection); Pump();
                    Assert(w.IsRecoveryView && list.Items.Count == 4 && ((Button)w.FindName("InstallButton")).IsEnabled, "same window switches to four valid backups");
                    Assert(((Button)w.FindName("DeleteBackupButton")).Visibility == Visibility.Visible, "selected backup can be deleted");
                    if (!english && !small && theme is "Standard" or "Classic" or "Neon Blau") Save(w, "Sicherungen-" + theme.Replace(" ", "-"));
                    if (!english && small && theme == "Standard") Save(w, "Sicherungen-Klein");
                                        list.SelectedIndex = 1; Wait(w.PendingSelection);
                    var selected = list.SelectedItem;
                    w.ShowNotesView(); Pump();
                    ((Button)w.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(w.IsRecoveryView && !w.IsNotesView && ReferenceEquals(selected, list.SelectedItem), "notes back retains selected backup");
                    cases++;
                }
                finally { w.Close(); Pump(); }
            }
                        var owner = new Window { Width = 540, Height = 430, ShowActivated = false }; owner.Show(); Pump();
            var owned = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false, store, _ => Task.FromResult(releases), false) { Owner = owner, ShowActivated = false };
            owned.Show(); Pump();
            Assert(owned.Width < owner.ActualWidth && owned.Height < owner.ActualHeight, "manager is smaller than owner window");
            owned.Close(); owner.Close();
            var minimum = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false, store, _ => Task.FromResult(releases), false) { ShowActivated = false };
            minimum.Show(); Wait(minimum.ReloadAsync()); minimum.Width = 340; minimum.Height = 340; Pump();
            foreach (var name in new[] { "InstallButton", "CloseButton", "HeaderCloseButton" })
            {
                var el = (FrameworkElement)minimum.FindName(name); var r = el.TransformToAncestor(minimum).TransformBounds(new Rect(el.RenderSize));
                Assert(r.Left >= 0 && r.Top >= 0 && r.Right <= minimum.ActualWidth + 1 && r.Bottom <= minimum.ActualHeight + 1, "minimum-size reachable " + name);
            }
            Save(minimum, "Updates-Minimum"); minimum.Close();
            var broken = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false, store, _ => throw new IOException("fixture offline"), false) { ShowActivated = false };
            broken.Show(); Wait(broken.ReloadAsync()); broken.ShowRecoveryView(); Wait(broken.PendingSelection);
            Assert(((ListBox)broken.FindName("VersionList")).Items.Count == 4 && ((Button)broken.FindName("InstallButton")).IsEnabled, "offline releases do not disable local restoration");
            File.AppendAllText(store.List()[0].BinaryPath, "bad"); broken.ShowRecoveryView(); Wait(broken.PendingSelection);
            Assert(!((Button)broken.FindName("InstallButton")).IsEnabled && ((Button)broken.FindName("DeleteBackupButton")).IsEnabled, "damaged backup cannot restore but remains deletable");
            File.Copy(source, store.List()[0].BinaryPath, true); broken.Close();

            // Exercise the actual delete button and its confirmation using only this process's fixture dialog.
            var deleting = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false, store, _ => Task.FromResult(releases), false) { ShowActivated = false };
            deleting.Show(); deleting.ShowRecoveryView(); Wait(deleting.PendingSelection);
            void ReplyToConfirmation(bool yes)
            {
                var answer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                answer.Tick += (_, _) =>
                {
                    var dialog = app.Windows.OfType<ThemedMessageBoxWindow>().FirstOrDefault(w => w.IsLoaded);
                    if (dialog is null) return;
                    answer.Stop(); ((Button)dialog.FindName(yes ? "ConfirmButton" : "CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                answer.Start();
            }
            ReplyToConfirmation(false); ((Button)deleting.FindName("DeleteBackupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert(store.List().Count == 4, "delete confirmation cancellation preserves all backups");
            ReplyToConfirmation(true); ((Button)deleting.FindName("DeleteBackupButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deletionDeadline = DateTime.UtcNow.AddSeconds(10);
            while (store.List().Count == 4 && DateTime.UtcNow < deletionDeadline) { Pump(); Thread.Sleep(10); }
            Wait(deleting.PendingSelection);
            Assert(store.List().Count == 3, "actual delete button removes the confirmed fixture backup"); deleting.Close();

            var picking = new UpdateManagerWindow(ThemePalette.For("Standard"), false, () => false, store, _ => Task.FromResult(releases), false);
            picking.Loaded += async (_, _) =>
            {
                picking.ShowRecoveryView(); await picking.PendingSelection;
                ReplyToConfirmation(true); ((Button)picking.FindName("InstallButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            var accepted = picking.ShowDialog() == true;
            Assert(accepted && picking.SelectedUpdate?.SourceBackup is not null, "restore selection and confirmation return selected local backup without installing it");

            // Real filesystem recovery with a persistent backup, including the failure path.
            foreach (var success in new[] { true, false })
            {
                var target = Path.Combine(output, "recovery-target.exe"); var staged = target + ".new";
                File.Copy(source, target, true); File.WriteAllText(staged, "new candidate");
                var backup = store.List()[0]; bool restarted = false;
                var operation = UpdateRecovery.InstallAsync(target, staged, backup.BinaryPath, () => Task.FromResult(success), () => Task.CompletedTask, () => restarted = true, retainBackup: true);
                Wait(operation);
                Assert(File.Exists(backup.BinaryPath) && operation.Result.Restored == !success && restarted == !success, "persistent backup survives " + (success ? "successful install" : "rollback"));
                Assert(success ? File.ReadAllText(target) == "new candidate" : File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(source)), "replacement/restoration bytes verified");
            }
            var countBeforeDelete = store.List().Count; var deleted = store.List().Last(); store.Delete(deleted);
            Assert(store.List().Count == countBeforeDelete - 1 && !Directory.Exists(deleted.Folder), "selected backup deletion removes only its own directory");
            Assert(errors.Count == 0, "no unhandled dispatcher errors");
            Console.WriteLine($"PASS UPDATE MANAGER: {checks} assertions, {cases} theme/language/size pairs in all three views. Fixture data and directories only; no power actions or installed EXE replacement.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
