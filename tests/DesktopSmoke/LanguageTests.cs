using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class LanguageTests
{
    internal static int Run(App app, string output)
    {
        UiSoundService.MuteForTests = true;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        int checks = 0; var errors = new List<Exception>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
        void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        void Wait(Task task) { while (!task.IsCompleted) { Pump(); Thread.Sleep(5); } task.GetAwaiter().GetResult(); Pump(); }
        void Save(Window w, string name)
        {
            w.UpdateLayout();
            // PrintWindow may return a black surface before the first compositor frame.
            // Render the live WPF tree synchronously for the language/layout captures.
            var image = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(w);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, name + ".png")); png.Save(file);
        }
        IEnumerable<DependencyObject> Tree(DependencyObject node)
        {
            yield return node;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
        void Audit(Window w, string name, bool english)
        {
            Pump();
            var texts = Tree(w).OfType<FrameworkElement>().Where(e => e.IsVisible)
                .Select(e => (e is TextBlock t ? t.Text : e is ContentControl c && c.Content is string s ? s : "") + " " + (e.ToolTip as string ?? "")).Where(s => s.Trim().Length > 0).Distinct().ToArray();
            File.WriteAllLines(Path.Combine(output, name + ".txt"), texts);
            if (english)
            {
                var german = new Regex(@"\b(Abbrechen|Schließen|Benutzerübersicht|Benutzer erstellen|Benutzer bearbeiten|Herunterfahren|Neustart|Sperren|Ordner öffnen|Einstellungen|Aktualisieren|Minimieren|Beenden|Speichern|Löschen|Warnung aktiv|Reparaturprotokoll|Reparatur auswählen|Live-Diagnose folgt|Glocken-Status|Zurück)\b");
                Assert(!texts.Any(s => german.IsMatch(s)), "English UI captions and tooltips " + name + ": " + string.Join(" | ", texts.Where(s => german.IsMatch(s))));
            }
            Save(w, name);
        }
        try
        {
            Assert(AppVersion.Installed.ToString() == "1.1.4" && AppVersion.UserAgent.EndsWith("/1.1.4"), "assembly version and update user agent agree");
            Assert(AppReleaseNotes.Select(AppReleaseNotes.Current, false) == AppReleaseNotes.German.Trim(), "complete German changelog selected");
            Assert(AppReleaseNotes.Select(AppReleaseNotes.Current, true) == AppReleaseNotes.English.Trim(), "complete English changelog selected");
            Assert(AppReleaseNotes.German.Count(c => c == '•') == AppReleaseNotes.English.Count(c => c == '•'), "equal number of release changes in both languages");
            Assert(AppReleaseNotes.Select("## English\r\nOnly EN\r\n## Deutsch\r\nNur DE", true) == "Only EN", "language section order and CRLF supported");
            Assert(AppReleaseNotes.Select("Original without translation", true).Contains("Original release notes:"), "legacy untranslated notes explicitly identified");
            Assert(AppReleaseNotes.Select("## Deutsch\nNur DE", true).Contains("translation is not available"), "missing language does not produce blank notes");
            Assert(UiText.Translate("Ungültige Sicherung.", true) == "Invalid backup." && UiText.Translate("Ungültige Sicherung.", false) == "Ungültige Sicherung.", "application errors localized");
            Assert(UiText.Translate("Windows raw text", true) == "Windows raw text", "external technical evidence preserved");
            var originalLog = new RepairLogEntry("12:00", "SFC", "FEHLER", "CBS.log konnte nicht gelesen werden.");
            Assert(originalLog.WithLanguage(true).DisplayLevel == "ERROR" && originalLog.WithLanguage(true).DisplayMessage == "CBS.log could not be read." && originalLog.WithLanguage(true).ToLine() == originalLog.ToLine(), "repair log display translates without changing original evidence");
            File.WriteAllText(Path.Combine(output, "release-v1.1.4.md"), "# PowerLeitstand PRO 1.1.4\n\n" + AppReleaseNotes.Current);

            foreach (var theme in new[] { "Standard", "Classic" })
            foreach (var english in new[] { false, true })
            foreach (var small in new[] { false, true })
            {
                var main = new MainWindow(Path.Combine(output, "Fixture")) { ShowActivated = false };
                try
                {
                    typeof(MainWindow).GetField("_english", flags)!.SetValue(main, english);
                    typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                    var themes = (ComboBox)main.FindName("ThemeBox");
                    themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().First(i => (i.Tag?.ToString() ?? i.Content?.ToString()) == theme);
                    main.Show(); Pump(); main.Width = small ? 860 : 1200; main.Height = small ? 620 : 760; Pump();
                    // No historical log or account descriptions in this caption audit.
                    foreach (var field in new[] { "_eventLogEntries", "_repairLogEntries" })
                        ((System.Collections.IList)typeof(MainWindow).GetField(field, flags)!.GetValue(main)!).Clear();
                    foreach (var grid in Tree(main).OfType<DataGrid>()) grid.ItemsSource = null;
                    var suffix = theme + "-" + (english ? "EN" : "DE") + (small ? "-Klein" : "-Gross");
                    Audit(main, "Seite1-" + suffix, english);
                    Assert(new[] { "TimerMinusHourButton", "CompactTimerMinusHour", "StandardTimerMinusHour" }.All(name => ((Button)main.FindName(name)).Content?.ToString() == (english ? "−1 hr" : "−1 Std")), "all responsive timer variants translated " + suffix);
                    main.ShowUsersPage(); Audit(main, "Seite2-" + suffix, english);
                    main.ShowRepairPage(); Audit(main, "Seite3-" + suffix, english);
                    typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(main, new object[] { true, false });
                    Audit(main, "Optionen-" + suffix, english);
                    Assert(((TextBlock)main.FindName("DoneLabel")).IsVisible, "options Done reachable " + suffix);
                    typeof(MainWindow).GetMethod("SetSettingsOverlay", flags)!.Invoke(main, new object[] { false, false });
                    // A second language application must preserve theme IDs and selected design.
                    typeof(MainWindow).GetMethod("ApplyLanguage", flags)!.Invoke(main, null);
                    Assert(main.CurrentThemeName == theme, "theme identity preserved " + suffix);
                    if (theme == "Standard" && !small)
                    {
                        var user = new UserEditorDialog(main, null) { ShowActivated = false }; user.Show(); Audit(user, "Benutzer-" + suffix, english); user.Close();
                        var confirm = new RepairConfirmWindow("DISM /Online /Cleanup-Image /RestoreHealth", ThemePalette.For(theme), english) { Owner = main, ShowActivated = false };
                        confirm.Show(); Audit(confirm, "Reparaturdialog-" + suffix, english); confirm.Close();
                        var summary = RepairResultSummary.Create("sfc", 0, false, "No integrity violations", "", TimeSpan.FromSeconds(15), english);
                        var result = new RepairResultWindow(summary, ThemePalette.For(theme), english) { Owner = main, ShowActivated = false };
                        result.Show(); Audit(result, "Reparaturergebnis-" + suffix, english); result.Close();
                        var logs = new RepairLogWindow(new[] { originalLog }, ThemePalette.For(theme), english, null) { Owner = main, ShowActivated = false };
                        logs.Show(); Audit(logs, "Reparaturprotokoll-" + suffix, english); logs.Close();
                    }
                }
                finally { main.Close(); Pump(); }
            }
            var store = new AppBackupStore(Path.Combine(output, "BilingualBackups"));
            var source = Path.Combine(output, "fixture.exe"); File.WriteAllText(source, "fixture");
            AppBackup? backup = null;
            var create = store.CreateAsync(source, "1.1.3", AppReleaseNotes.Current); Wait(create); backup = create.Result;
            foreach (var english in new[] { false, true })
            {
                var release = new UpdateRelease(new AvailableUpdate(new Version(1, 1, 4), "", new string('a', 64)) { Notes = AppReleaseNotes.Current }, AppReleaseNotes.Current, DateTimeOffset.Now);
                var manager = new UpdateManagerWindow(ThemePalette.For("Standard"), english, () => false, store, _ => Task.FromResult<IReadOnlyList<UpdateRelease>>(new[] { release }), false) { ShowActivated = false };
                manager.Show(); Wait(manager.ReloadAsync()); manager.ShowNotesView(); Pump();
                Assert(((TextBlock)manager.FindName("NotesText")).Text == (english ? AppReleaseNotes.English : AppReleaseNotes.German).Trim(), "GitHub notes use selected language");
                Save(manager, "Aenderungen-" + (english ? "EN" : "DE"));
                manager.ShowRecoveryView(); Wait(manager.PendingSelection); manager.ShowNotesView(); Pump();
                Assert(((TextBlock)manager.FindName("NotesText")).Text == (english ? AppReleaseNotes.English : AppReleaseNotes.German).Trim(), "saved backup notes use selected language"); manager.Close();
            }
            Assert(errors.Count == 0, "no unhandled language-test errors");
            Console.WriteLine($"PASS LANGUAGES: {checks} assertions; 32 main-page/options states, DE/EN dialogs, release and backup language selection."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
