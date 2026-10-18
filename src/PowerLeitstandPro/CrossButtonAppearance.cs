using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using Path = System.Windows.Shapes.Path;

namespace PowerControlProWpf;

// Apply one appearance to actual cross buttons, including those in separate dialogs.
// Informational/error symbols outside buttons retain their existing appearance.
internal static class CrossButtonAppearance
{
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(255, 75, 75));
    private static readonly Brush Hover = new SolidColorBrush(Color.FromRgb(233, 39, 73));
    private static readonly Brush Pressed = new SolidColorBrush(Color.FromRgb(143, 19, 43));
    private static readonly DependencyProperty AppliedProperty = DependencyProperty.RegisterAttached(
        "Applied", typeof(bool), typeof(CrossButtonAppearance), new PropertyMetadata(false));
    private static bool _initialized;
    private static readonly DependencyProperty CustomSurfaceProperty = DependencyProperty.RegisterAttached(
        "CustomSurface", typeof(bool), typeof(CrossButtonAppearance), new PropertyMetadata(false));

    internal static void PreserveCustomSurface(Button button) => button.SetValue(CustomSurfaceProperty, true);
    internal static bool HasCustomSurface(Button button) => (bool)button.GetValue(CustomSurfaceProperty);

    internal static void Prepare(Button button) => OnButtonLoaded(button, new RoutedEventArgs());

    internal static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EventManager.RegisterClassHandler(typeof(Button), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnButtonLoaded), true);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window)
                    foreach (var button in ContentTree<Button>(window.Content).ToArray())
                        OnButtonLoaded(button, new RoutedEventArgs());
            }), true);
    }

    private static void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || HasCustomSurface(button) || (bool)button.GetValue(AppliedProperty)) return;
        var paths = ContentTree<Path>(button.Content).Where(IsCross).ToArray();
        var crossText = button.Content is string text && text.Trim() is "×" or "✕" or "✖";
        var textGlyphs = ContentTree<TextBlock>(button.Content)
            .Where(block => block.Text.Trim() is "×" or "✕" or "✖").ToArray();
        if (!crossText && paths.Length == 0 && textGlyphs.Length == 0) return;

        // Captions keep their readable text colour; only the X changes colour.
        foreach (var block in ContentTree<TextBlock>(button.Content).Except(textGlyphs))
            if (!crossText && block.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue)
                block.Foreground = block.Foreground;

        foreach (var path in paths)
            path.SetBinding(Path.StrokeProperty, GlyphBinding());
        foreach (var block in textGlyphs)
            block.SetBinding(TextBlock.ForegroundProperty, GlyphBinding());
        button.Background = System.Windows.Media.Brushes.Transparent;
        button.BorderBrush = System.Windows.Media.Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        button.Template = CreateTemplate();
        button.SetValue(AppliedProperty, true);
    }

    private static Binding GlyphBinding() => new()
    {
        Path = new PropertyPath("(0)", TextElement.ForegroundProperty),
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1)
    };

    private static bool IsCross(Path path)
    {
        var data = Regex.Replace(path.Data?.ToString(CultureInfo.InvariantCulture) ?? "", @"[\s,]+", "");
        return data is "M33L1616M163L316" or "M33L1717M173L317"
            or "M22L1818M182L218" or "M44L1818M184L418";
    }

    private static ControlTemplate CreateTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var frame = new FrameworkElementFactory(typeof(Border), "CrossFrame");
        frame.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        frame.SetValue(TextElement.ForegroundProperty, Red);
        frame.SetBinding(Border.PaddingProperty, new Binding(nameof(Button.Padding))
            { RelativeSource = RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        frame.AppendChild(content);
        template.VisualTree = frame;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Hover, "CrossFrame"));
        hover.Setters.Add(new Setter(TextElement.ForegroundProperty, System.Windows.Media.Brushes.White, "CrossFrame"));
        hover.Setters.Add(new Setter(UIElement.EffectProperty, new DropShadowEffect
        {
            Color = Color.FromRgb(255, 36, 72), ShadowDepth = 0, BlurRadius = 19, Opacity = .95
        }, "CrossFrame"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Pressed, "CrossFrame"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .5, "CrossFrame"));
        template.Triggers.Add(disabled);
        return template;
    }

    private static IEnumerable<T> ContentTree<T>(object? content) where T : DependencyObject
    {
        if (content is T result) yield return result;
        if (content is not DependencyObject parent) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
            foreach (var descendant in ContentTree<T>(child)) yield return descendant;
    }
}
