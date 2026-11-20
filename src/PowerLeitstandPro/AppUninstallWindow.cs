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
    internal string[] DataGroups { get; private set; } = [];

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
        body.Children.Add(Card(installation is null ? L("Portable Programmdatei", "Portable executable") : L("WinGet-Installation", "WinGet installation"),
            Text(executable), Text(L("Die App schließt sich nach deiner Bestätigung.", "The app closes when you confirm."), true)));
        var choices = new List<CheckBox>();
        var sizeToken = new CancellationTokenSource();
        Closed += (_, _) => sizeToken.Cancel();
        CheckBox Choice(string id, string label, string paths)
        {
            var box = new CheckBox { Tag = id, Content = Text(label + " · " + paths), Margin = new Thickness(0, 4, 0, 2), IsChecked = false };
            AutomationProperties.SetName(box, label);
            var token = sizeToken.Token;
            Loaded += async (_, _) =>
            {
                try
                {
                    long bytes = await Task.Run(() =>
                    {
                        long total = 0;
                        var items = new Stack<string>(paths.Split(", ").Select(leaf => Path.Combine(AppUninstall.DataFolder, leaf)));
                        while (items.TryPop(out var path))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!ProgramRemovalService.NoLinks(path)) throw new IOException();
                            FileAttributes attr;
                            try { attr = File.GetAttributes(path); } catch (FileNotFoundException) { continue; } catch (DirectoryNotFoundException) { continue; }
                            if ((attr & FileAttributes.Directory) == 0) total += new FileInfo(path).Length;
                            else foreach (var child in Directory.EnumerateFileSystemEntries(path)) items.Push(child);
                        }
                        return total;
                    }, token);
                    if (!token.IsCancellationRequested) box.Content = Text(label + " · " + paths + " · " + (bytes < 1024 ? bytes + " B" : (bytes / 1048576d).ToString("0.##") + " MB"));
                }
                catch (Exception ex) when (ex is OperationCanceledException || ProgramPathResolver.ReadFailure(ex)) { }
            };
            choices.Add(box);
            return box;
        }
        body.Children.Add(Card(L("Zusätzlich entfernen", "Also remove"),
            Text(AppUninstall.DataFolder, true),
            Choice("logs", L("Protokolle und Diagnoseberichte", "Logs and diagnostic reports"), "logs"),
            Choice("settings", L("Einstellungen und Cache", "Settings and cache"), "settings.json, sound-cache"),
            Choice("backups", L("Programmsicherungen", "Program backups"), "Backups"),
            Text(L("Ohne Auswahl wird nur das Programm entfernt. Weitere Kopien in diesem Windows-Konto können dieselben Daten verwenden.",
                "Leave all boxes clear to remove only the program. Other copies in this Windows account may use the same data."), true)));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L("Abbrechen", "Cancel"), MinWidth = 87, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var action = new Button
        {
            Name = "ConfirmAppUninstall",
            Content = Text(L("Deinstallieren", "Uninstall")),
            Height = 34, MinWidth = 145
        };
        action.Click += (_, _) =>
        {
            DataGroups = choices.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToArray();
            DialogResult = true;
        };
        footer.Children.Add(cancel);
        footer.Children.Add(action);
        Resources["AppUninstallCard"] = owner.FindResource("InputBrush");
        var frame = new Border();
        Content = frame;
        DialogLayout.Compose(this, frame, palette, Title, english, body, footer, preferredWidth: 638);
        cancel.Style = (Style)FindResource("ManagerDialogTextButton");
        UnifiedDialogAppearance.Button(this, action, DialogButtonKind.Danger);
        var header = UnifiedDialogAppearance.Children<Grid>(frame).Single(grid => grid.Name == "UnifiedDialogHeader");
        var subtitle = UnifiedDialogAppearance.Children<TextBlock>(header).Single(text => text.Text == "PowerLeitstand PRO");
        subtitle.Text = L("Version ", "Version ") + AppVersion.Installed + " · " + (installation is null
            ? L("Portable Kopie", "Portable copy") : L("Über WinGet installiert", "Installed through WinGet"));
        Loaded += (_, _) => Keyboard.Focus(cancel);
    }
}
