using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace PowerControlProWpf;

internal sealed class ProgramInstallTargetPicker : StackPanel
{
    internal bool IsValid { get; private set; } = true;
    internal ProgramInstallTargetPicker(Window owner, ProgramEntry entry, bool english, Action<string?, bool> changed,
        string? unavailableReason = null, string? existingPath = null)
    {
        string L(string de, string en) => english ? en : de;
        TextBlock Text(string value, double size = 11)
        { var t = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap }; t.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedText"); return t; }
        Margin = new(0, 10, 0, 0);
        Name = "ProgramDestinationSection";
        var section = new StackPanel();
        var frame = new Border { Child = section, Padding = new(10), CornerRadius = new(6), BorderThickness = new(1) };
        frame.SetResourceReference(Border.BackgroundProperty, "InputBrush"); frame.SetResourceReference(Border.BorderBrushProperty, "InputBorderBrush"); Children.Add(frame);
        var title = Text(L("Installationsort", "Installation location"), 12); title.FontWeight = FontWeights.SemiBold; title.Margin = new(0, 0, 0, 6); section.Children.Add(title);
        var toggleRow = new Grid(); toggleRow.ColumnDefinitions.Add(new()); toggleRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var caption = Text(L("Eigenen Zielordner wählen", "Choose a custom destination")); caption.VerticalAlignment = VerticalAlignment.Center; toggleRow.Children.Add(caption);
        var toggle = new CheckBox { Name = "ProgramDestinationToggle", Style = (Style)owner.FindResource("DialogStatusSwitch"), Width = 42, Height = 23, IsChecked = false, IsEnabled = unavailableReason is null };
        AutomationProperties.SetName(toggle, caption.Text + " · " + entry.Name); Grid.SetColumn(toggle, 1); toggleRow.Children.Add(toggle); section.Children.Add(toggleRow);
        if (unavailableReason is not null)
        {
            var current = new TextBox { Name = "ProgramDestinationExistingPath", Text = existingPath ?? L("Standardort: vom Installer verwaltet", "Default location: managed by the installer"),
                IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new(7, 5, 7, 5), Margin = new(0, 7, 0, 5), FontSize = 11 };
            current.SetResourceReference(TextBox.BackgroundProperty, "InputBrush"); current.SetResourceReference(TextBox.ForegroundProperty, "UnifiedText"); current.SetResourceReference(TextBox.BorderBrushProperty, "InputBorderBrush");
            AutomationProperties.SetName(current, title.Text + " · " + entry.Name); section.Children.Add(current);
            var reason = Text(unavailableReason, 10); reason.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedMuted"); section.Children.Add(reason);
            return;
        }
        var fields = new StackPanel { Margin = new(0, 7, 0, 0), Visibility = Visibility.Collapsed };
        var drives = new ComboBox { Height = 28, Margin = new(0, 0, 0, 6) };
        AutomationProperties.SetName(drives, L("Ziellaufwerk", "Destination drive") + " · " + entry.Name);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                drives.Items.Add(new ComboBoxItem { Tag = drive.RootDirectory.FullName, Content = drive.Name + " · " + (drive.AvailableFreeSpace / (1024d * 1024 * 1024)).ToString("0.0") + L(" GB frei", " GB free") });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        drives.SelectedItem = drives.Items.Cast<ComboBoxItem>().FirstOrDefault(x => ((string)x.Tag).Equals("D:\\", StringComparison.OrdinalIgnoreCase))
            ?? drives.Items.Cast<ComboBoxItem>().FirstOrDefault(); fields.Children.Add(drives);
        var pathRow = new Grid(); pathRow.ColumnDefinitions.Add(new()); pathRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var path = new TextBox { Name = "ProgramDestinationPath", Height = 29, Padding = new(7, 4, 7, 4), FontSize = 11, Text = drives.SelectedItem is ComboBoxItem first ? ProgramInstallTargets.Suggest(entry, (string)first.Tag) : "" };
        path.SetResourceReference(TextBox.BackgroundProperty, "InputBrush"); path.SetResourceReference(TextBox.ForegroundProperty, "UnifiedText"); path.SetResourceReference(TextBox.BorderBrushProperty, "UnifiedAccent");
        AutomationProperties.SetName(path, L("Vollständiger Installationsordner", "Full installation folder") + " · " + entry.Name); pathRow.Children.Add(path);
        var browse = new Button { Content = "…", Width = 32, Height = 29, MinHeight = 29, Margin = new(6, 0, 0, 0) }; UnifiedDialogAppearance.Button(owner, browse, DialogButtonKind.Primary);
        AutomationProperties.SetName(browse, L("Zielordner auswählen", "Choose destination folder")); Grid.SetColumn(browse, 1); pathRow.Children.Add(browse); fields.Children.Add(pathRow);
        var note = Text("", 10); note.Margin = new(0, 5, 0, 0); fields.Children.Add(note); section.Children.Add(fields);
        var standard = Text(L("Standardpfad: Vom Installer festgelegt", "Default path: Determined by the installer"), 10); standard.Margin = new(0, 5, 0, 0); standard.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedMuted"); section.Children.Add(standard);
        bool syncingDrive = false;
        void Refresh()
        {
            fields.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; standard.Visibility = toggle.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            if (toggle.IsChecked != true) { IsValid = true; changed(null, true); return; }
            try
            {
                string target = ProgramInstallTargets.Validate(path.Text); IsValid = true; note.Text = L("Ziel: ", "Destination: ") + target; path.ToolTip = target;
                var item = drives.Items.Cast<ComboBoxItem>().FirstOrDefault(x => ((string)x.Tag).Equals(Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase));
                syncingDrive = true; try { drives.SelectedItem = item; } finally { syncingDrive = false; }
                changed(target, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { IsValid = false; note.Text = UiText.Error(e, english); changed(null, false); }
        }
        toggle.Checked += (_, _) => Refresh(); toggle.Unchecked += (_, _) => Refresh(); path.TextChanged += (_, _) => Refresh();
        drives.SelectionChanged += (_, _) => { if (!syncingDrive && drives.SelectedItem is ComboBoxItem item) path.Text = ProgramInstallTargets.Suggest(entry, (string)item.Tag); };
        browse.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = L("Vollständigen Programmzielordner auswählen", "Choose the full application destination folder"), UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (dialog.ShowDialog(new WindowHandle(owner)) == System.Windows.Forms.DialogResult.OK) path.Text = dialog.SelectedPath;
        };
    }
    private sealed class WindowHandle(Window window) : System.Windows.Forms.IWin32Window
    { public nint Handle => new System.Windows.Interop.WindowInteropHelper(window).Handle; }
}
