using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Controls;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;

namespace PowerControlProWpf;

public partial class RepairResultWindow : Window
{
    internal RepairResultWindow(RepairResultSummary summary, ThemePalette palette, bool english = false,
        DiagnosticReportStore? reports = null, string? reportTitle = null)
    {
        InitializeComponent();
        var keys = new[] { "AppBrush", "CardBrush", "InputBrush", "TextBrush", "SubtleTextBrush",
            "AccentBrush", "CardBorderBrush", "ButtonBrush" };
        var colors = new[] { palette.Colors[0], palette.Colors[3], palette.Colors[4], palette.Colors[5],
            palette.Colors[6], palette.Colors[7], palette.IsNeon ? palette.Colors[7] : palette.Colors[8],
            palette.Colors[9] };
        for (var index = 0; index < keys.Length; index++)
            Resources[keys[index]] = Solid(colors[index]);
        Resources["DialogSurface"] = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(palette.Colors[3]),
            (Color)ColorConverter.ConvertFromString(palette.Colors[4]), 45);
        Resources["ReadyBrush"] = Solid(palette.Ready);
        Resources["DangerBorderBrush"] = Solid(palette.Actions[1]);
        Resources["SuccessBrush"] = Solid(palette.Actions[2]);
        FontFamily = new FontFamily(palette.FontFamily);
        if (palette.Glow is not null)
        {
            ResultCard.Effect = new DropShadowEffect
            {
                Color = (Color)ColorConverter.ConvertFromString(palette.Glow),
                ShadowDepth = 0, BlurRadius = 12, Opacity = palette.GlowOpacity * 0.55
            };
        }
        DialogIcon.Source = new BitmapImage(new Uri(
            $"pack://application:,,,/Assets/icons/{palette.IconSet}/status.png", UriKind.Absolute));
        DialogHeader.Text = reports is not null || summary.Output is not null
            ? (english ? "Windows diagnostic result" : "Windows-Diagnose: Ergebnis")
            : (english ? "Windows repair result" : "Windows-Reparatur: Ergebnis");
        ResultTitle.Text = summary.Title;
        ResultMessage.Text = summary.Message;
        ResultDetail.Text = summary.Detail;
        LaterLabel.Text = summary.CanRestart
            ? (english ? "Later" : "Später") : (english ? "Close" : "Schließen");
        RestartLabel.Text = english ? "Restart now" : "Jetzt neu starten";
        RestartButton.Visibility = summary.CanRestart ? Visibility.Visible : Visibility.Collapsed;
        if (!summary.CanRestart)
        {
            System.Windows.Controls.Grid.SetColumnSpan(LaterButton, 2);
            LaterButton.Margin = new Thickness(0);
        }
        if (!summary.IsSuccess)
        {
            ResultBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DangerBorderBrush");
            ResultTitle.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBorderBrush");
            ResultSymbol.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBorderBrush");
            ResultSymbol.Text = "!";
        }
        if (summary.WasCancelled)
        {
            Resources["CancelledBrush"] = Solid(palette.Actions[5]);
            ResultBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "CancelledBrush");
            ResultTitle.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "CancelledBrush");
            ResultSymbol.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "CancelledBrush");
        }
        var frame = (System.Windows.Controls.Border)Content;
        ResultCard.Margin = new Thickness(0); ResultCard.Padding = new Thickness(12);
        ResultMessage.FontSize = 14; ResultMessage.LineHeight = 20;
        ResultDetail.FontSize = 13; ResultDetail.LineHeight = 19;
        DialogLayout.Take(ResultBadge); DialogLayout.Take(ResultTitle); DialogLayout.Take(ResultMessage); DialogLayout.Take(ResultDetail);
        ResultBadge.BorderThickness = new Thickness(0); ResultBadge.Width = ResultBadge.Height = 30; ResultBadge.Margin = new Thickness(0);
        ResultSymbol.FontSize = 24; ResultTitle.FontSize = 15;
        ResultMessage.Margin = new Thickness(0, 5, 0, 0);
        var status = new System.Windows.Controls.Grid();
        status.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(38) });
        status.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        ResultBadge.VerticalAlignment = VerticalAlignment.Top; status.Children.Add(ResultBadge);
        var explanation = DialogLayout.Stack(ResultTitle, ResultMessage);
        System.Windows.Controls.Grid.SetColumn(explanation, 1); status.Children.Add(explanation); ResultCard.Child = status;
        ResultDetail.Margin = new Thickness(0, 12, 0, 0);
        var body = DialogLayout.Stack(ResultCard, ResultDetail);
        StackPanel? diagnostic = null;
        if (reports is not null)
        {
            ResultCard.Child = null;
            ResultCard.Background = (Brush)Resources["InputBrush"];
            ResultCard.Padding = new Thickness(12, 8, 12, 6);
            status.ColumnDefinitions[0].Width = new GridLength(25);
            ResultBadge.Width = 18;
            ResultBadge.Height = 22;
            ResultSymbol.FontSize = 16;
            ResultDetail.Margin = new Thickness(0, 2, 0, 0);
            ResultDetail.FontSize = 12;
            ResultDetail.LineHeight = 16;
            if (!string.IsNullOrWhiteSpace(summary.Output) && summary.IsSuccess)
                explanation.Children.Remove(ResultMessage);
            diagnostic = DialogLayout.Stack(status, ResultDetail);
            ResultCard.Child = diagnostic;
        }
        Button? outputToggle = null;
        if (!string.IsNullOrWhiteSpace(summary.Output))
        {
            var output = new TextBox
            {
                Text = summary.Output, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"), FontSize = 12,
                Foreground = (Brush)Resources["TextBrush"], Background = (Brush)Resources["InputBrush"],
                BorderBrush = (Brush)Resources["CardBorderBrush"], Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(8)
            };
            if (diagnostic is null) body.Children.Add(output);
            else if (summary.ShowFullOutput) diagnostic.Children.Add(output);
            else
            {
                output.MaxHeight = 135;
                output.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                var firstLine = summary.Output.Split(['\r', '\n'], 2)[0];
                var preview = DialogLayout.Note(firstLine, palette, 12);
                preview.FontFamily = output.FontFamily;
                preview.TextWrapping = TextWrapping.NoWrap;
                preview.TextTrimming = TextTrimming.CharacterEllipsis;
                diagnostic.Children.Add(new Border
                {
                    BorderBrush = (Brush)Resources["CardBorderBrush"], BorderThickness = new Thickness(0, 1, 0, 0),
                    Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(0, 6, 0, 0), Child = preview
                });
                output.Visibility = Visibility.Collapsed;
                var toggle = new Button
                {
                    Name = "DiagnosticFullOutput",
                    Content = english ? "Show full output ▾" : "Vollständige Ausgabe anzeigen ▾",
                    Height = 22, Padding = new Thickness(3, 0, 3, 0), FontSize = 11,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left
                };
                toggle.Click += (_, _) =>
                {
                    var show = output.Visibility == Visibility.Collapsed;
                    output.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    toggle.Content = show ? (english ? "Hide full output ▴" : "Vollständige Ausgabe ausblenden ▴")
                        : (english ? "Show full output ▾" : "Vollständige Ausgabe anzeigen ▾");
                };
                diagnostic.Children.Add(toggle);
                diagnostic.Children.Add(output);
                outputToggle = toggle;
            }
        }
        FrameworkElement footer = summary.CanRestart ? DialogLayout.Actions(LaterButton, RestartButton) : DialogLayout.Actions(LaterButton);
        if (reports is not null)
        {
            body.Children.Add(DiagnosticReportHistory.Build(this, palette, english, reports, reportTitle ?? summary.Title));
            if (!summary.CanRestart)
            {
                footer = DialogLayout.Take(LaterButton);
                LaterButton.Width = 100;
                LaterButton.Height = LaterButton.MinHeight = 31;
                LaterButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                LaterButton.Content = english ? "Close" : "Schließen";
            }
        }
        DialogLayout.Compose(this, frame, palette, DialogHeader.Text, english, body, footer,
            preferredWidth: reports is null ? 548 : 670);
        if (reports is not null && !summary.CanRestart)
            UnifiedDialogAppearance.Button(this, LaterButton, DialogButtonKind.Primary);
        if (outputToggle is not null)
        {
            outputToggle.Background = System.Windows.Media.Brushes.Transparent;
            outputToggle.BorderThickness = new Thickness(0);
            outputToggle.SetResourceReference(Button.ForegroundProperty, "UnifiedMuted");
        }
    }

    private static Brush Solid(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    private void DragDialog(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void CloseDialog(object sender, RoutedEventArgs e) => DialogResult = false;
    private void RestartDialog(object sender, RoutedEventArgs e) => DialogResult = true;
}
