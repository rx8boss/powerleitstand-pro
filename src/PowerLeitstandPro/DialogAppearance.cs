using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Path = System.Windows.Shapes.Path;
using Control = System.Windows.Controls.Control;

namespace PowerControlProWpf;

internal static class DialogAppearance
{
    internal static void ApplySurface(Window dialog, Border frame, ThemePalette palette)
    {
        Color Read(int index) => (Color)ColorConverter.ConvertFromString(palette.Colors[index]);
        static Color Blend(Color a, Color b, double ratio) => Color.FromRgb(
            (byte)Math.Round(a.R * (1 - ratio) + b.R * ratio),
            (byte)Math.Round(a.G * (1 - ratio) + b.G * ratio),
            (byte)Math.Round(a.B * (1 - ratio) + b.B * ratio));
        if (palette.Colors.SequenceEqual(ThemePalette.For("Standard").Colors))
        {
            dialog.Resources["DialogSurface"] = new LinearGradientBrush(new GradientStopCollection
            {
                new((Color)ColorConverter.ConvertFromString("#001F3D"), 0),
                new((Color)ColorConverter.ConvertFromString("#001F3E"), .55),
                new((Color)ColorConverter.ConvertFromString("#011C3A"), 1)
            }, new Point(0, 0), new Point(1, 0));
            dialog.Resources["DialogButtonBackground"] = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#01365F"), (Color)ColorConverter.ConvertFromString("#00335C"), 90);
        }
        else
        {
            dialog.Resources["DialogSurface"] = new LinearGradientBrush(Blend(Read(3), Read(4), .4), Read(4), 0);
            dialog.Resources["DialogButtonBackground"] = new LinearGradientBrush(Read(9), Blend(Read(9), Read(4), .12), 90);
        }
        dialog.Resources["DialogBorder"] = new SolidColorBrush(Read(7));
        dialog.Resources["DialogSheen"] = Brushes.Transparent;
        frame.ClipToBounds = true;
        if (palette.IsNeon)
            frame.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = (Color)ColorConverter.ConvertFromString(palette.Glow!), ShadowDepth = 0, BlurRadius = 15, Opacity = palette.GlowOpacity };
    }

    internal static void ApplyRedButton(Button button, Style style)
    {
        CrossButtonAppearance.PreserveCustomSurface(button);
        button.ClearValue(Control.TemplateProperty);
        button.Style = style;
        button.Foreground = Brushes.White;
        if (button.Content is not StackPanel content) return;
        foreach (var text in content.Children.OfType<TextBlock>()) text.Foreground = Brushes.White;
        foreach (var path in content.Children.OfType<Path>())
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding
            {
                Path = new PropertyPath("(0)", System.Windows.Documents.TextElement.ForegroundProperty),
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Border), 1)
            });
        // Resolve the new template after it is attached. An ancestor binding can
        // otherwise retain the outer dialog border when the template changes.
        void BindCrossColours()
        {
            button.ApplyTemplate();
            if (button.Template.FindName("RedCancelFrame", button) is not Border frame) return;
            foreach (var path in content.Children.OfType<Path>())
                path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding
                {
                    Source = frame,
                    Path = new PropertyPath("(0)", System.Windows.Documents.TextElement.ForegroundProperty)
                });
        }
        button.Loaded += (_, _) => BindCrossColours();
        if (button.IsLoaded) BindCrossColours();
    }
}
