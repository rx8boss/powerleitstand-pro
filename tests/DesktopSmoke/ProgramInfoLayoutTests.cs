using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PowerControlProWpf;

internal static class ProgramInfoLayoutTests
{
    internal static int Run(App app, string output)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher)); UiSoundService.MuteForTests = true;
        int checks = 0; var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
        void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var entry = ProgramCatalog.Entries.Single(x => x.Id == "powertoys");
        var swarmEntry = ProgramCatalog.Entries.Single(x => x.Id == "turtle-beach-swarm-ii");
        using var releaseNotes = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            {"Hardware":{"Swarm II":{"99.0.0.0":{}}},"Software":{"Swarm II":{"1.0.0.9":{},"1.0.0.64":{},"2.0.0.0-beta":{}}}}
            """));
        Assert(ProgramPublisherVersions.SwarmReleaseNoteVersion(releaseNotes) == new Version(1, 0, 0, 64),
            "Swarm release notes select the newest numeric software version, excluding firmware and previews");
        string folder = Path.Combine(output, "FixtureProgram"); Directory.CreateDirectory(folder); string executable = Path.Combine(folder, "PowerToys.exe"); File.WriteAllText(executable, "Test fixture; never execute");
        int started = 0;
        var service = new ProgramInstallService(query: (_, _) => Task.FromResult(new WinGetResult(0, "Found PowerToys [Microsoft.PowerToys]\nVersion: 0.96.1\n")),
            install: (_, _) => { started++; return Task.FromResult(0); }, github: (_, _) => Task.FromResult<ProgramPackage?>(null));
        var evidence = new Dictionary<string, ProgramInstallation>
        {
            [entry.Id] = new("0.96.1", "Registry", Path: executable),
            [swarmEntry.Id] = new(null, "Registry", Path: folder)
        };
        ((Dictionary<string, string>)typeof(ProgramInstallService).GetField("_installedPackageIds", flags)!.GetValue(service)!)[entry.Id] = entry.PackageId!;
        typeof(ProgramInstallService).GetField("_recentExport", flags)!.SetValue(service, new Dictionary<string, string?> { [entry.PackageId!] = "0.96.1" });
        typeof(ProgramInstallService).GetField("_lastEvidence", flags)!.SetValue(service, evidence);
        foreach (string theme in new[] { "Standard", "Classic", "Neon Blau", "Neon Pink", "Neon Grün", "Linux Mint", "Dark" })
        {
            var main = new MainWindow(Path.Combine(output, "Fixtures", theme)) { ShowActivated = false };
            try
            {
                main.Show(); main.ShowWindowsUpdatesPage(); var page = (WindowsUpdatesPage)main.FindName("WindowsUpdatesPage"); var view = page.ShowPrograms(false);
                view.Assets.AllowOnlineIcons = false; view.Service = service;
                typeof(ProgramCatalogView).GetProperty("Inventory", flags)!.SetValue(view,
                    new ProgramInventory(true, new Dictionary<string, string?> { [entry.Id] = "0.96.1", [swarmEntry.Id] = null }, Evidence: evidence));
                var themes = (ComboBox)main.FindName("ThemeBox"); themes.SelectedItem = themes.Items.OfType<ComboBoxItem>().Single(x => x.Tag?.ToString() == theme);
                foreach (bool english in new[] { false, true }) foreach (int size in new[] { 1, 4 })
                {
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size; page.SetLanguage(english); view.SetCategory("tools"); Pump();
                    var tile = view.Tiles.Single(x => x.Tag?.ToString() == entry.Id); var open = UnifiedDialogAppearance.Children<Button>(tile).Single();
                    Assert(open.ActualWidth >= tile.ActualWidth - 3 && open.ActualHeight >= tile.ActualHeight - 3, "Installed tile info action spans full surface");
                    Assert(UnifiedDialogAppearance.Children<CheckBox>(tile).Single().Visibility == Visibility.Collapsed, "Installed tile without update keeps checkbox hidden");
                    var host = (Grid)main.FindName("Root"); int previousChildren = host.Children.Count;
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); var info = view.Info!;
                    var limit = Stopwatch.StartNew(); while (!info.Loading.IsCompleted && limit.Elapsed.TotalSeconds < 5) { Pump(); Thread.Sleep(5); } info.Loading.GetAwaiter().GetResult(); Pump();
                    var buttons = UnifiedDialogAppearance.Children<Button>(info.Frame).ToArray();
                    Assert(buttons.Any(b => b.Name == "ProgramInfoStatusReinstall") && !buttons.Any(b => b.Name == "ProgramInfoFooterReinstall"), "Single inline reinstall action, no duplicated footer action");
                    var description = UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Single(t => t.Name == "ProgramInfoDescription");
                    var versionLabel = UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Single(t => t.Text == (english ? "Installed version:" : "Installierte Version:"));
                    Assert(description.TranslatePoint(new Point(), info.Frame).Y < versionLabel.TranslatePoint(new Point(), info.Frame).Y, "Description precedes version rows as approved");
                    var website = buttons.Single(b => b.Name == "ProgramInfoWebsite");
                    Assert(((Grid)website.Parent).Children.OfType<Button>().Count() == 2, "Reference footer contains website and close only");
                    Assert(((Grid)website.Parent).Children.OfType<Button>().All(b => Math.Abs(b.ActualWidth - website.ActualWidth) < 1), "Footer actions have identical widths with a separate gap");
                    Assert(info.Frame.ActualHeight <= 540 && description.FontSize >= 14, "Reference proportions and readable description applied");
                    var details = (TextBlock)typeof(ProgramInfoCard).GetField("_details", flags)!.GetValue(info)!;
                    var detailsToggle = buttons.Single(b => b.Name == "ProgramInfoDetailsToggle");
                    Assert(details.Visibility == Visibility.Collapsed, "Technical details initially stay behind the compact source row");
                    var scroll = UnifiedDialogAppearance.Children<ScrollViewer>(info.Frame).First();
                    Assert(scroll.ScrollableHeight < 1, "Confirmed installation information fits without scrolling in both sizes");
                    double footerY = website.TranslatePoint(new Point(), info.Frame).Y;
                    detailsToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(details.Visibility == Visibility.Visible && details.Text.Contains(entry.PackageId!), "Source row reveals full package and detection details");
                    Assert(Math.Abs(website.TranslatePoint(new Point(), info.Frame).Y - footerY) < 1, "Expanding details keeps footer fixed and visible");
                    detailsToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(details.Visibility == Visibility.Collapsed, "Details collapse again without changing dialog height");
                    var location = info.Frame.TranslatePoint(new Point(), host);
                    Assert(Math.Abs(location.X + info.Frame.ActualWidth / 2 - host.ActualWidth / 2) < 2 && Math.Abs(location.Y + info.Frame.ActualHeight / 2 - host.ActualHeight / 2) < 2, "Dialog centered in both directions");
                    Assert(location.Y >= 0 && location.Y + info.Frame.ActualHeight <= host.ActualHeight, "Entire dialog bounded within compact and large app");
                    foreach (var button in buttons.Where(b => b.Name.StartsWith("ProgramInfo")))
                    {
                        var position = button.TranslatePoint(new Point(), info.Frame);
                        Assert(position.X >= 0 && position.X + button.ActualWidth <= info.Frame.ActualWidth + 1, "Action inside compact info frame");
                        if (button.Content is TextBlock text)
                        {
                            var formatted = new FormattedText(text.Text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, Brushes.White, 1);
                            if (button != detailsToggle) Assert(formatted.Width <= button.ActualWidth - button.Padding.Left - button.Padding.Right - 2, "Action label fits: " + text.Text);
                        }
                    }
                    Assert(UnifiedDialogAppearance.Children<TextBlock>(info.Frame).Any(t => t.Text.Contains("FixtureProgram")), "Confirmed installation folder visible");
                    if (!english && (theme == "Standard" || theme == "Classic" || theme == "Neon Blau"))
                    {
                        main.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(main);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, "Programminfo-" + theme.Replace(' ', '-') + "-" + size + ".png")); png.Save(stream);
                    }
                    double descriptionSize = description.FontSize;
                    ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size == 1 ? 4 : 1; Pump();
                    Assert(info.IsOpen && Math.Abs(info.Frame.ActualWidth - (view.Compact ? 420 : 580)) < 1, "Open info adapts when compact/large size changes");
                    Assert(description.FontSize == descriptionSize, "App resizing does not apply an extra text scale to the dialog");
                    var resized = info.Frame.TranslatePoint(new Point(), host);
                    Assert(resized.Y >= 0 && resized.Y + info.Frame.ActualHeight <= host.ActualHeight + 1, "Resized dialog and footer stay within app");
                    view.CloseInfo(); Assert(host.Children.Count == previousChildren, "Dialog and background shade removed together"); Assert(started == 0, "Info display did not start an installer");
                    if (theme == "Standard")
                    {
                        ((ComboBox)main.FindName("SizeBox")).SelectedIndex = size;
                        view.SetCategory("gaming"); Pump();
                        var swarmTile = view.Tiles.Single(x => x.Tag?.ToString() == swarmEntry.Id);
                        UnifiedDialogAppearance.Children<Button>(swarmTile).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Pump(); var swarmInfo = view.Info!;
                        var swarmWait = Stopwatch.StartNew();
                        while (!swarmInfo.Loading.IsCompleted && swarmWait.Elapsed.TotalSeconds < 5) { Pump(); Thread.Sleep(5); }
                        swarmInfo.Loading.GetAwaiter().GetResult(); Pump();
                        Visibility FieldVisibility(string field) => ((UIElement)typeof(ProgramInfoCard).GetField(field, flags)!.GetValue(swarmInfo)!).Visibility;
                        Assert(FieldVisibility("_installedVersionField") == Visibility.Collapsed && FieldVisibility("_availableVersionField") == Visibility.Collapsed,
                            "Swarm version rows with no confirmed check route are hidden");
                        Assert(FieldVisibility("_updateAction") == Visibility.Collapsed, "Swarm has no unusable recheck action");
                        Assert(UnifiedDialogAppearance.Children<TextBlock>(swarmInfo.Frame).Any(t => t.Text == (english ? "Location:" : "Speicherort:") && t.IsVisible),
                            "Swarm keeps its location visible");
                        view.CloseInfo();
                    }
                }
            }
            finally { main.Close(); Pump(); }
        }
        Console.WriteLine("PASS " + checks + " installed-info and full-tile layout checks; no real installer executed"); return 0;
    }
}
