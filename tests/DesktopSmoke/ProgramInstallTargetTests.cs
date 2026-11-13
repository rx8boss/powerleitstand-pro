using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramInstallTargetTests
{
    private const string Manifest = "PackageIdentifier: VideoLAN.VLC\nPackageVersion: 3.0.21\nInstallerType: nullsoft\nInstallers:\n- Architecture: x64\n  InstallerUrl: https://example.invalid/test.exe\n  InstallerSha256: abc\n- Architecture: x86\n  InstallerUrl: https://example.invalid/test.exe\nManifestType: installer\nManifestVersion: 1.6.0\n";
    internal static int Run(App app, string output, bool ui)
    {
        int checks = 0; void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        bool Rejected(string target) { try { ProgramInstallTargets.Validate(target); return false; } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return true; } }
        var proof = ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest);
        Assert(proof is { Kind: "nullsoft" }, "Standard Nullsoft manifest supports the known location switch");
        foreach (string type in new[] { "inno", "portable" }) Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("nullsoft", type)) is not null, type + " location profile");
        foreach (string type in new[] { "msi", "wix", "msix", "exe" }) Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("nullsoft", type)) is null, type + " without explicit location proof is unavailable");
        Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("InstallerType: nullsoft", "InstallerType: exe\nInstallerSwitches:\n  InstallLocation: '/dir=<INSTALLPATH>'")) is not null, "Explicit EXE location switch");
        Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("InstallerType: nullsoft", "InstallerType: nullsoft\nUnsupportedArguments:\n- location")) is null, "Unsupported location fails closed");
        Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("- Architecture: x86", "- Architecture: x86\n  InstallerType: msix")) is null, "Mixed installer variants fail closed");
        Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("InstallerType: nullsoft", "InstallerType: nullsoft\nInstallerSwitches:\n  InstallLocation: ''")) is null, "Explicit empty switch disables location");
        Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("InstallerType: nullsoft", "InstallerType: &alias nullsoft")) is null, "YAML aliases not interpreted");
        Assert(ProgramInstallTargets.Parse("Wrong.Package", "3.0.21", Manifest) is null && ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.22", Manifest) is null, "Exact manifest identity and version required");
        Assert(ProgramInstallTargets.Parse("VideoLAN.VLC", "3.0.21", Manifest.Replace("ManifestType:", "InstallerType: inno\nManifestType:")) is null, "Duplicate relevant fields rejected");
        var vlc = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
        Assert(ProgramInstallTargets.Suggest(vlc, "D:\\") == @"D:\Programme\VLC", "Drive-based program-specific path");
        string root = Path.Combine(output, "TargetFixtures"); Directory.CreateDirectory(root); string target = Path.Combine(root, "VLC");
        Assert(ProgramInstallTargets.Validate(target, probeWrite: true) == target, "New target and temporary write probe");
        Assert(!Directory.EnumerateFiles(root).Any(), "Write probe leaves no file");
        foreach (string invalid in new[] { @"C:\", @"relative\app", @"\\server\share\app", root + @"\..\app", root + @"\CON", root + @"\app.", root + "\\bad\npath", Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\App" })
            Assert(Rejected(invalid), "Unsafe target rejected: " + invalid);
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "existing.txt"), "preserve"); Assert(Rejected(target), "Non-empty folder rejected");
        Assert(ProgramInstallTargets.MatchesLocation(target + @"\vlc.exe", target) && !ProgramInstallTargets.MatchesLocation(target + @"-other\vlc.exe", target), "Destination prefix must use folder boundary");
        string marksFile = Path.Combine(output, "Installations.json"); File.WriteAllText(marksFile, "[\"firefox\"]");
        var marks = new ProgramInstallationMarks(marksFile); Assert(marks.Contains("firefox") && marks.InstalledAt("firefox") is null, "Legacy marks preserve manual status without invented date");
        marks.RecordInstalled("vlc", DateTimeOffset.Parse("2026-10-23T10:00:00Z")); marks.RecordInstalled("notepad", DateTimeOffset.Parse("2026-10-23T11:00:00Z"));
        marks = new(marksFile); Assert(marks.Contains("firefox") && marks.MostRecentlyInstalledId == "notepad" && marks.InstalledAt("absent") is null, "Dates and manual marks persist independently");
        marks.ForgetInstallation("notepad"); Assert(marks.MostRecentlyInstalledId == "vlc", "Removing latest entry selects remaining latest install");
        ProcessStartInfo? captured = null;
        var service = new ProgramInstallService(query: (_, _) => Task.FromResult(new WinGetResult(0, "")), install: (start, _) => { captured = start; return Task.FromResult(0); });
        var fields = BindingFlags.Instance | BindingFlags.NonPublic;
        ((Dictionary<string, string>)typeof(ProgramInstallService).GetField("_installedPackageIds", fields)!.GetValue(service)!)[vlc.Id] = vlc.PackageId!;
        typeof(ProgramInstallService).GetField("_recentExport", fields)!.SetValue(service, new Dictionary<string, string?> { [vlc.PackageId!] = "3.0.21" });
        typeof(ProgramInstallService).GetField("_lastEvidence", fields)!.SetValue(service, new Dictionary<string, ProgramInstallation> { [vlc.Id] = new("3.0.21", "Registry") });
        var inventory = new ProgramInventory(true, new Dictionary<string, string?> { [vlc.Id] = "3.0.21" });
        Assert(service.CanReinstall(vlc, inventory), "Confirmed exact WinGet package can be reinstalled");
        var package = service.ResolveReinstall(vlc, inventory); service.ReinstallAsync(vlc, package, inventory, () => { }).GetAwaiter().GetResult();
        Assert(captured!.ArgumentList.Contains("--force") && captured.ArgumentList.Contains("3.0.21") && !captured.ArgumentList.Contains("--ignore-security-hash") && !captured.ArgumentList.Contains("--no-upgrade"), "Reinstall pins version and keeps hash verification");
        Assert(!service.CanReinstall(vlc, inventory with { InstalledSources = new Dictionary<string, string> { [vlc.Id] = "Windows-App" } }), "Store editions excluded from reinstall route");
        captured = null; bool blocked = false;
        try { service.InstallAtAsync(vlc, package, Path.Combine(root, "new"), () => { }).GetAwaiter().GetResult(); } catch (IOException) { blocked = true; }
        Assert(blocked && captured is null, "No installation starts without destination proof");
        var command = service.Command(vlc, true, "3.0.21", location: Path.Combine(root, "new"));
        Assert(command.ArgumentList[command.ArgumentList.IndexOf("--location") + 1] == Path.Combine(root, "new") && !command.UseShellExecute && command.CreateNoWindow, "Destination stays one argument in hidden installer command");
        if (ui) checks += Ui(app, output, proof!);
        Console.WriteLine("PASS " + checks + " installation destination, history, reinstall and UI checks. Fixture data only; no real installer executed."); return 0;
    }
    private static int Ui(App app, string output, ProgramInstallTargetProof proof)
    {
        int checks = 0; void Assert(bool ok, string message) { if (!ok) throw new Exception(message); checks++; }
        UiSoundService.MuteForTests = true; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        void Pump() { app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
        void Wait(Task task) { var time = Stopwatch.StartNew(); while (!task.IsCompleted && time.Elapsed.TotalSeconds < 15) { Pump(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Pump(); }
        var previous = ProgramRemovalDialog.Reader;
        ProgramRemovalDialog.Reader = _ => [new("vlc", "VLC", "VideoLAN", "3.0.21", null, "Registry", CatalogId: "vlc"), new("firefox", "Firefox", "Mozilla", "131.0", null, "Registry", CatalogId: "firefox"), new("other", "Unknown application", "Fixture", null, null, "Registry")];
        try
        {
            foreach (string theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
            {
                var main = new MainWindow(Path.Combine(output, "UiFixtures", theme)) { ShowActivated = false };
                try
                {
                    main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false); view.Assets.AllowOnlineIcons = false; view.Service = new ProgramCatalogTests.Fake(); view.RecordInstalled("vlc");
                    var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                    foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); Pump();
                        var host = (Grid)main.FindName("Root"); var vlc = ProgramCatalog.Entries.Single(e => e.Id == "vlc");
                        var task = ProgramSelectionDialog.ShowAsync(view, [new(vlc, new(vlc.PackageId!, "3.0.21", "", DateTime.UtcNow, VersionSource: "WinGet", TargetProof: proof))]); Pump();
                        var picker = UnifiedDialogAppearance.Children<ProgramInstallTargetPicker>(host).Single();
                        var toggle = UnifiedDialogAppearance.Children<CheckBox>(picker).Single(); toggle.IsChecked = true; Pump();
                        var path = UnifiedDialogAppearance.Children<TextBox>(picker).Single(); path.Text = Path.Combine(output, "ValidUiTarget-" + theme.Replace(' ', '-') + "-" + size); Pump(); Assert(picker.IsValid, "Chosen path is valid");
                        path.Text = @"relative\invalid"; Pump(); Assert(!picker.IsValid, "Invalid path identified in dialog");
                        var start = UnifiedDialogAppearance.Children<Button>(host).Single(b => b.Content is TextBlock t && t.Text == (english ? "↓ Start selected" : "↓ Auswahl starten")); Assert(!start.IsEnabled, "Invalid path disables installation start");
                        path.Text = Path.Combine(output, "UiTarget-" + theme.Replace(' ', '-') + "-" + size); Pump(); Assert(start.IsEnabled, "Valid path allows confirmation");
                        var drives = UnifiedDialogAppearance.Children<ComboBox>(picker).Single(); Assert(((ComboBoxItem)drives.SelectedItem).Tag.ToString() == Path.GetPathRoot(path.Text), "Drive selector follows manually edited destination");
                        foreach (var button in UnifiedDialogAppearance.Children<Button>(picker))
                        { var bounds = button.TransformToAncestor(host).TransformBounds(new Rect(button.RenderSize)); Assert(bounds.Right <= host.ActualWidth + 1 && bounds.Bottom <= host.ActualHeight + 1, "Folder action fits compact and large"); }
                        if (!english && theme == "Standard") Save(main, Path.Combine(output, "Zielordner-" + size + ".png"));
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(task); Assert(task.Result![0].TargetPath == path.Text, "Confirmation returns exact destination without launching installer");
                        var removal = ProgramRemovalDialog.ShowAsync(view); Pump();
                        var overlay = host.Children.OfType<Grid>().Single(x => x.Name == "InstalledProgramsOverlay");
                        var watch = Stopwatch.StartNew(); while (!UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "Firefox") && watch.Elapsed.TotalSeconds < 10) { Pump(); Thread.Sleep(5); }
                        var categories = UnifiedDialogAppearance.Children<ComboBox>(overlay).Single(); categories.SelectedItem = categories.Items.OfType<ComboBoxItem>().Single(x => (string)x.Tag == "browser"); Pump();
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "Firefox") && !UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "VLC"), "Installed category filters correctly");
                        categories.SelectedItem = categories.Items.OfType<ComboBoxItem>().Single(x => (string)x.Tag == "recent"); Pump();
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "VLC") && !UnifiedDialogAppearance.Children<TextBlock>(overlay).Any(t => t.Text == "Firefox"), "Unknown installation date not included in recent filter");
                        if (!english && theme == "Standard") Save(main, Path.Combine(output, "Installiert-" + size + ".png"));
                        UnifiedDialogAppearance.Children<Button>(overlay).Single(b => b.Content is TextBlock t && t.Text == (english ? "Close" : "Schließen")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(removal);
                    }
                }
                finally { main.Close(); Pump(); }
            }
        }
        finally { ProgramRemovalDialog.Reader = previous; }
        return checks;
    }
    private static void Save(Window window, string path)
    { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); png.Save(stream); }
    internal static int Live(string output) => Task.Run(async () =>
    {
        int found = 0;
        foreach (var item in new[] { ("VideoLAN.VLC", "3.0.21"), ("Mozilla.Firefox", "131.0") })
        {
            var proof = await ProgramInstallTargets.ResolveAsync(new(item.Item1, item.Item2, "", DateTime.UtcNow, VersionSource: "WinGet"), CancellationToken.None);
            File.WriteAllText(Path.Combine(output, item.Item1 + ".json"), System.Text.Json.JsonSerializer.Serialize(proof));
            Console.WriteLine(item.Item1 + " " + item.Item2 + ": " + (proof?.Kind ?? "no confirmed path support")); if (proof is not null) found++;
        }
        return found == 0 ? 2 : 0;
    }).GetAwaiter().GetResult();
}
