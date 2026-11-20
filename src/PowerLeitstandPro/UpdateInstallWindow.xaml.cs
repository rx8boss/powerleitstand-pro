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
    internal AvailableUpdate SelectedUpdate { get; private set; }

    internal UpdateInstallWindow(ThemePalette palette, bool english, Version installed, AvailableUpdate update, AvailableUpdate? alternative = null)
    {
        InitializeComponent();
        SelectedUpdate = update;
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
        var currentBuild = installed == AppVersion.Installed ? AppVersion.Build : new AppBuild(installed);
        InstalledText.Text = "Version " + currentBuild.Display;
        TargetText.Text = "Version " + update.Build.Display;
        RestartText.Text = english ? "The app will restart after installation." : "Die App startet nach der Installation neu.";
        SafetyText.Text = restoring
            ? english ? "The backup is verified. The current version is saved before replacement." : "Die Sicherung wird geprüft. Vor dem Austausch wird die aktuelle Version gesichert."
            : english ? "The current version is saved before replacement. The download is checked for completeness and verified using its SHA-256 checksum."
                      : "Vor dem Austausch wird die aktuelle Version gesichert. Der Download wird auf Vollständigkeit und mit seiner SHA-256-Prüfsumme geprüft.";
        if (update.Build.CompareTo(currentBuild) < 0)
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
        UnifiedDialogAppearance.Resources(this, palette);
        HeaderCloseButton.Tag = "UnifiedHeaderClose";
        UnifiedDialogAppearance.Buttons(this, palette);
        RestartText.Margin = new Thickness(0, 0, 0, 8);
        SafetyText.Margin = new Thickness(0); SafetyText.FontSize = 13;
        System.Windows.Controls.StackPanel body;
        if (alternative is null)
        {
            body = DialogLayout.Stack(DialogLayout.Field(InstalledCaption, InstalledText, palette),
                DialogLayout.Field(TargetCaption, TargetText, palette), RestartText, SafetyText, OlderVersionText);
        }
        else
        {
            Resources["DialogAccent"] = Solid(palette.Colors[7]);
            var hotfix = update.Channel == UpdateChannel.Hotfix ? update : alternative;
            var stable = update.Channel == UpdateChannel.Stable ? update : alternative;
            HeaderText.Text = english ? "Install update" : "Update installieren";
            Title = "PowerLeitstand PRO · " + HeaderText.Text;
            TargetCaption.Text = english ? "Update channel" : "Update-Kanal";
            TargetText.Text = english ? "Stable + hotfixes" : "Stabil + Hotfixes";
            EditionPrompt.Text = english ? "Which edition would you like to install?" : "Welche Fassung möchtest du installieren?";
            HotfixEdition.Content = english ? $"{hotfix.Version} with Hotfix {hotfix.Revision}" : $"{hotfix.Version} mit Hotfix {hotfix.Revision}";
            HotfixEdition.Tag = english ? $"Latest hotfix for version {hotfix.Version}" : $"Neuester Hotfix für Version {hotfix.Version}";
            StableEdition.Content = english ? $"{stable.Version} without hotfix" : $"{stable.Version} ohne Hotfix";
            StableEdition.Tag = stable.Build == currentBuild ? (english ? "Already installed" : "Bereits installiert")
                : (english ? "Install the stable base version" : "Stabile Grundversion installieren");
            CancelCaption.Text = english ? "Back to versions" : "Zur Versionsliste";
            CancelIcon.Data = Geometry.Parse("M 8,0 L 2,6 L 8,12 M 2,6 L 14,6");
            CancelIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DialogAccent");
            UnifiedDialogAppearance.Button(this, CancelButton, DialogButtonKind.Primary);
            System.Windows.Automation.AutomationProperties.SetName(CancelButton, CancelCaption.Text);
            void SelectEdition(AvailableUpdate selected)
            {
                SelectedUpdate = selected;
                ConfirmButton.IsEnabled = selected.Build != currentBuild;
                ConfirmCaption.Text = selected.Channel == UpdateChannel.Hotfix
                    ? (english ? "Install with hotfix" : "Mit Hotfix installieren")
                    : (english ? "Install stable version" : "Grundversion installieren");
                OlderVersionText.Visibility = selected.Build.CompareTo(currentBuild) < 0 ? Visibility.Visible : Visibility.Collapsed;
                OlderVersionText.Text = english ? "Earlier versions may not contain all current features." : "Ältere Versionen enthalten möglicherweise nicht alle aktuellen Funktionen.";
                System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, ConfirmCaption.Text);
            }
            HotfixEdition.Checked += (_, _) => SelectEdition(hotfix);
            StableEdition.Checked += (_, _) => SelectEdition(stable);
            (update.Channel == UpdateChannel.Hotfix ? HotfixEdition : StableEdition).IsChecked = true;
            body = DialogLayout.Stack(VersionSummary, EditionChoices, SafetyText, OlderVersionText);
        }
        DialogLayout.Compose(this, DialogFrame, palette, HeaderText.Text, english, body,
            DialogLayout.Actions(ConfirmButton, CancelButton), HeaderCloseButton);
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
