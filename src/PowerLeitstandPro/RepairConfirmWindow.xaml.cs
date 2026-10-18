using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

public partial class RepairConfirmWindow : Window
{
    private readonly bool _english;

    internal RepairConfirmWindow(string command, ThemePalette palette, bool english)
    {
        InitializeComponent();
        ClassicCancelAppearance.RegisterDialog(this, palette);
        _english = english;
        var keys = new[] { "DialogBackground", "DialogText", "DialogSubtleText",
            "DialogAccent", "DialogBorder", "DialogButtonBackground", "DialogHover" };
        var colors = new[] { palette.Colors[0], palette.Colors[5], palette.Colors[6],
            palette.Colors[7], palette.IsNeon ? palette.Colors[7] : palette.Colors[8],
            palette.Colors[9], palette.Colors[10] };
        for (var index = 0; index < keys.Length; index++)
            Resources[keys[index]] = Solid(colors[index]);
        Resources["DialogSurface"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(palette.Colors[3]),
            (Color)ColorConverter.ConvertFromString(palette.Colors[4]), 45);
        var baseColor = (Color)ColorConverter.ConvertFromString(palette.Colors[9]);
        var accentColor = (Color)ColorConverter.ConvertFromString(palette.Colors[7]);
        Resources["DialogPrimaryBackground"] = new SolidColorBrush(Mix(baseColor, accentColor, 0.36));
        Resources["DialogPrimaryHover"] = new SolidColorBrush(Mix(baseColor, accentColor, 0.58));
        var surface = (Color)ColorConverter.ConvertFromString(palette.Colors[3]);
        var lightSurface = (surface.R * 299 + surface.G * 587 + surface.B * 114) / 1000 > 150;
        Resources["DialogDangerBackground"] = Solid(lightSurface ? "#98283F" : "#6A1F32");
        Resources["DialogDangerBorder"] = Solid(lightSurface ? "#A91F3D" : "#FF5A74");
        Resources["DialogDangerHover"] = Solid(lightSurface ? "#B93550" : "#98283F");
        DialogAppearance.ApplySurface(this, DialogFrame, palette);
        DialogAppearance.ApplyRedButton(CancelButton, (Style)FindResource("CancelButtonStyle"));
        Background = (Brush)Resources["DialogBackground"];
        FontFamily = new FontFamily(palette.FontFamily);
        DialogIcon.Source = new BitmapImage(new Uri(
            $"/{typeof(RepairConfirmWindow).Assembly.GetName().Name};component/Assets/icons/{palette.IconSet}/settings.png", UriKind.Relative));
        DialogHeader.Text = (english ? "Windows repair" : "Windows-Reparatur") +
            (command.StartsWith("DISM", StringComparison.Ordinal) ? " · DISM" : " · SFC");
        CommandText.Text = command;
        QuestionText.Text = english
            ? $"{(command.StartsWith("DISM", StringComparison.Ordinal) ? "Run DISM" : "Run SFC")} as administrator? Windows files may be repaired."
            : $"{(command.StartsWith("DISM", StringComparison.Ordinal) ? "DISM" : "SFC")} mit Administratorrechten ausführen? Windows-Dateien können repariert werden.";
        CancelCaption.Text = english ? "Cancel" : "Abbrechen";
        StartCaption.Text = english ? "Start repair" : "Reparatur starten";
        CopyCaption.Text = english ? "Copy" : "Kopieren";
        CommandText.ToolTip = english ? "Select and copy the command" : "Befehl markieren und kopieren";
        System.Windows.Automation.AutomationProperties.SetName(StartButton, StartCaption.Text);
        System.Windows.Automation.AutomationProperties.SetName(CancelButton, CancelCaption.Text);
        System.Windows.Automation.AutomationProperties.SetName(CopyButton,
            english ? "Copy repair command" : "Reparaturbefehl kopieren");
    }

    private static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private static Color Mix(Color first, Color second, double secondShare) => Color.FromRgb(
        (byte)Math.Round(first.R * (1 - secondShare) + second.R * secondShare),
        (byte)Math.Round(first.G * (1 - secondShare) + second.G * secondShare),
        (byte)Math.Round(first.B * (1 - secondShare) + second.B * secondShare));

    private void DragDialog(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CancelDialog(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void StartDialog(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

    private void CopyCommand(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(CommandText.Text);
            CopyCaption.Text = _english ? "Copied" : "Kopiert";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            CopyButton.ToolTip = _english ? "Clipboard unavailable" : "Zwischenablage nicht verfügbar";
        }
    }
}
