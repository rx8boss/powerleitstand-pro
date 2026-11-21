using System.Windows;
using System.Windows.Controls;
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

    private readonly bool _english;
    private readonly ThemePalette _palette;
    private readonly Version _installed;
    private bool _chooseEdition;

    internal UpdateInstallWindow(ThemePalette palette, bool english, Version installed, AvailableUpdate update,
        IReadOnlyList<AvailableUpdate>? editions = null, bool finalSummary = false)
    {
        InitializeComponent();
        _english = english;
        _palette = palette;
        _installed = installed;
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
        var hotfixes = editions?.Where(u => u.Channel == UpdateChannel.Hotfix).ToArray() ?? [];
        var stable = editions?.FirstOrDefault(u => u.Channel == UpdateChannel.Stable);
        if (finalSummary || !restoring && update.Channel == UpdateChannel.Hotfix && stable is null)
        {
            HeaderText.Text = english ? "Confirm installation" : "Installation bestätigen";
            Title = "PowerLeitstand PRO · " + HeaderText.Text;
            TargetCaption.Text = english ? "Selected" : "Ausgewählt";
            var heading = DialogLayout.Note(update.Channel == UpdateChannel.Hotfix
                ? (english ? $"What changes with Hotfix {update.Revision}?" : $"Was ändert sich mit Hotfix {update.Revision}?")
                : (english ? "Stable base version" : "Stabile Grundversion"), palette, 14);
            heading.FontWeight = FontWeights.SemiBold;
            heading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DialogBorder");
            heading.Margin = new Thickness(0, 0, 0, 5);
            var changeHeading = DialogLayout.Note(update.Channel == UpdateChannel.Hotfix
                ? (english ? $"What changed in Hotfix {update.Revision}?" : $"Was hat sich mit Hotfix {update.Revision} geändert?")
                : (english ? "What changed?" : "Was hat sich geändert?"), palette, 15);
            changeHeading.FontWeight = FontWeights.SemiBold;
            changeHeading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DialogBorder");
            var changeCards = ReleaseDetailsWindow.NotesFor(update, update.Notes, english);
            var changePanel = CreateChangeCards(changeCards, palette);
            var changeSection = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 4), Children = { changeHeading, changePanel } };
            var changeFrame = new Border { Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = changeSection };
            changeFrame.SetResourceReference(Border.BackgroundProperty, "DialogInput");
            changeFrame.SetResourceReference(Border.BorderBrushProperty, "DialogBorder");
            ConfirmCaption.Text = update.Channel == UpdateChannel.Hotfix
                ? (english ? $"Install Hotfix {update.Revision}" : $"Hotfix {update.Revision} installieren")
                : (english ? "Install stable version" : "Grundversion installieren");
            CancelCaption.Text = english ? "Back" : "Zurück";
            CancelIcon.Data = Geometry.Parse("M 8,0 L 2,6 L 8,12 M 2,6 L 14,6");
            CancelIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DialogBorder");
            System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, ConfirmCaption.Text);
            System.Windows.Automation.AutomationProperties.SetName(CancelButton, CancelCaption.Text);
            body = DialogLayout.Stack(VersionSummary, changeFrame, OlderVersionText, RestartText);
        }
        else if (hotfixes.Length == 0 || stable is null)
        {
            body = DialogLayout.Stack(DialogLayout.Field(InstalledCaption, InstalledText, palette),
                DialogLayout.Field(TargetCaption, TargetText, palette), RestartText, SafetyText, OlderVersionText);
        }
        else
        {
            Resources["DialogAccent"] = Solid(palette.Colors[7]);
            _chooseEdition = true;
            var hotfix = update.Channel == UpdateChannel.Hotfix ? update : hotfixes[0];
            HeaderText.Text = english ? "Install update" : "Update installieren";
            Title = "PowerLeitstand PRO · " + HeaderText.Text;
            TargetCaption.Text = english ? "Update channel" : "Update-Kanal";
            TargetText.Text = english ? "Stable + hotfixes" : "Stabil + Hotfixes";
            EditionPrompt.Text = english ? "Which edition would you like to install?" : "Welche Fassung möchtest du installieren?";
            HotfixEdition.Content = english ? $"{hotfix.Version} with hotfix" : $"{hotfix.Version} mit Hotfix";
            System.Windows.Automation.AutomationProperties.SetName(HotfixPicker, english ? "Select hotfix" : "Hotfix auswählen");
            foreach (var fix in hotfixes)
                HotfixPicker.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"Hotfix {fix.Revision}", Tag = fix });
            HotfixPicker.SelectedIndex = Array.IndexOf(hotfixes, hotfix);
            if (System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() is { } main)
            {
                foreach (string key in new[] { "CardBrush", "TextBrush", "InputBrush", "AccentBrush", "HoverBrush" })
                    if (main.TryFindResource(key) is Brush brush) Resources[key] = brush;
                HotfixPicker.Style = main.TryFindResource(typeof(System.Windows.Controls.ComboBox)) as Style;
                HotfixPicker.ItemContainerStyle = main.TryFindResource(typeof(System.Windows.Controls.ComboBoxItem)) as Style;
            }
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
                ConfirmCaption.Text = english ? "Continue to installation" : "Weiter zur Installation";
                HotfixInfo.Visibility = selected.Channel == UpdateChannel.Hotfix ? Visibility.Visible : Visibility.Collapsed;
                if (selected.Channel == UpdateChannel.Hotfix)
                {
                    FullNotes.Children.Clear();
                    var changesHeading = DialogLayout.Note(english ? "What changed?" : "Was hat sich geändert?", _palette, 15);
                    changesHeading.FontWeight = FontWeights.SemiBold;
                    changesHeading.SetResourceReference(TextBlock.ForegroundProperty, "DialogBorder");
                    FullNotes.Children.Add(changesHeading);
                    FullNotes.Children.Add(CreateChangeCards(ReleaseDetailsWindow.NotesFor(selected, selected.Notes, english), _palette));

                }
                OlderVersionText.Visibility = selected.Build.CompareTo(currentBuild) < 0 ? Visibility.Visible : Visibility.Collapsed;
                OlderVersionText.Text = english ? "Earlier versions may not contain all current features." : "Ältere Versionen enthalten möglicherweise nicht alle aktuellen Funktionen.";
                System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, ConfirmCaption.Text);
            }
            AvailableUpdate Picked() => (AvailableUpdate)((System.Windows.Controls.ComboBoxItem)HotfixPicker.SelectedItem).Tag;
            HotfixEdition.Checked += (_, _) => SelectEdition(Picked());
            HotfixPicker.DropDownOpened += (_, _) => HotfixEdition.IsChecked = true;
            HotfixPicker.SelectionChanged += (_, _) =>
            {
                if (HotfixEdition.IsChecked == true) SelectEdition(Picked());
                else HotfixEdition.IsChecked = true;
            };
            StableEdition.Checked += (_, _) => SelectEdition(stable);
            (update.Channel == UpdateChannel.Hotfix || update.Build == currentBuild ? HotfixEdition : StableEdition).IsChecked = true;
            body = DialogLayout.Stack(VersionSummary, EditionChoices, SafetyText, OlderVersionText);
        }
        DialogLayout.Compose(this, DialogFrame, palette, HeaderText.Text, english, body,
            DialogLayout.Actions(ConfirmButton, CancelButton), HeaderCloseButton,
            fitHeightToOwner: false);
        if (_chooseEdition)
            Loaded += (_, _) =>
            {
                SizeToContent = SizeToContent.Manual;
                Height = MaxHeight;
            };
        ContentRendered += (_, _) => WindowPlacementService.CenterOnOwner(this);
    }

    private static System.Windows.Controls.StackPanel CreateChangeCards(IEnumerable<ReleaseDetailsWindow.Card> cards, ThemePalette palette)
    {
        var panel = new System.Windows.Controls.StackPanel();
        foreach (var card in cards)
        {
            var text = new System.Windows.Controls.StackPanel();
            if (!string.IsNullOrWhiteSpace(card.Heading))
            {
                var heading = DialogLayout.Note(card.Heading, palette, 14);
                heading.FontWeight = FontWeights.SemiBold;
                heading.SetResourceReference(TextBlock.ForegroundProperty, "DialogText");
                text.Children.Add(heading);
            }
            if (!string.IsNullOrWhiteSpace(card.Body))
            {
                var description = DialogLayout.Note(card.Body, palette, 13);
                description.SetResourceReference(TextBlock.ForegroundProperty, "DialogText");
                description.LineHeight = 20;
                text.Children.Add(description);
            }
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(card.Icon), Width = 18, Height = 18, Stretch = Stretch.Uniform,
                StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Top
            };
            icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "DialogBorder");
            var iconTile = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(7), Padding = new Thickness(7),
                Margin = new Thickness(0, 0, 9, 0), Child = icon
            };
            iconTile.SetResourceReference(Border.BackgroundProperty, "DialogButtonBackground");
            var content = new System.Windows.Controls.Grid();
            content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(iconTile);
            System.Windows.Controls.Grid.SetColumn(text, 1);
            content.Children.Add(text);
            var frame = new Border
            {
                Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 7), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Child = content
            };
            frame.SetResourceReference(Border.BackgroundProperty, "DialogInput");
            frame.SetResourceReference(Border.BorderBrushProperty, "DialogLine");
            panel.Children.Add(frame);
        }
        return panel;
    }

    private string Summary(AvailableUpdate selected, bool includeDescription = false)
    {
        var cards = ReleaseDetailsWindow.NotesFor(selected, selected.Notes, _english);
        var text = string.Join(includeDescription ? "\n" : " · ", cards.Take(2).Select(c =>
            includeDescription && c.Body.Length > 0
                ? System.Text.RegularExpressions.Regex.Match(c.Body, @"^.+?(?:[.!?](?=\s|$)|$)", System.Text.RegularExpressions.RegexOptions.Singleline).Value
                : c.Heading.Length > 0 ? c.Heading : c.Body));
        return text.Length > 220 ? text[..217].TrimEnd() + "…" : text;
    }

    private void HotfixTileClicked(object sender, MouseButtonEventArgs e)
    {
        if (!HotfixPicker.IsMouseOver) HotfixEdition.IsChecked = true;
    }

    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled) return;
        if (_chooseEdition && new UpdateInstallWindow(_palette, _english, _installed, SelectedUpdate, finalSummary: true)
            { Owner = this }.ShowDialog() != true) return;
        DialogResult = true;
    }
    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source)
            for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
                if (current is System.Windows.Controls.Button) return;
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
