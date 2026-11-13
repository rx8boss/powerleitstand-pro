using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;

namespace PowerControlProWpf;

internal static class ProgramRemovalDialog
{
    internal static Func<CancellationToken, IReadOnlyList<InstalledProgram>> Reader = ProgramRemovalService.Read;
    internal static Task ShowAsync(ProgramCatalogView view, ProgramEntry? target = null, string? exactLocation = null)
    {
        var owner = Window.GetWindow(view)!; var host = (Grid)owner.FindName("Root");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new CancellationTokenSource(); var readToken = cancellation.Token; var reader = Reader;
        bool busy = false, closed = false, reading = true, readComplete = false;
        string L(string de, string en) => view.English ? en : de;
        UnifiedDialogAppearance.Resources(owner, view.Palette);
        TextBlock Text(string value, double size = 12, bool muted = false)
        { var t = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText"); return t; }
        Button Action(string caption, bool danger = false)
        {
            var b = new Button { Content = new TextBlock { Text = caption, Foreground = Brushes.White }, Height = 34, MinHeight = 34, Padding = new(9, 0, 9, 0) };
            UnifiedDialogAppearance.Button(owner, b, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary); AutomationProperties.SetName(b, caption); return b;
        }
        var layer = new Grid { Name = "InstalledProgramsOverlay", Background = new SolidColorBrush(Color.FromArgb(190, 0, 10, 20)), Focusable = true };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count)); Panel.SetZIndex(layer, 6000);
        var card = new Border { Padding = new(18), CornerRadius = new(10), BorderThickness = new(1.2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "DialogSurface"); card.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new(0, 0, 0, 12) }; header.ColumnDefinitions.Add(new() { Width = new GridLength(37) }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        header.Children.Add(ProgramFallbackIcon.Create(27, "UnifiedAccent"));
        var heading = Text(target is null ? L("Installierte Programme", "Installed programs") : L("Deinstallieren · ", "Uninstall · ") + target.Name, 20); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new(0, 0, 55, 0); Grid.SetColumn(heading, 1); header.Children.Add(heading);
        var stripes = new System.Windows.Shapes.Path { Data = Geometry.Parse("M48,0 L50,0 L8,40 L6,40 Z M67,0 L69,0 L27,40 L25,40 Z M73,0 L87,0 L45,40 L31,40 Z"), Width = 50, Height = 30, Stretch = Stretch.Fill, Opacity = .2, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
        stripes.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UnifiedAccent"); Grid.SetColumn(stripes, 1); header.Children.Add(stripes);
        var close = new Button { Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L12,12 M12,0 L0,12"), Width = 11, Height = 11, StrokeThickness = 1.5, Stretch = Stretch.Uniform } };
        UnifiedDialogAppearance.Button(owner, close, DialogButtonKind.Close); AutomationProperties.SetName(close, L("Schließen", "Close")); Grid.SetColumn(close, 2); header.Children.Add(close); layout.Children.Add(header);
        var search = new TextBox { Height = 30, Padding = new(8, 4, 8, 4), Margin = new(0, 0, 0, 9) };
        search.SetResourceReference(TextBox.BackgroundProperty, "InputBrush"); search.SetResourceReference(TextBox.ForegroundProperty, "UnifiedText"); search.SetResourceReference(TextBox.BorderBrushProperty, "UnifiedAccent");
        search.ToolTip = L("Installierte Programme filtern", "Filter installed programs"); AutomationProperties.SetName(search, search.ToolTip.ToString()); Grid.SetRow(search, 1); layout.Children.Add(search);
        layout.RowDefinitions.Insert(2, new RowDefinition { Height = GridLength.Auto });
        var category = new System.Windows.Controls.ComboBox { Height = 30, Margin = new(0, 0, 0, 9), Padding = new(7, 3, 7, 3), Background = Brushes.Transparent };
        category.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "UnifiedText"); category.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "UnifiedAccent");
        var categoryOptions = new List<(string Id, string Label)> { ("all", L("Alle Kategorien", "All categories")), ("recent", L("★ Zuletzt installiert", "★ Installed most recently")), ("other", L("Weitere Programme", "Other programs")) };
        categoryOptions.AddRange(ProgramCatalog.Categories.Where(x => x != "all").Select(x => (x, ProgramCatalog.Category(x, view.English))));
        foreach (var option in categoryOptions) category.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Id });
        category.SelectedIndex = 0; AutomationProperties.SetName(category, L("Kategorie installierter Programme", "Installed program category")); Grid.SetRow(category, 2); layout.Children.Add(category);
        var rows = new StackPanel(); var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3); layout.Children.Add(scroll); DialogLayout.ScrollHint(scroll, view.English);
        var loading = new ProgramInstalledLoading(view.English); Grid.SetRow(loading, 3); layout.Children.Add(loading);
        search.IsEnabled = category.IsEnabled = false;
        var footer = new StackPanel { Margin = new(0, 10, 0, 0) }; var status = Text(L("Programme werden gelesen …", "Reading installed programs …"), 11, true); footer.Children.Add(status);
        var finish = Action(L("Schließen", "Close"), true); finish.Width = 150; finish.Margin = new(0, 9, 0, 0); finish.HorizontalAlignment = HorizontalAlignment.Center; footer.Children.Add(finish); Grid.SetRow(footer, 4); layout.Children.Add(footer);
        card.Child = layout; layer.Children.Add(card);
        IReadOnlyList<InstalledProgram> programs = [];
        void Lock(bool value) { busy = value; close.IsEnabled = finish.IsEnabled = !value; search.IsEnabled = category.IsEnabled = !value && readComplete; view.WorkStateChanged?.Invoke(value); }
        Border Frame(UIElement child)
        {
            var f = new Border { Child = child, Padding = new(10), Margin = new(0, 0, 0, 7), CornerRadius = new(6), BorderThickness = new(1) };
            f.SetResourceReference(Border.BackgroundProperty, "InputBrush"); f.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush"); return f;
        }
        void Render()
        {
            rows.Children.Clear();
            if (target is not null && programs.Count == 0)
            {
                rows.Children.Add(Text(L("Für dieses Programm ist kein eindeutig zugeordneter Deinstallationsweg bestätigt. Bitte über Windows oder die vorhandene Installationsquelle entfernen. Es werden keine fremden Programmordner gelöscht.", "No unambiguous uninstallation route is verified for this program. Remove it through Windows or its existing installation source. No unrelated application folders will be deleted."), 12, true));
                var settings = Action(L("Windows-App-Einstellungen öffnen", "Open Windows app settings")); settings.Margin = new(0, 12, 0, 0);
                settings.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); }
                    catch (Exception e) { status.Text = UiText.Error(e, view.English); }
                };
                rows.Children.Add(settings); return;
            }
            string selectedCategory = (category.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
            string? latestId = view.MostRecentlyInstalledId;
            bool MatchesCategory(InstalledProgram p)
            {
                string? catalogId = p.CatalogId;
                string[] categories = catalogId is null ? [] : ProgramCatalog.Entries.FirstOrDefault(x => x.Id == catalogId)?.Categories ?? [];
                return selectedCategory switch
                {
                    "all" => true,
                    "recent" => catalogId is not null && view.InstalledAt(catalogId) is not null,
                    "other" => categories.Length == 0,
                    _ => categories.Contains(selectedCategory, StringComparer.OrdinalIgnoreCase)
                };
            }
            var visible = programs.Where(p => (p.Name + " " + p.Publisher).Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) && MatchesCategory(p));
            if (selectedCategory == "recent") visible = visible.OrderByDescending(p => p.CatalogId is { } id ? view.InstalledAt(id) : null);
            foreach (var p in visible)
            {
                var row = new Grid(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = new GridLength(142) });
                var labels = new StackPanel { Margin = new(0, 0, 8, 0) }; var name = Text(p.Name, 14); name.FontWeight = FontWeights.SemiBold; labels.Children.Add(name);
                string? categoryLabel = p.CatalogId is null ? null : ProgramCatalog.Entries.FirstOrDefault(x => x.Id == p.CatalogId)?.Categories.FirstOrDefault();
                if (categoryLabel is not null) labels.Children.Add(Text(ProgramCatalog.Category(categoryLabel, view.English), 10, true));
                if (p.CatalogId is { } knownId && view.InstalledAt(knownId) is { } installedAt)
                    labels.Children.Add(Text((knownId == latestId ? L("★ Zuletzt installiert · ", "★ Installed most recently · ") : L("Installiert am ", "Installed ")) + installedAt.ToLocalTime().ToString("g") + L(" · über diese App", " · through this app"), 10));
                else labels.Children.Add(Text(L("Installationsdatum nicht ermittelbar", "Installation date unavailable"), 10, true));
                labels.Children.Add(Text((p.Version ?? L("Version nicht ermittelbar", "Version unavailable")) + " · " + (p.Source.StartsWith("Portable") ? "Portable" : p.Source == "Windows-App" ? L("Windows-App · Paketversion", "Windows app · package version") : p.Source is "Scoop" or "Chocolatey" ? p.Source : L("Windows-Programmliste", "Windows installed programs")), 10.5, true));
                if (p.PackageVersion is { } packageVersion) labels.Children.Add(Text(L("Paketversion: ", "Package version: ") + packageVersion + L(" · Verwaltung über ", " · managed through ") + p.Source, 10.5, true));
                var location = Text(p.Location is { Length: > 0 } path ? ProgramLocation.Shorten(path) : L("Speicherort nicht verfügbar", "Location unavailable"), 10.5, true); labels.Children.Add(location); row.Children.Add(labels);
                var remove = Action(p.Source.StartsWith("Portable") ? L("EXE entfernen", "Remove EXE") : L("Deinstallieren", "Uninstall"), true); remove.IsEnabled = !p.Protected; remove.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(remove, 1); row.Children.Add(remove);
                if (p.Protected) remove.ToolTip = p.Source is "Scoop" or "Chocolatey"
                    ? L("Über den vorhandenen Paketmanager verwalten; hier nur lesend erfasst.", "Manage through the existing package manager; listed read-only here.")
                    : L("Kein bestätigter Deinstallationsweg in dieser App; bei Bedarf über Windows verwalten.", "No verified uninstallation route in this app; manage through Windows if needed.");
                remove.Click += async (_, _) =>
                {
                    if (busy) return;
                    string question = p.Name + "\n\n" + (p.Source.StartsWith("Portable")
                        ? L("Portable Startdatei entfernen? Die EXE wird vorher in AppData gesichert. Weitere Programmdateien bleiben erhalten.", "Remove the portable executable? It is backed up in AppData first. Other program files are preserved.")
                        : p.Source == "Windows-App"
                        ? L("Diese Store-App für das aktuelle Windows-Konto deinstallieren? App-Daten können dabei entfernt werden.", "Uninstall this Store app for the current Windows account? App data may be removed.")
                        : L("Dieses Programm über seinen registrierten Uninstaller deinstallieren?", "Uninstall this program using its registered uninstaller?"));
                    if (ThemedMessageBox.Show(owner, question, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    Lock(true); rows.IsEnabled = false; status.Text = L("Deinstallation läuft: ", "Uninstalling: ") + p.Name;
                    try
                    {
                        await view.PauseBackgroundInspectionAsync();
                        if (view.Service is ProgramInstallService service) service.InvalidateSaved(p.CatalogId);
                        int code = await ProgramRemovalService.RemoveAsync(p);
                        view.WriteLog?.Invoke(p.Name + L(": Deinstallation beendet, Code ", ": uninstaller exited, code ") + code);
                        if (code != 0 || await Task.Run(() => ProgramRemovalService.IsPresent(p))) { status.Text = L("Deinstallation nicht bestätigt. Bei Bedarf neu starten und erneut prüfen.", "Uninstall not verified. Restart if required and check again."); return; }
                        var remnants = await Task.Run(() => ProgramRemovalService.ScanRemainders(p));
                        programs = programs.Where(x => x.Id != p.Id).ToArray();
                        if (p.CatalogId is { } catalogId && !programs.Any(x => x.CatalogId == catalogId)) view.ForgetInstallation(catalogId);
                        Render();
                        if (remnants.Count > 0) ShowRemainders(p, remnants);
                        else status.Text = L("Deinstallation bestätigt. Keine eindeutig zugeordneten Reste gefunden.", "Uninstall verified. No clearly attributable leftovers found.");
                    }
                    catch (Exception e) { status.Text = UiText.Error(e, view.English); view.WriteLog?.Invoke(p.Name + ": " + e.Message); }
                    finally { Lock(false); rows.IsEnabled = true; }
                };
                rows.Children.Add(Frame(row));
            }
            scroll.ScrollToTop();
        }
        void ShowRemainders(InstalledProgram program, IReadOnlyList<ProgramRemainder> remnants)
        {
            heading.Text = L("Gefundene Programmreste", "Program leftovers"); search.Visibility = category.Visibility = Visibility.Collapsed; rows.Children.Clear();
            var selected = new HashSet<ProgramRemainder>();
            foreach (var item in remnants)
            {
                var descriptions = item.Description.Split(" / ");
                string description = view.English ? descriptions[^1] : descriptions[0];
                string kind = item.Kind switch { "Directory" => L("Ordner", "Folder"), "Shortcut" => L("Verknüpfung", "Shortcut"), _ => L("Registrierung", "Registry") };
                var box = new CheckBox { Content = Text(kind + " · " + item.Path + "\n" + description, 11), Margin = new(0, 3, 0, 3), IsChecked = false };
                box.SetResourceReference(CheckBox.ForegroundProperty, "UnifiedText");
                box.Checked += (_, _) => selected.Add(item); box.Unchecked += (_, _) => selected.Remove(item); rows.Children.Add(Frame(box));
            }
            var clean = Action(L("Ausgewählte Reste entfernen", "Remove selected leftovers"), true); clean.Margin = new(0, 8, 0, 0); rows.Children.Add(clean);
            status.Text = L("Prüfbereich: exakte AppData-Ordner, HKCU-Schlüssel und Benutzerverknüpfungen. Auswahl kann persönliche Einstellungen enthalten. Vor Entfernung wird gesichert.", "Scan scope: exact AppData folders, HKCU keys and user shortcuts. Selection may include personal settings. Backups are created before removal.");
            clean.Click += async (_, _) =>
            {
                if (busy || selected.Count == 0) return;
                string question = L("Diese ausgewählten Reste nach Sicherung entfernen?\n\n", "Back up and remove these selected leftovers?\n\n") + string.Join("\n", selected.Select(x => x.Path));
                if (ThemedMessageBox.Show(owner, question, "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                Lock(true); rows.IsEnabled = false;
                try { string backup = await ProgramRemovalService.CleanAsync(program, selected.ToArray()); rows.Children.Clear(); status.Text = L("Reste entfernt. Sicherung: ", "Leftovers removed. Backup: ") + backup; view.WriteLog?.Invoke(status.Text); }
                catch (Exception e) { status.Text = UiText.Error(e, view.English); }
                finally { Lock(false); rows.IsEnabled = true; }
            };
        }
        void Resize(object? sender, SizeChangedEventArgs? e)
        { card.Width = Math.Min(view.Compact ? 650 : 800, Math.Max(0, host.ActualWidth - 32)); card.Height = Math.Max(220, Math.Min(650, host.ActualHeight - 48)); }
        void Finish()
        {
            if (busy || closed) return; closed = true; loading.Dispose(); cancellation.Cancel(); if (!reading) cancellation.Dispose();
            owner.SizeChanged -= Resize; owner.PreviewKeyDown -= Key; owner.Closed -= OwnerClosed; view.Unloaded -= Unloaded; view.IsVisibleChanged -= ViewVisibilityChanged;
            host.Children.Remove(layer); view.SelectionOpen = false; view.RefreshAvailability(); done.TrySetResult();
        }
        void Key(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Finish(); } }
        void Unloaded(object sender, RoutedEventArgs e) => Finish();
        void OwnerClosed(object? sender, EventArgs e) => Finish();
        void ViewVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!view.IsVisible) Finish(); }
        close.Click += (_, _) => Finish(); finish.Click += (_, _) => Finish(); search.TextChanged += (_, _) => { if (!busy && readComplete) Render(); }; category.SelectionChanged += (_, _) => { if (!busy && readComplete) { Render(); scroll.ScrollToTop(); } };
        owner.SizeChanged += Resize; owner.PreviewKeyDown += Key; owner.Closed += OwnerClosed; view.Unloaded += Unloaded; view.IsVisibleChanged += ViewVisibilityChanged;
        host.Children.Add(layer); view.SelectionOpen = true; view.RefreshAvailability(); Resize(null, null);
        async Task Load()
        {
            try
            {
                var found = await Task.Run(() => reader(readToken), readToken);
                readToken.ThrowIfCancellationRequested();
                if (closed) return;
                programs = target is null ? found : found.Where(p => MatchesTarget(p, target, view.Inventory.Evidence?.GetValueOrDefault(target.Id))
                    && (exactLocation is null || p.Location is { } location && string.Equals(location.TrimEnd('\\'), exactLocation.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))).ToArray();
                readComplete = true;
                Render(); status.Text = programs.Count + L(" Programme gefunden · Windows, Paketmanager, Katalog-Portables und Store-Apps", " programs found · Windows, package managers, catalog portables and Store apps"); if (ProgramRemovalService.LastReadWarning is { } warning) status.Text += L("\nStore-Prüfung unvollständig: ", "\nStore check incomplete: ") + warning;
            }
            catch (OperationCanceledException) when (readToken.IsCancellationRequested) { }
            catch (Exception e)
            {
                if (!closed)
                {
                    string message = L("Programme konnten nicht gelesen werden: ", "Could not read installed programs: ") + UiText.Error(e, view.English);
                    status.Text = message; status.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedDangerBorder");
                    var error = Text(message, 13); error.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedDangerBorder"); rows.Children.Clear(); rows.Children.Add(error);
                }
            }
            finally
            {
                reading = false; loading.Dispose();
                if (closed) cancellation.Dispose();
                else search.IsEnabled = category.IsEnabled = readComplete && !busy;
            }
        }
        _ = Load(); return done.Task;
    }
    internal static bool MatchesTarget(InstalledProgram program, ProgramEntry target, ProgramInstallation? proof)
    {
        if (program.CatalogId == target.Id) return true;
        // Search names are not identities. Require both exact product/publisher matching and
        // the already confirmed local location before exposing a discovered uninstaller.
        return proof is { Source: "Registry", Path: { } path } && program.Source == "Registry"
            && program.Location is { } location && string.Equals(path.TrimEnd('\\'), location.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && ProgramLocalInventory.Matches(target, program.Name, program.Publisher);
    }
}
