using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

public partial class UpdateInstallWindow : Window
{
    internal UpdateInstallWindow(ThemePalette palette, bool english, Version installed, AvailableUpdate update)
    {
        InitializeComponent();
        Brush Solid(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        foreach (var (key, index) in new[] { ("DialogBackground", 0), ("DialogText", 5), ("DialogSubtleText", 6), ("DialogInput", 4), ("DialogLine", 8) })
            Resources[key] = Solid(palette.Colors[index]);
        Resources["DialogDangerBackground"] = Solid("#642035");
        Resources["DialogDangerBorder"] = Solid("#FF5370");
        Resources["DialogDangerHover"] = Solid("#98283F");
        DialogAppearance.ApplySurface(this, DialogFrame, palette);
        DialogAppearance.ApplyRedButton(CancelButton, (Style)FindResource("CancelStyle"));
        CrossButtonAppearance.Prepare(HeaderCloseButton);
        FontFamily = new FontFamily(palette.FontFamily);
        var restoring = update.SourceBackup is not null;
        HeaderText.Text = restoring ? english ? "Restore version?" : "Version wiederherstellen?"
            : english ? "Install version?" : "Version installieren?";
        Title = "PowerLeitstand PRO · " + HeaderText.Text;
        InstalledCaption.Text = english ? "Installed" : "Installiert";
        TargetCaption.Text = restoring ? english ? "Application backup" : "Programmsicherung"
            : english ? "Selected version" : "Neue Version";
        InstalledText.Text = "Version " + installed;
        TargetText.Text = "Version " + update.Version;
        RestartText.Text = english ? "The app will restart after installation." : "Die App startet nach der Installation neu.";
        SafetyText.Text = restoring
            ? english ? "The backup is verified. The current version is saved before replacement." : "Die Sicherung wird geprüft. Vor dem Austausch wird die aktuelle Version gesichert."
            : english ? "The current version is saved before replacement. The download is checked for completeness and verified using its SHA-256 checksum."
                      : "Vor dem Austausch wird die aktuelle Version gesichert. Der Download wird auf Vollständigkeit und mit seiner SHA-256-Prüfsumme geprüft.";
        if (update.Version < installed)
        {
            OlderVersionText.Visibility = Visibility.Visible;
            OlderVersionText.Text = english ? "Earlier versions may not contain all current features." : "Ältere Versionen enthalten möglicherweise nicht alle aktuellen Funktionen.";
        }
        ConfirmCaption.Text = restoring ? english ? "Restore" : "Wiederherstellen" : english ? "Install" : "Installieren";
        CancelCaption.Text = english ? "Cancel" : "Abbrechen";
        HeaderCloseButton.ToolTip = CancelCaption.Text;
        System.Windows.Automation.AutomationProperties.SetName(HeaderCloseButton, CancelCaption.Text);
        System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, ConfirmCaption.Text);
        System.Windows.Automation.AutomationProperties.SetName(CancelButton, CancelCaption.Text);
        Loaded += (_, _) =>
        {
            if (Owner is null) return;
            Width = Math.Min(Width, Math.Max(MinWidth, Owner.ActualWidth - 32));
            Height = Math.Min(Height, Math.Max(MinHeight, Owner.ActualHeight - 32));
        };
    }

    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
