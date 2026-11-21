using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace PowerControlProWpf;

public partial class ReleaseDetailsWindow : Window
{
    internal sealed record Card(string Heading, string Body, string Icon);
    private const string ListIcon = "M 2,3 H 20 V 19 H 2 Z M 6,8 H 16 M 6,12 H 14";
    private const string DownloadIcon = "M 11,1 V 13 M 6,8 L 11,13 L 16,8 M 2,14 V 19 H 20 V 14";
    private const string DisplayIcon = "M 2,2 H 21 V 15 H 2 Z M 11,15 V 20 M 6,20 H 16";

    internal ReleaseDetailsWindow(ThemePalette palette, bool english, UpdateRelease release, AppBuild installed)
    {
        InitializeComponent();
        DialogAppearance.ApplySurface(this, Frame, palette);
        WindowCorners.Apply(this, Frame.CornerRadius.TopLeft);
        UnifiedDialogAppearance.Resources(this, palette);
        UnifiedDialogAppearance.Button(this, CloseButton, DialogButtonKind.Close);
        UnifiedDialogAppearance.Button(this, BackToList, DialogButtonKind.Primary);
        FontFamily = new System.Windows.Media.FontFamily(palette.FontFamily);
        Resources["CardSurface"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[3]));
        Resources["CardLine"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[8])) { Opacity = .8 };
        var build = release.Update.Build;
        Heading.Text = build.Channel switch
        {
            UpdateChannel.Stable => english ? "Stable release" : "Stabile Version",
            UpdateChannel.Beta => $"Beta {build.Number}",
            _ => $"Hotfix {build.Number}"
        };
        Product.Text = $"PowerLeitstand PRO  /  Version {build.Version}";
        Title = $"{Product.Text} · {Heading.Text}";
        BadgeText.Text = english ? "✓ Installed" : "✓ Installiert";
        InstalledBadge.Visibility = build == installed ? Visibility.Visible : Visibility.Collapsed;
        ChangesHeading.Text = english ? "What changed?" : "Was hat sich geändert?";
        BackToList.Content = english ? "←  Back to version list" : "←  Zurück zur Versionsliste";
        AutomationProperties.SetName(CloseButton, english ? "Close" : "Schließen");
        CloseButton.ToolTip = AutomationProperties.GetName(CloseButton);
        Cards.ItemsSource = NotesFor(release.Update, release.Notes, english);
        SourceInitialized += (_, _) =>
        {
            var area = WindowPlacementService.GetCursorWorkAreaInDips(this);
            Width = Math.Min(548, Math.Max(260, Math.Min(area.Width - 24, Owner?.ActualWidth ?? 548)));
            MaxHeight = Math.Max(220, Math.Min(Math.Min(680, area.Height - 36), Owner is { ActualHeight: > 0 } owner ? owner.ActualHeight - 24 : 680));
            ChangesScroll.MaxHeight = Math.Max(64, MaxHeight - 196);
        };
        ContentRendered += (_, _) => WindowPlacementService.CenterOnOwner(this);
    }

    internal static Card[] NotesFor(AvailableUpdate update, string notes, bool english)
    {
        if (update.Channel == UpdateChannel.Stable)
            notes = Regex.Split(notes, @"(?im)^##[ \t]+Hotfix[ \t]+\d+[ \t]*\r?$")[0];
        return MakeCards(AppReleaseNotes.Select(notes, english)).ToArray();
    }

    private static IEnumerable<Card> MakeCards(string notes)
    {
        var text = notes.Replace("\r", "").Trim();
        if (text.Contains('\n'))
            text = Regex.Replace(text, @"^(?:Neu in Version|New in version)\b[^\n]*\n+", "", RegexOptions.IgnoreCase);
        foreach (var part in Regex.Split(text, @"(?m)^[ \t]*[•*-][ \t]+|\n[ \t]*\n"))
        {
            var body = part.Trim();
            if (body.Length == 0) continue;
            var heading = "";
            var marked = Regex.Match(body, @"^\*\*(.+?)\*\*\s*");
            var sentence = Regex.Match(body, @"^(.{4,75}?[.!?])\s+(?=\S)");
            if (marked.Success || sentence.Success)
            {
                var m = marked.Success ? marked : sentence;
                heading = m.Groups[1].Value;
                body = body[m.Length..];
            }
            var icon = Regex.IsMatch(part, "Fußzeile|footer|Versionsnummer|Infofenster|details window", RegexOptions.IgnoreCase)
                ? DisplayIcon : Regex.IsMatch(part, "install|herunterladen|download", RegexOptions.IgnoreCase) ? DownloadIcon : ListIcon;
            yield return new Card(heading, body, icon);
        }
    }

    private void CloseDetails(object sender, RoutedEventArgs e) => Close();
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !CloseButton.IsMouseOver) DragMove();
    }
}
