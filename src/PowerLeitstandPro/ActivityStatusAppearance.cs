using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using ToolTip = System.Windows.Controls.ToolTip;
using FontFamily = System.Windows.Media.FontFamily;

namespace PowerControlProWpf;

internal static class ActivityStatusAppearance
{
    internal static string Description(bool english, bool timer, bool repair, bool stopping, bool update) =>
        timer ? (english ? "Timer active" : "Timer aktiv")
        : repair ? stopping ? (english ? "Stopping repair…" : "Reparatur wird beendet…")
            : (english ? "Windows repair active" : "Windows-Reparatur aktiv")
        : update ? (english ? "Update management active" : "Updateverwaltung aktiv")
        : (english ? "Ready · no action active" : "Bereit · keine Aktion aktiv");

    internal static void Update(Ellipse dot, ThemePalette palette, bool busy, string description)
    {
        Brush Solid(string value) => new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value));
        dot.Fill = Solid(busy ? palette.Actions[5] : palette.Ready);
        if (dot.ToolTip is not ToolTip tip)
        {
            tip = new ToolTip
            {
                Placement = PlacementMode.Top, Padding = new Thickness(11, 8, 11, 8), BorderThickness = new Thickness(1),
                FontSize = 12, HasDropShadow = false,
                Template = (ControlTemplate)XamlReader.Parse("""
                    <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ToolTip">
                      <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}" CornerRadius="8">
                        <ContentPresenter/>
                      </Border>
                    </ControlTemplate>
                    """)
            };
            dot.ToolTip = tip;
            ToolTipService.SetInitialShowDelay(dot, 250);
            ToolTipService.SetShowDuration(dot, 15000);
        }
        tip.Background = Solid(palette.Colors[4]);
        tip.BorderBrush = Solid(palette.Colors[7]);
        tip.Foreground = Solid(palette.Colors[5]);
        tip.FontFamily = new FontFamily(palette.FontFamily);
        if (tip.Content is not TextBlock text) tip.Content = text = new TextBlock { MaxWidth = 280, TextWrapping = TextWrapping.Wrap };
        text.Text = description; text.Foreground = tip.Foreground;
        System.Windows.Automation.AutomationProperties.SetName(dot, description);
    }
}
