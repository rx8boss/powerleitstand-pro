using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerControlProWpf;

internal sealed class AppUninstallWindow : Window
{
    internal bool KeepData { get; private set; } = true;

    internal AppUninstallWindow(Window owner, ThemePalette palette, bool english, string executable, AppUninstall.Registration? installation)
    {
        string L(string de, string en) => english ? en : de;
        Title = L("PowerLeitstand PRO deinstallieren", "Uninstall PowerLeitstand PRO");
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        TextBlock Text(string text, bool muted = false)
        {
            var label = new TextBlock { Text = text, FontSize = muted ? 12 : 13, TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty, muted ? "UnifiedMuted" : "UnifiedText");
            return label;
        }
        Border Card(string heading, params FrameworkElement[] children)
        {
            var contents = new StackPanel();
            var label = Text(heading);
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 0, 0, 6);
            label.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedAccent");
            contents.Children.Add(label);
            foreach (var child in children) contents.Children.Add(child);
            var card = new Border { Child = contents, Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "AppUninstallCard");
            card.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
            return card;
        }

        var body = new StackPanel();
        var keep = new CheckBox
        {
            Name = "KeepUninstallData",
            Content = Text(L("Einstellungen und Protokolle behalten", "Keep settings and logs")),
            IsChecked = true,
            Margin = new Thickness(0, 3, 0, 0)
        };
        AutomationProperties.SetName(keep, L("Einstellungen und Protokolle behalten", "Keep settings and logs"));
        if (installation is not null)
        {
            body.Children.Add(Card(L("Was wird entfernt?", "What will be removed?"),
                Text(L("WinGet entfernt die installierte App und ihren Eintrag.", "WinGet removes the installed app and its registration."))));
            var data = Text(L("Ohne Haken werden diese Daten deines Windows-Kontos gelöscht.", "Uncheck to delete these files from your Windows account."), true);
            var paths = Text(Path.Combine(AppUninstall.DataFolder, "settings.json") + "\n" + Path.Combine(AppUninstall.DataFolder, "logs"), true);
            paths.Margin = new Thickness(0, 5, 0, 0);
            paths.Visibility = Visibility.Collapsed;
            keep.Unchecked += (_, _) => paths.Visibility = Visibility.Visible;
            keep.Checked += (_, _) => paths.Visibility = Visibility.Collapsed;
            body.Children.Add(Card(L("Einstellungen und Protokolle", "Settings and logs"), keep, data, paths));
            body.Children.Add(Text(L("PowerLeitstand PRO wird vor der Deinstallation geschlossen.", "PowerLeitstand PRO closes before it is uninstalled."), true));
        }
        else
        {
            body.Children.Add(Card(L("Manuell entpackte Kopie", "Manually extracted copy"),
                Text(L("Schließe PowerLeitstand PRO und entferne diese Datei anschließend im Explorer.", "Close PowerLeitstand PRO, then remove this file in File Explorer.")),
                Text(executable, true)));
            body.Children.Add(Text(L("Deine Einstellungen und Protokolle bleiben erhalten.", "Your settings and logs are kept."), true));
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("Abbrechen", "Cancel"), MinWidth = 87, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var action = new Button
        {
            Name = "ConfirmAppUninstall",
            Content = Text(installation is null ? L("Ordner öffnen", "Open folder") : L("Deinstallieren", "Uninstall")),
            Height = 34, MinWidth = 145
        };
        action.Click += (_, _) =>
        {
            KeepData = keep.IsChecked == true;
            DialogResult = true;
        };
        footer.Children.Add(cancel);
        footer.Children.Add(action);
        Resources["AppUninstallCard"] = owner.FindResource("InputBrush");
        var frame = new Border();
        Content = frame;
        DialogLayout.Compose(this, frame, palette, Title, english, body, footer, preferredWidth: 638);
        cancel.Style = (Style)FindResource("ManagerDialogTextButton");
        UnifiedDialogAppearance.Button(this, action, installation is null ? DialogButtonKind.Primary : DialogButtonKind.Danger);
        var header = UnifiedDialogAppearance.Children<Grid>(frame).Single(grid => grid.Name == "UnifiedDialogHeader");
        var subtitle = UnifiedDialogAppearance.Children<TextBlock>(header).Single(text => text.Text == "PowerLeitstand PRO");
        subtitle.Text = L("Version ", "Version ") + AppVersion.Installed + " · " + (installation is null
            ? L("Portable Kopie", "Portable copy") : L("Über WinGet installiert", "Installed through WinGet"));
        Loaded += (_, _) => Keyboard.Focus(cancel);
    }
}
