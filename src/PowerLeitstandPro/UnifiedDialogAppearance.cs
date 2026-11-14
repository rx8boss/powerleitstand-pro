using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Path = System.Windows.Shapes.Path;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;
using Binding = System.Windows.Data.Binding;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PowerControlProWpf;

internal enum DialogButtonKind { Primary, Danger, Close }

internal static class UnifiedDialogAppearance
{
    internal static IEnumerable<T> Children<T>(object? value) where T : DependencyObject
    {
        if (value is T item) yield return item;
        if (value is not DependencyObject parent) yield break;
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
            foreach (var descendant in Children<T>(child)) yield return descendant;
    }

    internal static void Resources(Window window, ThemePalette palette)
    {
        window.Resources["DialogPalette"] = palette;
        if (!window.Resources.Contains("UnifiedDialogAction"))
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/{typeof(UnifiedDialogAppearance).Assembly.GetName().Name};component/DialogStyles.xaml", UriKind.Relative)
            });
        foreach (var (key, index) in new[] { ("UnifiedText", 5), ("UnifiedAccent", 7), ("UnifiedHover", 10), ("UnifiedMuted", 6) })
            window.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Colors[index]));
        window.Resources["UnifiedDangerBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#642035"));
        window.Resources["UnifiedDangerBorder"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF5370"));
        window.Resources["UnifiedDangerHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98283F"));
    }

    internal static void Button(Window owner, Button button, DialogButtonKind kind)
    {
        // Preserve dimensions inherited from the former style, especially header icons.
        var width = button.Width;
        var height = button.Height;
        CrossButtonAppearance.PreserveCustomSurface(button);
        button.ClearValue(Control.TemplateProperty);
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        button.ClearValue(Control.BorderThicknessProperty);
        button.ClearValue(UIElement.OpacityProperty);
        button.Style = (Style)owner.FindResource(kind switch
        {
            DialogButtonKind.Danger => "UnifiedDialogDanger",
            DialogButtonKind.Close => "UnifiedDialogClose",
            _ => "UnifiedDialogAction"
        });
        if (!double.IsNaN(width)) button.Width = width;
        if (!double.IsNaN(height)) button.Height = height;
        if (kind == DialogButtonKind.Close)
        {
            button.Width = button.Height = button.MinHeight = 28;
            button.Padding = new Thickness(0);
        }
        else if (!double.IsNaN(button.Height) && button.Height < 32) button.MinHeight = button.Height;
        if (kind == DialogButtonKind.Danger)
        {
            button.Foreground = System.Windows.Media.Brushes.White;
            foreach (var text in Children<TextBlock>(button.Content)) text.Foreground = System.Windows.Media.Brushes.White;
        }
        var crosses = new List<Path>();
        foreach (var path in Children<Path>(button.Content))
        {
            if (kind == DialogButtonKind.Close || CrossButtonAppearance.IsCross(path))
            {
                crosses.Add(path);
                path.SetBinding(Shape.StrokeProperty, new Binding
                {
                    Path = new PropertyPath("(0)", TextElement.ForegroundProperty),
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Border), 1)
                });
            }
        }
        void BindCrosses()
        {
            button.ApplyTemplate();
            var name = kind == DialogButtonKind.Close ? "CloseFrame" : kind == DialogButtonKind.Danger ? "RedCancelFrame" : "UnifiedButtonFrame";
            if (button.Template.FindName(name, button) is not Border frame) return;
            foreach (var cross in crosses) cross.SetBinding(Shape.StrokeProperty,
                new Binding { Source = frame, Path = new PropertyPath("(0)", TextElement.ForegroundProperty) });
        }
        if (crosses.Count > 0)
        {
            button.Loaded += (_, _) => BindCrosses();
            if (button.IsLoaded) BindCrosses();
        }
    }

    internal static void Buttons(Window window, ThemePalette palette)
    {
        Resources(window, palette);
        foreach (var button in Children<Button>(window.Content).ToArray())
        {
            var caption = button.Content as string ?? string.Join(" ", Children<TextBlock>(button.Content).Select(t => t.Text));
            var close = button.Tag as string == "UnifiedHeaderClose" || caption.Trim() is "×" or "✕";
            var danger = System.Text.RegularExpressions.Regex.IsMatch(caption, @"\b(Abbrechen|Cancel|Schließen|Close|Später|Later|Löschen|Delete|Herunterfahren|Shut down|Stop repair|Reparatur beenden)\b");
            Button(window, button, close ? DialogButtonKind.Close : danger ? DialogButtonKind.Danger : DialogButtonKind.Primary);
        }
    }

    internal static void Apply(Window window, Border frame, Grid layout, ThemePalette palette, string title, bool english, bool growFixedHeight = false)
    {
        window.Resources["DialogEnglish"] = english;
        Resources(window, palette);
        DialogAppearance.ApplySurface(window, frame, palette);
        frame.SetResourceReference(Border.BackgroundProperty, "DialogSurface");
        frame.SetResourceReference(Border.BorderBrushProperty, "UnifiedAccent");
        frame.BorderThickness = new Thickness(1.3);
        WindowCorners.Apply(window, frame.CornerRadius.TopLeft);
        ReplaceHeader(window, layout, title, english, growFixedHeight);
        Buttons(window, palette);
        foreach (var scroll in Children<ScrollViewer>(frame))
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        window.FontFamily = new System.Windows.Media.FontFamily(palette.FontFamily);
    }

    internal static void ReplaceHeader(Window owner, Grid layout, string title, bool english, bool growFixedHeight = false, int height = 60, Action? closeAction = null)
    {
        var old = layout.Children.Cast<UIElement>().Where(c => Grid.GetRow(c) == 0).ToArray();
        var close = old.SelectMany(Children<Button>).FirstOrDefault();
        if (close?.Parent is Panel panel) panel.Children.Remove(close);
        else if (close?.Parent is Border border) border.Child = null;
        else if (close?.Parent is ContentControl content) content.Content = null;
        foreach (var child in old) layout.Children.Remove(child);
        var previousHeight = layout.RowDefinitions[0].Height.IsAbsolute ? layout.RowDefinitions[0].Height.Value : layout.RowDefinitions[0].MinHeight;
        var delta = Math.Max(0, height - previousHeight);
        layout.RowDefinitions[0].Height = GridLength.Auto;
        layout.RowDefinitions[0].MinHeight = Math.Max(height, layout.RowDefinitions[0].MinHeight);
        if (growFixedHeight && !double.IsNaN(owner.Height))
        {
            var newHeight = owner.Height + delta;
            owner.MaxHeight = Math.Max(owner.MaxHeight, newHeight);
            if (owner.MinHeight == owner.Height) owner.MinHeight = newHeight;
            owner.Height = newHeight;
        }
        var header = new Grid { Name = "UnifiedDialogHeader", Margin = new Thickness(0, 0, 0, 12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var bolt = new Path { Data = Geometry.Parse("M 18,0 L 3,24 L 15,24 L 9,43 L 31,14 L 18,14 Z"), Width = 25, Height = 38, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        bolt.SetResourceReference(Shape.FillProperty, "UnifiedAccent"); header.Children.Add(bolt);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 100, 0) };
        var heading = new TextBlock { Name = "UnifiedDialogHeading", Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedText"); labels.Children.Add(heading);
        var subtitle = new TextBlock { Text = "PowerLeitstand PRO", FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "UnifiedMuted"); labels.Children.Add(subtitle);
        Grid.SetColumn(labels, 1); header.Children.Add(labels);
        if (close is null)
        {
            close = new Button();
            close.Click += (_, _) => { if (closeAction is not null) closeAction(); else owner.Close(); };
        }
        close.Tag = "UnifiedHeaderClose";
        close.Content = new Path { Data = Geometry.Parse("M 0,0 L 12,12 M 12,0 L 0,12"), Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        close.ToolTip = english ? "Close" : "Schließen";
        System.Windows.Automation.AutomationProperties.SetName(close, english ? "Close" : "Schließen");
        Button(owner, close, DialogButtonKind.Close); Grid.SetColumn(close, 2); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (closeAction is not null || e.LeftButton != MouseButtonState.Pressed) return;
            for (var node = e.OriginalSource as DependencyObject; node is not null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node)) if (node is Button) return;
            try { owner.DragMove(); } catch (InvalidOperationException) { }
        };
        layout.Children.Add(header);
        var stripes = new Path
        {
            Name = "UnifiedDialogStripes",
            Data = Geometry.Parse("M 65,0 L 67,0 L 8,54 L 6,54 Z M 90,0 L 92,0 L 33,54 L 31,54 Z M 94,0 L 110,0 L 51,54 L 35,54 Z"),
            Width = 92, Height = 50, Stretch = Stretch.Fill, Opacity = .16,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -12, 26, 0), IsHitTestVisible = false
        };
        stripes.SetResourceReference(Shape.FillProperty, "UnifiedAccent");
        // Keep decoration inside the header, away from body and clickable actions.
        stripes.Margin = new Thickness(0, -8, 30, 0);
        stripes.ClipToBounds = true;
        header.Children.Add(stripes); Grid.SetColumnSpan(stripes, 3);
    }
}
