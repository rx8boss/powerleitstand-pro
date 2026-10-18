using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Path = System.Windows.Shapes.Path;
using Brush = System.Windows.Media.Brush;

namespace PowerControlProWpf;

internal static class ClassicCancelAppearance
{
    private sealed record Saved(ControlTemplate Template, object Opacity,
        Dictionary<TextBlock, Brush> Text, Dictionary<Path, Brush> Strokes);
    private static readonly DependencyProperty SavedProperty = DependencyProperty.RegisterAttached(
        "Saved", typeof(Saved), typeof(ClassicCancelAppearance));

    internal static void RegisterDialog(Window dialog, bool classic)
    {
        if (!classic) return;
        dialog.Loaded += (_, _) =>
        {
            foreach (var button in Children<Button>(dialog.Content).ToArray())
            {
                if (CrossButtonAppearance.HasCustomSurface(button)) continue;
                if (!Children<TextBlock>(button.Content).Any(text => text.Text is "Abbrechen" or "Cancel" or "Stop repair")) continue;
                CrossButtonAppearance.Prepare(button);
                Apply(button, true, "CrossFrame");
            }
        };
    }

    internal static void RegisterDialog(Window dialog, ThemePalette palette) =>
        RegisterDialog(dialog, palette.Colors.SequenceEqual(ThemePalette.For("Classic").Colors));

    internal static void Apply(Button button, bool classic, string frameName)
    {
        var saved = (Saved?)button.GetValue(SavedProperty);
        if (!classic)
        {
            if (saved is null) return;
            button.Template = saved.Template;
            if (saved.Opacity == DependencyProperty.UnsetValue) button.ClearValue(UIElement.OpacityProperty);
            else button.SetValue(UIElement.OpacityProperty, saved.Opacity);
            foreach (var (text, brush) in saved.Text) text.Foreground = brush;
            foreach (var path in saved.Strokes.Keys) path.SetBinding(Path.StrokeProperty, Foreground());
            button.ClearValue(SavedProperty);
            return;
        }
        if (saved is not null) return;
        saved = new Saved(button.Template, button.ReadLocalValue(UIElement.OpacityProperty),
            Children<TextBlock>(button.Content).ToDictionary(text => text, text => text.Foreground),
            Children<Path>(button.Content).ToDictionary(path => path, path => path.Stroke));
        button.SetValue(SavedProperty, saved);
        button.Opacity = 1;
        button.Template = (ControlTemplate)XamlReader.Parse($$"""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Border x:Name="{{frameName}}" Background="#FCE8EC" BorderBrush="#B51F3A"
                  BorderThickness="1.5" CornerRadius="6" Padding="{TemplateBinding Padding}"
                  TextElement.Foreground="#A41932" SnapsToDevicePixels="True">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <MultiTrigger>
                  <MultiTrigger.Conditions><Condition Property="IsEnabled" Value="True"/><Condition Property="IsMouseOver" Value="True"/></MultiTrigger.Conditions>
                  <Setter TargetName="{{frameName}}" Property="Background" Value="#C82743"/>
                  <Setter TargetName="{{frameName}}" Property="TextElement.Foreground" Value="White"/>
                  <Setter TargetName="{{frameName}}" Property="Effect"><Setter.Value><DropShadowEffect Color="#D52B48" BlurRadius="14" ShadowDepth="0" Opacity="0.45"/></Setter.Value></Setter>
                </MultiTrigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="{{frameName}}" Property="Background" Value="#8F132B"/></Trigger>
                <Trigger Property="IsEnabled" Value="False">
                  <Setter TargetName="{{frameName}}" Property="Background" Value="#E3E7ED"/>
                  <Setter TargetName="{{frameName}}" Property="BorderBrush" Value="#8793A3"/>
                  <Setter TargetName="{{frameName}}" Property="TextElement.Foreground" Value="#566273"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        button.ApplyTemplate();
        var frame = (Border)button.Template.FindName(frameName, button);
        foreach (var text in saved.Text.Keys) text.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding { Path = new PropertyPath("(0)", TextElement.ForegroundProperty), Source = frame });
        foreach (var path in saved.Strokes.Keys) path.SetBinding(Path.StrokeProperty,
            new System.Windows.Data.Binding { Path = new PropertyPath("(0)", TextElement.ForegroundProperty), Source = frame });
    }

    private static System.Windows.Data.Binding Foreground() => new()
    {
        Path = new PropertyPath("(0)", TextElement.ForegroundProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1)
    };

    private static IEnumerable<T> Children<T>(object? content) where T : DependencyObject
    {
        if (content is T item) yield return item;
        if (content is not DependencyObject parent) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
            foreach (var descendant in Children<T>(child)) yield return descendant;
    }
}
