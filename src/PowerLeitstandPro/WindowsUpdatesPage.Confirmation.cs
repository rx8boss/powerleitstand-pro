using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace PowerControlProWpf;

public partial class WindowsUpdatesPage
{
    private bool ConfirmInstall(WindowsUpdateItem[] items)
    {
        if (ConfirmForTests is not null)
            return ConfirmForTests(string.Join("\n", items.Select(item => item.Title + "\n" + item.Eula)));
        return CreateInstallConfirmation(items).ShowDialog() == true;
    }

    internal Window CreateInstallConfirmation(WindowsUpdateItem[] items)
    {
        var palette = _manufacturerPalette;
        string title = L("Windows-Updates installieren?", "Install Windows updates?");
        var window = new Window
        {
            Owner = Window.GetWindow(this), Title = title, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent
        };
        var list = new StackPanel();
        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var row = new Border { Padding = new Thickness(8, 5, 8, 5) };
            if (index % 2 != 0) row.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.Colors[3]));
            var name = DialogLayout.Note(item.Title, palette, 13);
            name.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedText");
            row.Child = DialogLayout.Stack(name, DialogLayout.Note(item.Category, palette, 11));
            list.Children.Add(row);
        }
        var selected = new Border
        {
            Child = list, Padding = new Thickness(4), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 5, 0, 10),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(palette.Colors[4]))
        };
        selected.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        var count = DialogLayout.Note(items.Length + L(" ausgewählt", " selected"), palette, 12);
        var body = DialogLayout.Stack(count, selected, DialogLayout.Note(L(
            "Ein erforderlicher Neustart wird angezeigt.", "You will be notified if a restart is required."), palette, 12));
        var licenses = items.Where(item => !string.IsNullOrWhiteSpace(item.Eula)).ToArray();
        if (licenses.Length > 0)
        {
            var terms = DialogLayout.Note(L("Mit der Installation akzeptierst du die folgenden Lizenzbedingungen.",
                "By installing, you accept the following licence terms."), palette, 12);
            terms.Margin = new Thickness(0, 10, 0, 6);
            body.Children.Add(terms);
            foreach (var item in licenses)
                body.Children.Add(DialogLayout.Field(DialogLayout.Note(item.Title, palette), DialogLayout.Note(item.Eula, palette), palette));
        }
        var cancel = new Button { Content = L("Abbrechen", "Cancel"), IsCancel = true };
        string caption = _english ? $"Install {items.Length} {(items.Length == 1 ? "update" : "updates")}"
            : $"{items.Length} {(items.Length == 1 ? "Update" : "Updates")} installieren";
        var install = new Button { Content = caption, IsDefault = true };
        install.Click += (_, _) => window.DialogResult = true;
        var close = new Button();
        close.Click += (_, _) => window.Close();
        var frame = new Border();
        window.Content = frame;
        DialogLayout.Compose(window, frame, palette, title, _english, body, DialogLayout.Actions(cancel, install), close);
        UnifiedDialogAppearance.Button(window, cancel, DialogButtonKind.Primary);
        return window;
    }
}
