using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Border = System.Windows.Controls.Border;

namespace PowerControlProWpf;

internal static class ThemedMessageBox
{
    internal static bool ShowElevationPrompt(MainWindow owner)
    {
        var english = owner.IsEnglish;
        var dialog = new ThemedMessageBoxWindow(
            english ? "This app is not running with administrator rights. Restart it as administrator? Windows will ask for confirmation."
                    : "Diese App läuft nicht mit Administratorrechten. Als Administrator neu starten? Windows fragt nach einer Bestätigung.",
            "PowerLeitstand PRO", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            ThemePalette.For(owner.CurrentThemeName), english);
        dialog.Owner = owner;
        dialog.ConfigureElevationPrompt(english);
        dialog.Loaded += (_, _) => UiSoundService.PlayAdminWarning();
        dialog.ShowDialog();
        return dialog.Result == MessageBoxResult.Yes;
    }

    internal static MessageBoxResult Show(string message, string title = "PowerLeitstand PRO",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        Show(Application.Current?.MainWindow, message, title, buttons, icon);

    internal static MessageBoxResult Show(Window? owner, string message, string title = "PowerLeitstand PRO",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var main = owner as MainWindow ?? Application.Current?.MainWindow as MainWindow;
        var dialog = new ThemedMessageBoxWindow(message, title, buttons, icon,
            ThemePalette.For(main?.CurrentThemeName ?? "Standard"), main?.IsEnglish ?? false);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }
}

public partial class ThemedMessageBoxWindow : Window
{
    private readonly MessageBoxButton _buttons;
    private readonly ThemePalette _palette;
    internal MessageBoxResult Result { get; private set; }

    internal ThemedMessageBoxWindow(string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon, ThemePalette palette, bool english)
    {
        InitializeComponent();
        _palette = palette;
        ClassicCancelAppearance.RegisterDialog(this, palette);
        _buttons = buttons;
        Result = buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.No;
        var lower = message.ToLowerInvariant();
        var deleting = lower.Contains("löschen") || lower.Contains("delete") || lower.Contains("clear");
        var logDeletion = buttons == MessageBoxButton.YesNo && deleting &&
            (lower.Contains("protokoll") || lower.Contains("log"));
        var stoppingRepair = lower.Contains("laufende windows-reparatur abbrechen") ||
                             lower.Contains("stop this running windows repair");
        var action = icon == MessageBoxImage.Question
            ? lower.Contains("windows sperren") || lower.Contains("lock windows") ? "lock"
            : lower.Contains("herunterfahren") || lower.Contains("shutdown") ? "shutdown"
            : lower.Contains("neustart") || lower.Contains("restart") ? "restart"
            : lower.Contains("abmelden") || lower.Contains("sign out") ? "signout"
            : null
            : null;
        var actionTitle = action switch
        {
            "lock" => english ? "Lock Windows?" : "Windows sperren?",
            "shutdown" => english ? "Shut down Windows?" : "Windows herunterfahren?",
            "restart" => english ? "Restart Windows?" : "Windows neu starten?",
            "signout" => english ? "Sign out?" : "Von Windows abmelden?",
            _ => title
        };
        Title = DialogTitle.Text = actionTitle;
        DialogMessage.Text = message;
        DialogSymbol.Text = icon switch
        {
            MessageBoxImage.Error => "×",
            MessageBoxImage.Warning => "!",
            MessageBoxImage.Question => "?",
            _ => "i"
        };

        if (icon == MessageBoxImage.Warning)
        {
            IconBadge.Visibility = Visibility.Collapsed;
            WarningTriangle.Visibility = Visibility.Visible;
            WarningMark.Visibility = Visibility.Visible;
        }
        else if (action is not null)
        {
            IconBadge.Visibility = Visibility.Collapsed;
            ActionGlyph.Data = IconGeometry(action);
            ActionGlyph.Fill = System.Windows.Media.Brushes.Transparent;
            ActionGlyph.Visibility = Visibility.Visible;
        }

        SetBrush("DialogBackground", palette.Colors[0]);
        Background = (Brush)Resources["DialogBackground"];
        SetBrush("DialogCard", palette.Colors[3]);
        Resources["DialogSurface"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(palette.Colors[3]),
            (Color)ColorConverter.ConvertFromString(palette.Colors[4]), 45);
        SetBrush("DialogText", palette.Colors[5]);
        SetBrush("DialogBorder", palette.Colors[7]);
        SetBrush("DialogButtonBackground", palette.Colors[9]);
        SetBrush("DialogHover", palette.Colors[10]);
        var surface = (Color)ColorConverter.ConvertFromString(palette.Colors[3]);
        var lightSurface = (surface.R * 299 + surface.G * 587 + surface.B * 114) / 1000 > 150;
        SetBrush("DialogDangerBackground", lightSurface ? "#98283F" : "#6A1F32");
        SetBrush("DialogDangerBorder", lightSurface ? "#A91F3D" : "#FF5A74");
        SetBrush("DialogDangerHover", lightSurface ? "#B93550" : "#98283F");
        SetBrush("DialogIconBrush", icon switch
        {
            MessageBoxImage.Error => palette.Actions[1],
            MessageBoxImage.Warning => palette.Actions[5],
            MessageBoxImage.Question => palette.Colors[7],
            _ => palette.Ready
        });
        FontFamily = new FontFamily(palette.FontFamily);
        if (action is not null) ApplyReferenceDesign(action);
        else if (logDeletion || stoppingRepair)
        {
            var warningBrush = Resources["DialogIconBrush"];
            ApplyReferenceDesign("repair");
            Resources["DialogIconBrush"] = warningBrush;
            DialogStripes.Height = 30;
        }

        if (buttons == MessageBoxButton.OK)
        {
            ConfirmButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = CreateActionContent("OK", "confirm", (Brush)Resources["DialogIconBrush"]);
            CancelButton.Style = (Style)FindResource("DialogButton");
            CancelButton.IsDefault = true;
            CancelButton.IsCancel = true;
            System.Windows.Controls.Grid.SetColumnSpan(CancelButton, 2);
            CancelButton.Margin = new Thickness(0);
        }
        else if (buttons == MessageBoxButton.YesNo)
        {
            var confirmCaption = deleting
                ? english ? "Delete" : "Löschen"
                : stoppingRepair ? english ? "Stop repair" : "Abbrechen"
                : action switch
                {
                    "lock" => english ? "Lock" : "Sperren",
                    "shutdown" => english ? "Shut down" : "Herunterfahren",
                    "restart" => english ? "Restart" : "Neustart",
                    "signout" => english ? "Sign out" : "Abmelden",
                    _ => english ? "Yes" : "Ja"
                };
            ConfirmButton.Content = CreateActionContent(confirmCaption,
                deleting ? "delete" : stoppingRepair ? "cancel" : action ?? "confirm",
                deleting || stoppingRepair ? System.Windows.Media.Brushes.White : (Brush)Resources["DialogIconBrush"]);
            CancelButton.Content = stoppingRepair
                ? CreateActionContent(english ? "Continue" : "Fortsetzen", "continue", (Brush)Resources["DialogIconBrush"])
                : CreateActionContent(english ? "Cancel" : "Abbrechen", "cancel",
                    (Brush)Resources["DialogDangerBorder"]);
            System.Windows.Automation.AutomationProperties.SetName(ConfirmButton, confirmCaption);
            System.Windows.Automation.AutomationProperties.SetName(CancelButton,
                stoppingRepair ? english ? "Continue" : "Fortsetzen" : english ? "Cancel" : "Abbrechen");
            if (deleting || stoppingRepair)
            {
                ConfirmButton.Style = (Style)FindResource("DangerButton");
                if (stoppingRepair) CancelButton.Style = (Style)FindResource("DialogButton");
            }
            CancelButton.IsDefault = true;
            CancelButton.IsCancel = true;
            if (action is not null || logDeletion) ApplyRedCancel(english);
            if (stoppingRepair) DialogAppearance.ApplyRedButton(ConfirmButton, (Style)FindResource("ReferenceCancelButton"));
        }
        else
        {
            throw new NotSupportedException($"Unsupported dialog buttons: {buttons}");
        }
    }

    internal void ConfigureElevationPrompt(bool english)
    {
        ApplyReferenceDesign("admin");
        Width = 430;
        DialogTitle.Text = english ? "Administrator rights required" : "Administratorrechte erforderlich";
        WarningTriangle.Visibility = Visibility.Collapsed;
        WarningMark.Visibility = Visibility.Collapsed;
        ActionGlyph.Data = Geometry.Parse("M 12,1 L 21,5 L 20,13 C 19,18 15,21 12,23 C 9,21 5,18 4,13 L 3,5 Z M 8,12 L 11,15 L 16,9");
        ActionGlyph.Fill = System.Windows.Media.Brushes.Transparent;
        ActionGlyph.Visibility = Visibility.Visible;
        var columns = ((Grid)ConfirmButton.Parent).ColumnDefinitions;
        columns[0].Width = new GridLength(2, GridUnitType.Star);
        columns[1].Width = new GridLength(1, GridUnitType.Star);
        ConfirmButton.Content = CreateActionContent(english ? "Restart as administrator" : "Als Administrator neu starten",
            "shield", (Brush)Resources["DialogIconBrush"]);
        ConfirmButton.FontSize = 12;
        ApplyRedCancel(english);
        System.Windows.Automation.AutomationProperties.SetName(ConfirmButton,
            english ? "Restart as administrator" : "Als Administrator neu starten");
        System.Windows.Automation.AutomationProperties.SetName(CancelButton, english ? "Cancel" : "Abbrechen");
    }

    private void ApplyReferenceDesign(string kind)
    {
        DialogAppearance.ApplySurface(this, DialogFrame, _palette);
        SetBrush("DialogIconBrush", kind == "shutdown" ? _palette.Actions[1] : kind == "restart" ? _palette.Ready : _palette.Colors[7]);
        LegacyCorner.Visibility = Visibility.Collapsed;
        DialogStripes.Visibility = Visibility.Visible;
    }

    private void ApplyRedCancel(bool english)
    {
        CancelButton.Content = CreateActionContent(english ? "Cancel" : "Abbrechen", "cancel", Brushes.White);
        DialogAppearance.ApplyRedButton(CancelButton, (Style)FindResource("ReferenceCancelButton"));
    }

    private static Geometry IconGeometry(string kind) => Geometry.Parse(kind switch
    {
        "lock" => "M 6,9 L 6,6 C 6,0 16,0 16,6 L 16,9 M 4,9 L 18,9 L 18,21 L 4,21 Z M 11,13 L 11,17",
        "shutdown" => "M 11,1 L 11,11 M 6,4 C 0,8 1,20 11,21 C 21,20 22,8 16,4",
        "restart" => "M 18,7 A 9,9 0 1 0 20,13 M 18,3 L 18,9 L 12,9",
        "signout" => "M 10,3 L 2,3 L 2,20 L 10,20 M 10,11 L 21,11 M 16,6 L 21,11 L 16,16",
        "delete" => "M 2,5 L 20,5 M 5,5 L 6,20 L 16,20 L 17,5 M 8,3 L 14,3 M 9,8 L 9,17 M 13,8 L 13,17",
        "confirm" => "M 2,10 L 8,16 L 20,3",
        "continue" => "M 3,3 L 3,18 L 20,10 Z",
        "shield" => "M 11,1 L 20,5 L 19,13 C 18,18 14,21 11,23 C 8,21 4,18 3,13 L 2,5 Z M 7,12 L 10,15 L 15,9",
        _ => "M 4,4 L 18,18 M 18,4 L 4,18"
    });

    private static StackPanel CreateActionContent(string caption, string kind, Brush iconBrush)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = IconGeometry(kind),
            Stroke = kind == "cancel" ? new SolidColorBrush(Color.FromRgb(255, 75, 75)) : iconBrush,
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 7, 0)
        };
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    private void SetBrush(string key, string hex) =>
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private void DragDialog(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void ConfirmDialog(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Yes;
        DialogResult = true;
    }

    private void CancelDialog(object sender, RoutedEventArgs e)
    {
        Result = _buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.No;
        DialogResult = false;
    }
}
