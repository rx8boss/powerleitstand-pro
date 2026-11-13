using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Path = System.Windows.Shapes.Path;
using Binding = System.Windows.Data.Binding;
using Control = System.Windows.Controls.Control;

namespace PowerControlProWpf;

// Local vector controls: no font glyph dependence, downloads or extra animation clocks.
internal static class ProgramButtonIcons
{
    private static readonly Dictionary<string, (string Outline, string Accent)> Symbols = new()
    {
        ["all"] = ("M3,3 H9 V9 H3 Z M15,3 H21 V9 H15 Z M3,15 H9 V21 H3 Z", "M15,15 H21 V21 H15 Z"),
        ["browser"] = ("M12,2 A10,10 0 1 1 11.99,2 M2,12 H22", "M12,2 C5,7 5,17 12,22 M12,2 C19,7 19,17 12,22"),
        ["tools"] = ("M14,3 A6,6 0 0 0 8,11 L2,17 A3,3 0 0 0 7,22 L13,16 A6,6 0 0 0 22,9", "M14,3 V9 H20 M5,18 L6,19"),
        ["development"] = ("M8,5 L2,12 L8,19 M16,5 L22,12 L16,19", "M14,3 L10,21"),
        ["media"] = ("M3,3 H21 V21 H3 Z M3,8 H21 M3,16 H21 M7,3 V8 M17,3 V8 M7,16 V21 M17,16 V21", "M10,10 L15,12 L10,14 Z"),
        ["streaming"] = ("M7,5 H17 V14 H7 Z M3,8 V13 M21,8 V13 M9,22 H15 M12,18 V22", "M4,15 C7,20 17,20 20,15"),
        ["ai"] = ("M5,6 H19 V21 H5 Z M9,10 V12 M15,10 V12 M9,16 H15", "M12,6 V2 M10,2 H14 M2,11 V16 M22,11 V16"),
        ["gaming"] = ("M7,6 H17 C21,6 22,11 22,17 C22,21 18,20 16,16 H8 C6,20 2,21 2,17 C2,11 3,6 7,6 Z", "M6,10 V14 M4,12 H8 M16,11 L16.1,11 M19,14 L19.1,14"),
        ["office"] = ("M5,2 H15 L20,7 V22 H5 Z M15,2 V7 H20", "M8,11 H16 M8,15 H16 M8,19 H13"),
        ["communication"] = ("M2,3 H22 V17 H10 L5,22 V17 H2 Z", "M6,8 H18 M6,12 H14"),
        ["network"] = ("M8,2 H16 V8 H8 Z M2,17 H9 V23 H2 Z M15,17 H22 V23 H15 Z", "M12,8 V13 M5,17 V13 H19 V17"),
        ["search"] = ("M10,2 A8,8 0 1 1 9.99,2", "M16,16 L22,22"),
        ["check"] = ("M20,9 A9,9 0 1 0 20,16", "M20,2 V9 H13 M8,12 L11,15 L16,10"),
        ["install"] = ("M3,16 V22 H21 V16", "M12,2 V16 M6,10 L12,16 L18,10"),
        ["update"] = ("M3,9 A9,9 0 0 1 20,6 M21,15 A9,9 0 0 1 4,18", "M20,1 V7 H14 M4,23 V17 H10"),
        ["back"] = ("M17.5,4 L8.5,13 L17.5,22", ""),
        ["more"] = ("M3,6 H16 M3,12 H13 M3,18 H10", "M16,16 L20,20 L24,16"),
        ["stop"] = ("", "M6,6 L18,18 M18,6 L6,18"),
        ["folder"] = ("M2,6 H9 L11,9 H22 V20 H2 Z M2,12 H22", ""),
        ["delete"] = ("M4,6 H20 M9,6 V3 H15 V6 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17", ""),
        ["remove"] = ("M6,6 L18,18 M18,6 L6,18", "")
    };
    internal static void Set(Button button, string symbol, string caption, bool category = false)
    {
        string key = "ProgramButtonIcon:" + symbol;
        if (button.Content is StackPanel { Tag: string existing } content && existing == key)
        {
            if (content.Children.OfType<TextBlock>().FirstOrDefault() is { } label) label.Text = caption;
            else if (caption.Length > 0) content.Children.Add(new TextBlock { Text = caption, FontSize = category ? 10.5 : 11, VerticalAlignment = VerticalAlignment.Center });
            if (content.Children.OfType<Viewbox>().FirstOrDefault() is { } icon) icon.Margin = new(0, 0, caption.Length == 0 ? 0 : 4, 0);
        }
        else
        {
            var panel = new StackPanel { Tag = key, Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var canvas = new Canvas { Width = 26, Height = 26, IsHitTestVisible = false };
            var (outline, accent) = Symbols[symbol];
            Add(outline, null); Add(accent, symbol == "stop" ? "DangerBorderBrush" : "AccentBrush");
            void Add(string geometry, string? resource)
            {
                if (geometry.Length == 0) return;
                var path = new Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
                if (resource is not null) path.SetResourceReference(Shape.StrokeProperty, resource);
                else path.SetBinding(Shape.StrokeProperty, new Binding(nameof(Control.Foreground)) { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
                canvas.Children.Add(path);
            }
            panel.Children.Add(new Viewbox { Name = "ProgramActionIcon", Tag = symbol, Width = category ? 12 : 17, Height = category ? 12 : 17, Child = canvas, Stretch = Stretch.Uniform,
                Margin = new(0, 0, caption.Length == 0 ? 0 : 4, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            if (caption.Length > 0) panel.Children.Add(new TextBlock { Text = caption, FontSize = category ? 10.5 : 11, VerticalAlignment = VerticalAlignment.Center });
            button.Content = panel;
        }
        if (caption.Length > 0) AutomationProperties.SetName(button, caption);
    }
}
