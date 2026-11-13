using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;

namespace PowerControlProWpf;

internal sealed record ProgramSelection(ProgramEntry Entry, ProgramPackage? Installer, PortablePackage? Portable = null, string? Parent = null, bool Reinstall = false, string? TargetPath = null, IReadOnlyList<ProgramPackage>? ReinstallOptions = null);

internal static class ProgramSelectionDialog
{
    internal static Task<IReadOnlyList<ProgramSelection>?> ShowAsync(ProgramCatalogView view, IReadOnlyList<ProgramSelection> options, Func<string?>? selectFolder = null, ProgramInventory? checkedInventory = null)
    {
        var owner = Window.GetWindow(view) ?? throw new InvalidOperationException("No app window");
        var host = owner.FindName("Root") as Grid ?? owner.Content as Grid ?? throw new InvalidOperationException("No app surface");
        var completion = new TaskCompletionSource<IReadOnlyList<ProgramSelection>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool english = view.English; string L(string de, string en) => english ? en : de;
        var inventory = checkedInventory ?? view.Inventory;
        TextBlock Text(string value, double size = 12, bool muted = false)
        { var t = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText"); return t; }
        Button Action(string text, bool danger = false)
        {
            var b = new Button { Content = new TextBlock { Text = text, Foreground = Brushes.White }, Height = 34, MinHeight = 34, Padding = new(10, 0, 10, 0) };
            UnifiedDialogAppearance.Button(owner, b, danger ? DialogButtonKind.Danger : DialogButtonKind.Primary); AutomationProperties.SetName(b, text); return b;
        }
        UnifiedDialogAppearance.Resources(owner, view.Palette);
        var layer = new Grid { Background = new SolidColorBrush(Color.FromArgb(180, 0, 10, 20)), Focusable = true };
        KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
        Grid.SetRowSpan(layer, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(layer, Math.Max(1, host.ColumnDefinitions.Count)); Panel.SetZIndex(layer, 6000);
        var card = new Border { Width = Math.Min(view.Compact ? 620 : 680, Math.Max(0, host.ActualWidth - 32)), MaxHeight = Math.Max(0, host.ActualHeight - 32),
            Padding = new(18), CornerRadius = new(11), BorderThickness = new(1.2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "DialogSurface"); card.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { Margin = new(0, 0, 0, 14) }; header.ColumnDefinitions.Add(new() { Width = new GridLength(36) }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        header.Children.Add(ProgramFallbackIcon.Create(29, "UnifiedAccent"));
        bool reinstalling = options.Any(x => x.Reinstall);
        var labels = new StackPanel { Margin = new(0, 0, 64, 0) }; var title = Text(reinstalling ? L("Neuinstallation vorbereiten", "Prepare reinstallation") : options.Count > 1 ? L("Auswahl vorbereiten", "Prepare selection") : L("Installation vorbereiten", "Prepare installation"), 20); title.FontWeight = FontWeights.SemiBold; labels.Children.Add(title);
        labels.Children.Add(Text(options.Count + (options.Count == 1 ? L(" Programm · Auswahl vor dem Start prüfen", " program · review before starting") : L(" Programme · Auswahl vor dem Start prüfen", " programs · review before starting")), 11, true));
        if (options.Any(o => inventory.HasUpdate(o.Entry.Id))) labels.Children.Add(Text(L("Vor Updates werden zugehörige Programme geordnet geschlossen. Offene Arbeit bitte speichern.", "Before updates, matching applications are closed gracefully. Please save your work."), 11, true));
        Grid.SetColumn(labels, 1); header.Children.Add(labels);
        var stripe = new System.Windows.Shapes.Path { Data = Geometry.Parse("M48,0 L50,0 L8,40 L6,40 Z M67,0 L69,0 L27,40 L25,40 Z M73,0 L87,0 L45,40 L31,40 Z"),
            Width = 62, Height = 32, Stretch = Stretch.Fill, Opacity = .25, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, 12, 0) };
        stripe.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "UnifiedAccent"); Grid.SetColumn(stripe, 1); header.Children.Add(stripe);
        var close = new Button { Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L12,12 M12,0 L0,12"), Width = 11, Height = 11, Stretch = Stretch.Uniform, StrokeThickness = 1.5 } };
        UnifiedDialogAppearance.Button(owner, close, DialogButtonKind.Close); AutomationProperties.SetName(close, L("Schließen", "Close")); Grid.SetColumn(close, 2); header.Children.Add(close); layout.Children.Add(header);
        var rows = new StackPanel(); var chosen = options.Select(x => x with { Portable = null }).ToArray();
        var start = Action(reinstalling ? L("↻ Neu installieren", "↻ Reinstall") : L("↓ Auswahl starten", "↓ Start selected")); start.Width = 156;
        var count = Text("", 10, true); count.VerticalAlignment = VerticalAlignment.Center;
        var targetChecks = new List<Func<bool>>();
        void Update()
        {
            int portable = chosen.Count(x => x.Portable is not null || x.Installer?.PortableUpdate is not null);
            count.Text = (chosen.Length - portable) + " Installer · " + portable + " ZIP/Portable";
            var paths = chosen.Select(x => x.Portable is not null && x.Parent is not null
                ? ProgramPortable.Destination(x.Entry, x.Portable, x.Parent) : x.TargetPath).OfType<string>();
            bool overlap = ProgramInstallTargets.Overlapping(paths);
            if (overlap) count.Text = L("Bitte getrennte Programmordner wählen", "Choose separate application folders");
            start.IsEnabled = chosen.All(x => x.Portable is null || x.Parent is not null) && targetChecks.All(check => check()) && !overlap;
        }
        for (int n = 0; n < options.Count; n++)
        {
            int index = n; var option = options[n]; var entry = option.Entry;
            var stack = new StackPanel(); var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(38) }); heading.ColumnDefinitions.Add(new());
            var icon = view.Assets.Icon(entry.Id);
            heading.Children.Add(icon is null ? ProgramFallbackIcon.Create(26, "UnifiedAccent") : new Image { Source = icon, Width = 28, Height = 28, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            var names = new StackPanel(); var name = Text(entry.Name, 15); name.FontWeight = FontWeights.SemiBold; names.Children.Add(name);
            var sourceLine = Text((inventory.Installed.TryGetValue(entry.Id, out var installed) ? installed + " → " : "")
                + (option.Installer?.Version ?? L("Herstellerseite", "Publisher website")) + " · " + (option.Installer?.VersionSource ?? entry.Publisher), 10, true); names.Children.Add(sourceLine); var installerSource = sourceLine.Text;
            string ReinstallLabel(ProgramPackage package) => package.ManagedPackage is { } managed
                ? managed.Manager + " · " + managed.Target.Id + " · " + (managed.Target.Scope == "User" ? L("Benutzer", "User") : L("Alle Benutzer", "All users")) + " · " + managed.Target.OldVersion + " → " + managed.Version
                : (package.ReinstallProof?.SameVersion == true
                ? L("Dieselbe bestätigte Paketversion: ", "Same confirmed package version: ")
                : L("Aktuelle verfügbare Version: ", "Current available version: ")) + package.Version + " · " + package.VersionSource;
            if (option.Reinstall && option.Installer is { } reinstallPackage) sourceLine.Text = ReinstallLabel(reinstallPackage);
            Grid.SetColumn(names, 1); heading.Children.Add(names); stack.Children.Add(heading);
            if (option.Installer?.ReinstallProof?.Installed.VlcCopy is { } vlcCopy)
                stack.Children.Add(Text(L("Installationskopie: ", "Installation copy: ") + vlcCopy.Label, 10, true));
            TextBlock? managerDetails = null;
            if ((option.Reinstall || option.Installer?.ManagedPackage is not null) && option.ReinstallOptions is { Count: > 1 } versions)
            {
                var versionChoice = new System.Windows.Controls.ComboBox { Name = "ProgramReinstallVersionChoice", Margin = new(0, 9, 0, 4), MinHeight = 34 };
                foreach (var version in versions) versionChoice.Items.Add(new ComboBoxItem { Content = ReinstallLabel(version), Tag = version });
                versionChoice.SelectedIndex = 0;
                versionChoice.SelectionChanged += (_, _) =>
                {
                    if ((versionChoice.SelectedItem as ComboBoxItem)?.Tag is not ProgramPackage selected) return;
                    chosen[index] = chosen[index] with { Installer = selected }; sourceLine.Text = ReinstallLabel(selected); Update();
                    if (managerDetails is not null && selected.ManagedPackage is { } manager) managerDetails.Text = manager.Description(english);
                };
                AutomationProperties.SetName(versionChoice, option.Reinstall ? L("Version für Neuinstallation auswählen", "Select reinstallation version") : L("Paketinstallation auswählen", "Select package installation"));
                stack.Children.Add(versionChoice);
            }
            if (option.Installer?.ManagedPackage is { } managedPackage)
            {
                managerDetails = Text(managedPackage.Description(english), 11, true); stack.Children.Add(managerDetails);
                sourceLine.Text = ReinstallLabel(option.Installer);
            }
            else if (option.Reinstall && option.Installer?.ReinstallProof is { SameVersion: false } selected)
                stack.Children.Add(Text(selected.Installed.PackageVersion is null && selected.Installed.InstalledVersion is null
                    ? L("Installierte Version nicht bestätigt. Ausgeführt wird ausdrücklich die oben angegebene aktuelle Version.", "Installed version is unconfirmed. The current version shown above will explicitly be installed.")
                    : L("Dieselbe installierte Version wurde nicht als lieferbar bestätigt. Ausgeführt wird die oben angegebene aktuelle Version.", "The same installed version was not verified as available. The current version shown above will be installed."), 11, true));
            if (option.Installer?.PortableUpdate is { } direct)
            {
                sourceLine.Text = direct.InstalledVersion + " → " + direct.Version + L(" · Hersteller · Portable", " · Publisher · Portable");
                stack.Children.Add(Text(direct.Id == "vlc"
                    ? L("Ganzes VLC-ZIP-Paket · Sicherung unter AppData · VLC-Einstellungen bleiben erhalten", "Complete VLC ZIP package · backup under AppData · VLC settings are preserved")
                    : (direct.Parts?.Count ?? 1) + L(" Programmdatei(en) · Sicherung unter AppData · Einstellungen bleiben erhalten", " executable file(s) · backup under AppData · settings are preserved"), 10, true));
                var location = Text(ProgramLocation.Shorten(direct.Path), 10, true); location.ToolTip = direct.Path; stack.Children.Add(location);
            }
            if (option.Installer?.GitHubUpdate is { } github)
            {
                stack.Children.Add(Text(github.Kind == "VentoyArchive"
                    ? L("Windows-Paket · Dateisicherung vor Austausch · USB-Datenträger werden nicht verändert", "Windows package · file backup before replacement · USB drives are not modified")
                    : L("Offizieller Installer · Download mit SHA-256-Prüfung", "Official installer · download verified with SHA-256"), 10, true));
                stack.Children.Add(Text(ProgramLocation.Shorten(github.Path), 10, true));
            }
            if (option.Reinstall || !inventory.HasUpdate(entry.Id))
            {
                var picker = new ProgramInstallTargetPicker(owner, entry, english, (value, valid) => { chosen[index] = chosen[index] with { TargetPath = value }; Update(); },
                    ProgramDestinationPolicy.Restriction(option, inventory, english, view.IsInstalled(entry.Id)), ProgramDestinationPolicy.ExistingDirectory(option, inventory));
                stack.Children.Add(picker);
                targetChecks.Add(() => chosen[index].Portable is not null || picker.IsValid);
                if (option.Portable is not null && !view.IsInstalled(entry.Id))
                {
                    var variants = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(38, 9, 0, 0) };
                    string portableLabel = entry.Id == "vlc" ? "VLC ZIP" : "Portable";
                    var installer = Action("✓ Installer"); installer.Tag = "installer"; var portable = Action(portableLabel); portable.Tag = "portable"; foreach (var button in new[] { installer, portable }) { button.Width = 112; button.Height = button.MinHeight = 29; button.FontSize = 11; button.Margin = new(0, 0, 7, 0); variants.Children.Add(button); }
                    installer.BorderThickness = new(2); stack.Children.Add(variants);
                    var target = new Grid { Margin = new(38, 10, 0, 0), Visibility = Visibility.Collapsed }; target.ColumnDefinitions.Add(new()); target.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    var targetText = Text(L("Zielordner auswählen …", "Choose destination …"), 11, true); targetText.VerticalAlignment = VerticalAlignment.Center; target.Children.Add(targetText);
                    var browse = Action("…"); browse.Width = 32; browse.Height = browse.MinHeight = 25; browse.Margin = new(7, 0, 0, 0); Grid.SetColumn(browse, 1); target.Children.Add(browse); stack.Children.Add(target);
                    var note = Text(entry.Id == "vlc" ? L("Offizielles VLC-ZIP ohne Installer. VLC verwaltet seine Einstellungen weiterhin wie bisher.", "Official VLC ZIP without installer. VLC continues to manage its settings as before.")
                        : L("OBS wird mit eigener portabler Konfiguration eingerichtet.", "OBS will use its own portable configuration."), 10, true); note.Margin = new(38, 5, 0, 0); note.Visibility = Visibility.Collapsed; stack.Children.Add(note);
                    installer.Click += (_, _) => { chosen[index] = chosen[index] with { Portable = null }; picker.Visibility = Visibility.Visible; installer.Content = new TextBlock { Text = "✓ Installer", Foreground = Brushes.White }; portable.Content = new TextBlock { Text = portableLabel, Foreground = Brushes.White }; sourceLine.Text = installerSource; installer.BorderThickness = new(2); portable.BorderThickness = new(1); target.Visibility = note.Visibility = Visibility.Collapsed; Update(); };
                    portable.Click += (_, _) => { chosen[index] = chosen[index] with { Portable = option.Portable }; picker.Visibility = Visibility.Collapsed; installer.Content = new TextBlock { Text = "Installer", Foreground = Brushes.White }; portable.Content = new TextBlock { Text = "✓ " + portableLabel, Foreground = Brushes.White }; sourceLine.Text = option.Portable.Version + (entry.Id == "vlc" ? " · VideoLAN · VLC ZIP" : " · OBS Project · Portable"); installer.BorderThickness = new(1); portable.BorderThickness = new(2); target.Visibility = note.Visibility = Visibility.Visible; Update(); };
                    browse.Click += (_, _) =>
                    {
                        string? parent;
                        if (selectFolder is not null) parent = selectFolder();
                        else
                        {
                            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = L("Basisordner für die portable App", "Base folder for the portable app"), UseDescriptionForTitle = true };
                            parent = dialog.ShowDialog(new WindowHandle(owner)) == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
                        }
                        if (parent is null) return;
                        try
                        {
                            var destination = ProgramPortable.Destination(entry, option.Portable, parent);
                            if (System.IO.Directory.Exists(destination) || System.IO.File.Exists(destination)) throw new System.IO.IOException(L("Zielordner existiert bereits. Bitte einen anderen Ordner wählen.", "Destination already exists. Choose another folder."));
                            chosen[index] = chosen[index] with { Parent = parent }; targetText.Text = ProgramLocation.Shorten(destination); targetText.ToolTip = destination; Update();
                        }
                        catch (Exception e) { chosen[index] = chosen[index] with { Parent = null }; targetText.Text = UiText.Error(e, english); Update(); }
                    };
                }
            }
            var frame = new Border { Child = stack, Padding = new(12), CornerRadius = new(8), BorderThickness = new(1), Margin = new(0, 0, 0, 9) };
            frame.SetResourceReference(Border.BackgroundProperty, "InputBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush"); rows.Children.Add(frame);
        }
        rows.Children.Add(Text(L("Installation erfolgt automatisch. Erforderliche Zustimmungen und Administratorrechte bleiben erforderlich. Ein gestarteter Installer wird nicht gewaltsam beendet.", "Installation runs automatically. Required agreements and administrator privileges still apply. A started installer will not be forcibly terminated."), 11, true));
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = view.Compact ? 350 : 450 };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll); DialogLayout.ScrollHint(scroll, english);
        var footer = new Grid { Margin = new(0, 16, 0, 0) }; footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.Children.Add(count);
        var cancel = Action(L("Abbrechen", "Cancel"), true); cancel.Width = 112; cancel.Margin = new(8, 0, 8, 0); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel); Grid.SetColumn(start, 2); footer.Children.Add(start); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        card.Child = layout; layer.Children.Add(card);
        void Finish(IReadOnlyList<ProgramSelection>? selection)
        { if (completion.Task.IsCompleted) return; host.Children.Remove(layer); owner.PreviewKeyDown -= Key; owner.SizeChanged -= Resize; view.Unloaded -= Unloaded; view.SelectionOpen = false; completion.TrySetResult(selection); }
        void Key(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Finish(null); } }
        void Resize(object sender, SizeChangedEventArgs e) { card.Width = Math.Min(view.Compact ? 620 : 680, Math.Max(0, host.ActualWidth - 32)); card.MaxHeight = Math.Max(0, host.ActualHeight - 32); }
        void Unloaded(object sender, RoutedEventArgs e) => Finish(null);
        close.Click += (_, _) => Finish(null); cancel.Click += (_, _) => Finish(null); start.Click += (_, _) => { if (start.IsEnabled) Finish(chosen); };
        owner.PreviewKeyDown += Key; owner.SizeChanged += Resize; view.Unloaded += Unloaded; host.Children.Add(layer); view.SelectionOpen = true; Update(); cancel.Focus(); return completion.Task;
    }
    private sealed class WindowHandle(Window window) : System.Windows.Forms.IWin32Window
    { public nint Handle => new System.Windows.Interop.WindowInteropHelper(window).Handle; }
}
