using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PowerControlProWpf;

internal static class ProgramFallbackIcon
{
    internal static Path Create(double size, string brush = "AccentBrush")
    {
        var icon = new Path { Data = Geometry.Parse("M 38,5 L 15,36 L 29,36 L 24,59 L 49,26 L 35,26 Z"),
            Width = size, Height = size, Stretch = Stretch.Uniform, Margin = new(2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false };
        icon.SetResourceReference(Shape.FillProperty, brush);
        return icon;
    }
}
